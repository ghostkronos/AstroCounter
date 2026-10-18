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
        bool isTemp8 = false;
        if (!File.Exists(path8))
        {
            path8 = Path.Combine(Path.GetTempPath(), $"test_8bit_{Guid.NewGuid()}.tif");
            Create8BitRgbTiff(path8, 100, 100, 50, 100, 150);
            isTemp8 = true;
        }
        try
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
        finally
        {
            if (isTemp8 && File.Exists(path8)) File.Delete(path8);
        }

        // 2. 16-bit Grayscale TIFF
        string path16 = Path.Combine(sampleDir, "test_16bit_gray.tif");
        bool isTemp16 = false;
        if (!File.Exists(path16))
        {
            path16 = Path.Combine(Path.GetTempPath(), $"test_16bit_{Guid.NewGuid()}.tif");
            Create16BitGrayTiff(path16, 100, 100, 30000);
            isTemp16 = true;
        }
        try
        {
            using var bmp16 = ImagePreprocessor.LoadImage(path16);
            Assert.NotNull(bmp16);
            Assert.Equal(100, bmp16.Width);
            Assert.Equal(100, bmp16.Height);
            var px16 = bmp16.GetPixel(0, 0);
            Assert.InRange(px16.Red, 110, 125);
        }
        finally
        {
            if (isTemp16 && File.Exists(path16)) File.Delete(path16);
        }

        // 3. 32-bit Float TIFF
        string path32 = Path.Combine(sampleDir, "test_32bit_float.tif");
        bool isTemp32 = false;
        if (!File.Exists(path32))
        {
            path32 = Path.Combine(Path.GetTempPath(), $"test_32bit_{Guid.NewGuid()}.tif");
            Create32BitFloatTiff(path32, 100, 100, 0.5f);
            isTemp32 = true;
        }
        try
        {
            using var bmp32 = ImagePreprocessor.LoadImage(path32);
            Assert.NotNull(bmp32);
            Assert.Equal(100, bmp32.Width);
            Assert.Equal(100, bmp32.Height);
            var px32 = bmp32.GetPixel(0, 0);
            Assert.InRange(px32.Red, 120, 135);
        }
        finally
        {
            if (isTemp32 && File.Exists(path32)) File.Delete(path32);
        }
    }

    [Fact]
    public async Task ProcessImageAsync_TiffImage_DetectsStarsAndExportsAnnotated()
    {
        string baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string sampleTiff = Path.Combine(baseDir, "assets", "sample_images", "deep_sky_stack.tif");
        string modelPath = Path.Combine(baseDir, "models", "astro_yolo11n.onnx");

        string tiffImagePath;
        bool isTemp = false;

        if (File.Exists(sampleTiff))
        {
            tiffImagePath = sampleTiff;
        }
        else
        {
            string pngFallback = Path.Combine(baseDir, "assets", "sample_images", "deep_sky_stack.png");
            Assert.True(File.Exists(pngFallback), $"Fallback PNG sample image not found at: {pngFallback}");
            tiffImagePath = Path.Combine(Path.GetTempPath(), $"astro_test_stack_{Guid.NewGuid()}.tif");
            ConvertImageToTiff(pngFallback, tiffImagePath);
            isTemp = true;
        }

        try
        {
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
        finally
        {
            if (isTemp && File.Exists(tiffImagePath))
            {
                File.Delete(tiffImagePath);
            }
        }
    }

    private static void ConvertImageToTiff(string sourceImagePath, string targetTiffPath)
    {
        using var bmp = ImagePreprocessor.LoadImage(sourceImagePath);
        int width = bmp.Width;
        int height = bmp.Height;

        using var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(targetTiffPath, "w");
        if (tiff == null) throw new InvalidOperationException($"Cannot create test TIFF at: {targetTiffPath}");

        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGEWIDTH, width);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGELENGTH, height);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.SAMPLESPERPIXEL, 3);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.BITSPERSAMPLE, 8);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.ORIENTATION, BitMiracle.LibTiff.Classic.Orientation.TOPLEFT);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.PLANARCONFIG, BitMiracle.LibTiff.Classic.PlanarConfig.CONTIG);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.PHOTOMETRIC, BitMiracle.LibTiff.Classic.Photometric.RGB);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.ROWSPERSTRIP, 32);

        byte[] scanline = new byte[width * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var color = bmp.GetPixel(x, y);
                scanline[x * 3] = color.Red;
                scanline[x * 3 + 1] = color.Green;
                scanline[x * 3 + 2] = color.Blue;
            }
            tiff.WriteScanline(scanline, y);
        }
        tiff.WriteDirectory();
    }

    private static void Create8BitRgbTiff(string filePath, int width, int height, byte r, byte g, byte b)
    {
        using var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filePath, "w");
        if (tiff == null) return;
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGEWIDTH, width);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGELENGTH, height);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.SAMPLESPERPIXEL, 3);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.BITSPERSAMPLE, 8);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.ORIENTATION, BitMiracle.LibTiff.Classic.Orientation.TOPLEFT);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.PLANARCONFIG, BitMiracle.LibTiff.Classic.PlanarConfig.CONTIG);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.PHOTOMETRIC, BitMiracle.LibTiff.Classic.Photometric.RGB);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.ROWSPERSTRIP, height);

        byte[] scanline = new byte[width * 3];
        for (int x = 0; x < width; x++)
        {
            scanline[x * 3] = r;
            scanline[x * 3 + 1] = g;
            scanline[x * 3 + 2] = b;
        }
        for (int y = 0; y < height; y++)
        {
            tiff.WriteScanline(scanline, y);
        }
        tiff.WriteDirectory();
    }

    private static void Create16BitGrayTiff(string filePath, int width, int height, ushort val)
    {
        using var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filePath, "w");
        if (tiff == null) return;
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGEWIDTH, width);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGELENGTH, height);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.SAMPLESPERPIXEL, 1);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.BITSPERSAMPLE, 16);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.ORIENTATION, BitMiracle.LibTiff.Classic.Orientation.TOPLEFT);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.PLANARCONFIG, BitMiracle.LibTiff.Classic.PlanarConfig.CONTIG);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.PHOTOMETRIC, BitMiracle.LibTiff.Classic.Photometric.MINISBLACK);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.ROWSPERSTRIP, height);

        byte[] scanline = new byte[width * 2];
        for (int x = 0; x < width; x++)
        {
            scanline[x * 2] = (byte)(val & 0xFF);
            scanline[x * 2 + 1] = (byte)((val >> 8) & 0xFF);
        }
        for (int y = 0; y < height; y++)
        {
            tiff.WriteScanline(scanline, y);
        }
        tiff.WriteDirectory();
    }

    private static void Create32BitFloatTiff(string filePath, int width, int height, float val)
    {
        using var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filePath, "w");
        if (tiff == null) return;
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGEWIDTH, width);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGELENGTH, height);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.SAMPLESPERPIXEL, 1);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.BITSPERSAMPLE, 32);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.SAMPLEFORMAT, BitMiracle.LibTiff.Classic.SampleFormat.IEEEFP);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.ORIENTATION, BitMiracle.LibTiff.Classic.Orientation.TOPLEFT);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.PLANARCONFIG, BitMiracle.LibTiff.Classic.PlanarConfig.CONTIG);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.PHOTOMETRIC, BitMiracle.LibTiff.Classic.Photometric.MINISBLACK);
        tiff.SetField(BitMiracle.LibTiff.Classic.TiffTag.ROWSPERSTRIP, height);

        byte[] valBytes = BitConverter.GetBytes(val);
        byte[] scanline = new byte[width * 4];
        for (int x = 0; x < width; x++)
        {
            Buffer.BlockCopy(valBytes, 0, scanline, x * 4, 4);
        }
        for (int y = 0; y < height; y++)
        {
            tiff.WriteScanline(scanline, y);
        }
        tiff.WriteDirectory();
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
