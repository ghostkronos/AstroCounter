namespace AstroCounter.Tests;

using AstroCounter.Core.Astrometry;
using Xunit;

public class AstrometryTests
{
    [Fact]
    public void CelestialCoordinates_NormalizesRaAndDecCorrectly()
    {
        // 370 degrees RA should normalize to 10 degrees
        var c1 = new CelestialCoordinates(370.0, 45.0);
        Assert.Equal(10.0, c1.RaDegrees, precision: 4);
        Assert.Equal(45.0, c1.DecDegrees, precision: 4);

        // Negative RA (-30 deg) should normalize to 330 deg
        var c2 = new CelestialCoordinates(-30.0, -100.0);
        Assert.Equal(330.0, c2.RaDegrees, precision: 4);
        // Dec clamped to -90
        Assert.Equal(-90.0, c2.DecDegrees, precision: 4);
    }

    [Fact]
    public void CelestialCoordinates_FormatStringsCorrectly()
    {
        // M31 coordinates: RA ~ 10.6847 deg -> 00h 42m 44.3s, Dec ~ 41.2687 deg -> +41° 16′ 07″
        var m31 = new CelestialCoordinates(10.6847, 41.2687);
        string raStr = m31.FormatRa();
        string decStr = m31.FormatDec();

        Assert.StartsWith("00h 42m", raStr);
        Assert.StartsWith("+41° 16′", decStr);
    }

    [Fact]
    public void WcsMatrix_RoundTripProjection_PreservesCoordinates()
    {
        var center = new CelestialCoordinates(10.6847, 41.2687); // M31
        int width = 3000;
        int height = 2000;
        double pixelScale = 1.5; // arcsec/pixel

        var wcs = new WcsMatrix(
            centerCoordinates: center,
            referencePixelX: width / 2.0,
            referencePixelY: height / 2.0,
            pixelScaleArcsec: pixelScale,
            rotationDegrees: 15.0,
            imageWidth: width,
            imageHeight: height);

        // Center pixel should project exactly back to center RA/Dec
        var centerEquatorial = wcs.PixelToEquatorial(width / 2.0, height / 2.0);
        Assert.Equal(center.RaDegrees, centerEquatorial.RaDegrees, precision: 4);
        Assert.Equal(center.DecDegrees, centerEquatorial.DecDegrees, precision: 4);

        // Arbitrary pixel round-trip
        double px = 750.0;
        double py = 1200.0;
        var coord = wcs.PixelToEquatorial(px, py);

        bool projected = wcs.EquatorialToPixel(coord, out double backX, out double backY);
        Assert.True(projected);
        Assert.Equal(px, backX, precision: 1);
        Assert.Equal(py, backY, precision: 1);
    }

