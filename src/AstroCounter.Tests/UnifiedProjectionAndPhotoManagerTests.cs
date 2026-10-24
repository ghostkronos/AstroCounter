namespace AstroCounter.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AstroCounter.Core.Astrometry;
using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using SkiaSharp;
using Xunit;

public class UnifiedProjectionAndPhotoManagerTests
{
    [Fact]
    public void AstrophotographyObject_WithCelestialCoordinates_PreservesAllPropertiesAndAddsSkyCoordinates()
    {
        var original = new AstrophotographyObject(
            Id: 42,
            Box: new BoundingBox(100f, 200f, 15f, 15f),
            Confidence: 0.95f,
            ClassName: "bright_star",
            TileOffsetX: 0,
            TileOffsetY: 0,
            Snr: 12.4f,
            IsAnomaly: true,
            AnomalyType: "TRANSIENT_SUPERNOVA");

        Assert.False(original.HasCelestialCoordinates);
        Assert.Null(original.RaDegrees);
        Assert.Null(original.DecDegrees);
        Assert.Null(original.PanelId);

        var withCoords = original.WithCelestialCoordinates(280.5, 45.2, "panel_alpha");

        Assert.True(withCoords.HasCelestialCoordinates);
        Assert.Equal(280.5, withCoords.RaDegrees);
        Assert.Equal(45.2, withCoords.DecDegrees);
        Assert.Equal("panel_alpha", withCoords.PanelId);
        Assert.Equal(42, withCoords.Id);
        Assert.Equal("bright_star", withCoords.ClassName);
        Assert.Equal(0.95f, withCoords.Confidence);
        Assert.True(withCoords.IsAnomaly);
        Assert.Equal("TRANSIENT_SUPERNOVA", withCoords.AnomalyType);
    }

    [Fact]
    public void MosaicPanel_StatusAndCenterCoordinatesText_HandlesNullWcsAndSolvedWcsSafely()
    {
        var panelWithoutWcs = new MosaicPanel("p_unsolved", "Raw Image", 1920, 1080, null!, "test.png");

        Assert.False(panelWithoutWcs.IsSolved);
        Assert.Equal("-", panelWithoutWcs.CenterCoordinatesText);
        Assert.True(
            panelWithoutWcs.StatusText.Contains("Hizalanıyor", StringComparison.OrdinalIgnoreCase) ||
            panelWithoutWcs.StatusText.Contains("Aligning", StringComparison.OrdinalIgnoreCase));

        var wcs = new WcsMatrix(new CelestialCoordinates(305.55, 40.25), 960, 540, 2.0, 0.0, 1920, 1080);
        var panelSolved = new MosaicPanel("p_solved", "Sadr Star Field", 1920, 1080, wcs, "sadr.png", targetName: "Sadr");

        Assert.True(panelSolved.IsSolved);
        Assert.Contains("Sadr", panelSolved.StatusText);
        Assert.Contains("20h", panelSolved.CenterCoordinatesText);
        Assert.Equal("1920 × 1080 px", panelSolved.ResolutionText);
    }

    [Fact]
    public async Task MosaicStarCounter_StampsAllDeduplicatedStarsWithCelestialCoordinatesAndPanelId()
    {
        var project = new MosaicProject();

        // Create Panel 1 with a star at Center (RA 180, Dec 30)
        var wcs1 = new WcsMatrix(new CelestialCoordinates(180.0, 30.0), 500, 500, 2.5, 0.0, 1000, 1000);
        using var bmp1 = new SKBitmap(1000, 1000, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp1))
        {
            canvas.Clear(SKColors.Black);
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawCircle(500, 500, 4, paint);
        }

        var panel1 = new MosaicPanel("panel_1", "Panel 1", 1000, 1000, wcs1) { CachedBitmap = bmp1 };
        project.AddPanel(panel1);

        var config = new TilingConfig(DefaultConfidenceThreshold: 0.20f, MinConfidence: 0.02f);
        var result = await MosaicStarCounter.CountMosaicStarsAsync(project, config, matchToleranceArcsec: 4.0);

        Assert.True(result.TotalUniqueStars > 0);
        var firstStar = result.AllObjects.First();

