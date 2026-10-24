namespace AstroCounter.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AstroCounter.Core.Astrometry;
using AstroCounter.Core.Export;
using AstroCounter.Core.Inference;
using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using Xunit;

public class AstroCounterV120IntegrationTests
{
    [Fact]
    public async Task FullPipeline_EndToEnd_HybridFusion_AutoConfidence_Astrometry_Export()
    {
        string baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string testImagePath = Path.Combine(baseDir, "assets", "sample_images", "deep_sky_stack.png");
        string modelPath = Path.Combine(baseDir, "models", "astro_yolo11n.onnx");

        Assert.True(File.Exists(testImagePath), $"Test image not found at: {testImagePath}");

        // 1. Initialize Hybrid Fusion Detection Engine (v1.2.0 standard)
        using var engine = new OnnxInferenceEngine(
            modelPath: File.Exists(modelPath) ? modelPath : null,
            initialMode: DetectionEngineMode.HybridFusion);

        Assert.Equal(DetectionEngineMode.HybridFusion, engine.Mode);

        var config = new TilingConfig(TileWidth: 640, TileHeight: 640, OverlapRatio: 0.20f);
        var detectionResult = await engine.ProcessImageAsync(testImagePath, config);

        // 2. Validate Detection Results & SNR
        Assert.NotNull(detectionResult);
        Assert.True(detectionResult.AllObjects.Count > 0, "Hybrid fusion should extract real astronomical stars");
        Assert.True(detectionResult.AllObjects.Any(o => o.Snr > 0), "SNR values should be computed and recorded");

        // 3. Validate Auto-Confidence Knee-Point Calculation
        float autoConf = detectionResult.RecommendedConfidence;
        Assert.InRange(autoConf, 0.15f, 0.55f);

        var filteredResult = detectionResult.Filter(autoConf);
        Assert.True(filteredResult.TotalCount > 0);

        // 4. Validate WCS Astrometry & Anomaly Detection
        var wcs = new WcsMatrix(
            centerCoordinates: new CelestialCoordinates(10.6847, 41.2687), // M31
            referencePixelX: detectionResult.ImageWidth / 2.0,
            referencePixelY: detectionResult.ImageHeight / 2.0,
            pixelScaleArcsec: 2.4,
            rotationDegrees: 15.0,
            imageWidth: detectionResult.ImageWidth,
            imageHeight: detectionResult.ImageHeight
        );

        var anomalyChecked = TransientAnomalyDetector.DetectAnomalies(
            filteredResult.FilteredObjects,
            wcs,
            detectionResult.ImageWidth,
            detectionResult.ImageHeight,
            minSnr: 7.5f,
            minConfidence: 0.65f);

        Assert.NotNull(anomalyChecked);
        Assert.Equal(filteredResult.FilteredObjects.Count, anomalyChecked.Count);

        // 5. Validate Multi-Format Scientific Export
        string tempCsv = Path.Combine(Path.GetTempPath(), $"astro_v120_export_{Guid.NewGuid()}.csv");
        ResultExporter.ExportToCsv(anomalyChecked, tempCsv);
        Assert.True(File.Exists(tempCsv));
        string csvContent = File.ReadAllText(tempCsv);
        Assert.Contains("Id,ClassName,Confidence", csvContent);
        File.Delete(tempCsv);

        string tempPng = Path.Combine(Path.GetTempPath(), $"astro_v120_annotated_{Guid.NewGuid()}.png");
        ResultExporter.ExportAnnotatedImage(testImagePath, anomalyChecked, tempPng);
        Assert.True(File.Exists(tempPng));
        Assert.True(new FileInfo(tempPng).Length > 1000);
        File.Delete(tempPng);
    }

    [Fact]
    public void GaiaTychoCatalog_50kStars_QueryPerformanceUnderBudget()
    {
        // Performance test: Query dense star catalog repeatedly
        var sw = Stopwatch.StartNew();
        int totalFound = 0;

        for (int i = 0; i < 50; i++)
        {
            double ra = 10.0 + (i * 0.5);
            double dec = 40.0 + (i * 0.2);
            var stars = GaiaTychoCatalog.QueryRegion(ra, dec, radiusDeg: 2.0, maxMag: 12.0, maxResults: 100);
            totalFound += stars.Count;
        }

        sw.Stop();
        double avgQueryMs = sw.Elapsed.TotalMilliseconds / 50.0;
        Assert.True(avgQueryMs < 5.0, $"Average catalog spatial query took {avgQueryMs:F2} ms, expected < 5.0 ms");
    }
}
