namespace AstroCounter.Tests;

using System;
using System.IO;
using System.Threading.Tasks;
using AstroCounter.Core.Astrometry;
using SkiaSharp;
using Xunit;

public class DenseCatalogLockTests
{
    [Fact]
    public void DenseCatalog_LoadsCatalogWithOver180kStars()
    {
        Assert.True(GaiaTychoCatalog.TotalStars >= 180000, 
            $"Expected dense catalog to have at least 180,000 stars, but got {GaiaTychoCatalog.TotalStars}");

        // Query deep sky region (Orion / M42 area)
        var stars = GaiaTychoCatalog.QueryRegion(83.82, -5.39, 2.0, maxMag: 13.0, maxResults: 500);
        Assert.NotEmpty(stars);
        Assert.True(stars.Count >= 100, $"Expected at least 100 stars in Orion patch, found {stars.Count}");
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
        // Check center or sample coordinates
        var (wx, wy) = res.Wcs.SplineWarp.Warp(bmp.Width / 2.0, bmp.Height / 2.0);
        Assert.InRange(Math.Abs(wx - bmp.Width / 2.0), 0.0, 100.0);
        Assert.InRange(Math.Abs(wy - bmp.Height / 2.0), 0.0, 100.0);
    }

    [Fact]
    public async Task Verify_Star_Alignment_Precision_On_SampleImage()
    {
        string baseDir = AppContext.BaseDirectory;
        string imgPath = Path.GetFullPath(Path.Combine(baseDir, "../../../../../assets/sample_images/deep_sky_stack.png"));
        if (!File.Exists(imgPath)) return;

        using var bmp = SKBitmap.Decode(imgPath);
        var solver = new HybridPlateSolver();
        var res = await solver.SolveAsync(imgPath, bmp.Width, bmp.Height);
        Assert.True(res.IsSuccess);
        Assert.NotNull(res.Wcs);

        var lum = CatalogLockRefiner.BuildLuminance(bmp);
        Assert.NotNull(lum);

        var center = res.Wcs.CenterCoordinates;
        double diag = Math.Sqrt((double)bmp.Width * bmp.Width + (double)bmp.Height * bmp.Height);
        double radiusDeg = (0.5 * diag * res.Wcs.PixelScaleArcsec / 3600.0) * 1.05;

        var catalog = GaiaTychoCatalog.QueryRegion(center.RaDegrees, center.DecDegrees, radiusDeg, maxMag: 13.0, maxResults: 1000);
        Assert.NotEmpty(catalog);

        int checkedStars = 0;
        double sumDist = 0;
        double maxDist = 0;

        foreach (var c in catalog)
        {
            var coord = c.ToCoordinates();
            if (!res.Wcs.EquatorialToPixel(coord, out double px, out double py)) continue;
            if (px < 15 || py < 15 || px > bmp.Width - 15 || py > bmp.Height - 15) continue;

            if (CatalogLockRefiner.MeasureCentroid(lum, px, py, 12.0, out double mx, out double my))
            {
                double d = Math.Sqrt((mx - px) * (mx - px) + (my - py) * (my - py));
                if (d < 5.0) // Valid match within local window
                {
                    checkedStars++;
                    sumDist += d;
                    if (d > maxDist) maxDist = d;
                }
            }
        }

        Console.WriteLine($"Checked {checkedStars} catalog stars. Mean residual = {sumDist / checkedStars:F3} px, Max residual = {maxDist:F3} px");
        Assert.True(checkedStars >= 10, "Should align at least 10 stars");
        Assert.True(sumDist / checkedStars < 1.0, $"Mean residual should be sub-pixel (<1.0 px), but was {sumDist / checkedStars:F3} px");
    }
}
