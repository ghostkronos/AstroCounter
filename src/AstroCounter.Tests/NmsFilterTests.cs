namespace AstroCounter.Tests;

using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using Xunit;

public class NmsFilterTests
{
    [Fact]
    public void ApplyNms_IdenticalOverlappingBoxes_KeepsHighestConfidence()
    {
        var box1 = new BoundingBox(100, 100, 20, 20);
        var box2 = new BoundingBox(102, 101, 20, 20); // Very high overlap with box1

        var obj1 = new AstrophotographyObject(1, box1, 0.95f, "star");
        var obj2 = new AstrophotographyObject(2, box2, 0.40f, "star");

        var candidates = new[] { obj1, obj2 };
        var filtered = NmsFilter.ApplyNms(candidates, iouThreshold: 0.45f);

        Assert.Single(filtered);
        Assert.Equal(0.95f, filtered[0].Confidence);
    }

    [Fact]
    public void ApplyNms_DisjointStars_PreservesBoth()
    {
        var star1 = new AstrophotographyObject(1, new BoundingBox(50, 50, 10, 10), 0.90f, "star");
        var star2 = new AstrophotographyObject(2, new BoundingBox(500, 500, 12, 12), 0.85f, "star");

        var candidates = new[] { star1, star2 };
        var filtered = NmsFilter.ApplyNms(candidates, iouThreshold: 0.45f);

        Assert.Equal(2, filtered.Count);
    }
}
