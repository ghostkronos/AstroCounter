namespace AstroCounter.Tests;

using System;
using System.Collections.Generic;
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
}

