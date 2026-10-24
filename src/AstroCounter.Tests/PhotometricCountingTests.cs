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
}
