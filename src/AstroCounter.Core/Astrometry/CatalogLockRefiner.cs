namespace AstroCounter.Core.Astrometry;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AstroCounter.Core.Processing;
using SkiaSharp;

/// <summary>
/// Dense "catalog lock" refinement of a plate solution.
///
/// The triangle/RANSAC solver only uses ~38 bright stars that are matched with a similarity
/// (rotation + scale + shift) model, so stars towards the lens edges are rejected as outliers and the
/// residual lens distortion there is never corrected. This refiner fixes that:
///
/// 1. Hundreds of catalog stars covering the whole field are projected into the image with the current WCS.
/// 2. Each star is centroided directly on the image pixels around its predicted position (catalog-guided
///    centroiding, sub-pixel, hot-pixel resistant, crowded stars skipped).
/// 3. Outliers are rejected against the local displacement field of their neighbours.
/// 4. A full-field Thin Plate Spline is fitted (measured pixel -> ideal catalog pixel), so the photo can be
///    stretched/bent without limit until every matched star sits exactly on its catalog position.
/// 5. Coarse-to-fine iterations: the improved model predicts the next round, so the search window shrinks
///    and corner stars (most distorted) get locked as well.
/// </summary>
public static class CatalogLockRefiner
{
    /// <summary>Summary of a refinement run.</summary>
    public sealed record RefineReport(
        int MatchedStars,
        double RmsBeforePixels,
        double RmsAfterPixels,
        double MaxCorrectionPixels,
        int Iterations);

    /// <summary>8-bit luminance raster (possibly downscaled) of the original image.</summary>
    public sealed class LuminanceImage
    {
        public LuminanceImage(byte[] data, int width, int height, double scale)
        {
            Data = data;
            Width = width;
            Height = height;
            Scale = scale;
        }

        /// <summary>Row-major luminance values.</summary>
        public byte[] Data { get; }
        public int Width { get; }
        public int Height { get; }

        /// <summary>Luminance pixel = original pixel * Scale.</summary>
        public double Scale { get; }

        public float this[int x, int y] => Data[(y * Width) + x];
    }

    private const int MaxLuminanceSide = 6144;
    private const int MaxControlPoints = 520;

    /// <summary>
    /// Loads the image from disk and refines the WCS. Returns the original WCS when refinement fails.
    /// </summary>
    public static WcsMatrix Refine(string imagePath, WcsMatrix wcs, out RefineReport? report, CancellationToken ct = default)
    {
        report = null;
        if (wcs == null || string.IsNullOrWhiteSpace(imagePath) || !System.IO.File.Exists(imagePath))
        {
            return wcs!;
        }

        try
        {
            using var bmp = ImagePreprocessor.LoadImage(imagePath);
            var lum = BuildLuminance(bmp);
            if (lum == null) return wcs;
            return Refine(lum, wcs, out report, ct);
        }
        catch
        {
            return wcs;
        }
    }