    [Fact]
    public async Task CataloguePlateSolver_SolvesAndromedaImageSuccessfully()
    {
        var solver = new CataloguePlateSolver();
        var result = await solver.SolveAsync("m31_andromeda_stacked.tif", 4000, 3000);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Wcs);
        Assert.Contains("Andromeda", result.TargetName);
        Assert.Equal(10.6847, result.Wcs!.CenterCoordinates.RaDegrees, precision: 2);
        Assert.Equal(41.2687, result.Wcs.CenterCoordinates.DecDegrees, precision: 2);
    }

    [Fact]
    public async Task HybridPlateSolver_SolvesKnownCatalogues()
    {
        var hybrid = new HybridPlateSolver();
        var result = await hybrid.SolveAsync("m42_orion_nebula.png", 1920, 1080);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Wcs);
        Assert.Contains("Orion", result.TargetName);
        Assert.Equal(83.8221, result.Wcs!.CenterCoordinates.RaDegrees, precision: 2);
    }

    [Fact]
    public void StellariumStatus_ContractsWorkCorrectly()
    {
        var disconnected = AstroCounter.Core.Stellarium.StellariumStatus.Disconnected("http://localhost:8090");
        Assert.False(disconnected.IsConnected);

        var coord = new CelestialCoordinates(180.0, 30.0);
        var connected = AstroCounter.Core.Stellarium.StellariumStatus.Connected(coord, 45.0, "Jupiter", "http://localhost:8090");
        Assert.True(connected.IsConnected);
        Assert.Equal("Jupiter", connected.SelectedObjectName);
        Assert.Equal(180.0, connected.CurrentCoordinates.RaDegrees, precision: 4);
    }

    [Fact]
    public void StellariumLauncher_FindsExecutableOrReturnsNullGracefully()
    {
        string? exe = AstroCounter.Core.Stellarium.StellariumLauncher.FindStellariumExecutable();
        // Should not throw, returns valid path or null
        if (exe != null)
        {
            Assert.True(System.IO.File.Exists(exe));
            Assert.Contains("stellarium", exe, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task StarPatternPlateSolver_SolvesStarFieldByTriangleGeometry()
    {
        int width = 2400;
        int height = 1800;
        var trueCenter = new CelestialCoordinates(82.0, 4.0); // Orion - Taurus
        var trueWcs = new WcsMatrix(
            centerCoordinates: trueCenter,
            referencePixelX: width / 2.0,
            referencePixelY: height / 2.0,
            pixelScaleArcsec: 55.0,
            rotationDegrees: 18.0,
            imageWidth: width,
            imageHeight: height);

        // Project J2000 stars from the Orion-Taurus region into synthetic detected star centroids
        (double Ra, double Dec, float Flux)[] catalogStars =
        [
            (78.634, -8.202, 990f), // Rigel
            (88.793, 7.407, 970f),  // Betelgeuse
            (68.980, 16.509, 950f), // Aldebaran
            (81.283, 6.350, 920f),  // Bellatrix
            (81.573, 28.608, 910f), // Elnath
            (84.053, -1.202, 900f), // Alnilam
            (85.190, -1.943, 890f), // Alnitak
            (99.428, 16.399, 880f), // Alhena
            (86.939, -9.669, 870f), // Saiph
            (83.002, -0.299, 860f), // Mintaka
            (84.411, 21.143, 840f), // Zeta Tau
            (83.822, -5.391, 830f), // M42
            (67.166, 15.871, 800f), // Theta2 Tau
            (60.170, 12.490, 790f), // Lambda Tau
            (67.154, 19.180, 780f), // Epsilon Tau
            (64.948, 15.628, 770f), // Gamma Tau
        ];

        var syntheticObjects = new System.Collections.Generic.List<AstroCounter.Core.Models.AstrophotographyObject>();
        int id = 1;
        foreach (var s in catalogStars)
        {
            if (trueWcs.EquatorialToPixel(new CelestialCoordinates(s.Ra, s.Dec), out double px, out double py))
            {
                syntheticObjects.Add(new AstroCounter.Core.Models.AstrophotographyObject(
                    id++,
                    new AstroCounter.Core.Models.BoundingBox((float)px - 4f, (float)py - 4f, 8f, 8f),
                    s.Flux / 1000f,
                    "star",
                    s.Flux));
            }
        }

        var solver = new StarPatternPlateSolver();
        var res = await solver.SolveAsync("", width, height, syntheticObjects);

        Assert.True(res.IsSuccess);
        Assert.NotNull(res.Wcs);
        Assert.True(res.MatchedStarCount >= 10);
        Assert.InRange(res.Wcs!.CenterCoordinates.RaDegrees, 80.5, 83.5);
        Assert.InRange(res.Wcs.CenterCoordinates.DecDegrees, 2.5, 5.5);
        Assert.InRange(res.Wcs.PixelScaleArcsec, 52.0, 58.0);
    }

    [Fact]
    public void AutoBalanceBrightness_NormalizesTooDarkAndTooBrightImages()
    {
        int w = 256, h = 256;

        // 1. TooDark synthetic image (pitch-black background = 0, faint stars = 8..18, plus a couple hot pixels at 230)
        using var darkBmp = new SkiaSharp.SKBitmap(w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        darkBmp.Erase(new SkiaSharp.SKColor(0, 0, 0, 255));
        for (int i = 10; i < w - 10; i += 20)
        {
            for (int j = 10; j < h - 10; j += 20)
            {
                darkBmp.SetPixel(i, j, new SkiaSharp.SKColor(14, 14, 14, 255));
            }
        }
        // Add a few saturated hot pixels that previously tricked p9997 into skipping TooDark
        darkBmp.SetPixel(50, 50, new SkiaSharp.SKColor(240, 240, 240, 255));
        darkBmp.SetPixel(120, 120, new SkiaSharp.SKColor(250, 250, 250, 255));

        using var balancedDark = AstroCounter.Core.Processing.ImagePreprocessor.AutoBalanceBrightness(darkBmp, out var darkInfo);
        Assert.Equal(AstroCounter.Core.Processing.ImagePreprocessor.ExposureState.TooDark, darkInfo.State);
        Assert.True(darkInfo.WasAdjusted);
        Assert.True(balancedDark.GetPixel(30, 30).Green > 110);

        // 2. TooBright synthetic image (washed-out sky glow = 145, stars = 235)
        using var brightBmp = new SkiaSharp.SKBitmap(w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        brightBmp.Erase(new SkiaSharp.SKColor(150, 140, 130, 255));
        for (int i = 10; i < w - 10; i += 20)
        {
            for (int j = 10; j < h - 10; j += 20)
            {
                brightBmp.SetPixel(i, j, new SkiaSharp.SKColor(245, 245, 245, 255));
            }
        }
        using var balancedBright = AstroCounter.Core.Processing.ImagePreprocessor.AutoBalanceBrightness(brightBmp, out var brightInfo);
        Assert.Equal(AstroCounter.Core.Processing.ImagePreprocessor.ExposureState.TooBright, brightInfo.State);
        Assert.True(brightInfo.WasAdjusted);
        Assert.True(balancedBright.GetPixel(15, 15).Green < 25); // Sky glow subtracted to dark floor

        // 3. Optimal synthetic image (sky background = 18, bright stars = 240)
        using var optBmp = new SkiaSharp.SKBitmap(w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        optBmp.Erase(new SkiaSharp.SKColor(18, 18, 20, 255));
        for (int i = 8; i < w - 8; i += 12)
        {
            for (int j = 8; j < h - 8; j += 12)
            {
                optBmp.SetPixel(i, j, new SkiaSharp.SKColor(245, 245, 245, 255));
                optBmp.SetPixel(i + 1, j, new SkiaSharp.SKColor(210, 210, 210, 255));
            }
        }
        using var balancedOpt = AstroCounter.Core.Processing.ImagePreprocessor.AutoBalanceBrightness(optBmp, out var optInfo);
        Assert.Equal(AstroCounter.Core.Processing.ImagePreprocessor.ExposureState.Optimal, optInfo.State);
        Assert.False(optInfo.WasAdjusted);
    }

    [Fact]
    public async Task StarPatternPlateSolver_RejectsRandomUnmatchedStarField_NeverFalseMatchesAndromeda()
    {
        var rng = new System.Random(42);
        int width = 2000;
        int height = 1500;
        var randomStars = new System.Collections.Generic.List<AstroCounter.Core.Models.AstrophotographyObject>();

        for (int i = 0; i < 30; i++)
        {
            float px = (float)(100 + rng.NextDouble() * (width - 200));
            float py = (float)(100 + rng.NextDouble() * (height - 200));
            float flux = 900f - (i * 20f);
            randomStars.Add(new AstroCounter.Core.Models.AstrophotographyObject(
                i + 1,
                new AstroCounter.Core.Models.BoundingBox(px - 3f, py - 3f, 6f, 6f),
                0.85f,
                "star",
                flux));
        }

        var solver = new StarPatternPlateSolver();
        var res = await solver.SolveAsync("", width, height, randomStars);

        Assert.False(res.IsSuccess);
        Assert.Null(res.Wcs);
    }
}
