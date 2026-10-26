namespace AstroCounter.Core.Processing;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
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
    /// <summary>
    /// Fast star extraction optimized for plate solving and astrometric pattern matching.
    /// Downsamples massive multi-megapixel images to maxDimension (default 2048px) so star centroids
    /// are extracted in < 200ms without freezing the CPU or blowing up LOH memory.
    /// Star coordinates are automatically scaled back to exact full sensor resolution.
    /// </summary>
    public static List<AstrophotographyObject> DetectStarsFast(
        SKBitmap bitmap,
        TilingConfig? config = null,
        int maxDimension = 2048,
        CancellationToken cancellationToken = default)
    {
        config ??= new TilingConfig(DefaultConfidenceThreshold: 0.20f, MinConfidence: 0.05f);

        if (bitmap.Width <= maxDimension && bitmap.Height <= maxDimension)
        {
            return DetectStars(bitmap, config, cancellationToken: cancellationToken);
        }

        float scale = (float)maxDimension / Math.Max(bitmap.Width, bitmap.Height);
        int newW = Math.Max(64, (int)Math.Round(bitmap.Width * scale));
        int newH = Math.Max(64, (int)Math.Round(bitmap.Height * scale));

        using var scaledBmp = bitmap.Resize(new SKImageInfo(newW, newH, bitmap.ColorType, bitmap.AlphaType), SKSamplingOptions.Default);
        if (scaledBmp == null)
        {
            return DetectStars(bitmap, config, cancellationToken: cancellationToken);
        }

        var scaledStars = DetectStars(scaledBmp, config, cancellationToken: cancellationToken);
        float invScale = 1.0f / scale;
        var rescaledList = new List<AstrophotographyObject>(scaledStars.Count);

        for (int i = 0; i < scaledStars.Count; i++)
        {
            var s = scaledStars[i];
            rescaledList.Add(s with
            {
                Box = new BoundingBox(
                    s.Box.X * invScale,
                    s.Box.Y * invScale,
                    s.Box.Width * invScale,
                    s.Box.Height * invScale)
            });
        }

        return rescaledList;
    }


    private const int GridSize = 64;

    public static List<AstrophotographyObject> DetectStars(
        SKBitmap bitmap,
        TilingConfig config,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Smart downscaling for ultra-high-resolution images (> 4096 px, e.g. 108 Megapixels).
        // Simulates sensor pixel binning to avoid allocating gigabytes of RAM while preserving SNR.
        if (bitmap.Width > 4096 || bitmap.Height > 4096)
        {
            float scale = 4096f / Math.Max(bitmap.Width, bitmap.Height);
            int newW = (int)Math.Round(bitmap.Width * scale);
            int newH = (int)Math.Round(bitmap.Height * scale);
            using var scaledBmp = bitmap.Resize(new SKImageInfo(newW, newH, bitmap.ColorType, bitmap.AlphaType), SKSamplingOptions.Default);
            if (scaledBmp != null)
            {
                var scaledStars = DetectStars(scaledBmp, config, progress, cancellationToken);
                float invScale = 1.0f / scale;
                var rescaledList = new List<AstrophotographyObject>(scaledStars.Count);
                foreach (var s in scaledStars)
                {
                    rescaledList.Add(s with
                    {
                        Box = new BoundingBox(
                            s.Box.X * invScale,
                            s.Box.Y * invScale,
                            s.Box.Width * invScale,
                            s.Box.Height * invScale)
                    });
                }
                return rescaledList;
            }
        }

        int width = bitmap.Width;
        int height = bitmap.Height;

        // 1. Extract raw luminance matrix from image and create smoothed version for noise-immune background modeling
        float[] rawLuminance = ExtractLuminance(bitmap);
        float[] smoothedLuminance = Smooth3x3(rawLuminance, width, height);
        progress?.Report(0.15f);
        cancellationToken.ThrowIfCancellationRequested();

        // 2. 2D Background and Noise (sigma) modeling using local mesh grid (Parallel SIMD accelerated)
        int gridCols = (width + GridSize - 1) / GridSize;
        int gridRows = (height + GridSize - 1) / GridSize;

        float[] bgGrid = new float[gridCols * gridRows];
        float[] stdGrid = new float[gridCols * gridRows];

        Parallel.For(0, gridRows,
            () => ArrayPool<float>.Shared.Rent(GridSize * GridSize),
            (gy, loopState, sampleBuffer) =>
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
                            sampleBuffer[sampleCount++] = rawLuminance[rowOffset + x];
                        }
                    }

                    // Compute median and std with sigma clipping
                    (float median, float std) = ComputeSigmaClippedStats(sampleBuffer, sampleCount);
                    int gridIdx = (gy * gridCols) + gx;
                    bgGrid[gridIdx] = median;
                    stdGrid[gridIdx] = Math.Max(1.0f, std);
                }
                return sampleBuffer;
            },
            sampleBuffer => ArrayPool<float>.Shared.Return(sampleBuffer));

        // Clamp any outlier local noise to avoid distortion from dense clusters or nebulosity
        var allStds = (float[])stdGrid.Clone();
        Array.Sort(allStds);
        float globalMedianStd = allStds[allStds.Length / 2];
        float maxAllowedStd = Math.Max(4.0f, globalMedianStd * 2.8f);
        for (int i = 0; i < stdGrid.Length; i++)
        {
            stdGrid[i] = Math.Clamp(stdGrid[i], 1.0f, maxAllowedStd);
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
            return (bg, Math.Max(1.0f, std));
        }

        // 3. Peak Detection with 3x3 DAOStarFinder-style PSF local maximum filter
        var candidates = new List<AstrophotographyObject>();
        int id = 1;
        int border = 4;
        float minConfidence = config?.MinConfidence ?? 0.02f;
        float scanSigma = minConfidence < 0.25f ? 1.8f : 3.5f;

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
                // Effective standard deviation of Gaussian noise in 3x3 smoothed domain
                float smStd = localStd / 2.67f;
                float threshold = localBg + Math.Max(2.5f, scanSigma * smStd);

                float smoothCenter = smoothedLuminance[rowOffset + x];
                if (smoothCenter <= threshold) continue;

                // 3x3 DAOStarFinder strict local maximum on smoothed luminance
                bool isPeak = true;
                for (int dy = -1; dy <= 1 && isPeak; dy++)
                {
                    int nRow = (y + dy) * width;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        float neighborVal = smoothedLuminance[nRow + (x + dx)];
                        if (neighborVal > smoothCenter || (neighborVal == smoothCenter && (dy < 0 || (dy == 0 && dx < 0))))
                        {
                            isPeak = false;
                            break;
                        }
                    }
                }

                if (!isPeak) continue;

                float rawCenter = rawLuminance[rowOffset + x];
                float netRaw = rawCenter - localBg;
                float netSmooth = smoothCenter - localBg;
                float netPeak = Math.Max(netRaw, netSmooth);

                // Optical PSF sharpness / Hot pixel rejection:
                // An optical star has an Airy disc PSF spreading signal to adjacent pixels (netSmooth >= 0.32 * netRaw).
                // A single isolated hot pixel or noise spike has netSmooth <= 0.25 * netRaw.
                if (netSmooth < 0.32f * netRaw && netRaw > 8.0f)
                {
                    continue; // Single pixel noise artifact / cosmic ray
                }

                // Astronomical Confidence & Tier classification
                float sigRatio = netSmooth / smStd;
                float confidence;

                if ((netPeak >= 50.0f && sigRatio >= 10.0f) || netPeak >= 100.0f)
                {
                    // Tier 1 High (Green, 0.80 - 1.00): Prominent / Bright stars
                    float snrScore = Math.Clamp((sigRatio - 10.0f) / 35.0f, 0f, 1f);
                    float peakScore = Math.Clamp((netPeak - 50.0f) / 150.0f, 0f, 1f);
                    confidence = Math.Clamp(0.80f + (0.10f * snrScore) + (0.10f * peakScore), 0.80f, 1.0f);
                }
                else if (netPeak >= 16.0f && sigRatio >= 4.5f)
                {
                    // Tier 2 Medium (Yellow, 0.50 - 0.79): Medium stars
                    float snrScore = Math.Clamp((sigRatio - 4.5f) / 5.5f, 0f, 1f);
                    float peakScore = Math.Clamp((netPeak - 16.0f) / 34.0f, 0f, 1f);
                    confidence = 0.50f + (0.15f * snrScore) + (0.14f * peakScore);
                }
                else if (sigRatio >= 2.5f)
                {
                    // Tier 3 Low (Orange, 0.25 - 0.49): Standard astronomical faint star detection (2.5 - 4.5 sigma)
                    float snrScore = Math.Clamp((sigRatio - 2.5f) / 2.0f, 0f, 1f);
                    confidence = 0.25f + (0.24f * snrScore);
                }
                else
                {
                    // Filtered Noise (Red, 0.05 - 0.24): Noise floor (< 2.5 sigma)
                    float snrScore = Math.Clamp(sigRatio / 2.5f, 0f, 1f);
                    confidence = 0.05f + (0.19f * snrScore);
                }

                if (confidence < minConfidence) continue;

                // Universal sub-pixel centroid refinement for ALL stars
                float starCenterX = x;
                float starCenterY = y;
                float sumW = 0f, sumX = 0f, sumY = 0f;
                int rCentroid = rawCenter >= 235f ? 4 : 2;
                for (int cdy = -rCentroid; cdy <= rCentroid; cdy++)
                {
                    int cy = y + cdy;
                    if (cy < 0 || cy >= height) continue;
                    int crow = cy * width;
                    for (int cdx = -rCentroid; cdx <= rCentroid; cdx++)
                    {
                        int cx = x + cdx;
                        if (cx < 0 || cx >= width) continue;
                        float wgt = Math.Max(0f, rawLuminance[crow + cx] - localBg);
                        sumW += wgt;
                        sumX += cx * wgt;
                        sumY += cy * wgt;
                    }
                }
                if (sumW > 0f)
                {
                    float refinedX = sumX / sumW;
                    float refinedY = sumY / sumW;
                    if (Math.Abs(refinedX - x) <= 1.8f && Math.Abs(refinedY - y) <= 1.8f)
                    {
                        starCenterX = refinedX;
                        starCenterY = refinedY;
                    }
                }

                // Estimate Point Spread Function (PSF) size / star diameter
                float halfMax = localBg + (netPeak / 2.0f);
                float radius = MeasureFwhmRadius(rawLuminance, width, height, (int)Math.Round(starCenterX), (int)Math.Round(starCenterY), halfMax);
                float boxSize = Math.Clamp(radius * 2.4f, 6.0f, 54.0f);

                float boxX = Math.Max(0, starCenterX - (boxSize / 2.0f));
                float boxY = Math.Max(0, starCenterY - (boxSize / 2.0f));

                string classification;
                if (boxSize > 36.0f && netPeak < 45.0f && sigRatio < 9.0f)
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
                    TileOffsetY: 0,
                    Snr: sigRatio
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

            Parallel.For(0, h, y =>
            {
                byte* row = basePtr + ((long)y * rowBytes);
                int rowOffset = y * w;
                if (gray)
                {
                    for (int x = 0; x < w; x++)
                    {
                        result[rowOffset + x] = row[x * bytesPerPixel];
                    }
                }
                else
                {
                    for (int x = 0; x < w; x++)
                    {
                        byte* p = row + (x * bytesPerPixel);
                        result[rowOffset + x] = (p[redIdx] * 0.299f) + (p[1] * 0.587f) + (p[blueIdx] * 0.114f);
                    }
                }
            });

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
