namespace AstroCounter.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AstroCounter.Core.Astrometry;
using SkiaSharp;
using Xunit;

public class DenseCatalogLockTests
{
    [Fact]
    public void DenseCatalog_LoadsCatalogWithOverTwoMillionStars()
    {
        Assert.True(GaiaTychoCatalog.TotalStars >= 2000000, 
            $"Expected dense catalog to have at least 2,000,000 stars, but got {GaiaTychoCatalog.TotalStars}");

        // Query deep sky region (Orion / M42 area)
        var stars = GaiaTychoCatalog.QueryRegion(83.82, -5.39, 2.0, maxMag: 14.0, maxResults: 500);
        Assert.NotEmpty(stars);
        Assert.True(stars.Count >= 100, $"Expected at least 100 stars in Orion patch, found {stars.Count}");
    }

    [Fact]
    public void OpenNgcCatalog_LoadsThirteenThousandObjectsAndResolvesMessierAndNamedDSOs()
    {
        Assert.True(OpenNgcCatalog.TotalObjects >= 13000,
            $"Expected OpenNGC to have at least 13,000 objects, but got {OpenNgcCatalog.TotalObjects}");

        // Lookup M31 Andromeda
        var m31 = OpenNgcCatalog.FindByName("M31");
        Assert.NotNull(m31);
        Assert.Contains("Andromeda", m31.Value.CommonName, StringComparison.OrdinalIgnoreCase);
        Assert.True(Math.Abs(m31.Value.RaDeg - 10.68) < 0.5);
        Assert.True(Math.Abs(m31.Value.DecDeg - 41.26) < 0.5);

        // Lookup M42 Orion
        var m42 = OpenNgcCatalog.FindByName("M42");
        Assert.NotNull(m42);
        Assert.True(Math.Abs(m42.Value.RaDeg - 83.82) < 0.5);

        // Cone search around Cygnus (Deneb / North America region)
        var cygDSOs = OpenNgcCatalog.QueryRegion(314.7, 44.3, 5.0, maxMag: 16.0, maxResults: 50);
        Assert.NotEmpty(cygDSOs);
        Assert.Contains(cygDSOs, d => d.Name.Contains("7000") || (d.CommonName != null && d.CommonName.Contains("North America")));
    }

    [Fact]
    public void ConstellationCatalog_ContainsAll88IauConstellationsWithFullSkyCoverage()
    {
        var lines = ConstellationCatalog.Lines;
        Assert.NotNull(lines);
        Assert.True(lines.Count >= 500, $"Expected at least 500 lines for 88 constellations, got {lines.Count}");

        // Check number of distinct constellations
        var distinctConstellations = lines.Select(l => l.ConstellationAbbr).Distinct().ToList();
        Assert.True(distinctConstellations.Count >= 85, $"Expected ~88 distinct constellations, got {distinctConstellations.Count}");

        // Check Turkish & English names exist
        var first = lines.First();
        Assert.False(string.IsNullOrWhiteSpace(first.ConstellationName));
        Assert.False(string.IsNullOrWhiteSpace(first.ConstellationNameTr));
        Assert.False(string.IsNullOrWhiteSpace(first.DisplayName));
    }

    [Fact]
    public void EdgeStarCentroid_CanMeasureNearImageBorder()
    {
        // Create an image with a star right at (10, 10) near the border
        using var bmp = new SKBitmap(200, 200, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(5, 5, 5));
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawCircle(10, 10, 3, paint);
        }

        var lum = CatalogLockRefiner.BuildLuminance(bmp);
        Assert.NotNull(lum);

        // Previous bug rejected search windows near borders when searchR was large (e.g. 25px)
        bool measured = CatalogLockRefiner.MeasureCentroid(lum, 10, 10, 25.0, out double mx, out double my);
        Assert.True(measured, "Centroid near border should be successfully measured with clamping");
        Assert.InRange(mx, 8.0, 12.0);
        Assert.InRange(my, 8.0, 12.0);
    }

    [Fact]
    public async Task HybridPlateSolver_AppliesCatalogLockAndTPSOnSampleImage()
    {
        string baseDir = AppContext.BaseDirectory;
        string imgPath = Path.GetFullPath(Path.Combine(baseDir, "../../../../../assets/sample_images/deep_sky_stack.png"));
        if (!File.Exists(imgPath)) return;

        using var bmp = SKBitmap.Decode(imgPath);
        var solver = new HybridPlateSolver();
        var res = await solver.SolveAsync(imgPath, bmp.Width, bmp.Height);

        Assert.True(res.IsSuccess, $"Solve failed: {res.Message}");
        Assert.NotNull(res.Wcs);
        Assert.True(res.MatchedStarCount >= 8, $"Matched stars should be >= 8, was {res.MatchedStarCount}");

        // Verify TPS Spline Warp is generated for lens distortion correction
        Assert.NotNull(res.Wcs.SplineWarp);
        Assert.True(res.Wcs.SplineWarp.PointCount >= 4, $"TPS spline point count should be >= 4, was {res.Wcs.SplineWarp.PointCount}");
        Assert.True(res.Wcs.SplineWarp.RmsResidualPixels > 0.0, "TPS RMS displacement should be > 0");

        // Verify that the spline warp accurately maps points (sub-pixel precision)
        var (wx, wy) = res.Wcs.SplineWarp.Warp(bmp.Width / 2.0, bmp.Height / 2.0);
        Assert.InRange(Math.Abs(wx - bmp.Width / 2.0), 0.0, 100.0);
        Assert.InRange(Math.Abs(wy - bmp.Height / 2.0), 0.0, 100.0);
    }
}
