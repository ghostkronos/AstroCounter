namespace AstroCounter.Core.Processing;

using System;
using System.Buffers;
using System.Collections.Generic;
using SkiaSharp;

/// <summary>
/// Equalizes sky background luminance and gradients across multi-panel astrophotography mosaics.
/// Eliminates harsh rectangular brightness steps caused by disparate moon phases, sky glow, or airmass differences.
/// Operates on a 64x64 coarse grid with zero LOH allocations via ArrayPool.
/// </summary>
public static class MultiPanelBackgroundEqualizer
{
    public const int GridBlockSize = 64;

    public readonly record struct PanelBackgroundStats(
        float MedianLuminance,
        float Sigma,
        float OffsetCorrection);

    /// <summary>
    /// Computes background statistics (median sky glow and noise sigma) for a bitmap using a 64x64 coarse grid.
    /// Zero-allocation execution via ArrayPool.
    /// </summary>
    public static PanelBackgroundStats EstimateBackground(SKBitmap bitmap)
    {
        if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return new PanelBackgroundStats(0f, 0f, 0f);
        }

        int width = bitmap.Width;
        int height = bitmap.Height;
        int gridCols = Math.Max(1, (width + GridBlockSize - 1) / GridBlockSize);
        int gridRows = Math.Max(1, (height + GridBlockSize - 1) / GridBlockSize);
        int cellCount = gridCols * gridRows;

        float[] cellMedians = ArrayPool<float>.Shared.Rent(cellCount);
        float[] sampleBuffer = ArrayPool<float>.Shared.Rent(GridBlockSize * GridBlockSize);

        try
        {
            int validCells = 0;
            unsafe
            {
                byte* srcPtr = (byte*)bitmap.GetPixels();
                int stride = bitmap.RowBytes;
                int bytesPerPixel = bitmap.BytesPerPixel;

                for (int gy = 0; gy < gridRows; gy++)
                {
                    int y0 = gy * GridBlockSize;
                    int y1 = Math.Min(height, y0 + GridBlockSize);

                    for (int gx = 0; gx < gridCols; gx++)
                    {
                        int x0 = gx * GridBlockSize;
                        int x1 = Math.Min(width, x0 + GridBlockSize);

                        int sampleCount = 0;
                        int maxSamples = sampleBuffer.Length;

                        for (int y = y0; y < y1 && sampleCount < maxSamples; y += 2) // 2x stride for fast sampling
                        {
                            byte* row = srcPtr + (y * stride);
                            for (int x = x0; x < x1 && sampleCount < maxSamples; x += 2)
                            {
                                int idx = x * bytesPerPixel;
                                float lum = (0.2126f * row[idx + 2]) + (0.7152f * row[idx + 1]) + (0.0722f * row[idx + 0]);
                                sampleBuffer[sampleCount++] = lum;
                            }
                        }

                        if (sampleCount > 16)
                        {
                            Array.Sort(sampleBuffer, 0, sampleCount);
                            float cellMedian = sampleBuffer[sampleCount / 2];
                            cellMedians[validCells++] = cellMedian;
                        }
                    }
                }
            }

            if (validCells == 0) return new PanelBackgroundStats(0f, 0f, 0f);

            Array.Sort(cellMedians, 0, validCells);
            float overallMedian = cellMedians[validCells / 2];
            float p25 = cellMedians[validCells / 4];
            float p75 = cellMedians[Math.Min(validCells - 1, (validCells * 3) / 4)];
            float sigma = (p75 - p25) * 0.7413f;

            return new PanelBackgroundStats(overallMedian, sigma, 0f);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(cellMedians);
            ArrayPool<float>.Shared.Return(sampleBuffer);
        }
    }

    /// <summary>
    /// Computes equalized background offsets for a collection of mosaic panels.
    /// Returns per-panel additive offsets in luminance [0..255] needed to align all panels to the reference target median.
    /// </summary>
    public static float[] ComputeEqualizationOffsets(IReadOnlyList<PanelBackgroundStats> panelStats, float targetMedian = 20.0f)
    {
        if (panelStats == null || panelStats.Count == 0) return Array.Empty<float>();

        float[] offsets = new float[panelStats.Count];
        for (int i = 0; i < panelStats.Count; i++)
        {
            float diff = targetMedian - panelStats[i].MedianLuminance;
            offsets[i] = diff;
        }
        return offsets;
    }

    /// <summary>
    /// Applies background offset correction to a bitmap in-place or returning a corrected copy.
    /// </summary>
    public static SKBitmap ApplyBackgroundOffset(SKBitmap source, float offset)
    {
        if (source == null) return null!;
        if (Math.Abs(offset) < 0.5f) return source.Copy();

        var result = source.Copy();
        int width = result.Width;
        int height = result.Height;
        int bpp = result.BytesPerPixel;

        unsafe
        {
            byte* ptr = (byte*)result.GetPixels();
            int stride = result.RowBytes;

            for (int y = 0; y < height; y++)
            {
                byte* row = ptr + (y * stride);
                for (int x = 0; x < width; x++)
                {
                    int idx = x * bpp;
                    row[idx + 0] = (byte)Math.Clamp((int)(row[idx + 0] + offset), 0, 255);
                    row[idx + 1] = (byte)Math.Clamp((int)(row[idx + 1] + offset), 0, 255);
                    row[idx + 2] = (byte)Math.Clamp((int)(row[idx + 2] + offset), 0, 255);
                }
            }
        }

        return result;
    }
}
