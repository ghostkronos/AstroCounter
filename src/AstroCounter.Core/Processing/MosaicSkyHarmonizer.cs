namespace AstroCounter.Core.Processing;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SkiaSharp;

/// <summary>
/// Summary metrics produced by <see cref="MosaicSkyHarmonizer.HarmonizeBitmaps"/>.
/// </summary>
public readonly record struct MosaicHarmonizationResult(
    int PanelsAnalyzed,
    int FlattenedPanels,
    int PedestalShiftedPanels,
    double TargetRefLum);

/// <summary>
/// Shared multi-panel sky-background equalizer used by both live mosaic viewers (2D &amp; 3D) and export pipelines.
/// Flattens bright twilight/light-pollution sky domes and aligns dark-sky pedestals across panels without clipping stars.
/// </summary>
public static class MosaicSkyHarmonizer
{
    public readonly record struct SkyMetrics(
        double Spread,
        double Med50Lum,
        double InteriorMed50Lum,
        (double Lum, byte R, byte G, byte B) DarkQuartile);

    /// <summary>
    /// Equalizes harsh sky-background exposure mismatches across a list of BGRA8888 panel bitmaps in place.
    /// Any panel replaced by auto-balance has its previous bitmap disposed and the updated instance stored at <paramref name="bitmaps"/>[i].
    /// </summary>
    public static unsafe MosaicHarmonizationResult HarmonizeBitmaps(IList<SKBitmap> bitmaps)
    {
        ArgumentNullException.ThrowIfNull(bitmaps);
        if (bitmaps.Count < 2)
        {
            return new MosaicHarmonizationResult(bitmaps.Count, 0, 0, 0.0);
        }

        var stats = new (bool Valid, double MedR, double MedG, double MedB, double MedLum)[bitmaps.Count];
        var darkLums = new List<double>(bitmaps.Count);
        int flattenedCount = 0;

        for (int p = 0; p < bitmaps.Count; p++)
        {
            var bmp = bitmaps[p];
            if (bmp == null || bmp.Width < 4 || bmp.Height < 4) continue;

            if (bmp.ColorType != SKColorType.Bgra8888)
            {
                var bgra = bmp.Copy(SKColorType.Bgra8888);
                bmp.Dispose();
                bmp = bgra;
                bitmaps[p] = bmp;
            }

            var metrics = SamplePanelSkyMetrics(bmp);

            // Skip completely uniform synthetic test patches
            if (metrics.Spread < 6.0) continue;

            // Flatten spatially-varying light-pollution or lens-vignetting bright domes in bright frames
            if (Math.Max(metrics.Med50Lum, metrics.InteriorMed50Lum) > 42.0)
            {
                var balanced = ImagePreprocessor.AutoBalanceBrightness(bmp, out var info);
                if (info.WasAdjusted && info.State == ImagePreprocessor.ExposureState.TooBright)
                {
                    if (balanced.ColorType != SKColorType.Bgra8888)
                    {
                        var bgra = balanced.Copy(SKColorType.Bgra8888);
                        balanced.Dispose();
                        balanced = bgra;
                    }

                    bmp.Dispose();
                    bmp = balanced;
                    bitmaps[p] = bmp;
                    flattenedCount++;
                    metrics = SamplePanelSkyMetrics(bmp);
                }
                else
                {
                    balanced.Dispose();
                    if (metrics.InteriorMed50Lum > 42.0)
                    {
                        FlattenRadialSkyDome(bmp, targetFloor: 14.0);
                        flattenedCount++;
                        metrics = SamplePanelSkyMetrics(bmp);
                    }
                }
            }

            if (metrics.Spread < 6.0) continue;

            var s25 = metrics.DarkQuartile;
            stats[p] = (true, s25.R, s25.G, s25.B, s25.Lum);
            darkLums.Add(s25.Lum);
        }

        if (darkLums.Count < 2)
        {
            return new MosaicHarmonizationResult(bitmaps.Count, flattenedCount, 0, 0.0);
        }

        darkLums.Sort();
        // Target deep-sky pedestal = lower quartile of valid mosaic panels (bounded safely between 5 and 38 DN)
        int targetIdx = Math.Min(darkLums.Count - 1, Math.Max(0, darkLums.Count / 4));
        double refLum = Math.Clamp(darkLums[targetIdx], 5.0, 38.0);

        double refR = 0, refG = 0, refB = 0;
        int refCount = 0;
        double medianLimit = darkLums[Math.Min(darkLums.Count - 1, darkLums.Count / 2)] + 4.0;
        for (int p = 0; p < bitmaps.Count; p++)
        {
            if (stats[p].Valid && stats[p].MedLum <= medianLimit)
            {
                refR += stats[p].MedR;
                refG += stats[p].MedG;
                refB += stats[p].MedB;
                refCount++;
            }
        }

        if (refCount > 0)
        {
            refR = Math.Clamp(refR / refCount, 3.0, 42.0);
            refG = Math.Clamp(refG / refCount, 4.0, 42.0);
            refB = Math.Clamp(refB / refCount, 8.0, 48.0);
        }
        else
        {
            refR = refLum * 0.75;
            refG = refLum * 0.90;
            refB = Math.Min(45.0, refLum * 1.25);
        }

        int shiftedCount = 0;
        for (int p = 0; p < bitmaps.Count; p++)
        {
            if (!stats[p].Valid) continue;
            if (stats[p].MedLum - refLum <= 16.0) continue; // Already consistent dark sky

            shiftedCount++;
            byte[] lutR = BuildPedestalLut(stats[p].MedR, refR);
            byte[] lutG = BuildPedestalLut(stats[p].MedG, refG);
            byte[] lutB = BuildPedestalLut(stats[p].MedB, refB);

            var bmp = bitmaps[p];
            int w = bmp.Width;
            int h = bmp.Height;
            int stride = bmp.RowBytes;
            byte* basePtr = (byte*)bmp.GetPixels();

            Parallel.For(0, h, y =>
            {
                byte* row = basePtr + (y * stride);
                for (int x = 0; x < w; x++)
                {
                    int o = x * 4;
                    row[o + 0] = lutB[row[o + 0]];
                    row[o + 1] = lutG[row[o + 1]];
                    row[o + 2] = lutR[row[o + 2]];
                }
            });
        }

        return new MosaicHarmonizationResult(bitmaps.Count, flattenedCount, shiftedCount, refLum);
    }

