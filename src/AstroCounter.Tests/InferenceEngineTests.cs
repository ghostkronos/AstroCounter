namespace AstroCounter.Tests;

using AstroCounter.Core.Export;
using AstroCounter.Core.Inference;
using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using Xunit;

public class InferenceEngineTests
{
    [Fact]
    public async Task ProcessImageAsync_SyntheticStarfield_DetectsStarsAndExports()
    {
        string baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string testImagePath = Path.Combine(baseDir, "assets", "sample_images", "deep_sky_stack.png");
        string modelPath = Path.Combine(baseDir, "models", "astro_yolo11n.onnx");

        Assert.True(File.Exists(testImagePath), $"Test image not found at: {testImagePath}");

        using var engine = new OnnxInferenceEngine(File.Exists(modelPath) ? modelPath : null);
        var config = new TilingConfig(TileWidth: 640, TileHeight: 640, OverlapRatio: 0.20f);

        var result = await engine.ProcessImageAsync(testImagePath, config);

        Assert.NotNull(result);
        Assert.True(result.AllObjects.Count > 0, "Expected stars to be detected in the synthetic field");
        Assert.True(result.TileCount > 0, "Expected tile slices to be processed");
        Assert.True(result.ElapsedTime.TotalMilliseconds > 0);

        // Test in-memory filtering at 25% confidence
        var filtered25 = result.Filter(0.25f);
        Assert.True(filtered25.TotalCount > 0);
        Assert.True(filtered25.Tier1Count + filtered25.Tier2Count + filtered25.Tier3Count == filtered25.TotalCount);

        // Test in-memory filtering at 80% confidence (Tier 1 only)
        var filtered80 = result.Filter(0.80f);
        Assert.True(filtered80.Tier2Count == 0);
        Assert.True(filtered80.Tier3Count == 0);
        Assert.True(filtered80.TotalCount <= filtered25.TotalCount);

        // Test CSV Export
        string tempCsv = Path.Combine(Path.GetTempPath(), $"astro_output_{Guid.NewGuid()}.csv");
        ResultExporter.ExportToCsv(filtered25.FilteredObjects, tempCsv);
        Assert.True(File.Exists(tempCsv));
        File.Delete(tempCsv);

        // Test Annotated Image Export
        string tempPng = Path.Combine(Path.GetTempPath(), $"astro_annotated_{Guid.NewGuid()}.png");
        ResultExporter.ExportAnnotatedImage(testImagePath, filtered25.FilteredObjects, tempPng);
        Assert.True(File.Exists(tempPng));
        Assert.True(new FileInfo(tempPng).Length > 1000);
        File.Delete(tempPng);
    }
}
