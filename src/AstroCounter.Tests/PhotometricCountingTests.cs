namespace AstroCounter.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AstroCounter.Core.Astrometry;
using AstroCounter.Core.Inference;
using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using SkiaSharp;
using Xunit;

public class PhotometricCountingTests
{
    [Fact]
    public async Task PhotometricStarFinder_DetectsSubstantialStarsOnSampleImage()
    {
        string baseDir = AppContext.BaseDirectory;
        string deepSkyPath = Path.GetFullPath(Path.Combine(baseDir, "../../../../../assets/sample_images/deep_sky_stack.png"));
        if (!File.Exists(deepSkyPath)) return;

        using var engine = new OnnxInferenceEngine(initialMode: DetectionEngineMode.Photometric);
        var config = new TilingConfig(DefaultConfidenceThreshold: 0.25f, MinConfidence: 0.02f);

        var res = await engine.ProcessImageAsync(deepSkyPath, config);
        
        // Previously limited to ~700-2,000 due to harsh netPeak clamp. Now detects realistic astronomical counts.
        Assert.NotNull(res);
        Assert.True(res.AllObjects.Count >= 5000, 
            $"Expected at least 5,000 raw objects detected in deep sky stack, but found {res.AllObjects.Count}");

        var filteredDefault = res.Filter(0.25f);
        Assert.True(filteredDefault.TotalCount >= 4000, 
            $"Expected at least 4,000 filtered objects at 0.25 threshold, but found {filteredDefault.TotalCount}");
    }

    [Fact]
    public async Task HybridFusion_CombinesPhotometricAndAIWithoutOverFiltering()
    {
        string baseDir = AppContext.BaseDirectory;
        string deepSkyPath = Path.GetFullPath(Path.Combine(baseDir, "../../../../../assets/sample_images/deep_sky_stack.png"));
        if (!File.Exists(deepSkyPath)) return;

        using var engine = new OnnxInferenceEngine(initialMode: DetectionEngineMode.HybridFusion);
        var config = new TilingConfig(DefaultConfidenceThreshold: 0.25f, MinConfidence: 0.02f);

        var res = await engine.ProcessImageAsync(deepSkyPath, config);
        Assert.NotNull(res);
        Assert.True(res.AllObjects.Count >= 5000, 
            $"HybridFusion should yield substantial objects (>= 5,000), but found {res.AllObjects.Count}");

        var filtered = res.Filter(res.RecommendedConfidence);
        Assert.True(filtered.TotalCount >= 3000, 
            $"Filtered count at recommended threshold should be >= 3,000, was {filtered.TotalCount}");
    }

    [Fact]
    public async Task TransientAnomalyDetector_StrictlyLimitsAnomaliesToTwo()
    {
        string baseDir = AppContext.BaseDirectory;
        string deepSkyPath = Path.GetFullPath(Path.Combine(baseDir, "../../../../../assets/sample_images/deep_sky_stack.png"));
        if (!File.Exists(deepSkyPath)) return;

        using var engine = new OnnxInferenceEngine(initialMode: DetectionEngineMode.Photometric);
        var config = new TilingConfig(DefaultConfidenceThreshold: 0.25f, MinConfidence: 0.02f);
        var countRes = await engine.ProcessImageAsync(deepSkyPath, config);

        var solver = new HybridPlateSolver();
        using var bmp = SKBitmap.Decode(deepSkyPath);
        var solveRes = await solver.SolveAsync(deepSkyPath, bmp.Width, bmp.Height);
        Assert.True(solveRes.IsSuccess);

        var anomalies = TransientAnomalyDetector.DetectAnomalies(
            countRes.AllObjects,
            solveRes.Wcs!,
            bmp.Width,
            bmp.Height,
            minSnr: 7.5f,
            minConfidence: 0.65f,
            maxAnomalies: 2);

        int anomalyCount = anomalies.Count(a => a.IsAnomaly);
        Assert.InRange(anomalyCount, 0, 2);
    }

    [Fact]
    public void DetectStars_SoftWideFieldStarAndCompanionStar_BothDetectedOnCompressedFormat()
    {
        int w = 256, h = 256;
        using var bmp = new SkiaSharp.SKBitmap(w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double lum = 18.0;
                // 1. Bright wide star at (80, 80)
                double d1Sq = ((x - 80) * (x - 80)) + ((y - 80) * (y - 80));
                lum += 210.0 * Math.Exp(-d1Sq / (2.0 * 4.5 * 4.5));

                // 2. Distinct companion star 18px away at (98, 80) separated by a valley
                double d2Sq = ((x - 98) * (x - 98)) + ((y - 80) * (y - 80));
                lum += 85.0 * Math.Exp(-d2Sq / (2.0 * 2.0 * 2.0));

                // 3. Soft defocused faint/medium star at (180, 160), wide sigma=4.0, peak delta=18
                double d3Sq = ((x - 180) * (x - 180)) + ((y - 160) * (y - 160));
                lum += 18.0 * Math.Exp(-d3Sq / (2.0 * 4.0 * 4.0));

                byte v = (byte)Math.Clamp((int)Math.Round(lum), 0, 255);
                bmp.SetPixel(x, y, new SkiaSharp.SKColor(v, v, v, 255));
            }
        }

        var cfg = new TilingConfig(DefaultConfidenceThreshold: 0.25f, MinConfidence: 0.05f);
        var stars = PhotometricStarFinder.DetectStars(bmp, cfg, isCompressedFormat: true);

        Assert.Contains(stars, s => Math.Abs(s.CenterX - 80) < 4 && Math.Abs(s.CenterY - 80) < 4);
        Assert.Contains(stars, s => Math.Abs(s.CenterX - 98) < 4 && Math.Abs(s.CenterY - 80) < 4);
        Assert.Contains(stars, s => Math.Abs(s.CenterX - 180) < 5 && Math.Abs(s.CenterY - 160) < 5);
    }
}
