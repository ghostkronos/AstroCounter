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

    [Fact]
    public void FuseDetections_CompressedFormat_RejectsLowConfidenceOrLowSnrPhantomYoloDetections()
    {
        var photoStars = new List<AstrophotographyObject>
        {
            new AstrophotographyObject(1, new BoundingBox(100, 100, 10, 10), 0.78f, "star", Snr: 8.5f)
        };

        var yoloDetections = new List<AstrophotographyObject>
        {
            // Phantom compression noise: low confidence (0.18) and low SNR (1.9)
            new AstrophotographyObject(2, new BoundingBox(220, 220, 8, 8), 0.18f, "star", Snr: 1.9f),
            // Phantom compression ripple: moderate confidence (0.42) but sub-floor SNR (2.1)
            new AstrophotographyObject(3, new BoundingBox(260, 260, 8, 8), 0.42f, "star", Snr: 2.1f),
            // Genuine faint morphological galaxy/star above compressed floor
            new AstrophotographyObject(4, new BoundingBox(350, 350, 18, 18), 0.55f, "galaxy", Snr: 4.2f)
        };

        var fused = FusionDetectionEngine.FuseDetections(
            photoStars, yoloDetections, matchDistancePx: 10f, isCompressedFormat: true);

        Assert.Equal(2, fused.Count);
        Assert.Contains(fused, f => f.Box.X == 100 && f.Box.Y == 100);
        Assert.Contains(fused, f => f.Box.X == 350 && f.Box.Y == 350);
        Assert.DoesNotContain(fused, f => f.Box.X == 220);
        Assert.DoesNotContain(fused, f => f.Box.X == 260);
    }
}

