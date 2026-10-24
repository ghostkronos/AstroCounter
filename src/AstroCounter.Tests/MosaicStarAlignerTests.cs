namespace AstroCounter.Tests;

using System;
using System.Collections.Generic;
using AstroCounter.Core.Astrometry;
using AstroCounter.Core.Models;
using Xunit;

public class MosaicStarAlignerTests
{
    [Fact]
    public void TryAlign_WithOverlappingStarPatternAndRotation_DerivesExactWcs()
    {
        // Reference panel centered at Orion (RA 83.82°, Dec -5.39°)
        int w = 2000;
        int h = 2000;
        var refCenter = new CelestialCoordinates(83.82, -5.39);
        var refWcs = new WcsMatrix(refCenter, w / 2.0, h / 2.0, pixelScaleArcsec: 2.0, rotationDegrees: 0.0, imageWidth: w, imageHeight: h);
        var refPanel = new MosaicPanel("p1", "Panel 1", w, h, refWcs, "p1.fits");

        // Synthesize 12 non-collinear stars in reference panel
        var rand = new Random(42);
        var baseStars = new List<(double X, double Y)>();
        for (int i = 0; i < 12; i++)
        {
            double sx = 200 + (rand.NextDouble() * 1200);
            double sy = 200 + (rand.NextDouble() * 1200);
            baseStars.Add((sx, sy));
        }

        var refAstrophotographyObjects = new List<AstrophotographyObject>();
        for (int i = 0; i < baseStars.Count; i++)
        {
            var (sx, sy) = baseStars[i];
            var sky = refWcs.PixelToEquatorial(sx, sy);
            refAstrophotographyObjects.Add(new AstrophotographyObject(
                Id: i + 1,
                Box: new BoundingBox((float)(sx - 5), (float)(sy - 5), 10, 10),
                Confidence: 0.95f,
                ClassName: "star",
                TileOffsetX: 0,
                TileOffsetY: 0,
                Snr: 45.0f,
                RaDegrees: sky.RaDegrees,
                DecDegrees: sky.DecDegrees,
                PanelId: "p1"
            ));
        }

        // Unaligned panel with shift dx=+280, dy=+180, and rotation=+12 degrees
        double angleRad = 12.0 * (Math.PI / 180.0);
        double cosA = Math.Cos(angleRad);
        double sinA = Math.Sin(angleRad);
        double shiftX = 280.0;
        double shiftY = 180.0;

        var unalignedPanel = new MosaicPanel("p2", "Panel 2", w, h, null, "p2.fits");
        var unalignedAstrophotographyObjects = new List<AstrophotographyObject>();

        for (int i = 0; i < baseStars.Count; i++)
        {
            var (rx, ry) = baseStars[i];
            // Inverse transform to find (ux, uy) such that Transform(ux, uy) = (rx, ry)
            // rx = cosA * ux - sinA * uy + shiftX
            // ry = sinA * ux + cosA * uy + shiftY
            double dx = rx - shiftX;
            double dy = ry - shiftY;
            double ux = (cosA * dx) + (sinA * dy);
            double uy = (-sinA * dx) + (cosA * dy);

            unalignedAstrophotographyObjects.Add(new AstrophotographyObject(
                Id: 100 + i,
                Box: new BoundingBox((float)(ux - 5), (float)(uy - 5), 10, 10),
                Confidence: 0.92f,
                ClassName: "star",
                TileOffsetX: 0,
                TileOffsetY: 0,
                Snr: 40.0f,
                PanelId: "p2"
            ));
        }

        // Act
        var result = MosaicStarAligner.TryAlign(
            unalignedPanel,
            unalignedAstrophotographyObjects,
            refPanel,
            refAstrophotographyObjects);

        // Assert
        Assert.True(result.IsSuccess, $"Alignment failed: {result.Message}");
        Assert.NotNull(result.SolvedWcs);
        Assert.True(result.MatchedStars >= 8, $"Matched {result.MatchedStars} stars, expected >= 8");
        Assert.True(result.RmsResidualPixels < 1.0, $"RMS residual {result.RmsResidualPixels:F2} px, expected < 1.0");

        // Ground truth center celestial coordinates of unaligned panel:
        // (ux, uy) = (w/2, h/2) -> (rx, ry) = (cosA * 1000 - sinA * 1000 + 280, sinA * 1000 + cosA * 1000 + 180)
        double expectedRx = (cosA * (w / 2.0)) - (sinA * (h / 2.0)) + shiftX;
        double expectedRy = (sinA * (w / 2.0)) + (cosA * (h / 2.0)) + shiftY;
        var expectedCenterSky = refWcs.PixelToEquatorial(expectedRx, expectedRy);

        double distToExpectedCenterDeg = result.SolvedWcs.CenterCoordinates.AngularDistanceTo(expectedCenterSky);
        Assert.True(distToExpectedCenterDeg < 0.005, $"Solved center was {distToExpectedCenterDeg:F4} degrees away from ground truth!");

        // Pixel scale must match ~2.0 arcsec
        Assert.InRange(result.SolvedWcs.PixelScaleArcsec, 1.95, 2.05);

        // Rotation must match ~12.0 degrees
        Assert.InRange(result.SolvedWcs.RotationDegrees, 11.5, 12.5);
    }