    public static unsafe SkyMetrics SamplePanelSkyMetrics(SKBitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        int stride = bmp.RowBytes;
        byte* basePtr = (byte*)bmp.GetPixels();

        const int gridX = 40;
        const int gridY = 28;
        var samples = new List<(double Lum, byte R, byte G, byte B)>(gridX * gridY);
        var interiorLums = new List<double>((gridX * gridY) / 2);

        for (int gy = 0; gy < gridY; gy++)
        {
            int sy = Math.Clamp((gy * h) / gridY + (h / (gridY * 2)), 0, h - 1);
            byte* row = basePtr + (sy * stride);
            bool inY = gy >= (gridY / 5) && gy < ((gridY * 4) / 5);

            for (int gx = 0; gx < gridX; gx++)
            {
                int sx = Math.Clamp((gx * w) / gridX + (w / (gridX * 2)), 0, w - 1);
                int o = sx * 4;
                byte b = row[o + 0];
                byte g = row[o + 1];
                byte r = row[o + 2];
                double lum = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
                samples.Add((lum, r, g, b));
                if (inY && gx >= (gridX / 5) && gx < ((gridX * 4) / 5))
                {
                    interiorLums.Add(lum);
                }
            }
        }

        samples.Sort((a, b) => a.Lum.CompareTo(b.Lum));
        interiorLums.Sort();

        int idx10 = samples.Count / 10;
        int idx90 = (samples.Count * 9) / 10;
        double spread = samples[idx90].Lum - samples[idx10].Lum;
        double med50Lum = samples[samples.Count / 2].Lum;
        double interiorMed50Lum = interiorLums.Count > 0 ? interiorLums[interiorLums.Count / 2] : med50Lum;
        var darkQuartile = samples[samples.Count / 4];

        return new SkyMetrics(spread, med50Lum, interiorMed50Lum, darkQuartile);
    }

    public static unsafe void FlattenRadialSkyDome(SKBitmap bmp, double targetFloor)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        if (w < 16 || h < 16) return;

        int stride = bmp.RowBytes;
        byte* basePtr = (byte*)bmp.GetPixels();

