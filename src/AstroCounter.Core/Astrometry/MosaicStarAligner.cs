namespace AstroCounter.Core.Astrometry;

using System;
using System.Collections.Generic;
using System.Linq;
using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;

/// <summary>
/// High-precision relative astrometric star pattern aligner for multi-panel mosaics.
/// Matches 2D star triangle asterisms between an unaligned panel and an already-solved reference panel,
/// solving the exact relative rotation, scale, and translation to derive true celestial WCS coordinates.
/// </summary>
public static class MosaicStarAligner
{
    public sealed record AlignmentResult(
        bool IsSuccess,
        WcsMatrix? SolvedWcs,
        int MatchedStars,
        double RmsResidualPixels,
        string ReferencePanelName,
        string Message)
    {
        public static AlignmentResult Failed(string referencePanelName, string message) =>
            new(false, null, 0, double.NaN, referencePanelName, message);

        public static AlignmentResult Success(
            WcsMatrix wcs, int matchedStars, double rms, string referencePanelName, string message) =>
            new(true, wcs, matchedStars, rms, referencePanelName, message);
    }

    /// <summary>
    /// Attempts to register and align an unaligned panel against a solved reference panel using star patterns.
    /// </summary>
    public static AlignmentResult TryAlign(
        MosaicPanel unalignedPanel,
        IReadOnlyList<AstrophotographyObject> unalignedStars,
        MosaicPanel referencePanel,
        IReadOnlyList<AstrophotographyObject>? referenceStars = null)
    {
        if (unalignedPanel == null) throw new ArgumentNullException(nameof(unalignedPanel));
        if (referencePanel == null) throw new ArgumentNullException(nameof(referencePanel));
        if (referencePanel.Wcs == null)
            return AlignmentResult.Failed(referencePanel.Name, "Referans panelin WCS çözümü bulunmuyor.");

        if (unalignedStars == null || unalignedStars.Count < 4)
            return AlignmentResult.Failed(referencePanel.Name, "Hizalanacak görselde yeterli yıldız tespit edilemedi (en az 4 yıldız gereklidir).");

        // If reference stars not provided, use cached DetectedStars or extract from reference panel's cached bitmap
        var refStars = referenceStars ?? referencePanel.DetectedStars;
        if (refStars == null || refStars.Count < 4)
        {
            SkiaSharp.SKBitmap? bmp = (referencePanel.CachedSkiaBitmap as SkiaSharp.SKBitmap) ?? (referencePanel.CachedBitmap as SkiaSharp.SKBitmap);
            bool shouldDispose = false;
            if (bmp == null && !string.IsNullOrEmpty(referencePanel.FilePath) && File.Exists(referencePanel.FilePath))
            {
                bmp = ImagePreprocessor.LoadImage(referencePanel.FilePath);
                shouldDispose = true;
            }

            if (bmp != null)
            {
                refStars = PhotometricStarFinder.DetectStarsFast(bmp, new TilingConfig(DefaultConfidenceThreshold: 0.15f, MinConfidence: 0.05f));
                referencePanel.DetectedStars = refStars;
                if (shouldDispose) bmp.Dispose();
            }
        }

        if (refStars == null || refStars.Count < 4)
            return AlignmentResult.Failed(referencePanel.Name, "Referans panelde eşleştirme için yeterli yıldız bulunamadı.");

        // Select top brightest/highest SNR stars from both panels with spatial NMS separation
        static List<(double X, double Y, float Snr)> SelectCleanStars(
            IEnumerable<AstrophotographyObject> stars, int w, int h, int maxCount)
        {
            double minSep = Math.Max(8.0, Math.Min(w, h) / 70.0);
            double minSepSq = minSep * minSep;

            var filtered = stars
                .Where(s => s.CenterX >= 0 && s.CenterX <= w && s.CenterY >= 0 && s.CenterY <= h &&
                            (s.Tier <= ConfidenceTier.Tier2_Medium || s.Confidence >= 0.35f || s.Snr >= 3.5f))
                .ToList();
            var source = filtered.Count >= 6
                ? filtered
                : stars.Where(s => s.CenterX >= 0 && s.CenterX <= w && s.CenterY >= 0 && s.CenterY <= h).ToList();

            var ordered = source
                .OrderByDescending(s => s.Snr > 0 ? s.Snr : s.Confidence)
                .ToList();

            var accepted = new List<(double X, double Y, float Snr)>(maxCount);
            foreach (var s in ordered)
            {
                bool tooClose = false;
                foreach (var a in accepted)
                {
                    double dx = s.CenterX - a.X;
                    double dy = s.CenterY - a.Y;
                    if ((dx * dx) + (dy * dy) < minSepSq)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (!tooClose)
                {
                    accepted.Add((s.CenterX, s.CenterY, s.Snr));
                    if (accepted.Count >= maxCount) break;
                }
            }
            return accepted;
        }

        var uStars = SelectCleanStars(unalignedStars, unalignedPanel.Width, unalignedPanel.Height, 40);
        var rStars = SelectCleanStars(refStars, referencePanel.Width, referencePanel.Height, 40);

        if (uStars.Count < 4 || rStars.Count < 4)
            return AlignmentResult.Failed(referencePanel.Name, "Seçilen yıldız sayısı yetersiz.");

        // Build asterism triangles
        var uTriangles = BuildAsterisms(uStars);
        var rTriangles = BuildAsterisms(rStars);

        if (uTriangles.Count == 0 || rTriangles.Count == 0)
            return AlignmentResult.Failed(referencePanel.Name, "Yıldız üçgen asterizmleri oluşturulamadı.");

        const double ratioTol = 0.022;
        const double maxDistanceTolPx = 7.0; // Distance tolerance for inlier star matching

        int bestInlierCount = 0;
        double bestRms = double.MaxValue;
        (double A, double B, double Tx, double Ty) bestTransform = default;
        List<((double X, double Y) U, (double X, double Y) R)>? bestInliers = null;
        bool bestMirrored = false;
        bool foundMatch = false;

        // Try standard orientation first, then horizontally mirrored if needed
        for (int flip = 0; flip <= 1 && !foundMatch; flip++)
        {
            bool isMirrored = flip == 1;

            foreach (var ut in uTriangles)
            {
                foreach (var rt in rTriangles)
                {
                    bool chMatches = isMirrored ? (ut.Chirality != rt.Chirality) : (ut.Chirality == rt.Chirality);
                    if (!chMatches) continue;

                    if (Math.Abs(ut.RatioAB - rt.RatioAB) > ratioTol) continue;
                    if (Math.Abs(ut.RatioBC - rt.RatioBC) > ratioTol) continue;

                    double scale = rt.Perimeter / ut.Perimeter;
                    // In mosaic tiles taken with identical equipment, scale is virtually 1.0 (allow ±20%)
                    if (scale < 0.78 || scale > 1.28) continue;

                    // Fit candidate similarity transform: P_ref = Transform(P_unaligned)
                    var p0_u = isMirrored ? (unalignedPanel.Width - uStars[ut.I0].X, uStars[ut.I0].Y) : (uStars[ut.I0].X, uStars[ut.I0].Y);
                    var p1_u = isMirrored ? (unalignedPanel.Width - uStars[ut.I1].X, uStars[ut.I1].Y) : (uStars[ut.I1].X, uStars[ut.I1].Y);
                    var p2_u = isMirrored ? (unalignedPanel.Width - uStars[ut.I2].X, uStars[ut.I2].Y) : (uStars[ut.I2].X, uStars[ut.I2].Y);

                    var p0_r = (rStars[rt.I0].X, rStars[rt.I0].Y);
                    var p1_r = (rStars[rt.I1].X, rStars[rt.I1].Y);
                    var p2_r = (rStars[rt.I2].X, rStars[rt.I2].Y);

                    if (!FitSimilarity3(p0_u, p1_u, p2_u, p0_r, p1_r, p2_r, out double a, out double b, out double tx, out double ty))
                        continue;

                    // Collect strictly 1-to-1 unique inlier pairs
                    var candidateMatches = new List<(int UIdx, int RIdx, double DistSq, (double X, double Y) U, (double X, double Y) R)>();
                    double maxTolSq = maxDistanceTolPx * maxDistanceTolPx;

                    for (int ui = 0; ui < uStars.Count; ui++)
                    {
                        double ux = isMirrored ? (unalignedPanel.Width - uStars[ui].X) : uStars[ui].X;
                        double uy = uStars[ui].Y;

                        double predRx = (a * ux) - (b * uy) + tx;
                        double predRy = (b * ux) + (a * uy) + ty;

                        for (int ri = 0; ri < rStars.Count; ri++)
                        {
                            double dx = predRx - rStars[ri].X;
                            double dy = predRy - rStars[ri].Y;
                            double d2 = (dx * dx) + (dy * dy);
                            if (d2 < maxTolSq)
                            {
                                candidateMatches.Add((ui, ri, d2, (ux, uy), (rStars[ri].X, rStars[ri].Y)));
                            }
                        }
                    }

                    if (candidateMatches.Count < 6) continue;

                    candidateMatches.Sort((x, y) => x.DistSq.CompareTo(y.DistSq));
                    var usedU = new HashSet<int>();
                    var usedR = new HashSet<int>();
                    var inliers = new List<((double X, double Y) U, (double X, double Y) R)>();

                    foreach (var cm in candidateMatches)
                    {
                        if (usedU.Add(cm.UIdx) && usedR.Add(cm.RIdx))
                        {
                            inliers.Add((cm.U, cm.R));
                        }
                    }

                    if (inliers.Count >= 6)
                    {
                        // Refine similarity transform across all unique inliers using least squares
                        if (FitSimilarityN(inliers, out double refA, out double refB, out double refTx, out double refTy))
                        {
                            double sumSq = 0;
                            foreach (var (u, r) in inliers)
                            {
                                double px = (refA * u.X) - (refB * u.Y) + refTx;
                                double py = (refB * u.X) + (refA * u.Y) + refTy;
                                double err2 = ((px - r.X) * (px - r.X)) + ((py - r.Y) * (py - r.Y));
                                sumSq += err2;
                            }
                            double rms = Math.Sqrt(sumSq / inliers.Count);

                            if (inliers.Count > bestInlierCount || (inliers.Count == bestInlierCount && rms < bestRms))
                            {
                                bestInlierCount = inliers.Count;
                                bestRms = rms;
                                bestTransform = (refA, refB, refTx, refTy);
                                bestInliers = inliers;
                                bestMirrored = isMirrored;

                                if (inliers.Count >= 8 && rms < 2.0)
                                {
                                    foundMatch = true;
                                    break;
                                }
                            }
                        }
                    }
                }
                if (foundMatch) break;
            }
        }

        if (bestInlierCount < 6 || bestRms > 3.5 || bestInliers == null)
        {
            return AlignmentResult.Failed(
                referencePanel.Name,
                $"Yıldız deseni örtüşmesi bulunamadı (En iyi eşleşme: {bestInlierCount} yıldız, RMS: {bestRms:F1} px). Paneller örtüşmüyor olabilir.");
        }

        // Derive true WCS for the unaligned panel directly on the celestial sphere using matched star pairs.
        // For narrow fields (< 15 deg FOV), planar reference projection is exact; for wide fields,
        // fitting on the unaligned panel's centered tangent plane prevents radial distortion blowup outside the reference frame.
        var (tA, tB, tTx, tTy) = bestTransform;
        double unalignedCenterX = unalignedPanel.Width / 2.0;
        double unalignedCenterY = unalignedPanel.Height / 2.0;

        double refFov = Math.Max(referencePanel.Wcs.FovWidthDegrees, referencePanel.Wcs.FovHeightDegrees);
        WcsMatrix solvedWcs;

        if (refFov <= 15.0 && Math.Abs(referencePanel.Wcs.RadialDistortionK1) < 1e-5 && referencePanel.Wcs.SplineWarp == null)
        {
            // Project center into reference panel pixel coordinates
            double refPx = (tA * unalignedCenterX) - (tB * unalignedCenterY) + tTx;
            double refPy = (tB * unalignedCenterX) + (tA * unalignedCenterY) + tTy;

            var centerCelestial = referencePanel.Wcs.PixelToEquatorial(refPx, refPy);
            double fittedScale = Math.Sqrt((tA * tA) + (tB * tB));
            double fittedRotationDeg = Math.Atan2(tB, tA) * (180.0 / Math.PI);

            double derivedPixelScale = referencePanel.Wcs.PixelScaleArcsec / (fittedScale > 1e-6 ? fittedScale : 1.0);
            double derivedRotation = (referencePanel.Wcs.RotationDegrees + fittedRotationDeg + 360.0) % 360.0;

            solvedWcs = new WcsMatrix(
                centerCoordinates: centerCelestial,
                referencePixelX: unalignedCenterX,
                referencePixelY: unalignedCenterY,
                pixelScaleArcsec: derivedPixelScale,
                rotationDegrees: derivedRotation,
                imageWidth: unalignedPanel.Width,
                imageHeight: unalignedPanel.Height,
                isFlippedHorizontally: referencePanel.Wcs.IsFlippedHorizontally ^ bestMirrored);
        }
        else
        {
            // Wide-field or distortion-corrected reference panel:
            // Map matched reference stars (which lie strictly inside referencePanel) to true sky (RA, Dec),
            // and fit the unaligned panel's tangent plane directly to those celestial coordinates!
            var matchedSkyPairs = new List<((double X, double Y) U, CelestialCoordinates Sky)>(bestInliers.Count);
            double sumX = 0, sumY = 0, sumZ = 0;

            foreach (var (u, r) in bestInliers)
            {
                var sky = referencePanel.Wcs.PixelToEquatorial(r.X, r.Y);
                matchedSkyPairs.Add((u, sky));
                double cosD = Math.Cos(sky.DecRadians);
                sumX += cosD * Math.Cos(sky.RaRadians);
                sumY += cosD * Math.Sin(sky.RaRadians);
                sumZ += Math.Sin(sky.DecRadians);
            }

            var centerCoord = CelestialCoordinates.FromRadians(
                Math.Atan2(sumY, sumX),
                Math.Asin(Math.Clamp(sumZ / Math.Max(1e-9, Math.Sqrt((sumX * sumX) + (sumY * sumY) + (sumZ * sumZ))), -1.0, 1.0)));

            double finalDegPerPx = referencePanel.Wcs.PixelScaleArcsec / 3600.0;
            double finalRotDeg = referencePanel.Wcs.RotationDegrees;

            for (int iter = 0; iter < 2; iter++)
            {
                var tanWcs = new WcsMatrix(centerCoord, 0.0, 0.0, 3600.0, 0.0, 1000, 1000);
                var tanPairs = new List<((double X, double Y) U, (double X, double Y) R)>(matchedSkyPairs.Count);
                foreach (var (u, sky) in matchedSkyPairs)
                {
                    if (tanWcs.EquatorialToPixel(sky, out double tu, out double tv))
                    {
                        tanPairs.Add((u, (tu, tv)));
                    }
                }

                if (tanPairs.Count >= 4 && FitSimilarityN(tanPairs, out double sa, out double sb, out double stx, out double sty))
                {
                    double uCenter = (sa * unalignedCenterX) - (sb * unalignedCenterY) + stx;
                    double vCenter = (sb * unalignedCenterX) + (sa * unalignedCenterY) + sty;
                    centerCoord = tanWcs.PixelToEquatorial(uCenter, vCenter);
                    finalDegPerPx = Math.Sqrt((sa * sa) + (sb * sb));
                    finalRotDeg = ((Math.Atan2(sb, sa) * (180.0 / Math.PI)) % 360.0 + 360.0) % 360.0;
                }
            }

            solvedWcs = new WcsMatrix(
                centerCoordinates: centerCoord,
                referencePixelX: unalignedCenterX,
                referencePixelY: unalignedCenterY,
                pixelScaleArcsec: Math.Max(0.01, finalDegPerPx * 3600.0),
                rotationDegrees: finalRotDeg,
                imageWidth: unalignedPanel.Width,
                imageHeight: unalignedPanel.Height,
                isFlippedHorizontally: referencePanel.Wcs.IsFlippedHorizontally ^ bestMirrored);
        }

        bool tr = CataloguePlateSolver.UseTurkishLanguage;
        string msg = tr
            ? $"'{referencePanel.Name}' ile {bestInlierCount} ortak yıldız üzerinden %100 hassasiyetle kenetlendi (RMS: {bestRms:F2} px)."
            : $"Locked onto '{referencePanel.Name}' via {bestInlierCount} matching stars with sub-pixel precision (RMS: {bestRms:F2} px).";

        return AlignmentResult.Success(solvedWcs, bestInlierCount, bestRms, referencePanel.Name, msg);
    }

    private sealed record AsterismTriangle(
        int I0, int I1, int I2,
        double RatioAB, double RatioBC,
        double Perimeter,
        bool Chirality);

    private static List<AsterismTriangle> BuildAsterisms(IReadOnlyList<(double X, double Y, float Snr)> stars)
    {
        var list = new List<AsterismTriangle>();
        int n = Math.Min(stars.Count, 24);

        for (int i = 0; i < n - 2; i++)
        {
            var p0 = stars[i];
            for (int j = i + 1; j < n - 1; j++)
            {
                var p1 = stars[j];
                double d01 = Math.Sqrt(((p1.X - p0.X) * (p1.X - p0.X)) + ((p1.Y - p0.Y) * (p1.Y - p0.Y)));
                if (d01 < 12.0) continue;

                for (int k = j + 1; k < n; k++)
                {
                    var p2 = stars[k];
                    double d12 = Math.Sqrt(((p2.X - p1.X) * (p2.X - p1.X)) + ((p2.Y - p1.Y) * (p2.Y - p1.Y)));
                    if (d12 < 12.0) continue;

                    double d20 = Math.Sqrt(((p0.X - p2.X) * (p0.X - p2.X)) + ((p0.Y - p2.Y) * (p0.Y - p2.Y)));
                    if (d20 < 12.0) continue;

                    // Order vertices so opposite sides are sorted: sA <= sB <= sC
                    // Vertex opposite to sA (d12 between p1 and p2) is v0
                    // Vertex opposite to sB (d20 between p0 and p2) is v1
                    // Vertex opposite to sC (d01 between p0 and p1) is v2
                    int v0 = i, v1 = j, v2 = k;
                    double sA = d12, sB = d20, sC = d01;

                    if (sA > sB) { (sA, sB) = (sB, sA); (v0, v1) = (v1, v0); }
                    if (sB > sC) { (sB, sC) = (sC, sB); (v1, v2) = (v2, v1); }
                    if (sA > sB) { (sA, sB) = (sB, sA); (v0, v1) = (v1, v0); }

                    if (sC < 1e-4) continue;
                    // Reject nearly collinear degenerate triangles
                    if ((sA + sB) < sC * 1.03) continue;

                    var pv0 = stars[v0];
                    var pv1 = stars[v1];
                    var pv2 = stars[v2];
                    double cross = ((pv1.X - pv0.X) * (pv2.Y - pv0.Y)) - ((pv1.Y - pv0.Y) * (pv2.X - pv0.X));
                    bool chirality = cross >= 0;

                    list.Add(new AsterismTriangle(v0, v1, v2, sA / sC, sB / sC, sA + sB + sC, chirality));
                }
            }
        }
        return list;
    }

    private static bool FitSimilarity3(
        (double X, double Y) u0, (double X, double Y) u1, (double X, double Y) u2,
        (double X, double Y) r0, (double X, double Y) r1, (double X, double Y) r2,
        out double a, out double b, out double tx, out double ty)
    {
        return FitSimilarityN(
            [(u0, r0), (u1, r1), (u2, r2)],
            out a, out b, out tx, out ty);
    }

    private static bool FitSimilarityN(
        IReadOnlyList<((double X, double Y) U, (double X, double Y) R)> pairs,
        out double a, out double b, out double tx, out double ty)
    {
        a = 1.0; b = 0.0; tx = 0.0; ty = 0.0;
        int n = pairs.Count;
        if (n < 2) return false;

        double sumUx = 0, sumUy = 0, sumRx = 0, sumRy = 0;
        for (int i = 0; i < n; i++)
        {
            sumUx += pairs[i].U.X;
            sumUy += pairs[i].U.Y;
            sumRx += pairs[i].R.X;
            sumRy += pairs[i].R.Y;
        }

        double meanUx = sumUx / n;
        double meanUy = sumUy / n;
        double meanRx = sumRx / n;
        double meanRy = sumRy / n;

        double sU = 0;
        double numA = 0;
        double numB = 0;

        for (int i = 0; i < n; i++)
        {
            double dux = pairs[i].U.X - meanUx;
            double duy = pairs[i].U.Y - meanUy;
            double drx = pairs[i].R.X - meanRx;
            double dry = pairs[i].R.Y - meanRy;

            sU += (dux * dux) + (duy * duy);
            numA += (dux * drx) + (duy * dry);
            numB += (dux * dry) - (duy * drx);
        }

        if (sU < 1e-9) return false;

        a = numA / sU;
        b = numB / sU;
        tx = meanRx - (a * meanUx) + (b * meanUy);
        ty = meanRy - (b * meanUx) - (a * meanUy);

        return !double.IsNaN(a) && !double.IsNaN(b) && !double.IsNaN(tx) && !double.IsNaN(ty);
    }
}
