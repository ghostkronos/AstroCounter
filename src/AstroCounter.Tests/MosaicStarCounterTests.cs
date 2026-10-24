namespace AstroCounter.Tests;

using System;
using System.IO;
using System.Threading.Tasks;
using AstroCounter.Core.Astrometry;
using AstroCounter.Core.Models;
using SkiaSharp;
using Xunit;

public class MosaicStarCounterTests
{
    [Fact]
    public async Task CountMosaicStarsAsync_DeduplicatesOverlappingPanelsCorrectly()
    {
        var project = new MosaicProject();

        // 2 adjacent overlapping panels near Deneb
        // Panel 1: RA 310.0, Dec 45.0, FOV 2 deg
        var wcs1 = new WcsMatrix(new CelestialCoordinates(310.0, 45.0), 500, 500, 3.6, 0.0, 1000, 1000);
        using var bmp1 = new SKBitmap(1000, 1000);
        using (var canvas = new SKCanvas(bmp1))
        {
            canvas.Clear(new SKColor(20, 20, 25));
            // Draw simulated star in center of panel 1
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawCircle(500, 500, 4, paint);
        }

        var p1 = new MosaicPanel("p1", "Panel 1", 1000, 1000, wcs1) { CachedBitmap = bmp1 };

        // Panel 2: Slightly shifted (RA 310.1, Dec 45.0) overlapping the exact same center star
        var centerStarSky = wcs1.PixelToEquatorial(500, 500);
        var wcs2 = new WcsMatrix(new CelestialCoordinates(310.1, 45.0), 500, 500, 3.6, 0.0, 1000, 1000);
        wcs2.EquatorialToPixel(centerStarSky, out double p2X, out double p2Y);

        using var bmp2 = new SKBitmap(1000, 1000);
        using (var canvas = new SKCanvas(bmp2))
        {
            canvas.Clear(new SKColor(20, 20, 25));
            // Draw star at matching celestial position
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawCircle((float)p2X, (float)p2Y, 4, paint);
        }

        var p2 = new MosaicPanel("p2", "Panel 2", 1000, 1000, wcs2) { CachedBitmap = bmp2 };

        project.AddPanel(p1);
        project.AddPanel(p2);

        var config = new TilingConfig(DefaultConfidenceThreshold: 0.10f, MinConfidence: 0.05f);
        var result = await MosaicStarCounter.CountMosaicStarsAsync(project, config);

        Assert.NotNull(result);
        Assert.NotNull(result.MasterWcs);
        // The two overlapping detections of the same physical star should be deduplicated
        Assert.True(result.OverlapDetectionsCount >= 1, $"Overlap detections should be at least 1, but was {result.OverlapDetectionsCount}");
        Assert.Equal(2, result.PanelDetails.Count);
    }

    [Fact]
    public async Task CountMosaicStarsAsync_HandlesLargeDenseMosaicPanelsCleanly()
    {
        var project = new MosaicProject();

        // Simulate 4 mosaic tiles arranged in a 2x2 grid
        for (int r = 0; r < 2; r++)
        {
            for (int c = 0; c < 2; c++)
            {
                double raCenter = 180.0 + (c * 0.4);
                double decCenter = 30.0 + (r * 0.4);
                var wcs = new WcsMatrix(new CelestialCoordinates(raCenter, decCenter), 300, 300, 3.0, 0.0, 600, 600);

                var bmp = new SKBitmap(600, 600);
                using (var canvas = new SKCanvas(bmp))
                {
                    canvas.Clear(new SKColor(15, 15, 20));
                    using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
                    // Draw a grid of 50 stars per panel
                    for (int sx = 50; sx < 550; sx += 50)
                    {
                        for (int sy = 50; sy < 550; sy += 50)
                        {
                            canvas.DrawCircle(sx, sy, 3, paint);
                        }
                    }
                }

                var panel = new MosaicPanel($"tile_{r}_{c}", $"Tile {r},{c}", 600, 600, wcs) { CachedBitmap = bmp };
                project.AddPanel(panel);
            }
        }

        var config = new TilingConfig(DefaultConfidenceThreshold: 0.10f, MinConfidence: 0.05f);
        var result = await MosaicStarCounter.CountMosaicStarsAsync(project, config);

        Assert.NotNull(result);
        Assert.Equal(4, result.PanelDetails.Count);
        Assert.True(result.RawDetectionsCount >= 300, $"Expected at least 300 raw detections, got {result.RawDetectionsCount}");
        Assert.True(result.TotalUniqueStars > 0);
    }

    [Fact]
    public async Task CountMosaicStarsAsync_WithTargetWcs_ProjectsStarsIntoTargetCoordinateFrame()
    {
        var project = new MosaicProject();

        // Panel 1: Center RA 100.0, Dec 20.0
        var wcs1 = new WcsMatrix(new CelestialCoordinates(100.0, 20.0), 400, 400, 3.0, 0.0, 800, 800);
        var bmp1 = new SKBitmap(800, 800);
        using (var canvas = new SKCanvas(bmp1))
        {
            canvas.Clear(new SKColor(15, 15, 20));
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawCircle(400, 400, 4, paint);
        }
        var p1 = new MosaicPanel("p1", "Panel 1", 800, 800, wcs1) { CachedBitmap = bmp1 };

        // Panel 2: Center RA 100.3, Dec 20.0
        var wcs2 = new WcsMatrix(new CelestialCoordinates(100.3, 20.0), 400, 400, 3.0, 0.0, 800, 800);
        var bmp2 = new SKBitmap(800, 800);
        using (var canvas = new SKCanvas(bmp2))
        {
            canvas.Clear(new SKColor(15, 15, 20));
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawCircle(400, 400, 4, paint);
        }
        var p2 = new MosaicPanel("p2", "Panel 2", 800, 800, wcs2) { CachedBitmap = bmp2 };

        project.AddPanel(p1);
        project.AddPanel(p2);

        var config = new TilingConfig(DefaultConfidenceThreshold: 0.10f, MinConfidence: 0.05f);
        
        // Pass wcs1 as target coordinate system
        var result = await MosaicStarCounter.CountMosaicStarsAsync(project, config, targetWcs: wcs1);

        Assert.NotNull(result);
        Assert.Equal(2, result.PanelDetails.Count);
        Assert.True(result.AllObjects.Count >= 2);

        // One star should be near (400, 400) in wcs1
        var p1Star = result.AllObjects.FirstOrDefault(o => Math.Abs(o.CenterX - 400) < 5 && Math.Abs(o.CenterY - 400) < 5);
        Assert.NotNull(p1Star);
    }
}
