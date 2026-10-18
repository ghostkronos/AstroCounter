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

    [Fact]
    public void LoadImage_TiffFormats_DecodesSuccessfully()
    {
        string baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string sampleDir = Path.Combine(baseDir, "assets", "sample_images");

        // 1. 8-bit RGB TIFF
        string path8 = Path.Combine(sampleDir, "test_8bit.tif");
        if (File.Exists(path8))
        {
            using var bmp8 = ImagePreprocessor.LoadImage(path8);
            Assert.NotNull(bmp8);
            Assert.Equal(100, bmp8.Width);
            Assert.Equal(100, bmp8.Height);
            var px8 = bmp8.GetPixel(0, 0);
            Assert.Equal(50, px8.Red);
            Assert.Equal(100, px8.Green);
            Assert.Equal(150, px8.Blue);
        }

        // 2. 16-bit Grayscale TIFF
        string path16 = Path.Combine(sampleDir, "test_16bit_gray.tif");
        if (File.Exists(path16))
        {
            using var bmp16 = ImagePreprocessor.LoadImage(path16);
            Assert.NotNull(bmp16);
            Assert.Equal(100, bmp16.Width);
            Assert.Equal(100, bmp16.Height);
            var px16 = bmp16.GetPixel(0, 0);
            Assert.InRange(px16.Red, 110, 125);
        }

        // 3. 32-bit Float TIFF
        string path32 = Path.Combine(sampleDir, "test_32bit_float.tif");
        if (File.Exists(path32))
        {
            using var bmp32 = ImagePreprocessor.LoadImage(path32);
            Assert.NotNull(bmp32);
            Assert.Equal(100, bmp32.Width);
            Assert.Equal(100, bmp32.Height);
            var px32 = bmp32.GetPixel(0, 0);
            Assert.InRange(px32.Red, 120, 135);
        }
    }

    [Fact]
    public async Task ProcessImageAsync_TiffImage_DetectsStarsAndExportsAnnotated()
    {
        string baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string tiffImagePath = Path.Combine(baseDir, "assets", "sample_images", "deep_sky_stack.tif");
        string modelPath = Path.Combine(baseDir, "models", "astro_yolo11n.onnx");

        Assert.True(File.Exists(tiffImagePath), $"TIFF image not found at: {tiffImagePath}");

        // Test with Photometric mode
        using var photometricEngine = new OnnxInferenceEngine(initialMode: DetectionEngineMode.Photometric);
        var config = new TilingConfig(TileWidth: 640, TileHeight: 640, OverlapRatio: 0.20f);
        var resultPhotometric = await photometricEngine.ProcessImageAsync(tiffImagePath, config);

        Assert.NotNull(resultPhotometric);
        Assert.True(resultPhotometric.AllObjects.Count > 0, "Photometric mode should detect stars in TIFF");

        // Test with Deep Learning YOLO mode
        if (File.Exists(modelPath))
        {
            using var yoloEngine = new OnnxInferenceEngine(modelPath, initialMode: DetectionEngineMode.DeepLearningYolo);
            var resultYolo = await yoloEngine.ProcessImageAsync(tiffImagePath, config);
            Assert.NotNull(resultYolo);
            Assert.True(resultYolo.AllObjects.Count > 0, "YOLO mode should detect stars in TIFF");
        }

        // Test export annotated image with TIFF input
        string tempPng = Path.Combine(Path.GetTempPath(), $"astro_annotated_from_tiff_{Guid.NewGuid()}.png");
        ResultExporter.ExportAnnotatedImage(tiffImagePath, resultPhotometric.AllObjects, tempPng);
        Assert.True(File.Exists(tempPng));
        Assert.True(new FileInfo(tempPng).Length > 1000);
        File.Delete(tempPng);
    }

    [Fact]
    public void DetectStars_BrightStarVsFaintStars_BrightStarGetsTier1Green()
    {
        // Create a 200x200 synthetic image with:
        // Background sky = 15
        // A very bright star at (60, 60) with saturated peak 255 and halo
        // A faint star at (140, 140) with peak 40
        using var bmp = new SkiaSharp.SKBitmap(200, 200, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        using var canvas = new SkiaSharp.SKCanvas(bmp);
        canvas.Clear(new SkiaSharp.SKColor(15, 15, 15));

        // Draw bright star with halo
        using var brightPaint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(255, 255, 255), IsAntialias = true };
        canvas.DrawCircle(60, 60, 6, brightPaint);

        // Draw faint star
        using var faintPaint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(40, 40, 40), IsAntialias = true };
        canvas.DrawCircle(140, 140, 2, faintPaint);

        var config = new TilingConfig(MinConfidence: 0.10f);
        var stars = PhotometricStarFinder.DetectStars(bmp, config);

        Assert.True(stars.Count >= 2, $"Expected at least 2 stars, found {stars.Count}");

        var brightStar = stars.OrderBy(s => Math.Abs(s.CenterX - 60) + Math.Abs(s.CenterY - 60)).First();
        var faintStar = stars.OrderBy(s => Math.Abs(s.CenterX - 140) + Math.Abs(s.CenterY - 140)).First();

        // The brightest star must be Tier 1 High (confidence >= 0.80 -> Green)
        Assert.Equal(ConfidenceTier.Tier1_High, brightStar.Tier);
        Assert.True(brightStar.Confidence >= 0.85f, $"Bright star confidence was {brightStar.Confidence:P0}, expected >= 85%");

        // The bright star must have strictly higher confidence than the faint star
        Assert.True(brightStar.Confidence > faintStar.Confidence,
            $"Bright star confidence ({brightStar.Confidence}) should be higher than faint star ({faintStar.Confidence})");
    }
}