    [Fact]
    public void TryAlign_WithShuffledOrderAndRotation_DerivesExactWcs()
    {
        int w = 2000;
        int h = 2000;
        var refCenter = new CelestialCoordinates(45.0, 60.0);
        var refWcs = new WcsMatrix(refCenter, w / 2.0, h / 2.0, pixelScaleArcsec: 2.2, rotationDegrees: 0.0, imageWidth: w, imageHeight: h);
        var refPanel = new MosaicPanel("p1", "Panel 1", w, h, refWcs, "p1.fits");

        var rand = new Random(12345);
        var baseStars = new List<(double X, double Y)>();
        for (int i = 0; i < 14; i++)
        {
            baseStars.Add((250 + (rand.NextDouble() * 1100), 250 + (rand.NextDouble() * 1100)));
        }

        var refStars = new List<AstrophotographyObject>();
        for (int i = 0; i < baseStars.Count; i++)
        {
            var (sx, sy) = baseStars[i];
            var sky = refWcs.PixelToEquatorial(sx, sy);
            refStars.Add(new AstrophotographyObject(
                Id: i + 1,
                Box: new BoundingBox((float)(sx - 5), (float)(sy - 5), 10, 10),
                Confidence: 0.95f,
                ClassName: "star",
                TileOffsetX: 0,
                TileOffsetY: 0,
                Snr: 50.0f,
                RaDegrees: sky.RaDegrees,
                DecDegrees: sky.DecDegrees,
                PanelId: "p1"));
        }

        refPanel.DetectedStars = refStars;

        // Arbitrary rotation +25 degrees and translation
        double angleRad = 25.0 * (Math.PI / 180.0);
        double cosA = Math.Cos(angleRad);
        double sinA = Math.Sin(angleRad);
        double shiftX = -150.0;
        double shiftY = 220.0;

        var unalignedStars = new List<AstrophotographyObject>();
        for (int i = 0; i < baseStars.Count; i++)
        {
            var (rx, ry) = baseStars[i];
            double dx = rx - shiftX;
            double dy = ry - shiftY;
            double ux = (cosA * dx) + (sinA * dy);
            double uy = (-sinA * dx) + (cosA * dy);

            unalignedStars.Add(new AstrophotographyObject(
                Id: 200 + i,
                Box: new BoundingBox((float)(ux - 5), (float)(uy - 5), 10, 10),
                Confidence: 0.90f,
                ClassName: "star",
                TileOffsetX: 0,
                TileOffsetY: 0,
                Snr: 45.0f,
                PanelId: "p2"));
        }

        // Shuffle star list order so triangles test vertex-permutation invariance
        unalignedStars.Reverse();

        var unalignedPanel = new MosaicPanel("p2", "Panel 2", w, h, null, "p2.fits")
        {
            DetectedStars = unalignedStars
        };

        var result = MosaicStarAligner.TryAlign(unalignedPanel, unalignedStars, refPanel);

        Assert.True(result.IsSuccess, $"Alignment failed with shuffled order: {result.Message}");
        Assert.NotNull(result.SolvedWcs);
        Assert.True(result.MatchedStars >= 8, $"Matched {result.MatchedStars} stars");
        Assert.True(result.RmsResidualPixels < 1.0, $"RMS residual was {result.RmsResidualPixels:F2}");
        Assert.InRange(result.SolvedWcs.PixelScaleArcsec, 2.15, 2.25);
        Assert.InRange(result.SolvedWcs.RotationDegrees, 24.5, 25.5);
    }

    [Fact]
    public void TryAlign_NonOverlappingFields_FailsGracefully()
    {
        int w = 2000;
        int h = 2000;
        var refWcs = new WcsMatrix(new CelestialCoordinates(0, 0), w / 2.0, h / 2.0, 2.0, 0, w, h);
        var refPanel = new MosaicPanel("p1", "Panel 1", w, h, refWcs, "p1.fits");

        // 8 stars with distinct asterism A
        var refStars = new List<AstrophotographyObject>();
        for (int i = 0; i < 8; i++)
        {
            refStars.Add(new AstrophotographyObject(i + 1, new BoundingBox(100 * i, 150 * i, 10, 10), 0.9f, "star", 0, 0, 30f));
        }

        // Unaligned panel with completely different random geometric distribution
        var unalignedPanel = new MosaicPanel("p2", "Panel 2", w, h, null, "p2.fits");
        var unalignedStars = new List<AstrophotographyObject>
        {
            new(1, new BoundingBox(50, 700, 10, 10), 0.9f, "star", 0, 0, 30f),
            new(2, new BoundingBox(850, 200, 10, 10), 0.9f, "star", 0, 0, 30f),
            new(3, new BoundingBox(450, 950, 10, 10), 0.9f, "star", 0, 0, 30f),
            new(4, new BoundingBox(1200, 1200, 10, 10), 0.9f, "star", 0, 0, 30f),
            new(5, new BoundingBox(300, 300, 10, 10), 0.9f, "star", 0, 0, 30f),
        };

        var result = MosaicStarAligner.TryAlign(unalignedPanel, unalignedStars, refPanel, refStars);

        Assert.False(result.IsSuccess);
        Assert.Null(result.SolvedWcs);
    }

    [Fact]
    public void TryAlign_WithInsufficientStars_ReturnsFailed()
    {
        var refWcs = new WcsMatrix(new CelestialCoordinates(0, 0), 500, 500, 2.0, 0, 1000, 1000);
        var refPanel = new MosaicPanel("p1", "Panel 1", 1000, 1000, refWcs, "p1.fits");
        var unalignedPanel = new MosaicPanel("p2", "Panel 2", 1000, 1000, null, "p2.fits");

        var fewStars = new List<AstrophotographyObject>
        {
            new(1, new BoundingBox(10, 10, 5, 5), 0.9f, "star", 0, 0, 20f),
            new(2, new BoundingBox(20, 20, 5, 5), 0.9f, "star", 0, 0, 20f)
        };

        var result = MosaicStarAligner.TryAlign(unalignedPanel, fewStars, refPanel, fewStars);
        Assert.False(result.IsSuccess);
    }
}

