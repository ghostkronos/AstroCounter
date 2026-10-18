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
                stdGrid[gridIdx] = Math.Max(0.5f, std);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        // Clamp any outlier local noise to avoid distortion from dense clusters or nebulosity
        var allStds = (float[])stdGrid.Clone();
        Array.Sort(allStds);
        float globalMedianStd = allStds[allStds.Length / 2];
        float maxAllowedStd = Math.Max(4.0f, globalMedianStd * 2.8f);
        for (int i = 0; i < stdGrid.Length; i++)
        {
            stdGrid[i] = Math.Clamp(stdGrid[i], 0.5f, maxAllowedStd);
        }

        progress?.Report(0.35f);

        // Smooth bilinear interpolation helper for local background and noise
        (float localBg, float localStd) GetLocalBackgroundAndNoise(int px, int py)
        {
            float fy = ((py + 0.5f) / GridSize) - 0.5f;
            int gy0 = Math.Clamp((int)MathF.Floor(fy), 0, gridRows - 1);
            int gy1 = Math.Clamp(gy0 + 1, 0, gridRows - 1);
            float wy = Math.Clamp(fy - gy0, 0f, 1f);

            float fx = ((px + 0.5f) / GridSize) - 0.5f;
            int gx0 = Math.Clamp((int)MathF.Floor(fx), 0, gridCols - 1);
            int gx1 = Math.Clamp(gx0 + 1, 0, gridCols - 1);
            float wx = Math.Clamp(fx - gx0, 0f, 1f);

            int i00 = (gy0 * gridCols) + gx0;
            int i10 = (gy0 * gridCols) + gx1;
            int i01 = (gy1 * gridCols) + gx0;
            int i11 = (gy1 * gridCols) + gx1;

            float w00 = (1f - wx) * (1f - wy);
            float w10 = wx * (1f - wy);
            float w01 = (1f - wx) * wy;
            float w11 = wx * wy;

            float bg = (w00 * bgGrid[i00]) + (w10 * bgGrid[i10]) + (w01 * bgGrid[i01]) + (w11 * bgGrid[i11]);
            float std = (w00 * stdGrid[i00]) + (w10 * stdGrid[i10]) + (w01 * stdGrid[i01]) + (w11 * stdGrid[i11]);
            return (bg, Math.Max(0.5f, std));
        }

        // 3. Peak Detection with 5x5 strict local maximum filter
        var candidates = new List<AstrophotographyObject>();
        int id = 1;
        int border = 4;
        float minConfidence = config?.MinConfidence ?? 0.02f;
        float minSigma = Math.Clamp((minConfidence / 0.25f) * 3.0f, 0.20f, 3.0f);

        for (int y = border; y < height - border; y++)
        {
            if (y % 100 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(0.35f + (0.50f * ((float)y / height)));
            }

            int rowOffset = y * width;

            for (int x = border; x < width - border; x++)
            {
                var (localBg, localStd) = GetLocalBackgroundAndNoise(x, y);
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
                // Tier 1 High (Green, >= 0.80): Prominent / Bright stars (SNR >= 10 sigma OR netPeak >= 90)
                // Scales smoothly from 0.80 up to 1.00 based on both SNR and peak star brightness
                // Tier 2 Medium (Yellow, 0.50 - 0.79): Medium stars (SNR 5-10 sigma)
                // Tier 3 Low (Orange, 0.25 - 0.49): Faint stars (SNR 3-5 sigma)
                // Filtered Noise (Red, < 0.25): Noise (< 3 sigma)
                float confidence;
                if (snr >= 10.0f || netPeak >= 90.0f)
                {
                    float snrScore = Math.Clamp((snr - 10.0f) / 60.0f, 0f, 1f);
                    float peakScore = Math.Clamp(netPeak / 235.0f, 0f, 1f);
                    confidence = Math.Clamp(0.80f + (0.10f * snrScore) + (0.10f * peakScore), 0.80f, 1.0f);
                }
                else if (snr >= 5.0f)
                {
                    confidence = 0.50f + (((snr - 5.0f) / 5.0f) * 0.29f);
                }
                else if (snr >= 3.0f)
                {
                    confidence = 0.25f + (((snr - 3.0f) / 2.0f) * 0.24f);
                }
                else
                {
                    confidence = Math.Max(0.01f, (snr / 3.0f) * 0.24f);
                }

                if (confidence < minConfidence) continue;

                // Centroid refinement for saturated or bright stars
                float starCenterX = x;
                float starCenterY = y;
                if (centerVal >= 235f)
                {
                    float sumW = 0f, sumX = 0f, sumY = 0f;
                    int rCentroid = 4;
                    for (int cdy = -rCentroid; cdy <= rCentroid; cdy++)
                    {
                        int cy = y + cdy;
                        if (cy < 0 || cy >= height) continue;
                        int crow = cy * width;
                        for (int cdx = -rCentroid; cdx <= rCentroid; cdx++)
                        {
                            int cx = x + cdx;
                            if (cx < 0 || cx >= width) continue;
                            float wgt = Math.Max(0f, luminance[crow + cx] - localBg);
                            sumW += wgt;
                            sumX += cx * wgt;
                            sumY += cy * wgt;
                        }
                    }
                    if (sumW > 0)
                    {
                        starCenterX = sumX / sumW;
                        starCenterY = sumY / sumW;
                    }
                }

                // Estimate Point Spread Function (PSF) size / star diameter
                float halfMax = localBg + (netPeak / 2.0f);
                float radius = MeasureFwhmRadius(luminance, width, height, (int)Math.Round(starCenterX), (int)Math.Round(starCenterY), halfMax);
                float boxSize = Math.Clamp(radius * 2.4f, 6.0f, 54.0f);

                float boxX = Math.Max(0, starCenterX - (boxSize / 2.0f));
                float boxY = Math.Max(0, starCenterY - (boxSize / 2.0f));

                string classification;
                if (boxSize > 36.0f && netPeak < 45.0f && snr < 9.0f)
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

        // In astronomical images, stars only contribute POSITIVE flux above the sky background.
        // Therefore, pixels below the median (buffer[0..count/2]) are pure, uncontaminated sky noise.
        // For a Gaussian distribution, the variance of the lower half around the median is identically sigma^2.
        int halfCount = count / 2;
        if (halfCount > 0)
        {
            double lowerHalfSumSq = 0;
            for (int i = 0; i < halfCount; i++)
            {
                float diff = buffer[i] - median;
                lowerHalfSumSq += diff * diff;
            }
            float lowerHalfStd = (float)Math.Sqrt(lowerHalfSumSq / halfCount);

            // Also compute lower quartile IQR: P50 - P25 = 0.6745 * sigma -> sigma = IQR * 1.4826
            int q1Idx = count / 4;
            float iqrStd = (median - buffer[q1Idx]) * 1.4826f;

            // Use the robust lower-half estimator; this will NEVER be inflated by bright star flux
            float robustStd = Math.Max(0.5f, Math.Min(lowerHalfStd, iqrStd > 0.05f ? iqrStd : lowerHalfStd));
            return (median, robustStd);
        }

        return (median, 1.0f);
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
