namespace AstroCounter.Tests;

using System.Collections.Generic;
using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using Xunit;

public class FusionDetectionEngineTests
{
    [Fact]
    public void FuseDetections_PhotometryAndYoloMatch_BoostsConfidenceAndRefinesClass()
    {
        var photoStars = new List<AstrophotographyObject>
        {
            new AstrophotographyObject(1, new BoundingBox(100, 100, 10, 10), 0.75f, "star", Snr: 9.5f)
        };

        var yoloDetections = new List<AstrophotographyObject>
        {
            new AstrophotographyObject(10, new BoundingBox(102, 101, 12, 12), 0.85f, "galaxy")
        };

        var fused = FusionDetectionEngine.FuseDetections(photoStars, yoloDetections, matchDistancePx: 10f);

        Assert.Single(fused);
        var result = fused[0];
        Assert.Equal("galaxy", result.ClassName); // Adopted galaxy morphology from YOLO
        Assert.True(result.Confidence > 0.80f, "Confidence should be boosted by synergy");
        Assert.Equal(9.5f, result.Snr);
    }

    [Fact]
    public void FuseDetections_HotPixelLowSnrWithoutYolo_DownweightedOrFiltered()
    {
        var photoStars = new List<AstrophotographyObject>
        {
            new AstrophotographyObject(1, new BoundingBox(50, 50, 4, 4), 0.20f, "faint_star", Snr: 2.1f) // noise floor candidate
        };

        var yoloDetections = new List<AstrophotographyObject>
        {
            new AstrophotographyObject(2, new BoundingBox(200, 200, 10, 10), 0.90f, "star")
        };

        var fused = FusionDetectionEngine.FuseDetections(photoStars, yoloDetections, matchDistancePx: 10f);

        // Low SNR 2.1 sigma uncorroborated should be filtered (< 0.15 downweighted)
        Assert.DoesNotContain(fused, f => f.Box.X == 50 && f.Box.Y == 50);
    }

    [Fact]
    public void FuseDetections_BrightStarUnmatchedByYolo_RetainedWithPhotometricConfidence()
    {
        var photoStars = new List<AstrophotographyObject>
        {
            new AstrophotographyObject(1, new BoundingBox(300, 300, 16, 16), 0.92f, "bright_star", Snr: 15.0f)
        };

        var yoloDetections = new List<AstrophotographyObject>();

        var fused = FusionDetectionEngine.FuseDetections(photoStars, yoloDetections, matchDistancePx: 10f);

        Assert.Single(fused);
        Assert.Equal(0.92f, fused[0].Confidence);
        Assert.Equal(15.0f, fused[0].Snr);
    }

    [Fact]
    public void FuseDetections_ExtendedNebulaFromYolo_IncludedInFinalList()
    {
        var photoStars = new List<AstrophotographyObject>();

        var yoloDetections = new List<AstrophotographyObject>
        {
            new AstrophotographyObject(1, new BoundingBox(400, 400, 60, 60), 0.70f, "nebula")
        };

        var fused = FusionDetectionEngine.FuseDetections(photoStars, yoloDetections, matchDistancePx: 10f);

        Assert.Single(fused);
        Assert.Equal("nebula", fused[0].ClassName);
    }
}

