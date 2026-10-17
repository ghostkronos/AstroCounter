namespace AstroCounter.Core.Processing;

using System;
using System.IO;
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
            int[] raster = new int[width * height];
            if (tiff.ReadRGBAImageOriented(width, height, raster, Orientation.TOPLEFT))
            {
                var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                var skBitmap = new SKBitmap(info);
                unsafe
                {
                    fixed (int* pRaster = raster)
                    {
                        Buffer.MemoryCopy(pRaster, (void*)skBitmap.GetPixels(), (long)raster.Length * sizeof(int), (long)raster.Length * sizeof(int));
                    }
                }
                return skBitmap;
            }

            // 2. Fallback: Scanline decoding for 32-bit float / 32-bit integer / unusual TIFF configurations
            return DecodeTiffScanlines(tiff, width, height);
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

        byte[] scanlineBuf = new byte[scanlineSize];

        if (bps == 32 && sampleFormat == 3) // 32-bit IEEE Float (Astrophotography stack export)
        {
            float[] allFloats = new float[width * height * spp];
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
            for (int i = 0; i < allFloats.Length; i++)
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
        else if (bps == 32) // 32-bit uint
        {
            uint[] allUints = new uint[width * height * spp];
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
        else if (bps == 16) // 16-bit uint fallback
        {
            ushort[] allUshorts = new ushort[width * height * spp];
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

        return null;
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
}