        const int cols = 8;
        const int rows = 6;
        var bgR = new double[rows * cols];
        var bgG = new double[rows * cols];
        var bgB = new double[rows * cols];

        // Estimate coarse 25th-percentile local sky floor per cell (captures smooth vignette/sky-glow dome)
        var cellSamples = new List<(double lum, byte r, byte g, byte b)>(256);
        for (int cy = 0; cy < rows; cy++)
        {
            int y0 = (cy * h) / rows;
            int y1 = ((cy + 1) * h) / rows;
            int stepY = Math.Max(1, (y1 - y0) / 12);

            for (int cx = 0; cx < cols; cx++)
            {
                int x0 = (cx * w) / cols;
                int x1 = ((cx + 1) * w) / cols;
                int stepX = Math.Max(1, (x1 - x0) / 12);

                cellSamples.Clear();
                for (int y = y0; y < y1; y += stepY)
                {
                    byte* rPtr = basePtr + (y * stride);
                    for (int x = x0; x < x1; x += stepX)
                    {
                        int o = x * 4;
                        byte b = rPtr[o + 0];
                        byte g = rPtr[o + 1];
                        byte r = rPtr[o + 2];
                        cellSamples.Add(((0.2126 * r) + (0.7152 * g) + (0.0722 * b), r, g, b));
                    }
                }

                if (cellSamples.Count == 0) continue;
                cellSamples.Sort((a, b) => a.lum.CompareTo(b.lum));
                var q = cellSamples[cellSamples.Count / 4];
                int idx = (cy * cols) + cx;
                bgR[idx] = Math.Max(0.0, q.r - targetFloor);
                bgG[idx] = Math.Max(0.0, q.g - (targetFloor * 1.05));
                bgB[idx] = Math.Max(0.0, q.b - (targetFloor * 1.25));
            }
        }

        Parallel.For(0, h, y =>
        {
            double gy = (((y + 0.5) * rows) / h) - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(gy), 0, rows - 1);
            int y1 = Math.Clamp(y0 + 1, 0, rows - 1);
            double fy = Math.Clamp(gy - y0, 0.0, 1.0);

            byte* row = basePtr + (y * stride);
            for (int x = 0; x < w; x++)
            {
                double gx = (((x + 0.5) * cols) / w) - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(gx), 0, cols - 1);
                int x1 = Math.Clamp(x0 + 1, 0, cols - 1);
                double fx = Math.Clamp(gx - x0, 0.0, 1.0);

                int i00 = (y0 * cols) + x0;
                int i10 = (y0 * cols) + x1;
                int i01 = (y1 * cols) + x0;
                int i11 = (y1 * cols) + x1;

                double subB = ((bgB[i00] * (1.0 - fx) + bgB[i10] * fx) * (1.0 - fy)) + ((bgB[i01] * (1.0 - fx) + bgB[i11] * fx) * fy);
                double subG = ((bgG[i00] * (1.0 - fx) + bgG[i10] * fx) * (1.0 - fy)) + ((bgG[i01] * (1.0 - fx) + bgG[i11] * fx) * fy);
                double subR = ((bgR[i00] * (1.0 - fx) + bgR[i10] * fx) * (1.0 - fy)) + ((bgR[i01] * (1.0 - fx) + bgR[i11] * fx) * fy);

                int o = x * 4;
                row[o + 0] = (byte)Math.Clamp((int)Math.Round(row[o + 0] - subB), 0, 255);
                row[o + 1] = (byte)Math.Clamp((int)Math.Round(row[o + 1] - subG), 0, 255);
                row[o + 2] = (byte)Math.Clamp((int)Math.Round(row[o + 2] - subR), 0, 255);
            }
        });
    }

    private static byte[] BuildPedestalLut(double srcMed, double dstRef)
    {
        var lut = new byte[256];
        double denom = Math.Max(16.0, 255.0 - srcMed);
        double scale = (255.0 - dstRef) / denom;
        for (int i = 0; i < 256; i++)
        {
            double mapped = dstRef + ((i - srcMed) * scale);
            if (i < srcMed)
            {
                double t = i / Math.Max(1.0, srcMed);
                mapped = t * dstRef;
            }
            lut[i] = (byte)Math.Clamp((int)Math.Round(mapped), 0, 255);
        }
        return lut;
    }
}
