namespace AstroCounter.Core.Processing;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AstroCounter.Core.Models;
using SkiaSharp;

/// <summary>
/// High-precision Astronomical Photometry and Star Extractor (DAOStarFinder / SEP standard).
/// Performs 2D background modeling, local sigma-clipping, and point-spread-function peak detection.
/// Detection runs on a lightly smoothed (3x3 matched filter) luminance image, which suppresses
/// single-pixel sensor / JPEG noise that would otherwise be reported as thousands of false stars.
/// </summary>
public static class PhotometricStarFinder
{
    private const int GridSize = 64;

    public static List<AstrophotographyObject> DetectStars(
        SKBitmap bitmap,
        TilingConfig config,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;

        // 1. Extract luminance matrix from image and smooth it (noise suppression)
        float[] luminance = Smooth3x3(ExtractLuminance(bitmap), width, height);
        progress?.Report(0.15f);
        cancellationToken.ThrowIfCancellationRequested();

        // 2. 2D Background and Noise (sigma) modeling using local mesh grid
        int gridCols = (width + GridSize - 1) / GridSize;
        int gridRows = (height + GridSize - 1) / GridSize;

        float[] bgGrid = new float[gridCols * gridRows];
        float[] stdGrid = new float[gridCols * gridRows];

        var sampleBuffer = new float[GridSize * GridSize];

        for (int gy = 0; gy < gridRows; gy++)
        {
            int y0 = gy * GridSize;
            int y1 = Math.Min(height, y0 + GridSize);

            for (int gx = 0; gx < gridCols; gx++)
            {
                int x0 = gx * GridSize;
                int x1 = Math.Min(width, x0 + GridSize);

                int sampleCount = 0;
                for (int y = y0; y < y1; y++)
                {
                    int rowOffset = y * width;
                    for (int x = x0; x < x1; x++)
                    {
                        sampleBuffer[sampleCount++] = luminance[rowOffset + x];
                    }
                }

                // Compute median and std with sigma clipping
                (float median, float std) = ComputeSigmaClippedStats(sampleBuffer, sampleCount);
                int gridIdx = (gy * gridCols) + gx;
                bgGrid[gridIdx] = median;
                stdGrid[gridIdx] = Math.Max(0.75f, std);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        progress?.Report(0.35f);

        // 3. Peak Detection with 5x5 strict local maximum filter
        var candidates = new List<AstrophotographyObject>();
        int id = 1;
        int border = 4;
        float minConfidence = config?.MinConfidence ?? 0.02f;
        // 3.0 sigma maps to confidence 0.25 (i.e. 25%).
        // For confidence C < 0.25, SNR sigma = (C / 0.25) * 3.0.
        // We set a minimum sigma floor of 0.20 to prevent detecting flat noise.
        float minSigma = Math.Clamp((minConfidence / 0.25f) * 3.0f, 0.20f, 3.0f);

        for (int y = border; y < height - border; y++)
        {
            if (y % 100 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(0.35f + (0.50f * ((float)y / height)));
            }

            int gy = Math.Clamp(y / GridSize, 0, gridRows - 1);
            int rowOffset = y * width;

            for (int x = border; x < width - border; x++)
            {
                int gx = Math.Clamp(x / GridSize, 0, gridCols - 1);
                int gridIdx = (gy * gridCols) + gx;

                float localBg = bgGrid[gridIdx];
                float localStd = stdGrid[gridIdx];
                float threshold = localBg + (minSigma * localStd);

                float centerVal = luminance[rowOffset + x];
                if (centerVal <= threshold) continue;

                // Check 5x5 neighborhood strict local peak
                bool isPeak = true;
                for (int dy = -2; dy <= 2 && isPeak; dy++)
                {
                    int nRow = (y + dy) * width;
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        float neighborVal = luminance[nRow + (x + dx)];
                        // Must be greater than or equal, and break ties strictly
                        if (neighborVal > centerVal || (neighborVal == centerVal && (dy < 0 || (dy == 0 && dx < 0))))
                        {
                            isPeak = false;
                            break;
                        }
                    }
                }

                if (!isPeak) continue;

                // Compute precise Signal-to-Noise Ratio (SNR)
                float netPeak = centerVal - localBg;
                float snr = netPeak / localStd;

                // Astronomical Confidence score mapping:
                // SNR >= 10 sigma -> Tier 1 (c >= 0.80)
                // 5 - 10 sigma    -> Tier 2 (0.50 <= c < 0.80)
                // 3 - 5 sigma     -> Tier 3 (0.25 <= c < 0.50)
                // < 3 sigma       -> Noise (< 0.25)
                float confidence;
                if (snr >= 10.0f)
                {
                    confidence = Math.Clamp(0.80f + (((snr - 10.0f) / 10.0f) * 0.20f), 0.80f, 1.0f);
                }
                else if (snr >= 5.0f)
                {
                    confidence = 0.50f + (((snr - 5.0f) / 5.0f) * 0.30f);
                }
                else if (snr >= 3.0f)
                {
                    confidence = 0.25f + (((snr - 3.0f) / 2.0f) * 0.25f);
                }
                else
                {
                    confidence = Math.Max(0.01f, (snr / 3.0f) * 0.25f);
                }

                if (confidence < minConfidence) continue;

                // Estimate Point Spread Function (PSF) size / star diameter
                float halfMax = localBg + (netPeak / 2.0f);
                float radius = MeasureFwhmRadius(luminance, width, height, x, y, halfMax);
                float boxSize = Math.Clamp(radius * 2.4f, 6.0f, 48.0f);

                float boxX = Math.Max(0, x - (boxSize / 2.0f));
                float boxY = Math.Max(0, y - (boxSize / 2.0f));

                string classification;
                if (boxSize > 30.0f && snr < 12.0f)
                {
                    classification = "galaxy";
                }
                else if (confidence >= 0.80f)
                {
                    classification = "bright_star";
                }
                else if (confidence >= 0.50f)
                {
                    classification = "star";
                }
                else
                {
                    classification = "faint_star";
                }

                candidates.Add(new AstrophotographyObject(
                    Id: id++,
                    Box: new BoundingBox(boxX, boxY, boxSize, boxSize),
                    Confidence: confidence,
                    ClassName: classification,
                    TileOffsetX: 0,
                    TileOffsetY: 0
                ));
            }
        }

        progress?.Report(0.90f);

        // 4. Apply Non-Maximum Suppression to merge any close peak artifacts
        var finalStars = NmsFilter.ApplyNms(candidates, iouThreshold: 0.35f);

        progress?.Report(1.0f);
        return finalStars;
    }

    /// <summary>
    /// Converts the bitmap to a luminance (0..255) matrix. Handles BGRA, RGBA and grayscale layouts.
    /// </summary>
    private static unsafe float[] ExtractLuminance(SKBitmap bitmap)
    {
        SKBitmap? converted = null;
        var source = bitmap;
        var colorType = bitmap.ColorType;

        if (colorType != SKColorType.Bgra8888 && colorType != SKColorType.Rgba8888 && colorType != SKColorType.Gray8)
        {
            converted = bitmap.Copy(SKColorType.Bgra8888)
                        ?? throw new InvalidOperationException("Görsel piksel formatı dönüştürülemedi.");
            source = converted;
            colorType = SKColorType.Bgra8888;
        }

        try
        {
            int w = source.Width;
            int h = source.Height;
            var result = new float[w * h];

            byte* basePtr = (byte*)source.GetPixels().ToPointer();
            int bytesPerPixel = source.BytesPerPixel;
            int rowBytes = source.RowBytes;

            int redIdx = colorType == SKColorType.Rgba8888 ? 0 : 2;
            int blueIdx = colorType == SKColorType.Rgba8888 ? 2 : 0;
            bool gray = colorType == SKColorType.Gray8;

            for (int y = 0; y < h; y++)
            {
                byte* row = basePtr + ((long)y * rowBytes);
                int rowOffset = y * w;
                for (int x = 0; x < w; x++)
                {
                    byte* p = row + (x * bytesPerPixel);
                    result[rowOffset + x] = gray
                        ? p[0]
                        : (p[redIdx] * 0.299f) + (p[1] * 0.587f) + (p[blueIdx] * 0.114f);
                }
            }

            return result;
        }
        finally
        {
            converted?.Dispose();
        }
    }

    /// <summary>Separable [1 2 1]/4 smoothing with edge clamping.</summary>
    private static float[] Smooth3x3(float[] src, int w, int h)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];

