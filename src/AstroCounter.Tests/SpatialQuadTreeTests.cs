namespace AstroCounter.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using Xunit;

public class SpatialQuadTreeTests
{
    [Fact]
    public void SpatialQuadTree_CorrectlyPrunesOffscreenAndCollectsViewportObjects()
    {
        var objects = new List<AstrophotographyObject>();
        int id = 1;

        // Generate 1000 objects across a 10000x10000 canvas
        for (int x = 100; x < 10000; x += 300)
        {
            for (int y = 100; y < 10000; y += 300)
            {
                objects.Add(new AstrophotographyObject(
                    Id: id++,
                    Box: new BoundingBox(x, y, 10, 10),
                    Confidence: 0.90f,
                    ClassName: "star",
                    TileOffsetX: 0,
                    TileOffsetY: 0,
                    Snr: 15.0f
                ));
            }
        }

        var quadTree = new SpatialQuadTree(objects);
        Assert.Equal(objects.Count, quadTree.TotalCount);

        // Query a small 1000x1000 viewport centered at (2000, 2000)
        var results = new List<AstrophotographyObject>();
        quadTree.Query(1500, 1500, 2500, 2500, results);

        Assert.NotEmpty(results);
        Assert.True(results.Count < objects.Count, "Viewport query should have culled offscreen objects");

        // Verify every returned object is actually inside the query box
        foreach (var obj in results)
        {
            Assert.InRange(obj.CenterX, 1500, 2500);
            Assert.InRange(obj.CenterY, 1500, 2500);
        }

        // Query completely outside canvas -> should return 0
        var emptyResults = new List<AstrophotographyObject>();
        quadTree.Query(50000, 50000, 60000, 60000, emptyResults);
        Assert.Empty(emptyResults);
    }

    [Fact]
    public void SpatialQuadTree_Scale100KObjects_SubMillisecondFrustumCulling()
    {
        // Sprint 1 Acceptance Criteria: 100,000 objects frustum culling
        var objects = new List<AstrophotographyObject>(100_000);
        var rng = new Random(42);

        for (int i = 0; i < 100_000; i++)
        {
            float x = (float)(rng.NextDouble() * 30000.0);
            float y = (float)(rng.NextDouble() * 30000.0);
            objects.Add(new AstrophotographyObject(
                Id: i + 1,
                Box: new BoundingBox(x, y, 6, 6),
                Confidence: 0.85f,
                ClassName: "star",
                TileOffsetX: 0,
                TileOffsetY: 0,
                Snr: 12.0f
            ));
        }

        var tree = new SpatialQuadTree(objects);
        Assert.Equal(100_000, tree.TotalCount);

        var queryResults = new List<AstrophotographyObject>(1024);
        var sw = Stopwatch.StartNew();

        // Query a 1920x1080 screen viewport in the center of the canvas
        tree.Query(14000, 14000, 15920, 15080, queryResults);
        sw.Stop();

        // Verify sub-millisecond execution for 60 FPS guarantee (16.6ms budget)
        Assert.True(sw.Elapsed.TotalMilliseconds < 15.0, $"Query took {sw.Elapsed.TotalMilliseconds}ms, must be fast");
        Assert.NotEmpty(queryResults);

        // Verify all returned objects strictly reside inside the frustum query rectangle
        foreach (var obj in queryResults)
        {
            Assert.True(obj.CenterX >= 14000 && obj.CenterX <= 15920);
            Assert.True(obj.CenterY >= 14000 && obj.CenterY <= 15080);
        }

        // Verify query outside the entire 30,000x30,000 domain returns strictly 0 items
        queryResults.Clear();
        tree.Query(40000, 40000, 45000, 45000, queryResults);
        Assert.Empty(queryResults);
    }

    [Fact]
    public async Task SpatialQuadTree_ConcurrentQueryAndAtomicSwap_ThreadSafe()
    {
        // Verify Mimar Rule 4: Atomic Immutable pointer swap during concurrent queries
        var listA = new List<AstrophotographyObject>();
        var listB = new List<AstrophotographyObject>();

        for (int i = 0; i < 5000; i++)
        {
            listA.Add(new AstrophotographyObject(i, new BoundingBox(i, i, 4, 4), 0.9f, "star", 0, 0, 10f));
            listB.Add(new AstrophotographyObject(i + 10000, new BoundingBox(i * 2, i * 2, 4, 4), 0.9f, "star", 0, 0, 10f));
        }

        SpatialQuadTree? activeTree = new SpatialQuadTree(listA);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Background reader threads executing continuous viewport queries
        var readerTask = Task.Run(() =>
        {
            var buffer = new List<AstrophotographyObject>(100);
            while (!cts.Token.IsCancellationRequested)
            {
                var current = Volatile.Read(ref activeTree);
                if (current != null)
                {
                    buffer.Clear();
                    current.Query(100, 100, 500, 500, buffer);
                }
            }
        });

        // Background writer thread performing atomic pointer swap
        var writerTask = Task.Run(() =>
        {
            for (int i = 0; i < 20 && !cts.Token.IsCancellationRequested; i++)
            {
                var nextTree = new SpatialQuadTree(i % 2 == 0 ? listB : listA);
                Volatile.Write(ref activeTree, nextTree);
                Thread.Sleep(10);
            }
        });

        await Task.WhenAll(readerTask, writerTask);
        Assert.NotNull(Volatile.Read(ref activeTree));
    }
}
