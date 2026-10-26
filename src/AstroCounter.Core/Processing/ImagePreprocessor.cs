namespace AstroCounter.Core.Processing;

using System;
using System.Buffers;
using System.IO;
using System.Threading.Tasks;
using BitMiracle.LibTiff.Classic;
using SkiaSharp;

/// <summary>
/// Preprocessing utilities for astrophotography images using SkiaSharp and LibTiff.NET.
/// Supports standard formats (PNG, JPG, BMP) and astronomical TIFF formats (8-bit, 16-bit, 32-bit float).
/// </summary>
public static class ImagePreprocessor
{
    static ImagePreprocessor()
    {
        // Suppress LibTiff console output for warnings/non-fatal errors
        Tiff.SetErrorHandler(new SilentErrorHandler());
    }

    private class SilentErrorHandler : TiffErrorHandler
    {
        public override void WarningHandler(Tiff tif, string method, string format, params object[] args) { }
        public override void WarningHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args) { }
        public override void ErrorHandler(Tiff tif, string method, string format, params object[] args) { }
        public override void ErrorHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args) { }
    }

    /// <summary>
    /// Loads an image from file path using SkiaSharp, with full TIFF fallback support via LibTiff.NET.
    /// </summary>
    public static SKBitmap LoadImage(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Görsel dosyası bulunamadı: {filePath}", filePath);
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        // If it's a TIFF file, decode with LibTiff first
        if (ext is ".tif" or ".tiff")
        {
            var tiffBitmap = DecodeTiff(filePath);
            if (tiffBitmap != null)
            {
                return tiffBitmap;
            }
        }

        // Try standard SkiaSharp decode (for PNG, JPG, BMP, WebP, etc.)
        var bitmap = SKBitmap.Decode(filePath);
        if (bitmap != null)
        {
            return bitmap;
        }

        // Fallback: If Skia failed (could still be a TIFF with an atypical extension or format)
        var fallbackTiff = DecodeTiff(filePath);
        if (fallbackTiff != null)
        {
            return fallbackTiff;
        }

        throw new InvalidOperationException($"Desteklenmeyen veya bozuk görsel formatı: {filePath}");
    }

    /// <summary>
    /// Decodes a TIFF image using LibTiff.NET, supporting 8-bit, 16-bit, and 32-bit float astrophotography formats.
    /// </summary>
    public static SKBitmap? DecodeTiff(string filePath)
    {
        try
        {
            using var tiff = Tiff.Open(filePath, "r");
            if (tiff == null) return null;

            // In multi-directory TIFFs, pick the full resolution image
            short dirCount = tiff.NumberOfDirectories();
            if (dirCount > 1)
            {
                int maxPixels = 0;
                short bestDir = 0;
                for (short d = 0; d < dirCount; d++)
                {
                    tiff.SetDirectory(d);
                    int w = tiff.GetField(TiffTag.IMAGEWIDTH)?[0].ToInt() ?? 0;
                    int h = tiff.GetField(TiffTag.IMAGELENGTH)?[0].ToInt() ?? 0;
                    if (w * h > maxPixels)
                    {
                        maxPixels = w * h;
                        bestDir = d;
                    }
                }
                tiff.SetDirectory(bestDir);
            }

            int width = tiff.GetField(TiffTag.IMAGEWIDTH)?[0].ToInt() ?? 0;
            int height = tiff.GetField(TiffTag.IMAGELENGTH)?[0].ToInt() ?? 0;
            if (width <= 0 || height <= 0) return null;

            // 1. Try ReadRGBAImageOriented (supports 8-bit, 16-bit integer, standard compressions, RGB, Gray, Palette)
            int totalPixels = width * height;
            int[] raster = ArrayPool<int>.Shared.Rent(totalPixels);
            try
            {
                if (tiff.ReadRGBAImageOriented(width, height, raster, Orientation.TOPLEFT))
                {
                    var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                    var skBitmap = new SKBitmap(info);
                    unsafe
                    {
                        fixed (int* pRaster = raster)
                        {
                            Buffer.MemoryCopy(pRaster, (void*)skBitmap.GetPixels(), (long)totalPixels * sizeof(int), (long)totalPixels * sizeof(int));
                        }
                    }
                    return skBitmap;
                }

                // 2. Fallback: Scanline decoding for 32-bit float / 32-bit integer / unusual TIFF configurations
                return DecodeTiffScanlines(tiff, width, height);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(raster);
            }
        }
        catch
        {
            return null;
        }
    }

    private static SKBitmap? DecodeTiffScanlines(Tiff tiff, int width, int height)
    {
        int bps = tiff.GetField(TiffTag.BITSPERSAMPLE)?[0].ToInt() ?? 8;
        int spp = tiff.GetField(TiffTag.SAMPLESPERPIXEL)?[0].ToInt() ?? 1;
        var sampleFormatField = tiff.GetField(TiffTag.SAMPLEFORMAT);
        int sampleFormat = sampleFormatField != null ? sampleFormatField[0].ToInt() : 1; // 1 = uint, 3 = IEEEFP
        int photo = tiff.GetField(TiffTag.PHOTOMETRIC)?[0].ToInt() ?? 1; // 0 = WhiteIsZero, 1 = BlackIsZero, 2 = RGB

        int scanlineSize = tiff.ScanlineSize();
        if (scanlineSize <= 0) return null;

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var skBitmap = new SKBitmap(info);

        byte[] scanlineBuf = ArrayPool<byte>.Shared.Rent(scanlineSize);
        try
        {
            if (bps == 32 && sampleFormat == 3) // 32-bit IEEE Float (Astrophotography stack export)
            {
                int totalCount = width * height * spp;
                float[] allFloats = ArrayPool<float>.Shared.Rent(totalCount);
                try
                {
                    for (int y = 0; y < height; y++)
                    {
                        if (tiff.ReadScanline(scanlineBuf, y))
                        {
                            int offset = y * width * spp;
                            Buffer.BlockCopy(scanlineBuf, 0, allFloats, offset * sizeof(float), Math.Min(scanlineSize, (width * spp) * sizeof(float)));
                        }
                    }

                    // Find valid min/max
                    float minVal = float.MaxValue;
                    float maxVal = float.MinValue;
                    for (int i = 0; i < totalCount; i++)
                    {
                        float v = allFloats[i];
                        if (!float.IsNaN(v) && !float.IsInfinity(v))
                        {
                            if (v < minVal) minVal = v;
                            if (v > maxVal) maxVal = v;
                        }
                    }

                    if (maxVal <= minVal)
                    {
                        minVal = 0f;
                        maxVal = 1f;
                    }

                    bool isNormalized01 = minVal >= -0.01f && maxVal <= 1.05f;
                    float scale = isNormalized01 ? 255.0f : (255.0f / (maxVal - minVal));
                    float offsetVal = isNormalized01 ? 0f : minVal;

                    unsafe
                    {
                        byte* dstPtr = (byte*)skBitmap.GetPixels();
                        int stride = skBitmap.RowBytes;
                        for (int y = 0; y < height; y++)
                        {
                            byte* row = dstPtr + y * stride;
                            int srcRowOffset = y * width * spp;
                            for (int x = 0; x < width; x++)
                            {
                                int srcIdx = srcRowOffset + x * spp;
                                if (spp == 1)
                                {
                                    byte b = (byte)Math.Clamp((allFloats[srcIdx] - offsetVal) * scale, 0f, 255f);
                                    if (photo == 0) b = (byte)(255 - b);
                                    row[x * 4 + 0] = b;
                                    row[x * 4 + 1] = b;
                                    row[x * 4 + 2] = b;
                                    row[x * 4 + 3] = 255;
                                }
                                else
                                {
                                    byte r = (byte)Math.Clamp((allFloats[srcIdx] - offsetVal) * scale, 0f, 255f);
                                    byte g = (byte)Math.Clamp((allFloats[srcIdx + 1] - offsetVal) * scale, 0f, 255f);
                                    byte b = (byte)Math.Clamp((allFloats[srcIdx + 2] - offsetVal) * scale, 0f, 255f);
                                    row[x * 4 + 0] = r;
                                    row[x * 4 + 1] = g;
                                    row[x * 4 + 2] = b;
                                    row[x * 4 + 3] = 255;
                                }
                            }
                        }
                    }
                    return skBitmap;
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(allFloats);
                }
            }
            else if (bps == 32) // 32-bit uint
            {
                int totalCount = width * height * spp;
                uint[] allUints = ArrayPool<uint>.Shared.Rent(totalCount);
                try
                {
                    for (int y = 0; y < height; y++)
                    {
                        if (tiff.ReadScanline(scanlineBuf, y))
                        {
                            int offset = y * width * spp;
                            Buffer.BlockCopy(scanlineBuf, 0, allUints, offset * sizeof(uint), Math.Min(scanlineSize, (width * spp) * sizeof(uint)));
                        }
                    }

                    unsafe
                    {
                        byte* dstPtr = (byte*)skBitmap.GetPixels();
                        int stride = skBitmap.RowBytes;
                        for (int y = 0; y < height; y++)
                        {
                            byte* row = dstPtr + y * stride;
                            int srcRowOffset = y * width * spp;
                            for (int x = 0; x < width; x++)
                            {
                                int srcIdx = srcRowOffset + x * spp;
                                if (spp == 1)
                                {
                                    byte b = (byte)(allUints[srcIdx] >> 24);
                                    if (photo == 0) b = (byte)(255 - b);
                                    row[x * 4 + 0] = b;
                                    row[x * 4 + 1] = b;
                                    row[x * 4 + 2] = b;
                                    row[x * 4 + 3] = 255;
                                }
                                else
                                {
                                    row[x * 4 + 0] = (byte)(allUints[srcIdx] >> 24);
                                    row[x * 4 + 1] = (byte)(allUints[srcIdx + 1] >> 24);
                                    row[x * 4 + 2] = (byte)(allUints[srcIdx + 2] >> 24);
                                    row[x * 4 + 3] = 255;
                                }
                            }
                        }
                    }
                    return skBitmap;
                }
                finally
                {
                    ArrayPool<uint>.Shared.Return(allUints);
                }
            }
            else if (bps == 16) // 16-bit uint fallback
            {
                int totalCount = width * height * spp;
                ushort[] allUshorts = ArrayPool<ushort>.Shared.Rent(totalCount);
                try
                {
                    for (int y = 0; y < height; y++)
                    {
                        if (tiff.ReadScanline(scanlineBuf, y))
                        {
                            int offset = y * width * spp;
                            Buffer.BlockCopy(scanlineBuf, 0, allUshorts, offset * sizeof(ushort), Math.Min(scanlineSize, (width * spp) * sizeof(ushort)));
                        }
                    }

                    unsafe
                    {
                        byte* dstPtr = (byte*)skBitmap.GetPixels();
                        int stride = skBitmap.RowBytes;
                        for (int y = 0; y < height; y++)
                        {
                            byte* row = dstPtr + y * stride;
                            int srcRowOffset = y * width * spp;
                            for (int x = 0; x < width; x++)
                            {
                                int srcIdx = srcRowOffset + x * spp;
                                if (spp == 1)
                                {
                                    byte b = (byte)(allUshorts[srcIdx] >> 8);
                                    if (photo == 0) b = (byte)(255 - b);
                                    row[x * 4 + 0] = b;
                                    row[x * 4 + 1] = b;
                                    row[x * 4 + 2] = b;
                                    row[x * 4 + 3] = 255;
                                }
                                else
                                {
                                    row[x * 4 + 0] = (byte)(allUshorts[srcIdx] >> 8);
                                    row[x * 4 + 1] = (byte)(allUshorts[srcIdx + 1] >> 8);
                                    row[x * 4 + 2] = (byte)(allUshorts[srcIdx + 2] >> 8);
                                    row[x * 4 + 3] = 255;
                                }
                            }
                        }
                    }
                    return skBitmap;
                }
                finally
                {
                    ArrayPool<ushort>.Shared.Return(allUshorts);
                }
            }

            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scanlineBuf);
        }
    }

    /// <summary>
    /// Extracts a 640x640 tile slice into a flattened CHW float array (1 x 3 x 640 x 640)
    /// ready for ONNX model inference.
    /// </summary>
    public static float[] ExtractTileTensor(
        SKBitmap bitmap,
        TileSlice slice,
        int targetWidth = 640,
        int targetHeight = 640)
    {
        float[] tensor = new float[3 * targetWidth * targetHeight];
        int planeSize = targetWidth * targetHeight;

        int imgW = bitmap.Width;
        int imgH = bitmap.Height;

        for (int ty = 0; ty < slice.Height; ty++)
        {
            int srcY = slice.Y + ty;
            if (srcY >= imgH) break;

            for (int tx = 0; tx < slice.Width; tx++)
            {
                int srcX = slice.X + tx;
                if (srcX >= imgW) break;

                var pixel = bitmap.GetPixel(srcX, srcY);
                int destIndex = (ty * targetWidth) + tx;

                // Channel 0: Red, Channel 1: Green, Channel 2: Blue
                tensor[destIndex] = pixel.Red / 255.0f;
                tensor[planeSize + destIndex] = pixel.Green / 255.0f;
                tensor[(2 * planeSize) + destIndex] = pixel.Blue / 255.0f;
            }
        }

        return tensor;
    }

    public enum ExposureState
    {
        Optimal,
        TooDark,
        TooBright,
        LowContrast
    }

    public record BrightnessCorrectionInfo(
        ExposureState State,
        bool WasAdjusted,
        float OriginalMedianBg,
        float OriginalStarPeak999,
        string SummaryText);

    /// <summary>
    /// Applies real-time visual brightness (-100..100), exposure/gamma (0.2..3.0), and contrast (0.5..2.5)
    /// to an SKBitmap for interactive UI rendering, completely isolated from raw detection algorithms.
    /// </summary>
    public static SKBitmap ApplyDisplayToneAdjustment(SKBitmap source, double brightness, double exposure, double contrast)
    {
        if (Math.Abs(brightness) < 0.5 && Math.Abs(exposure - 1.0) < 0.01 && Math.Abs(contrast - 1.0) < 0.01)
        {
            return source.Copy();
        }

        int width = source.Width;
        int height = source.Height;
        var result = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

        byte[] lut = new byte[256];
        double invGamma = 1.0 / Math.Max(0.1, exposure);

        for (int i = 0; i < 256; i++)
        {
            double v = Math.Pow(i / 255.0, invGamma);
            v = ((v - 0.5) * contrast) + 0.5;
            int outVal = (int)Math.Round((v * 255.0) + brightness);
            lut[i] = (byte)Math.Clamp(outVal, 0, 255);
        }

        using var bgraSource = source.ColorType == SKColorType.Bgra8888 ? null : source.Copy(SKColorType.Bgra8888);
        SKBitmap src = bgraSource ?? source;

        unsafe
        {
            byte* srcPtr = (byte*)src.GetPixels();
            byte* dstPtr = (byte*)result.GetPixels();
            int totalBytes = width * height * 4;

            for (int i = 0; i < totalBytes; i += 4)
            {
                dstPtr[i + 0] = lut[srcPtr[i + 0]];
                dstPtr[i + 1] = lut[srcPtr[i + 1]];
                dstPtr[i + 2] = lut[srcPtr[i + 2]];
                dstPtr[i + 3] = srcPtr[i + 3];
            }
        }

        return result;
    }

    /// <summary>
    /// Automatically analyzes the astronomical image's sky background mesh and star signal dynamic range.
    /// - If the image is TOO DARK (underexposed / linear stack), applies local black-point subtraction
    ///   and an astronomical Midtone Transfer Function (MTF) stretch so faint/medium stars become clearly detectable.
    /// - If the image is TOO BRIGHT (overexposed / light pollution dome / twilight sky glow), subtracts the 2D
    ///   local color sky background gradient and stretches the true star signal above the noise floor.
    /// </summary>
    public static SKBitmap AutoBalanceBrightness(SKBitmap source, out BrightnessCorrectionInfo info)
    {
        int width = source.Width;
        int height = source.Height;

        using var bgraSource = source.ColorType == SKColorType.Bgra8888
            ? null
            : source.Copy(SKColorType.Bgra8888);
        SKBitmap workSrc = bgraSource ?? source;

        const int gridSize = 64;
        int gridCols = Math.Max(1, (width + gridSize - 1) / gridSize);
        int gridRows = Math.Max(1, (height + gridSize - 1) / gridSize);
        int cellCount = gridCols * gridRows;

        float[] gridBgR = new float[cellCount];
        float[] gridBgG = new float[cellCount];
        float[] gridBgB = new float[cellCount];
        float[] gridBgL = new float[cellCount];
        float[] gridSigma = new float[cellCount];
        bool[] gridValid = new bool[cellCount];

        var bufR = new float[1024];
        var bufG = new float[1024];
        var bufB = new float[1024];
        var bufL = new float[1024];
        var bufDev = new float[1024];

        unsafe
        {
            byte* srcPtr = (byte*)workSrc.GetPixels();
            int stride = workSrc.RowBytes;

            for (int gy = 0; gy < gridRows; gy++)
            {
                int y0 = gy * gridSize;
                int y1 = Math.Min(height, y0 + gridSize);
                int stepY = Math.Max(1, (y1 - y0) / 16);

                for (int gx = 0; gx < gridCols; gx++)
                {
                    int x0 = gx * gridSize;
                    int x1 = Math.Min(width, x0 + gridSize);
                    int stepX = Math.Max(1, (x1 - x0) / 16);

                    int count = 0;
                    int nonZero = 0;
                    for (int y = y0; y < y1 && count < 1024; y += stepY)
                    {
                        byte* row = srcPtr + (y * stride);
                        for (int x = x0; x < x1 && count < 1024; x += stepX)
                        {
                            byte b = row[(x * 4) + 0];
                            byte g = row[(x * 4) + 1];
                            byte r = row[(x * 4) + 2];
                            float lum = (0.299f * r) + (0.587f * g) + (0.114f * b);
                            if (r > 0 || g > 0 || b > 0) nonZero++;

                            bufR[count] = r;
                            bufG[count] = g;
                            bufB[count] = b;
                            bufL[count] = lum;
                            count++;
                        }
                    }

                    int idx = (gy * gridCols) + gx;
                    bool isOuterBorderCell = gy == 0 || gy == gridRows - 1 || gx == 0 || gx == gridCols - 1;
                    if (count < 4 || (nonZero == 0 && isOuterBorderCell))
                    {
                        gridValid[idx] = false;
                        continue;
                    }

                    Array.Sort(bufR, 0, count);
                    Array.Sort(bufG, 0, count);
                    Array.Sort(bufB, 0, count);
                    Array.Sort(bufL, 0, count);

                    // Use 30th percentile as sky background to avoid star contamination
                    int bgPos = (int)(count * 0.30f);
                    float medL = bufL[bgPos];
                    gridBgR[idx] = bufR[bgPos];
                    gridBgG[idx] = bufG[bgPos];
                    gridBgB[idx] = bufB[bgPos];
                    gridBgL[idx] = medL;

                    for (int i = 0; i < count; i++)
                    {
                        bufDev[i] = Math.Abs(bufL[i] - medL);
                    }
                    Array.Sort(bufDev, 0, count);
                    float mad = bufDev[count / 2];
                    gridSigma[idx] = Math.Max(0.25f, mad * 1.4826f);
                    gridValid[idx] = true;
                }
            }

            // Collect global background statistics from valid cells
            var validBgList = new System.Collections.Generic.List<float>(cellCount);
            var validSigmaList = new System.Collections.Generic.List<float>(cellCount);
            for (int i = 0; i < cellCount; i++)
            {
                if (gridValid[i])
                {
                    validBgList.Add(gridBgL[i]);
                    validSigmaList.Add(gridSigma[i]);
                }
            }

            float globalMedianBg = 0f;
            float globalSigma = 0.5f;
            if (validBgList.Count > 0)
            {
                validBgList.Sort();
                validSigmaList.Sort();
                globalMedianBg = validBgList[validBgList.Count / 2];
                globalSigma = validSigmaList[validSigmaList.Count / 2];
            }

            // Fill any invalid border cells with global median
            for (int i = 0; i < cellCount; i++)
            {
                if (!gridValid[i])
                {
                    gridBgR[i] = globalMedianBg;
                    gridBgG[i] = globalMedianBg;
                    gridBgB[i] = globalMedianBg;
                    gridBgL[i] = globalMedianBg;
                    gridSigma[i] = globalSigma;
                }
            }

            // Sample net star signals across the image (taking 2x2 local max so tiny 1-2px stars are never missed)
            int sampleStepY = Math.Max(1, height / 400);
            int sampleStepX = Math.Max(1, width / 400);
            var netSignals = new System.Collections.Generic.List<float>(160000);

            for (int y = 0; y < height; y += sampleStepY)
            {
                byte* row0 = srcPtr + (y * stride);
                byte* row1 = srcPtr + (Math.Min(height - 1, y + 1) * stride);
                int gy = Math.Clamp(y / gridSize, 0, gridRows - 1);
                for (int x = 0; x < width; x += sampleStepX)
                {
                    int gx = Math.Clamp(x / gridSize, 0, gridCols - 1);
                    int idx = (gy * gridCols) + gx;
                    if (!gridValid[idx]) continue;

                    int xNext = Math.Min(width - 1, x + 1);
                    float lum00 = (0.299f * row0[(x * 4) + 2]) + (0.587f * row0[(x * 4) + 1]) + (0.114f * row0[(x * 4) + 0]);
                    float lum10 = (0.299f * row0[(xNext * 4) + 2]) + (0.587f * row0[(xNext * 4) + 1]) + (0.114f * row0[(xNext * 4) + 0]);
                    float lum01 = (0.299f * row1[(x * 4) + 2]) + (0.587f * row1[(x * 4) + 1]) + (0.114f * row1[(x * 4) + 0]);
                    float lum = Math.Max(lum00, Math.Max(lum10, lum01));

                    float net = Math.Max(0f, lum - gridBgL[idx]);
                    netSignals.Add(net);
                }
            }

            netSignals.Sort();
            int nSig = netSignals.Count;
            float p95 = nSig > 0 ? netSignals[Math.Clamp((int)(nSig * 0.95f), 0, nSig - 1)] : 8f;
            float p98 = nSig > 0 ? netSignals[Math.Clamp((int)(nSig * 0.98f), 0, nSig - 1)] : 15f;
            float p99 = nSig > 0 ? netSignals[Math.Clamp((int)(nSig * 0.99f), 0, nSig - 1)] : 30f;
            float p995 = nSig > 0 ? netSignals[Math.Clamp((int)(nSig * 0.995f), 0, nSig - 1)] : 50f;
            float p999 = nSig > 0 ? netSignals[Math.Clamp((int)(nSig * 0.999f), 0, nSig - 1)] : 100f;
            float p9997 = nSig > 0 ? netSignals[Math.Clamp((int)(nSig * 0.9997f), 0, nSig - 1)] : 150f;

            ExposureState state;
            if (globalMedianBg > 45.0f)
            {
                state = ExposureState.TooBright;
            }
            else if (p999 < 110.0f ||
                     p995 < 42.0f ||
                     (globalMedianBg < 14.0f && p98 < 30.0f) ||
                     (globalMedianBg < 24.0f && p95 < 12.0f && p99 < 55.0f))
            {
                state = ExposureState.TooDark;
            }
            else if (p999 < 140.0f && p995 < 60.0f)
            {
                state = ExposureState.LowContrast;
            }
            else
            {
                state = ExposureState.Optimal;
            }

            bool tr = AstroCounter.Core.Astrometry.CataloguePlateSolver.UseTurkishLanguage;

            if (state == ExposureState.Optimal)
            {
                info = new BrightnessCorrectionInfo(
                    ExposureState.Optimal,
                    false,
                    globalMedianBg,
                    p999,
                    tr
                        ? $"Parlaklık dengeli (Arka plan: {globalMedianBg:F0}, Yıldız kontrastı: +{p999:F0})"
                        : $"Optimal exposure (Background: {globalMedianBg:F0}, Star contrast: +{p999:F0})");
                return source.Copy();
            }

            var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            byte* dstPtr = (byte*)result.GetPixels();
            int dstStride = result.RowBytes;

            // Precompute 4096-entry MTF lookup table for fast per-pixel transformation
            const int lutSize = 4096;
            float[] lut = new float[lutSize + 1];

            float whiteSpan;
            float targetFloor;

            if (state == ExposureState.TooBright)
            {
                // For overly bright / light-polluted images:
                // Subtract local sky glow and expand star peaks
                for (int i = 0; i <= lutSize; i++)
                {
                    float t = i / (float)lutSize;
                    lut[i] = MathF.Pow(t, 0.80f);
                }
                whiteSpan = Math.Max(14.0f, p9997 * 1.02f);
                targetFloor = 10.0f;
            }
            else
            {
                // For overly dark / low-contrast images:
                // Astronomical Midtone Transfer Function (MTF): MTF(t, m) = (m - 1)*t / ((2*m - 1)*t - m)
                // Lower m (0.06..0.14) gives a strong PixInsight/Siril STF stretch for very dark images.
                float m = p99 < 10.0f ? 0.06f
                        : p99 < 22.0f ? 0.09f
                        : 0.14f;

                for (int i = 0; i <= lutSize; i++)
                {
                    float t = i / (float)lutSize;
                    float denom = (((2.0f * m) - 1.0f) * t) - m;
                    lut[i] = Math.Abs(denom) > 1e-6f ? Math.Clamp(((m - 1.0f) * t) / denom, 0f, 1f) : t;
                }

                // Anchor white point to p99/p995 so a few saturated pixels at 255 never prevent faint stars from stretching
                whiteSpan = Math.Clamp(Math.Max(p99 * 2.2f, Math.Min(p995 * 1.6f, 95.0f)), 6.0f, 95.0f);
                targetFloor = 18.0f;
            }

            float invWhite = lutSize / whiteSpan;

            Parallel.For(0, height, y =>
            {
                byte* sRow = srcPtr + (y * stride);
                byte* dRow = dstPtr + (y * dstStride);

                // Bilinear grid coordinates in Y
                float fy = ((y + 0.5f) / gridSize) - 0.5f;
                int gy0 = Math.Clamp((int)MathF.Floor(fy), 0, gridRows - 1);
                int gy1 = Math.Clamp(gy0 + 1, 0, gridRows - 1);
                float wy = Math.Clamp(fy - gy0, 0f, 1f);

                for (int x = 0; x < width; x++)
                {
                    byte b = sRow[(x * 4) + 0];
                    byte g = sRow[(x * 4) + 1];
                    byte r = sRow[(x * 4) + 2];
                    byte a = sRow[(x * 4) + 3];

                    float fx = ((x + 0.5f) / gridSize) - 0.5f;
                    int gx0 = Math.Clamp((int)MathF.Floor(fx), 0, gridCols - 1);
                    int gx1 = Math.Clamp(gx0 + 1, 0, gridCols - 1);
                    float wx = Math.Clamp(fx - gx0, 0f, 1f);

                    int i00 = (gy0 * gridCols) + gx0;
                    int i10 = (gy0 * gridCols) + gx1;
                    int i01 = (gy1 * gridCols) + gx0;
                    int i11 = (gy1 * gridCols) + gx1;

                    // Preserve pure black panorama void borders only in invalid outer cells
                    if (r == 0 && g == 0 && b == 0 && (!gridValid[i00] || !gridValid[i11]))
                    {
                        dRow[(x * 4) + 0] = 0;
                        dRow[(x * 4) + 1] = 0;
                        dRow[(x * 4) + 2] = 0;
                        dRow[(x * 4) + 3] = a;
                        continue;
                    }

                    float w00 = (1f - wx) * (1f - wy);
                    float w10 = wx * (1f - wy);
                    float w01 = (1f - wx) * wy;
                    float w11 = wx * wy;

                    float locBgR = (w00 * gridBgR[i00]) + (w10 * gridBgR[i10]) + (w01 * gridBgR[i01]) + (w11 * gridBgR[i11]);
                    float locBgG = (w00 * gridBgG[i00]) + (w10 * gridBgG[i10]) + (w01 * gridBgG[i01]) + (w11 * gridBgG[i11]);
                    float locBgB = (w00 * gridBgB[i00]) + (w10 * gridBgB[i10]) + (w01 * gridBgB[i01]) + (w11 * gridBgB[i11]);
                    float locSig = (w00 * gridSigma[i00]) + (w10 * gridSigma[i10]) + (w01 * gridSigma[i01]) + (w11 * gridSigma[i11]);

                    float netR, netG, netB;
                    if (state == ExposureState.TooBright)
                    {
                        float noisePedestal = 0.85f * locSig;
                        netR = Math.Max(0f, r - (locBgR + noisePedestal));
                        netG = Math.Max(0f, g - (locBgG + noisePedestal));
                        netB = Math.Max(0f, b - (locBgB + noisePedestal));
                    }
                    else
                    {
                        // In dark images, subtract only a soft fraction of the background pedestal so faint stars and nebulae are never crushed
                        float darkPedR = Math.Max(0f, locBgR - 1.5f) * 0.65f;
                        float darkPedG = Math.Max(0f, locBgG - 1.5f) * 0.65f;
                        float darkPedB = Math.Max(0f, locBgB - 1.5f) * 0.65f;
                        netR = Math.Max(0f, r - darkPedR);
                        netG = Math.Max(0f, g - darkPedG);
                        netB = Math.Max(0f, b - darkPedB);
                    }

                    int idxR = Math.Clamp((int)(netR * invWhite), 0, lutSize);
                    int idxG = Math.Clamp((int)(netG * invWhite), 0, lutSize);
                    int idxB = Math.Clamp((int)(netB * invWhite), 0, lutSize);

                    float spanOut = 255.0f - targetFloor;
                    dRow[(x * 4) + 0] = (byte)Math.Clamp(targetFloor + (spanOut * lut[idxB]), 0f, 255f);
                    dRow[(x * 4) + 1] = (byte)Math.Clamp(targetFloor + (spanOut * lut[idxG]), 0f, 255f);
                    dRow[(x * 4) + 2] = (byte)Math.Clamp(targetFloor + (spanOut * lut[idxR]), 0f, 255f);
                    dRow[(x * 4) + 3] = a;
                }
            });

            string desc = state switch
            {
                ExposureState.TooDark => tr
                    ? $"Çok karanlık görüntü otomatik aydınlatıldı (MTF Stretch: Arka plan {globalMedianBg:F1} -> {targetFloor:F0})"
                    : $"Underexposed image stretched automatically (MTF Stretch: Background {globalMedianBg:F1} -> {targetFloor:F0})",
                ExposureState.TooBright => tr
                    ? $"Çok parlak arka plan temizlendi ve yıldızlar belirginleştirildi (Arka plan {globalMedianBg:F0} -> {targetFloor:F0})"
                    : $"Overexposed sky glow subtracted and star contrast enhanced (Background {globalMedianBg:F0} -> {targetFloor:F0})",
                ExposureState.LowContrast => tr
                    ? $"Düşük kontrastlı yıldız alanı otomatik güçlendirildi (+{p999:F0} -> +245)"
                    : $"Low-contrast star field enhanced automatically (+{p999:F0} -> +245)",
                _ => tr ? "Parlaklık dengelendi" : "Brightness balanced"
            };

            info = new BrightnessCorrectionInfo(state, true, globalMedianBg, p999, desc);
            return result;
        }
    }

        /// <summary>
        /// Corrects radial lens distortion (barrel / pincushion) on an astronomical image.
        /// Uses polynomial radial re-mapping with bilinear pixel interpolation.
        /// Positive k1 expands corners (fixes barrel distortion). Negative k1 pulls corners in (fixes pincushion).
        /// </summary>
        public static unsafe SKBitmap CorrectLensDistortion(SKBitmap source, float k1)
        {
            if (Math.Abs(k1) < 1e-4f)
            {
                return source.Copy();
            }

            int width = source.Width;
            int height = source.Height;
            float cx = width / 2.0f;
            float cy = height / 2.0f;
            float normRadius = 0.5f * (float)Math.Sqrt((width * width) + (height * height));
            float normRadiusSq = normRadius * normRadius;

            var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            byte* srcPtr = (byte*)source.GetPixels();
            byte* dstPtr = (byte*)result.GetPixels();
            int srcStride = source.RowBytes;
            int dstStride = result.RowBytes;

            Parallel.For(0, height, y =>
            {
                byte* dstRow = dstPtr + (y * dstStride);
                float dy = y - cy;

                for (int x = 0; x < width; x++)
                {
                    float dx = x - cx;
                    float rNormSq = ((dx * dx) + (dy * dy)) / normRadiusSq;

                    // Radial polynomial inverse mapping to source distorted coordinates
                    float factor = 1.0f / (1.0f + (k1 * rNormSq));
                    float srcX = cx + (dx * factor);
                    float srcY = cy + (dy * factor);

                    int x0 = (int)Math.Floor(srcX);
                    int y0 = (int)Math.Floor(srcY);
                    int x1 = x0 + 1;
                    int y1 = y0 + 1;

                    if (x0 >= 0 && x1 < width && y0 >= 0 && y1 < height)
                    {
                        float fx = srcX - x0;
                        float fy = srcY - y0;
                        float w00 = (1.0f - fx) * (1.0f - fy);
                        float w10 = fx * (1.0f - fy);
                        float w01 = (1.0f - fx) * fy;
                        float w11 = fx * fy;

                        byte* p00 = srcPtr + (y0 * srcStride) + (x0 * 4);
                        byte* p10 = srcPtr + (y0 * srcStride) + (x1 * 4);
                        byte* p01 = srcPtr + (y1 * srcStride) + (x0 * 4);
                        byte* p11 = srcPtr + (y1 * srcStride) + (x1 * 4);

                        dstRow[(x * 4) + 0] = (byte)((w00 * p00[0]) + (w10 * p10[0]) + (w01 * p01[0]) + (w11 * p11[0]));
                        dstRow[(x * 4) + 1] = (byte)((w00 * p00[1]) + (w10 * p10[1]) + (w01 * p01[1]) + (w11 * p11[1]));
                        dstRow[(x * 4) + 2] = (byte)((w00 * p00[2]) + (w10 * p10[2]) + (w01 * p01[2]) + (w11 * p11[2]));
                        dstRow[(x * 4) + 3] = (byte)((w00 * p00[3]) + (w10 * p10[3]) + (w01 * p01[3]) + (w11 * p11[3]));
                    }
                    else
                    {
                        dstRow[(x * 4) + 0] = 0;
                        dstRow[(x * 4) + 1] = 0;
                        dstRow[(x * 4) + 2] = 0;
                        dstRow[(x * 4) + 3] = 255;
                    }
                }
            });

            return result;
        }
    }