        Assert.True(firstStar.HasCelestialCoordinates, "Mosaic counted stars must carry celestial coordinates");
        Assert.NotNull(firstStar.RaDegrees);
        Assert.NotNull(firstStar.DecDegrees);
        Assert.Equal("panel_1", firstStar.PanelId);
        Assert.True(Math.Abs(firstStar.RaDegrees.Value - 180.0) < 0.1);
        Assert.True(Math.Abs(firstStar.DecDegrees.Value - 30.0) < 0.1);
    }

    [Fact]
    public void FusionDetectionEngine_SubtleAiDetections_GivenBaselineConfidenceToRemainVisible()
    {
        var photoStars = new List<AstrophotographyObject>
        {
            new AstrophotographyObject(1, new BoundingBox(100, 100, 10, 10), 0.90f, "star", Snr: 12.0f)
        };

        // Faint borderline source barely above noise (confidence 0.05 from YOLO)
        var yoloDetections = new List<AstrophotographyObject>
        {
            new AstrophotographyObject(2, new BoundingBox(450, 450, 6, 6), 0.05f, "faint_candidate")
        };

        var fused = FusionDetectionEngine.FuseDetections(photoStars, yoloDetections, matchDistancePx: 10f);

        Assert.Equal(2, fused.Count);
        var faintObj = fused.FirstOrDefault(o => o.ClassName == "faint_candidate");
        Assert.NotNull(faintObj);
        Assert.True(faintObj.Confidence >= 0.25f, "Subtle AI detections should be lifted to visible baseline in Hybrid Fusion");
    }

    [Fact]
    public void MosaicPanel_CachedSkiaBitmapPreserved_WhenCachedBitmapAssignedWpfObject()
    {
        using var skBmp = new SKBitmap(200, 200);
        var panel = new MosaicPanel("p_test", "Test Panel", 200, 200, null, "test.png")
        {
            CachedSkiaBitmap = skBmp,
            CachedBitmap = "WpfBitmapSourcePlaceholder"
        };

        Assert.NotNull(panel.CachedSkiaBitmap);
        Assert.Same(skBmp, panel.CachedSkiaBitmap);
        Assert.IsType<string>(panel.CachedBitmap);
    }

    [Fact]
    public void CelestialProjection_CenteredEye_ZeroCurvatureShear()
    {
        // Screen aspect ratio 16:9
        double screenW = 1920.0;
        double screenH = 1080.0;
        double cameraFov = 45.0; // horizontal FOV in degrees
        double sphereR = 98.2;

        double f = (screenW * 0.5) / Math.Tan(cameraFov * (Math.PI / 360.0));

        // Point at center (0, 0, -sphereR)
        double xc = 0.0, yc = 0.0, zc = sphereR;
        double sx = (screenW * 0.5) + (f * (xc / zc));
        double sy = (screenH * 0.5) - (f * (yc / zc));

        Assert.Equal(960.0, sx);
        Assert.Equal(540.0, sy);
    }

    [Fact]
    public async Task CataloguePlateSolver_DoesNotForceFalseTarget_WhenHintCoordinatesProvided()
    {
        var solver = new CataloguePlateSolver();
        // Hint near M31 (RA 10.68, Dec 41.27) but image is random uncataloged name
        var hint = new CelestialCoordinates(10.68, 41.27);
        var res = await solver.SolveAsync("IMG_20260515_010113.jpg", 4000, 3000, null, default, hint);

        // Must reject and return Failed instead of falsely assigning M31!
        Assert.False(res.IsSuccess);
    }

    [Fact]
    public void StarPatternCandidateExtraction_RejectsLowConfidenceNoise_WhenDetectedStarsPassed()
    {
        // 50 noisy stars with very low confidence (< 0.10)
        var noisyStars = new List<AstrophotographyObject>();
        for (int i = 0; i < 50; i++)
        {
            noisyStars.Add(new AstrophotographyObject(
                Id: i,
                Box: new BoundingBox(10 * i, 10 * i, 2, 2),
                Confidence: 0.05f,
                ClassName: "star",
                TileOffsetX: 0,
                TileOffsetY: 0,
                Snr: 1.2f));
        }

        // 8 genuine bright stars with high confidence
        for (int i = 0; i < 8; i++)
        {
            noisyStars.Add(new AstrophotographyObject(
                Id: 100 + i,
                Box: new BoundingBox(200 + (100 * i), 300 + (80 * i), 10, 10),
                Confidence: 0.95f,
                ClassName: "star",
                TileOffsetX: 0,
                TileOffsetY: 0,
                Snr: 15.0f));
        }

        var candidateSets = StarPatternPlateSolver.ExtractCandidateStarSets("", 4000, 3000, noisyStars, maxStars: 38);

        Assert.Single(candidateSets);
        var set = candidateSets[0];
        Assert.Equal(8, set.Count);
        // All extracted stars must be the genuine bright stars (confidence >= 0.35)
        Assert.All(set, s => Assert.True(s.Flux > 5.0));
    }

    [Fact]
    public async Task MultiPhotoAlignment_BothBootesUserPhotos_SolveToSameCelestialFieldAndAlignCommonStars()
    {
        string photo1Path = @"C:\Users\unala\.gemini\antigravity\brain\b15cadec-52bb-45f3-9cef-cd26e10d6fa3\.user_uploaded\media_1792694959922_af60c903.jpg";
        string photo2Path = @"C:\Users\unala\.gemini\antigravity\brain\b15cadec-52bb-45f3-9cef-cd26e10d6fa3\.user_uploaded\media_1792696090606_066067e6.jpg";
        if (!System.IO.File.Exists(photo1Path) || !System.IO.File.Exists(photo2Path))
            return;

        using var bmp1 = ImagePreprocessor.LoadImage(photo1Path);
        using var bmp2 = ImagePreprocessor.LoadImage(photo2Path);

        var solver = new HybridPlateSolver();

        // 1. Solve Photo 1
        var res1 = await solver.SolveAsync(photo1Path, bmp1.Width, bmp1.Height, detectedStars: null, hintCoordinates: null);
        Assert.True(res1.IsSuccess, $"Photo 1 solve failed: {res1.Message} ({res1.DiagnosticReason})");
        Assert.NotNull(res1.Wcs);

        // 2. Solve Photo 2 WITH hintCoordinates from Photo 1 (exact multi-photo pipeline behavior)
        var res2 = await solver.SolveAsync(photo2Path, bmp2.Width, bmp2.Height, detectedStars: null, hintCoordinates: res1.Wcs!.CenterCoordinates);
        Assert.True(res2.IsSuccess, $"Photo 2 solve failed: {res2.Message} ({res2.DiagnosticReason})");
        Assert.NotNull(res2.Wcs);

        // Both photos are wide-field (~50-80 deg FOV) centered in the Bootes / Corona Borealis / Hercules / Draco region
        Assert.InRange(res1.Wcs.FovWidthDegrees, 45.0, 90.0);
        Assert.InRange(res2.Wcs!.FovWidthDegrees, 40.0, 85.0);

        // Their centers should be within ~20 degrees on the sky (same overlapping constellation region)
        double centerDistDeg = res1.Wcs.CenterCoordinates.AngularDistanceTo(res2.Wcs.CenterCoordinates);
        Assert.True(centerDistDeg < 22.0, $"Expected Photo 1 and Photo 2 centers to be within 22°, but got {centerDistDeg:F2}° (P1={res1.Wcs.CenterCoordinates}, P2={res2.Wcs.CenterCoordinates})");

        // Build 2-panel MosaicProject and verify MasterWcs bounds are tight and valid
        var project = new MosaicProject();
        project.AddPanel(new MosaicPanel("p1", "Photo 1", bmp1.Width, bmp1.Height, res1.Wcs, photo1Path, res1.TargetName));
        project.AddPanel(new MosaicPanel("p2", "Photo 2", bmp2.Width, bmp2.Height, res2.Wcs, photo2Path, res2.TargetName));

        var bounds = project.ComputeBounds();
        Assert.NotNull(bounds);
        Assert.InRange(bounds!.AngularWidthDegrees, 50.0, 120.0);

        var masterWcs = project.ComputeMasterWcs();
        Assert.NotNull(masterWcs);

        // Verify common anchor stars (Arcturus, Alphecca, Izar) project inside both photos and map to consistent celestial coordinates
        var alphecca = new CelestialCoordinates(233.672, 26.715); // Alpha CrB
        Assert.True(res1.Wcs.EquatorialToPixel(alphecca, out double p1x, out double p1y));
        Assert.True(res2.Wcs.EquatorialToPixel(alphecca, out double p2x, out double p2y));
        Assert.InRange(p1x, 0, bmp1.Width);
        Assert.InRange(p1y, 0, bmp1.Height);
        Assert.InRange(p2x, 0, bmp2.Width);
        Assert.InRange(p2y, 0, bmp2.Height);

        var skyFromP1 = res1.Wcs.PixelToEquatorial(p1x, p1y);
        var skyFromP2 = res2.Wcs.PixelToEquatorial(p2x, p2y);
        Assert.True(skyFromP1.AngularDistanceTo(skyFromP2) < 0.25, $"Alphecca sky coordinates diverged between P1 and P2 by {skyFromP1.AngularDistanceTo(skyFromP2):F3}°");

        // Verify 2D canvas projection (`MosaicStarCounter` + `GetPanelCanvasTransform`):
        // Every star in Panel 1 and Panel 2 must map strictly inside its own 2D rendered quad without Gnomonic cone blowup
        var countRes = await MosaicStarCounter.CountMosaicStarsAsync(
            project,
            new AstroCounter.Core.Models.TilingConfig(DefaultConfidenceThreshold: 0.35f, MinConfidence: 0.20f),
            matchToleranceArcsec: 3.0,
            targetWcs: masterWcs);

        Assert.NotEmpty(countRes.AllObjects);
        var tr1 = MosaicProject.GetPanelCanvasTransform(project.Panels[0], masterWcs, 0);
        var tr2 = MosaicProject.GetPanelCanvasTransform(project.Panels[1], masterWcs, 1);

        foreach (var star in countRes.AllObjects)
        {
            var tr = star.PanelId == "p1" ? tr1 : tr2;
            var p = star.PanelId == "p1" ? project.Panels[0] : project.Panels[1];

            // Invert the 2D affine matrix to recover local sensor pixel (lx, ly)
            double det = (tr.M11 * tr.M22) - (tr.M12 * tr.M21);
            double dx = star.CenterX - tr.OffsetX;
            double dy = star.CenterY - tr.OffsetY;
            double lx = ((dx * tr.M22) - (dy * tr.M21)) / det;
            double ly = ((-dx * tr.M12) + (dy * tr.M11)) / det;

            Assert.InRange(lx, -1.0, p.Width + 1.0);
            Assert.InRange(ly, -1.0, p.Height + 1.0);
        }
    }

    [Fact]
    public void MultiPhoto2DCanvas_ThreeOrMorePanelsWithMixedScales_DoesNotOverZoomAnyPanel()
    {
        var project = new MosaicProject();

        // Panel 1 & 2: Wide-field photos (~55 arcsec/px)
        var wcs1 = new WcsMatrix(new CelestialCoordinates(215.0, 25.0), 2000, 1500, 55.0, 12.0, 4000, 3000);
        var wcs2 = new WcsMatrix(new CelestialCoordinates(225.0, 28.0), 2000, 1500, 52.0, -8.0, 4000, 3000);
        // Panel 3: Narrow-field photo (1.6 arcsec/px, e.g. catalogue deep-sky image)
        var wcs3 = new WcsMatrix(new CelestialCoordinates(220.0, 26.5), 1000, 750, 1.6, 0.0, 2000, 1500);

        project.AddPanel(new MosaicPanel("p1", "Wide 1", 4000, 3000, wcs1, "wide1.jpg", "Bootes 1"));
        project.AddPanel(new MosaicPanel("p2", "Wide 2", 4000, 3000, wcs2, "wide2.jpg", "Bootes 2"));
        project.AddPanel(new MosaicPanel("p3", "DeepSky 3", 2000, 1500, wcs3, "m31.jpg", "M31"));

        var masterWcs = project.ComputeMasterWcs();
        Assert.NotNull(masterWcs);

        for (int i = 0; i < project.Panels.Count; i++)
        {
            var p = project.Panels[i];
            var tr = MosaicProject.GetPanelCanvasTransform(p, masterWcs, i);
            double scaleU = Math.Sqrt((tr.M11 * tr.M11) + (tr.M12 * tr.M12));
            double renderedWidth = p.Width * scaleU;

            // None of the panels may be blown up to 10x-40x its size or swallow the canvas
            Assert.InRange(scaleU, 0.15, 2.5);
            Assert.InRange(renderedWidth, 300.0, masterWcs!.ImageWidth * 0.85);
        }
    }
}
