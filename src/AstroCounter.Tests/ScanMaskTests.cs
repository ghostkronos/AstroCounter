namespace AstroCounter.Tests;

using System.Diagnostics;
using System.Numerics;
using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using Xunit;

public class ScanMaskTests
{
    private static SelectionRegion Rect(RegionKind kind, float x0, float y0, float x1, float y1) =>
        new(kind, new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) });

    [Fact]
    public void EmptyMask_AllowsEverything()
    {
        var mask = new ScanMask();
        Assert.True(mask.IsAllowed(10, 10));
    }

    [Fact]
    public void ExcludeRegion_BlocksInsideOnly()
    {
        var mask = new ScanMask();
        mask.Add(Rect(RegionKind.Exclude, 0, 0, 100, 100));

        Assert.False(mask.IsAllowed(50, 50));
        Assert.True(mask.IsAllowed(150, 150));
    }

    [Fact]
    public void IncludeRegion_RestrictsToInside_ExcludeStillWins()
    {
        var mask = new ScanMask();
        mask.Add(Rect(RegionKind.Include, 0, 0, 200, 200));
        mask.Add(Rect(RegionKind.Exclude, 50, 50, 100, 100));

        Assert.True(mask.IsAllowed(10, 10));
        Assert.False(mask.IsAllowed(75, 75));
        Assert.False(mask.IsAllowed(300, 300));
    }

    [Fact]
    public void TriangleRegion_UsesPolygonGeometry()
    {
        var tri = new SelectionRegion(RegionKind.Exclude, new[] { new Vector2(0, 0), new Vector2(100, 0), new Vector2(0, 100) });

        Assert.True(tri.Contains(10, 10));
        Assert.False(tri.Contains(90, 90));
    }

    [Fact]
    public void Filter_AppliesMaskToCentres()
    {
        var objects = new[]
        {
            new AstrophotographyObject(1, new BoundingBox(10, 10, 6, 6), 0.9f),
            new AstrophotographyObject(2, new BoundingBox(300, 300, 6, 6), 0.9f)
        };
        var result = new DetectionResult(objects, 500, 500, 1, TimeSpan.Zero, "test");
        var mask = new ScanMask();
        mask.Add(Rect(RegionKind.Exclude, 0, 0, 100, 100));

        var filtered = result.Filter(0.25f, mask);

        Assert.Equal(1, filtered.TotalCount);
        Assert.Equal(2, filtered.FilteredObjects[0].Id);
    }

    [Fact]
    public void ApplyNms_LargeCandidateSet_CompletesQuickly()
    {
        var candidates = new List<AstrophotographyObject>();
        int id = 1;
        for (int y = 0; y < 200; y++)
        {
            for (int x = 0; x < 200; x++)
            {
                candidates.Add(new AstrophotographyObject(id++, new BoundingBox(x * 12, y * 12, 6, 6), 0.5f));
            }
        }

        var sw = Stopwatch.StartNew();
        var kept = NmsFilter.ApplyNms(candidates, 0.35f);
        sw.Stop();

        Assert.Equal(40000, kept.Count);
        Assert.True(sw.Elapsed.TotalSeconds < 5, $"NMS took too long: {sw.Elapsed}");
    }
}
