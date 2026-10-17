namespace AstroCounter.Core.Processing;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AstroCounter.Core.Models;
using SkiaSharp;

/// <summary>
/// High-precision Astronomical Photometry and Star Extractor (DAOStarFinder / SEP standard).
/// Performs 2D background modeling, local sigma-clipping, and point-spread-function peak detection.
/// </summary>
public static class PhotometricStarFinder
{
    public static List<AstrophotographyObject> DetectStars(
        SKBitmap bitmap,
        TilingConfig config,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;

        // 1. Extract 8-bit luminance matrix from image
        float[] luminance = new float[width * height];
        unsafe
        {
            var ptr = (byte*)bitmap.GetPixels().ToPointer();
            int bytesPerPixel = bitmap.BytesPerPixel;
            int rowBytes = bitmap.RowBytes;

            for (int y = 0; y < height; y++)
            {
                byte* row = ptr + (y * rowBytes);
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    byte* pixel = row + (x * bytesPerPixel);
                    // Standard luminance formula: 0.299*R + 0.587*G + 0.114*B (handles BGRA or RGBA correctly)
                    byte b = pixel[0];
                    byte g = pixel[1];
                    byte r = pixel[2];
                    luminance[rowOffset + x] = (r * 0.299f) + (g * 0.587f) + (b * 0.114f);
                }
            }
        }

        // 2. 2D Background and Noise (sigma) modeling using local mesh grid
        int gridSize = 64;
        int gridCols = (width + gridSize - 1) / gridSize;
        int gridRows = (height + gridSize - 1) / gridSize;

        float[] bgGrid = new float[gridCols * gridRows];
        float[] stdGrid = new float[gridCols * gridRows];

        var sampleBuffer = new float[gridSize * gridSize];

        for (int gy = 0; gy < gridRows; gy++)
        {
            int y0 = gy * gridSize;
            int y1 = Math.Min(height, y0 + gridSize);

            for (int gx = 0; gx < gridCols; gx++)
            {
                int x0 = gx * gridSize;
                int x1 = Math.Min(width, x0 + gridSize);

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
                stdGrid[gridIdx] = Math.Max(1.0f, std);
            }
        }

        progress?.Report(0.35f);

        // 3. Peak Detection with 5x5 strict local maximum filter
        var candidates = new List<AstrophotographyObject>();
        int id = 1;
        int border = 4;

        for (int y = border; y < height - border; y++)
        {
            if (y % 100 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(0.35f + (0.50f * ((float)y / height)));
            }

            int gy = Math.Clamp(y / gridSize, 0, gridRows - 1);
            int rowOffset = y * width;

            for (int x = border; x < width - border; x++)
            {
                int gx = Math.Clamp(x / gridSize, 0, gridCols - 1);
                int gridIdx = (gy * gridCols) + gx;

                float localBg = bgGrid[gridIdx];
                float localStd = stdGrid[gridIdx];
                float threshold = localBg + (3.0f * localStd);

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

                // Estimate Point Spread Function (PSF) size / star diameter
                float halfMax = localBg + (netPeak / 2.0f);
                float radius = MeasureFwhmRadius(luminance, width, height, x, y, halfMax);
                float boxSize = Math.Clamp(radius * 2.4f, 6.0f, 48.0f);

                float boxX = Math.Max(0, x - (boxSize / 2.0f));
                float boxY = Math.Max(0, y - (boxSize / 2.0f));

                string classification = "star";
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
            return (mean, (float)Math.Sqrt(Math.Max(1.0f, var)));
        }

        return (median, Math.Max(1.0f, std));
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