        Parallel.For(0, h, y =>
        {
            int o = y * w;
            for (int x = 0; x < w; x++)
            {
                int l = x > 0 ? x - 1 : x;
                int r = x < w - 1 ? x + 1 : x;
                tmp[o + x] = (src[o + l] + (2f * src[o + x]) + src[o + r]) * 0.25f;
            }
        });

        Parallel.For(0, h, y =>
        {
            int up = Math.Max(0, y - 1) * w;
            int mid = y * w;
            int dn = Math.Min(h - 1, y + 1) * w;
            for (int x = 0; x < w; x++)
            {
                dst[mid + x] = (tmp[up + x] + (2f * tmp[mid + x]) + tmp[dn + x]) * 0.25f;
            }
        });

        return dst;
    }

    private static (float median, float std) ComputeSigmaClippedStats(float[] buffer, int count)
    {
        if (count == 0) return (0f, 1f);

        Array.Sort(buffer, 0, count);
        float median = buffer[count / 2];

        // Standard deviation around median
        float sumSq = 0f;
        for (int i = 0; i < count; i++)
        {
            float diff = buffer[i] - median;
            sumSq += diff * diff;
        }
        float std = (float)Math.Sqrt(sumSq / count);

        // One-iteration sigma clip at 2.5 sigma
        float lower = median - (2.5f * std);
        float upper = median + (2.5f * std);

        float clippedSum = 0f;
        float clippedSumSq = 0f;
        int clippedCount = 0;

        for (int i = 0; i < count; i++)
        {
            float v = buffer[i];
            if (v >= lower && v <= upper)
            {
                clippedSum += v;
                clippedSumSq += v * v;
                clippedCount++;
            }
        }

        if (clippedCount > 0)
        {
            float mean = clippedSum / clippedCount;
            float var = (clippedSumSq / clippedCount) - (mean * mean);
            return (mean, (float)Math.Sqrt(Math.Max(0.25f, var)));
        }

        return (median, Math.Max(0.5f, std));
    }

    private static float MeasureFwhmRadius(float[] lum, int width, int height, int cx, int cy, float halfMax)
    {
        // Measure horizontal and vertical extent where luminance drops below halfMax
        int rx = 1;
        while (cx + rx < width && lum[(cy * width) + cx + rx] > halfMax && rx < 24) rx++;
        int lx = 1;
        while (cx - lx >= 0 && lum[(cy * width) + cx - lx] > halfMax && lx < 24) lx++;

        int dy = 1;
        while (cy + dy < height && lum[((cy + dy) * width) + cx] > halfMax && dy < 24) dy++;
        int uy = 1;
        while (cy - uy >= 0 && lum[((cy - uy) * width) + cx] > halfMax && uy < 24) uy++;

        float avgRadius = (rx + lx + dy + uy) / 4.0f;
        return Math.Max(2.5f, avgRadius);
    }
}