    /// <summary>
    /// Builds a (downscaled if huge) 8-bit luminance raster. Uses mip-mapped linear filtering so faint
    /// point sources survive downsampling.
    /// </summary>
    public static LuminanceImage? BuildLuminance(SKBitmap bmp)
    {
        if (bmp == null || bmp.Width < 16 || bmp.Height < 16) return null;

        double scale = Math.Min(1.0, (double)MaxLuminanceSide / Math.Max(bmp.Width, bmp.Height));
        int lw = Math.Max(16, (int)Math.Round(bmp.Width * scale));
        int lh = Math.Max(16, (int)Math.Round(bmp.Height * scale));
        scale = (double)lw / bmp.Width;

        SKBitmap work = bmp;
        bool disposeWork = false;
        if (lw != bmp.Width || lh != bmp.Height || (bmp.ColorType != SKColorType.Bgra8888 && bmp.ColorType != SKColorType.Rgba8888))
        {
            var resized = bmp.Resize(
                new SKImageInfo(lw, lh, SKColorType.Bgra8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            if (resized == null) return null;
            work = resized;
            disposeWork = true;
        }

        try
        {
            bool bgra = work.ColorType == SKColorType.Bgra8888;
            var span = work.GetPixelSpan();
            int rowBytes = work.RowBytes;
            var data = new byte[lw * lh];

            for (int y = 0; y < lh; y++)
            {
                int row = y * rowBytes;
                int o = y * lw;
                for (int x = 0; x < lw; x++)
                {
                    int i = row + (x * 4);
                    byte c0 = span[i];
                    byte c1 = span[i + 1];
                    byte c2 = span[i + 2];
                    byte r = bgra ? c2 : c0;
                    byte b = bgra ? c0 : c2;
                    data[o + x] = (byte)Math.Min(255, (int)((0.299 * r) + (0.587 * c1) + (0.114 * b) + 0.5));
                }
            }

            // Adaptive contrast stretch for dark astrophotography images
            long sumLum = 0;
            for (int i = 0; i < data.Length; i++) sumLum += data[i];
            double meanLum = (double)sumLum / data.Length;
            if (meanLum < 28.0)
            {
                double stretch = Math.Min(4.5, 32.0 / Math.Max(4.0, meanLum));
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = (byte)Math.Min(255, (int)(data[i] * stretch));
                }
            }

            return new LuminanceImage(data, lw, lh, scale);
        }
        finally
        {
            if (disposeWork) work.Dispose();
        }
    }

    private readonly record struct CatEntry(double RaDeg, double DecDeg, double Mag);

    private readonly record struct Pair(double Sx, double Sy, double Tx, double Ty, double Mag, double PredX, double PredY);

    /// <summary>
    /// Refines the WCS against the supplied luminance raster. Returns the original WCS when refinement fails.
    /// </summary>
    public static WcsMatrix Refine(LuminanceImage img, WcsMatrix wcs, out RefineReport? report, CancellationToken ct = default)
    {
        report = null;
        int w = wcs.ImageWidth;
        int h = wcs.ImageHeight;
        if (w <= 0 || h <= 0) return wcs;

        double diag = Math.Sqrt(((double)w * w) + ((double)h * h));

        // 1. Catalog stars covering the full field (brightest first)
        var center = wcs.PixelToEquatorial(w / 2.0, h / 2.0);
        double radiusDeg = Math.Min(89.0, (0.5 * diag * wcs.PixelScaleArcsec / 3600.0) * 1.08);
        var catalog = QueryCatalog(center.RaDegrees, center.DecDegrees, radiusDeg);
        if (catalog.Count < 4) return wcs;

        // Ideal model = linear TAN + radial polynomial, without the old (sparse) spline.
        var baseWcs = new WcsMatrix(
            wcs.CenterCoordinates,
            wcs.ReferencePixelX,
            wcs.ReferencePixelY,
            wcs.PixelScaleArcsec,
            wcs.RotationDegrees,
            w,
            h,
            wcs.RadialDistortionK1,
            wcs.RadialDistortionK2,
            isFlippedHorizontally: wcs.IsFlippedHorizontally);

        WcsMatrix current = wcs;
        ThinPlateSplineWarp? bestSpline = null;
        List<Pair>? bestPairs = null;
        double rmsBefore = double.NaN;
        double lastRms = double.NaN;
        int iterationsDone = 0;
        bool coarseRecoveryAttempted = false;

        // Coarse-to-fine schedule: (search radius as fraction of diagonal, number of catalog stars used)
        var schedule = new (double RadiusFrac, int StarCount)[]
        {
            (0.025, 200),
            (0.014, 450),
            (0.007, 950),
            (0.0035, 1500),
        };

        for (int iter = 0; iter < schedule.Length; iter++)
        {
            ct.ThrowIfCancellationRequested();

            double searchR = Math.Clamp(diag * schedule[iter].RadiusFrac, 7.0, 180.0);
            if (!double.IsNaN(lastRms))
            {
                // Never shrink below what the previous fit can actually guarantee
                searchR = Math.Max(Math.Min(searchR, Math.Max(6.0, lastRms * 5.0)), 4.0);
            }

            var pairs = MatchStars(img, current, baseWcs, catalog, schedule[iter].StarCount, searchR, w, h);
            pairs = RejectOutliers(pairs, diag);
            if (pairs.Count < 4)
            {
                if (iter == 0 && !coarseRecoveryAttempted)
                {
                    // Initial WCS produced insufficient matches (likely due to fallback 0° rotation,
                    // horizontal mirror flip, or significant initial translation). Attempt coarse recovery!
                    coarseRecoveryAttempted = true;
                    var recoveredWcs = CoarseAlign(img, wcs, catalog, w, h, diag);
                    if (!ReferenceEquals(recoveredWcs, wcs))
                    {
                        baseWcs = new WcsMatrix(
                            recoveredWcs.CenterCoordinates,
                            recoveredWcs.ReferencePixelX,
                            recoveredWcs.ReferencePixelY,
                            recoveredWcs.PixelScaleArcsec,
                            recoveredWcs.RotationDegrees,
                            w,
                            h,
                            recoveredWcs.RadialDistortionK1,
                            recoveredWcs.RadialDistortionK2,
                            isFlippedHorizontally: recoveredWcs.IsFlippedHorizontally);
                        current = baseWcs;
                        iter--; // Retry iteration 0 with recovered WCS
                        continue;
                    }
                    return wcs;
                }
                break;
            }

            if (iter == 0 && pairs.Count >= 6)
            {
                // Detect global rigid pointing translation between image centroids and initial WCS prediction
                double medShiftX = Median(pairs.Select(p => p.Sx - p.PredX).ToArray());
                double medShiftY = Median(pairs.Select(p => p.Sy - p.PredY).ToArray());
                if (Math.Abs(medShiftX) > 1.2 || Math.Abs(medShiftY) > 1.2)
                {
                    // Global translational offset detected: re-center baseWcs to eliminate telescope pointing error
                    baseWcs = baseWcs.WithPixelShift(medShiftX, medShiftY);
                    current = baseWcs;
                    pairs = MatchStars(img, current, baseWcs, catalog, schedule[iter].StarCount, searchR, w, h);
                    pairs = RejectOutliers(pairs, diag);
                    if (pairs.Count < 4) break;
                }
            }

            if (double.IsNaN(rmsBefore))
            {
                rmsBefore = Rms(pairs.Select(p => Hypot(p.Sx - p.PredX, p.Sy - p.PredY)));
            }

            // Continuous Radial Distortion LSQ Refit & Quadrant Check (Architect Directive 2)
            double refinedK1 = baseWcs.RadialDistortionK1;
            double refinedK2 = baseWcs.RadialDistortionK2;
            if (TryFitRadialDistortion(pairs, w, h, diag * 0.5, out double fittedK1, out double fittedK2))
            {
                refinedK1 = fittedK1;
                refinedK2 = fittedK2;
            }

            var control = ThinSpatially(pairs, w, h);
            var points = control
                .Select(p => new ThinPlateSplineWarp.ControlPoint(p.Sx, p.Sy, p.Tx, p.Ty))
                .ToList();
            AddBoundaryAnchors(points, control, w, h, diag, refinedK1, refinedK2);

            var spline = ThinPlateSplineWarp.Create(points, w, h, regularization: 1e-5, addCornerStabilizers: false);
            if (spline == null) break;

            // Residual of all accepted pairs (incl. those not used as control points = honest check)
            double rms = Rms(pairs.Select(p =>
            {
                var (ux, uy) = spline.Warp(p.Sx, p.Sy);
                return Hypot(ux - p.Tx, uy - p.Ty);
            }));

            if (!double.IsNaN(lastRms) && rms > lastRms * 1.25 && bestSpline != null)
            {
                break; // Diverging - keep the previous best
            }

            bestSpline = spline;
            bestPairs = pairs;
            lastRms = rms;
            iterationsDone = iter + 1;

            current = new WcsMatrix(
                baseWcs.CenterCoordinates,
                baseWcs.ReferencePixelX,
                baseWcs.ReferencePixelY,
                baseWcs.PixelScaleArcsec,
                baseWcs.RotationDegrees,
                w,
                h,
                refinedK1,
                refinedK2,
                spline,
                isFlippedHorizontally: baseWcs.IsFlippedHorizontally);
        }

        if (bestSpline == null || bestPairs == null) return wcs;

        // Accept only if it is genuinely better than the incoming solution
        if (!double.IsNaN(rmsBefore) && lastRms > rmsBefore)
        {
            return wcs;
        }

        double maxCorr = 0.0;
        foreach (var p in bestPairs)
        {
            maxCorr = Math.Max(maxCorr, Hypot(p.Tx - p.Sx, p.Ty - p.Sy));
        }

        report = new RefineReport(bestPairs.Count, rmsBefore, lastRms, maxCorr, iterationsDone);
        return current;
    }

    /// <summary>
    /// Performs fast coarse alignment checks (translation consensus, horizontal mirror flip,
    /// and fallback rotation angle scan) to ensure initial predictions are within capture range.
    /// </summary>
    private static WcsMatrix CoarseAlign(
        LuminanceImage img,
        WcsMatrix initialWcs,
        List<CatEntry> catalog,
        int w,
        int h,
        double diag)
    {
        double searchR = Math.Clamp(diag * 0.045, 24.0, 160.0);
        int testStarsCount = 28;

        // 1. Evaluate current initial WCS
        int currentMatches = CountAndDisplace(img, initialWcs, catalog, testStarsCount, searchR, w, h, out double medDx, out double medDy);

        WcsMatrix bestWcs = initialWcs;
        int bestCount = currentMatches;

        // If current alignment already has good matches (>= 6), just apply median displacement if significant
        if (currentMatches >= 6)
        {
            if (Math.Abs(medDx) > 2.0 || Math.Abs(medDy) > 2.0)
            {
                bestWcs = initialWcs.WithPixelShift(medDx, medDy);
            }
            return bestWcs;
        }

        // 2. Test horizontal mirror flip if matches are sparse
        var flippedWcs = initialWcs.WithFlip();
        int flippedMatches = CountAndDisplace(img, flippedWcs, catalog, testStarsCount, searchR, w, h, out double flipDx, out double flipDy);
        if (flippedMatches > bestCount && flippedMatches >= 6)
        {
            bestWcs = Math.Abs(flipDx) > 2.0 || Math.Abs(flipDy) > 2.0
                ? flippedWcs.WithPixelShift(flipDx, flipDy)
                : flippedWcs;
            bestCount = flippedMatches;
            if (bestCount >= 8) return bestWcs;
        }

        // 3. ONLY scan rotation angles if rotation was never solved (fallback mode with rotation == 0.0)
        // and matches are still sparse (< 6). Never overwrite a non-zero solved rotation angle!
        if (bestCount < 6 && Math.Abs(initialWcs.RotationDegrees) < 1e-4)
        {
            double[] candidateAngles = [0, 30, 60, 90, 120, 150, 180, 210, 240, 270, 300, 330];
            bool[] parities = [false, true];

            double bestScanAngle = initialWcs.RotationDegrees;
            bool bestScanFlip = initialWcs.IsFlippedHorizontally;
            double bestScanDx = 0, bestScanDy = 0;

            foreach (bool flip in parities)
            {
                foreach (double rot in candidateAngles)
                {
                    var cand = new WcsMatrix(
                        initialWcs.CenterCoordinates,
                        initialWcs.ReferencePixelX,
                        initialWcs.ReferencePixelY,
                        initialWcs.PixelScaleArcsec,
                        rot,
                        w, h,
                        initialWcs.RadialDistortionK1,
                        initialWcs.RadialDistortionK2,
                        isFlippedHorizontally: flip);

                    int count = CountAndDisplace(img, cand, catalog, 20, searchR, w, h, out double dx, out double dy);
                    if (count > bestCount)
                    {
                        bestCount = count;
                        bestScanAngle = rot;
                        bestScanFlip = flip;
                        bestScanDx = dx;
                        bestScanDy = dy;
                    }
                }
            }

            if (bestCount >= 5)
            {
                // Fine-scan around the best angle: +/- 15 deg in 3 deg steps
                for (double rotOffset = -15.0; rotOffset <= 15.0; rotOffset += 3.0)
                {
                    double testRot = (bestScanAngle + rotOffset + 360.0) % 360.0;
                    var cand = new WcsMatrix(
                        initialWcs.CenterCoordinates,
                        initialWcs.ReferencePixelX,
                        initialWcs.ReferencePixelY,
                        initialWcs.PixelScaleArcsec,
                        testRot,
                        w, h,
                        initialWcs.RadialDistortionK1,
                        initialWcs.RadialDistortionK2,
                        isFlippedHorizontally: bestScanFlip);

                    int count = CountAndDisplace(img, cand, catalog, 24, searchR * 0.8, w, h, out double dx, out double dy);
                    if (count > bestCount)
                    {
                        bestCount = count;
                        bestScanAngle = testRot;
                        bestScanDx = dx;
                        bestScanDy = dy;
                    }
                }

                bestWcs = new WcsMatrix(
                    initialWcs.CenterCoordinates,
                    initialWcs.ReferencePixelX,
                    initialWcs.ReferencePixelY,
                    initialWcs.PixelScaleArcsec,
                    bestScanAngle,
                    w, h,
                    initialWcs.RadialDistortionK1,
                    initialWcs.RadialDistortionK2,
                    isFlippedHorizontally: bestScanFlip);

                if (Math.Abs(bestScanDx) > 2.0 || Math.Abs(bestScanDy) > 2.0)
                {
                    bestWcs = bestWcs.WithPixelShift(bestScanDx, bestScanDy);
                }
            }
        }

        return bestWcs;
    }

    private static int CountAndDisplace(
        LuminanceImage img,
        WcsMatrix testWcs,
        List<CatEntry> catalog,
        int count,
        double searchR,
        int w,
        int h,
        out double medDx,
        out double medDy)
    {
        medDx = 0;
        medDy = 0;
        var dxs = new List<double>(count);
        var dys = new List<double>(count);

        int tested = 0;
        for (int i = 0; i < catalog.Count && tested < count; i++)
        {
            var c = catalog[i];
            var coord = new CelestialCoordinates(c.RaDeg, c.DecDeg);
            if (!testWcs.EquatorialToPixel(coord, out double px, out double py)) continue;
            if (px < 10 || px > w - 10 || py < 10 || py > h - 10) continue;
            tested++;

            if (MeasureCentroid(img, px, py, searchR, out double mx, out double my))
            {
                dxs.Add(mx - px);
                dys.Add(my - py);
            }
        }

        if (dxs.Count == 0) return 0;

        medDx = Median(dxs.ToArray());
        medDy = Median(dys.ToArray());
        return dxs.Count;
    }

    private static List<CatEntry> QueryCatalog(double ra, double dec, double radiusDeg)
    {
        var list = new List<CatEntry>(3000);
        try
        {
            foreach (var s in GaiaTychoCatalog.QueryRegion(ra, dec, radiusDeg, maxMag: 13.0, maxResults: 3000))
            {
                list.Add(new CatEntry(s.RaDeg, s.DecDeg, s.Mag));
            }
        }
        catch
        {
            // fall through to Hipparcos
        }

        if (list.Count < 50)
        {
            list.Clear();
            foreach (var s in HipparcosCatalog.QueryRegion(ra, dec, radiusDeg, maxMag: 12.5, maxResults: 3000))
            {
                list.Add(new CatEntry(s.RaDeg, s.DecDeg, s.Mag));
            }
        }

        list.Sort((a, b) => a.Mag.CompareTo(b.Mag));
        return list;
    }

    private static List<Pair> MatchStars(
        LuminanceImage img,
        WcsMatrix current,
        WcsMatrix baseWcs,
        List<CatEntry> catalog,
        int maxStars,
        double searchR,
        int w,
        int h)
    {
        // Project the whole catalog slice once (used for crowding checks)
        var projected = new List<(CatEntry Star, double Px, double Py, double Tx, double Ty)>(catalog.Count);
        double margin = 2.0;
        foreach (var c in catalog)
        {
            var coord = new CelestialCoordinates(c.RaDeg, c.DecDeg);
            if (!current.EquatorialToPixel(coord, out double px, out double py)) continue;
            if (px < -searchR || py < -searchR || px > w + searchR || py > h + searchR) continue;
            if (!baseWcs.EquatorialToPixel(coord, out double tx, out double ty)) continue;
            projected.Add((c, px, py, tx, ty));
        }

        // Spatial hash for crowding checks
        double cell = Math.Max(8.0, searchR * 1.6);
        var grid = new Dictionary<long, List<int>>();
        for (int i = 0; i < projected.Count; i++)
        {
            long key = Key(projected[i].Px, projected[i].Py, cell);
            if (!grid.TryGetValue(key, out var l))
            {
                l = new List<int>(4);
                grid[key] = l;
            }
            l.Add(i);
        }

        var pairs = new List<Pair>(Math.Min(maxStars, projected.Count));
        int used = 0;

        for (int i = 0; i < projected.Count && used < maxStars; i++)
        {
            var e = projected[i];
            if (e.Px < margin || e.Py < margin || e.Px > w - margin || e.Py > h - margin) continue;
            used++;

            // Crowding: skip if another catalog star of comparable brightness lies inside the search window
            if (IsCrowded(projected, grid, i, cell, searchR * 1.5, 2.2)) continue;

            if (MeasureCentroid(img, e.Px, e.Py, searchR, out double mx, out double my))
            {
                pairs.Add(new Pair(mx, my, e.Tx, e.Ty, e.Star.Mag, e.Px, e.Py));
            }
        }

        return pairs;
    }

    private static bool IsCrowded(
        List<(CatEntry Star, double Px, double Py, double Tx, double Ty)> projected,
        Dictionary<long, List<int>> grid,
        int index,
        double cell,
        double radius,
        double magTolerance)
    {
        var e = projected[index];
        long cx = (long)Math.Floor(e.Px / cell);
        long cy = (long)Math.Floor(e.Py / cell);
        double r2 = radius * radius;

        for (long gy = cy - 1; gy <= cy + 1; gy++)
        {
            for (long gx = cx - 1; gx <= cx + 1; gx++)
            {
                if (!grid.TryGetValue((gy << 32) ^ (gx & 0xFFFFFFFFL), out var list)) continue;
                foreach (int j in list)
                {
                    if (j == index) continue;
                    var o = projected[j];
                    double dx = o.Px - e.Px;
                    double dy = o.Py - e.Py;
                    if ((dx * dx) + (dy * dy) <= r2 && o.Star.Mag <= e.Star.Mag + magTolerance)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static long Key(double x, double y, double cell)
    {
        long gx = (long)Math.Floor(x / cell);
        long gy = (long)Math.Floor(y / cell);
        return (gy << 32) ^ (gx & 0xFFFFFFFFL);
    }

    /// <summary>
    /// Catalog-guided centroiding: finds the brightest (3x3-smoothed, hot-pixel resistant) source inside the
    /// search window around the predicted position and computes an intensity-weighted sub-pixel centroid.
    /// Coordinates in/out are original-image pixels.
    /// </summary>
    public static bool MeasureCentroid(LuminanceImage img, double px, double py, double searchR, out double mx, out double my)
    {
        mx = my = 0;
        double s = img.Scale;
        double lx = px * s;
        double ly = py * s;
        int r = Math.Max(3, (int)Math.Ceiling(searchR * s));

        int x0 = Math.Max(0, (int)Math.Floor(lx) - r);
        int y0 = Math.Max(0, (int)Math.Floor(ly) - r);
        int x1 = Math.Min(img.Width - 1, (int)Math.Floor(lx) + r);
        int y1 = Math.Min(img.Height - 1, (int)Math.Floor(ly) + r);
        if (x1 - x0 < 4 || y1 - y0 < 4) return false;

        // Local background + noise from the window border ring
        double sum = 0, sumSq = 0;
        int n = 0;
        for (int x = x0; x <= x1; x++)
        {
            Acc(img[x, y0]); Acc(img[x, y1]);
        }
        for (int y = y0 + 1; y < y1; y++)
        {
            Acc(img[x0, y]); Acc(img[x1, y]);
        }

        void Acc(float v)
        {
            sum += v;
            sumSq += v * v;
            n++;
        }

        double bg = sum / n;
        double noise = Math.Sqrt(Math.Max(0.0, (sumSq / n) - (bg * bg)));
        noise = Math.Max(noise, 1.2);

        // Peak search on 3x3 box sums (single hot pixels cannot win against real PSFs)
        double best = double.MinValue;
        int bx = -1, by = -1;
        for (int y = Math.Max(1, y0 + 1); y < Math.Min(img.Height - 1, y1); y++)
        {
            for (int x = Math.Max(1, x0 + 1); x < Math.Min(img.Width - 1, x1); x++)
            {
                double v = img[x - 1, y - 1] + img[x, y - 1] + img[x + 1, y - 1]
                         + img[x - 1, y] + img[x, y] + img[x + 1, y]
                         + img[x - 1, y + 1] + img[x, y + 1] + img[x + 1, y + 1];
                if (v > best)
                {
                    best = v;
                    bx = x;
                    by = y;
                }
            }
        }

        if (bx < 0) return false;

        double peak = (best / 9.0) - bg;
        if (peak < Math.Max(4.0 * noise, 3.5)) return false;

        // Peak touching the window edge = probably the halo of a neighbouring star (only if not image edge)
        if ((x0 > 0 && bx - x0 < 2) || (x1 < img.Width - 1 && x1 - bx < 2) ||
            (y0 > 0 && by - y0 < 2) || (y1 < img.Height - 1 && y1 - by < 2)) return false;

        // Adaptive centroid box: grow until the ring falls below half maximum (handles saturated stars)
        double half = bg + (0.5 * peak);
        int hs = 2;
        for (; hs < 12; hs++)
        {
            if (bx - hs - 1 < 0 || by - hs - 1 < 0 || bx + hs + 1 >= img.Width || by + hs + 1 >= img.Height) break;
            double ring = 0;
            int rc = 0;
            for (int k = -hs; k <= hs; k++)
            {
                ring += img[bx + k, by - hs] + img[bx + k, by + hs] + img[bx - hs, by + k] + img[bx + hs, by + k];
                rc += 4;
            }
            if ((ring / rc) < half) break;
        }

        double thr = bg + (0.25 * peak);
        double sw = 0, swx = 0, swy = 0;
        for (int y = by - hs; y <= by + hs; y++)
        {
            for (int x = bx - hs; x <= bx + hs; x++)
            {
                if (x < 0 || y < 0 || x >= img.Width || y >= img.Height) continue;
                double v = img[x, y] - thr;
                if (v <= 0) continue;
                sw += v;
                swx += v * (x + 0.5);
                swy += v * (y + 0.5);
            }
        }

        if (sw <= 0) return false;

        mx = (swx / sw) / s;
        my = (swy / sw) / s;
        return true;
    }

    /// <summary>
    /// Rejects mismatches by comparing each pair's displacement with the median displacement of its
    /// spatial neighbours (lens distortion is smooth; a wrong match is not).
    /// </summary>
    private static List<Pair> RejectOutliers(List<Pair> pairs, double diag)
    {
        if (pairs.Count < 8) return pairs;

        int n = pairs.Count;
        int k = Math.Min(10, n - 1);
        var dev = new double[n];
        var dxs = new double[k];
        var dys = new double[k];
        var dist = new (double D, int J)[n];

        for (int i = 0; i < n; i++)
        {
            var p = pairs[i];
            for (int j = 0; j < n; j++)
            {
                var q = pairs[j];
                double ddx = q.Sx - p.Sx;
                double ddy = q.Sy - p.Sy;
                dist[j] = (j == i ? double.MaxValue : (ddx * ddx) + (ddy * ddy), j);
            }
            Array.Sort(dist, (a, b) => a.D.CompareTo(b.D));

            for (int m = 0; m < k; m++)
            {
                var q = pairs[dist[m].J];
                dxs[m] = q.Tx - q.Sx;
                dys[m] = q.Ty - q.Sy;
            }

            double medX = Median(dxs);
            double medY = Median(dys);
            dev[i] = Hypot((p.Tx - p.Sx) - medX, (p.Ty - p.Sy) - medY);
        }

        double medDev = Median((double[])dev.Clone());
        double limit = Math.Max(Math.Max(2.0, diag * 0.0006), 3.5 * 1.4826 * medDev);

        var kept = new List<Pair>(n);
        for (int i = 0; i < n; i++)
        {
            if (dev[i] <= limit) kept.Add(pairs[i]);
        }

        return kept;
    }

    /// <summary>
    /// Keeps the brightest stars per grid cell so the spline gets uniform full-field coverage
    /// (and the linear solve stays fast).
    /// </summary>
    private static List<Pair> ThinSpatially(List<Pair> pairs, int w, int h)
    {
        if (pairs.Count <= MaxControlPoints) return pairs;

        int cols = 22;
        int rows = Math.Max(8, (int)Math.Round(cols * (double)h / w));
        int perCell = Math.Max(1, (int)Math.Ceiling((double)MaxControlPoints / (cols * rows)));
        var counts = new int[cols * rows];
        var result = new List<Pair>(MaxControlPoints);

        foreach (var p in pairs.OrderBy(p => p.Mag))
        {
            int cx = Math.Clamp((int)(p.Sx / w * cols), 0, cols - 1);
            int cy = Math.Clamp((int)(p.Sy / h * rows), 0, rows - 1);
            int idx = (cy * cols) + cx;
            if (counts[idx] >= perCell) continue;
            counts[idx]++;
            result.Add(p);
            if (result.Count >= MaxControlPoints) break;
        }

        return result;
    }

    /// <summary>
    /// Fits optical radial lens distortion (k1, k2) via Least Squares if balanced star distribution exists.
    /// Implements Architect Directive 2 (Risk B: Quadrant Check to prevent overfitting in sparse fields).
    /// </summary>
    private static bool TryFitRadialDistortion(List<Pair> pairs, int w, int h, double normRadius, out double k1, out double k2)
    {
        k1 = 0; k2 = 0;
        double cx = w * 0.5;
        double cy = h * 0.5;

        int q1 = 0, q2 = 0, q3 = 0, q4 = 0;
        foreach (var p in pairs)
        {
            if (p.Sx < cx)
            {
                if (p.Sy < cy) q1++; else q3++;
            }
            else
            {
                if (p.Sy < cy) q2++; else q4++;
            }
        }

        // Strict Quadrant Rule (Architect Directive 2):
        // Must have >= 20 total pairs AND >= 3 pairs in every single quadrant to prevent edge extrapolation blowup
        if (pairs.Count < 20 || q1 < 3 || q2 < 3 || q3 < 3 || q4 < 3)
        {
            return false;
        }

        double s11 = 0.0, s12 = 0.0, s22 = 0.0;
        double v1 = 0.0, v2 = 0.0;
        int validCount = 0;

        foreach (var p in pairs)
        {
            double dx = p.Sx - cx;
            double dy = p.Sy - cy;
            double rd = Math.Sqrt((dx * dx) + (dy * dy)) / normRadius;
            if (rd < 0.08) continue; // Skip near-center points where derivative is negligible

            double tx = p.Tx - cx;
            double ty = p.Ty - cy;
            double ru = Math.Sqrt((tx * tx) + (ty * ty)) / normRadius;
            double deltaR = ru - rd;
            double rd3 = rd * rd * rd;
            double rd5 = rd3 * rd * rd;

            s11 += rd3 * rd3;
            s12 += rd3 * rd5;
            s22 += rd5 * rd5;
            v1 += deltaR * rd3;
            v2 += deltaR * rd5;
            validCount++;
        }

        if (validCount < 12) return false;

        double det = (s11 * s22) - (s12 * s12);
        if (Math.Abs(det) > 1e-9)
        {
            k1 = Math.Clamp(((v1 * s22) - (v2 * s12)) / det, -0.25, 0.25);
            k2 = Math.Clamp(((s11 * v2) - (s12 * v1)) / det, -0.15, 0.15);
            return true;
        }
        else if (s11 > 1e-6)
        {
            k1 = Math.Clamp(v1 / s11, -0.20, 0.20);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Adds boundary anchors (corners and edge midpoints) using analytical radial lens distortion (r^3, r^5)
    /// combined with local residual smoothing, eliminating corner flattening and residual drift (Architect Directive 2).
    /// </summary>
    private static void AddBoundaryAnchors(List<ThinPlateSplineWarp.ControlPoint> points, List<Pair> pairs, int w, int h, double diag, double k1, double k2)
    {
        var anchors = new (double X, double Y)[]
        {
            (0, 0), (w / 2.0, 0), (w, 0),
            (0, h / 2.0), (w, h / 2.0),
            (0, h), (w / 2.0, h), (w, h),
        };

        double cx = w * 0.5;
        double cy = h * 0.5;
        double normRadius = Math.Max(1.0, 0.5 * diag);
        double emptyR = diag * 0.08;

        foreach (var (ax, ay) in anchors)
        {
            var nearest = pairs
                .Select(p => (P: p, D: Hypot(p.Sx - ax, p.Sy - ay)))
                .OrderBy(t => t.D)
                .Take(6)
                .ToList();

            if (nearest.Count == 0 || nearest[0].D < emptyR) continue;

            double sw = 0, sResDx = 0, sResDy = 0;
            foreach (var (p, d) in nearest)
            {
                double wt = 1.0 / Math.Max(1.0, d * d);
                sw += wt;
                sResDx += wt * (p.Tx - p.Sx);
                sResDy += wt * (p.Ty - p.Sy);
            }
            double localDx = sw > 0 ? sResDx / sw : 0;
            double localDy = sw > 0 ? sResDy / sw : 0;

            // Analytical radial distortion projection (Architect Directive 2):
            // Projects true optical lens curve (r^3, r^5) so corners do not flatten into the frame
            double dax = ax - cx;
            double day = ay - cy;
            double distAnchor = Math.Sqrt((dax * dax) + (day * day));
            double rNorm = distAnchor / normRadius;

            double radialDisp = (k1 * rNorm * rNorm * rNorm) + (k2 * rNorm * rNorm * rNorm * rNorm * rNorm);
            double radPx = radialDisp * normRadius;
            double uX = distAnchor > 1e-4 ? dax / distAnchor : 0;
            double uY = distAnchor > 1e-4 ? day / distAnchor : 0;

            double totalTargetX = ax + (radPx * uX) + (localDx * 0.5);
            double totalTargetY = ay + (radPx * uY) + (localDy * 0.5);

            points.Add(new ThinPlateSplineWarp.ControlPoint(ax, ay, totalTargetX, totalTargetY));
        }
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0) return 0;
        Array.Sort(values);
        int m = values.Length / 2;
        return (values.Length % 2 == 1) ? values[m] : 0.5 * (values[m - 1] + values[m]);
    }

    private static double Rms(IEnumerable<double> values)
    {
        double s = 0;
        int n = 0;
        foreach (var v in values)
        {
            s += v * v;
            n++;
        }
        return n == 0 ? double.NaN : Math.Sqrt(s / n);
    }

    private static double Hypot(double a, double b) => Math.Sqrt((a * a) + (b * b));
}
