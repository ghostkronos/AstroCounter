namespace AstroCounter.Core.Processing;

using SkiaSharp;

/// <summary>
/// Preprocessing utilities for astrophotography images using SkiaSharp (tiling, stretching, and tensor layout).
/// </summary>
public static class ImagePreprocessor
{
    /// <summary>
    /// Loads an image from file path using SkiaSharp.
    /// </summary>
    public static SKBitmap LoadImage(string filePath)
    {
        var bitmap = SKBitmap.Decode(filePath);
        if (bitmap == null)
        {
            throw new InvalidOperationException($"Desteklenmeyen veya bozuk görsel formatı: {filePath}");
        }
        return bitmap;
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
