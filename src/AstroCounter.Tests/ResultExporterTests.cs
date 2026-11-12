namespace AstroCounter.Tests;

using AstroCounter.Core.Astrometry;
using AstroCounter.Core.Export;
using AstroCounter.Core.Models;
using SkiaSharp;
using Xunit;

public class ResultExporterTests
{
    [Fact]
    public void ExportToCsv_WritesValidHeaderAndData()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"astro_test_{Guid.NewGuid()}.csv");
        try
        {
            var objects = new[]
            {
                new AstrophotographyObject(1, new BoundingBox(12.5f, 34.0f, 6.0f, 6.0f), 0.92f, "star")
            };

            ResultExporter.ExportToCsv(objects, tempFile);

            Assert.True(File.Exists(tempFile));
            string[] lines = File.ReadAllLines(tempFile);
            Assert.True(lines.Length >= 2);
            Assert.Contains("Id,ClassName,Confidence,Tier,SNR_Sigma,X,Y,Width,Height,CenterX,CenterY", lines[0]);
            Assert.Contains("star", lines[1]);
            Assert.Contains("Tier 1 (High)", lines[1]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(-0.18, 0.08)]
    [InlineData(0.14, -0.22)]
    [InlineData(-0.25, -0.10)]
    public void WcsMatrix_EquatorialToPixel_FarOffAxisWithRadialDistortion_NeverFoldsBackIntoSensor(double k1, double k2)
    {
        var center = new CelestialCoordinates(285.0, 32.0);
        var wcs = new WcsMatrix(
            center,
            referencePixelX: 600,
            referencePixelY: 450,
            pixelScaleArcsec: 45.0, // ~15 deg wide-field photo
            rotationDegrees: 22.0,
            imageWidth: 1200,
            imageHeight: 900,
            radialDistortionK1: k1,
            radialDistortionK2: k2);

        // Verify far off-axis sky points (25..88 degrees away) never fold back into the [0..1200, 0..900] box
        for (double offDeg = 25.0; offDeg <= 88.0; offDeg += 3.0)
        {
            for (double azDeg = 0.0; azDeg < 360.0; azDeg += 30.0)
            {
                double ra = (285.0 + offDeg * Math.Cos(azDeg * Math.PI / 180.0)) % 360.0;
                if (ra < 0) ra += 360.0;
                double dec = Math.Clamp(32.0 + (offDeg * 0.55 * Math.Sin(azDeg * Math.PI / 180.0)), -89.0, 89.0);

                if (center.AngularDistanceTo(new CelestialCoordinates(ra, dec)) < 22.0)
                {
                    continue;
                }

                if (wcs.EquatorialToPixel(ra, dec, out double px, out double py))
                {
                    bool insideSensor = px >= -2.0 && px <= 1202.0 && py >= -2.0 && py <= 902.0;
                    Assert.False(insideSensor,
                        $"Far sky point ({ra:F1}, {dec:F1}) folded back onto sensor ({px:F1}, {py:F1}) for k1={k1}, k2={k2}!");
                }
            }
        }
    }

    [Fact]
    public void ExportEquirectangularPanorama_DistortedMultiExposurePanels_ProducesCleanSkyWithoutRings()
    {
        string panel1Path = Path.Combine(Path.GetTempPath(), $"astro_p1_{Guid.NewGuid()}.png");
        string panel2Path = Path.Combine(Path.GetTempPath(), $"astro_p2_{Guid.NewGuid()}.png");
        string outPath = Path.Combine(Path.GetTempPath(), $"astro_pano_{Guid.NewGuid()}.png");

        try
        {
            CreateSyntheticSkyImage(panel1Path, 400, 300, bgR: 12, bgG: 16, bgB: 28, starIntensity: 255);
            // Bright twilight/light-pollution second exposure overlapping panel 1
            CreateSyntheticSkyImage(panel2Path, 400, 300, bgR: 165, bgG: 165, bgB: 195, starIntensity: 255);

            var wcs1 = new WcsMatrix(new CelestialCoordinates(358.0, 20.0), 200, 150, 120.0, 8.0, 400, 300, radialDistortionK1: -0.15, radialDistortionK2: 0.06);
            var wcs2 = new WcsMatrix(new CelestialCoordinates(3.0, 22.0), 200, 150, 120.0, -5.0, 400, 300, radialDistortionK1: 0.10, radialDistortionK2: -0.18);

            var project = new MosaicProject();
            project.AddPanel(new MosaicPanel("p1", "Panel 1", 400, 300, wcs1, panel1Path, "Cygnus"));
            project.AddPanel(new MosaicPanel("p2", "Panel 2", 400, 300, wcs2, panel2Path, "Cygnus"));

            var objects = new[]
            {
                new AstrophotographyObject(1, new BoundingBox(195, 145, 10, 10), 0.95f, "star", RaDegrees: 358.0, DecDegrees: 20.0, PanelId: "p1"),
                new AstrophotographyObject(2, new BoundingBox(195, 145, 10, 10), 0.90f, "star", RaDegrees: 3.0, DecDegrees: 22.0, PanelId: "p2")
            };

            var progressValues = new List<int>();
            var progress = new SyncProgress<ExportProgress>(p => progressValues.Add(p.Percent));

            var cfg = new ExportConfig(
                Format: "PNG",
                IncludeBoundingBoxes: true,
                IncludeLabels: false,
                IncludeSphericalGrid: false,
                IncludeConstellations: false,
                ExportProjectionMode: "Panorama360",
                ResolutionMode: "Custom",
                CustomWidth: 1000,
                CustomHeight: 500,
                IncludeWatermark: true,
                TotalDetectedCount: 2,
                HarmonizeMosaicExposure: true);

            ResultExporter.ExportAdvancedImage(panel1Path, objects, outPath, cfg, wcs1, null, project, progress, CancellationToken.None);

            Assert.True(File.Exists(outPath));
            Assert.Contains(100, progressValues);

            using var outBmp = SKBitmap.Decode(outPath);
            Assert.Equal(1000, outBmp.Width);
            Assert.Equal(500, outBmp.Height);

            // Check distant southern hemisphere sky (y in [340..410], safely above watermark rect) is clean deep sky
            // Pinpoint Hipparcos stars have diameter ~2-4px; folded ring artifacts create continuous multi-pixel arcs/smears >= 10px wide.
            int strayArcStreaks = 0;
            for (int y = 340; y <= 410; y += 4)
            {
                int streak = 0;
                for (int x = 60; x <= 940; x++)
                {
                    var px = outBmp.GetPixel(x, y);
                    if (px.Red > 50 || px.Green > 50)
                    {
                        streak++;
                        if (streak >= 10)
                        {
                            strayArcStreaks++;
                            break;
                        }
                    }
                    else
                    {
                        streak = 0;
                    }
                }
            }
            Assert.Equal(0, strayArcStreaks);

            // Verify bright twilight panel 2 sky background (away from detection box at ~493,189) was harmonized down smoothly
            var panel2Sky = outBmp.GetPixel(475, 180);
            Assert.True(panel2Sky.Red < 70 && panel2Sky.Green < 70 && panel2Sky.Blue < 85,
                $"Expected sky-harmonized dark tone at panel 2 sky, got RGB=({panel2Sky.Red},{panel2Sky.Green},{panel2Sky.Blue})");

            // Verify Observatory watermark pill was drawn in an empty corner (bright cyan/white badge border pixels exist near corner)
            int watermarkPixels = 0;
            for (int y = 430; y < 495; y++)
            {
                for (int x = 680; x < 995; x++)
                {
                    var wp = outBmp.GetPixel(x, y);
                    if (wp.Blue > 150 && wp.Green > 140) watermarkPixels++;
                }
            }
            Assert.True(watermarkPixels > 30, $"Expected Observatory watermark badge in empty corner, found {watermarkPixels} bright badge pixels.");
        }
        finally
        {
            if (File.Exists(panel1Path)) File.Delete(panel1Path);
            if (File.Exists(panel2Path)) File.Delete(panel2Path);
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    [Fact]
    public void ComputeMosaicExportBounds_And_NativeResolution_CoversFullStitchedExtentWithoutCropping()
    {
        string p1Path = Path.Combine(Path.GetTempPath(), $"astro_m1_{Guid.NewGuid()}.png");
        string p2Path = Path.Combine(Path.GetTempPath(), $"astro_m2_{Guid.NewGuid()}.png");
        string outNativePath = Path.Combine(Path.GetTempPath(), $"astro_native_{Guid.NewGuid()}.png");
        string outCustomPath = Path.Combine(Path.GetTempPath(), $"astro_custom_{Guid.NewGuid()}.jpg");

        try
        {
            CreateSyntheticSkyImage(p1Path, 500, 400, bgR: 15, bgG: 20, bgB: 35, starIntensity: 240);
            CreateSyntheticSkyImage(p2Path, 500, 400, bgR: 25, bgG: 20, bgB: 45, starIntensity: 240);

            // Shift panel 2 diagonally outside panel 1 bounds (both East/North in sky)
            var wcs1 = new WcsMatrix(new CelestialCoordinates(180.0, 20.0), 250, 200, 30.0, 0.0, 500, 400);
            var wcs2 = new WcsMatrix(new CelestialCoordinates(183.5, 22.5), 250, 200, 30.0, 12.0, 500, 400);

            var project = new MosaicProject();
            project.AddPanel(new MosaicPanel("m1", "M1", 500, 400, wcs1, p1Path, "Target"));
            project.AddPanel(new MosaicPanel("m2", "M2", 500, 400, wcs2, p2Path, "Target"));

            var bounds = ResultExporter.ComputeMosaicExportBounds(project, wcs1);
            Assert.True(bounds.PixelWidth > 650, $"Expected full mosaic width > 650px, got {bounds.PixelWidth}");
            Assert.True(bounds.PixelHeight > 520, $"Expected full mosaic height > 520px, got {bounds.PixelHeight}");
            Assert.Equal((long)bounds.PixelWidth * bounds.PixelHeight, bounds.TotalPixels);

            var cfgNative = new ExportConfig(
                Format: "PNG",
                ResolutionMode: "Native",
                IncludeWatermark: true,
                TotalDetectedCount: 12);

            ResultExporter.ExportAdvancedImage(p1Path, Array.Empty<AstrophotographyObject>(), outNativePath, cfgNative, wcs1, null, project);

            using (var nativeBmp = SKBitmap.Decode(outNativePath))
            {
                Assert.Equal(bounds.PixelWidth, nativeBmp.Width);
                Assert.Equal(bounds.PixelHeight, nativeBmp.Height);
            }

            var cfgCustom = new ExportConfig(
                Format: "JPG",
                ResolutionMode: "Custom",
                CustomWidth: 1280,
                CustomHeight: 800,
                JpegQuality: 90,
                IncludeWatermark: true,
                TotalDetectedCount: 12);

            ResultExporter.ExportAdvancedImage(p1Path, Array.Empty<AstrophotographyObject>(), outCustomPath, cfgCustom, wcs1, null, project);

            using (var customBmp = SKBitmap.Decode(outCustomPath))
            {
                Assert.Equal(1280, customBmp.Width);
                Assert.Equal(800, customBmp.Height);
            }
        }
        finally
        {
            if (File.Exists(p1Path)) File.Delete(p1Path);
            if (File.Exists(p2Path)) File.Delete(p2Path);
            if (File.Exists(outNativePath)) File.Delete(outNativePath);
            if (File.Exists(outCustomPath)) File.Delete(outCustomPath);
        }
    }

    [Fact]
    public void ChooseSmartWatermarkCorner_AlwaysSelectsEmptyStarFreeCorner()
    {
        using var bmp = new SKBitmap(1200, 800, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(new SKColor(5, 8, 15));

            // Populate bright patch + stars inside BottomRight, BottomLeft, and TopRight corners
            using var starPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
            c.DrawRect(850, 680, 350, 120, new SKPaint { Color = new SKColor(60, 70, 95) });
            c.DrawCircle(1050, 740, 22, starPaint);
            c.DrawCircle(120, 740, 25, starPaint);
            c.DrawCircle(1080, 60, 24, starPaint);
        }

        // Also register detected stars in those 3 corners while leaving TopLeft empty & dark
        var markers = new List<(float X, float Y)>
        {
            (1050f, 740f),
            (120f, 740f),
            (1080f, 60f)
        };

        var corner = ResultExporter.ChooseSmartWatermarkCorner(bmp, markers, badgeWidth: 260, badgeHeight: 54, margin: 20);
        Assert.Equal(WatermarkCorner.TopLeft, corner);

        // Inverse case: crowd TopLeft, TopRight, BottomLeft and keep BottomRight empty
        using var bmp2 = new SKBitmap(1200, 800, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c2 = new SKCanvas(bmp2))
        {
            c2.Clear(new SKColor(5, 8, 15));
        }
        var markers2 = new List<(float X, float Y)> { (80f, 50f), (1100f, 50f), (80f, 740f) };
        var corner2 = ResultExporter.ChooseSmartWatermarkCorner(bmp2, markers2, badgeWidth: 260, badgeHeight: 54, margin: 20);
        Assert.Equal(WatermarkCorner.BottomRight, corner2);
    }

    private static void CreateSyntheticSkyImage(string path, int w, int h, byte bgR, byte bgG, byte bgB, byte starIntensity)
    {
        using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        using var gradPaint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(w, h),
                new[]
                {
                    new SKColor(bgR, bgG, bgB),
                    new SKColor((byte)Math.Min(255, bgR + 12), (byte)Math.Min(255, bgG + 12), (byte)Math.Min(255, bgB + 14))
                },
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(0, 0, w, h, gradPaint);

        using var starPaint = new SKPaint { Color = new SKColor(starIntensity, starIntensity, starIntensity), IsAntialias = true };
        var rng = new Random(w * 31 + bgR);
        for (int i = 0; i < 65; i++)
        {
            float sx = rng.Next(12, w - 12);
            float sy = rng.Next(12, h - 12);
            float r = (i % 5 == 0) ? 2.6f : 1.4f;
            canvas.DrawCircle(sx, sy, r, starPaint);
        }

        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Open(path, FileMode.Create, FileAccess.Write);
        data.SaveTo(fs);
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
