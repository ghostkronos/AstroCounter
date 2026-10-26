namespace AstroCounter.Core.Astrometry;

using System;
using System.Collections.Generic;
using System.Linq;
using AstroCounter.Core.Models;

/// <summary>
/// Represents a scientifically verified optical transient or supernova anomaly candidate.
/// </summary>
public readonly record struct TransientCandidate(
    double Ra,
    double Dec,
    float Flux,
    float Fwhm,
    float Ellipticity,
    bool IsSupernovaCandidate,
    string? HostGalaxyId,
    string AnomalyType);

/// <summary>
/// Scientific Dual-Verification Transient & Anomaly Hunter.
/// Identifies unexplained optical transients, supernovae, novae, and high-SNR anomalies
/// by cross-matching high-confidence detected celestial objects with high-density reference catalogs (Gaia DR3 / Tycho / Hipparcos).
/// Strictly caps candidate anomalies (maximum 1 or 2 per image) to prevent false positives from catalog depth limits.
/// </summary>
public static class TransientAnomalyDetector
{
    public static List<AstrophotographyObject> DetectAnomalies(
        IReadOnlyList<AstrophotographyObject> detections,
        WcsMatrix wcs,
        int imageWidth,
        int imageHeight,
        float minSnr = 7.5f,
        float minConfidence = 0.65f,
        int maxAnomalies = 2)
    {
        if (detections == null || detections.Count == 0 || wcs == null)
        {
            return detections != null ? new List<AstrophotographyObject>(detections) : new List<AstrophotographyObject>();
        }

        double fovDeg = Math.Max(wcs.FovWidthDegrees, wcs.FovHeightDegrees);
        double pixelScaleDeg = fovDeg / Math.Max(Math.Max(imageWidth, imageHeight), 100);
        double imgDiag = Math.Sqrt(((double)imageWidth * imageWidth) + ((double)imageHeight * imageHeight));
        double cx = imageWidth / 2.0;
        double cy = imageHeight / 2.0;

        // Store candidate indices and their anomaly metadata
        var candidateList = new List<(int Index, float Score, string AnomalyType)>();

        for (int i = 0; i < detections.Count; i++)
        {
            var obj = detections[i];

            // Optical quality check: High SNR, authentic celestial source, safely inside optical active area (margin > 25px)
            if (obj.Snr >= minSnr && obj.Confidence >= minConfidence &&
                obj.CenterX > 25 && obj.CenterX < imageWidth - 25 &&
                obj.CenterY > 25 && obj.CenterY < imageHeight - 25)
            {
                // Pass 1: PSF Morphology Validation (Eliminates single-pixel cosmic ray spikes and elongated satellite streaks)
                float boxW = Math.Max(0.5f, obj.Box.Width);
                float boxH = Math.Max(0.5f, obj.Box.Height);
                float estimatedFwhm = (boxW + boxH) * 0.42f;
                float ellipticity = 1.0f - (Math.Min(boxW, boxH) / Math.Max(boxW, boxH));

                // Hot pixels and cosmic rays have FWHM < 1.1px; streaks have ellipticity > 0.45
                if (estimatedFwhm < 1.15f || ellipticity > 0.45f)
                {
                    continue;
                }
                // Radial distance from optical center for adaptive distortion tolerance
                double dx = obj.CenterX - cx;
                double dy = obj.CenterY - cy;
                double rNorm = Math.Sqrt((dx * dx) + (dy * dy)) / Math.Max(1.0, 0.5 * imgDiag);

                // Adaptive match radius: expands gracefully towards lens corners to absorb residual lens distortion
                // For prominent/bright stars, expand radius significantly to avoid false anomaly alarms from residual distortion
                bool isProminent = obj.Confidence >= 0.80f || obj.ClassName == "bright_star";
                double effRadiusMult = isProminent ? 4.5 : (2.8 + (3.5 * rNorm));
                double matchRadiusDeg = Math.Max(pixelScaleDeg * effRadiusMult, 0.010);

                var eq = wcs.PixelToEquatorial(obj.CenterX, obj.CenterY);

                // 1. Query Gaia / Tycho high-density stellar catalog
                var nearbyStars = GaiaTychoCatalog.QueryRegion(
                    eq.RaDegrees,
                    eq.DecDegrees,
                    radiusDeg: matchRadiusDeg * 1.5,
                    maxMag: 14.5,
                    maxResults: 12);

                bool hasCatalogStar = false;
                if (nearbyStars.Count > 0)
                {
                    foreach (var s in nearbyStars)
                    {
                        double dRa = (eq.RaDegrees - s.RaDeg) * Math.Cos(eq.DecDegrees * Math.PI / 180.0);
                        double dDec = eq.DecDegrees - s.DecDeg;
                        double distDeg = Math.Sqrt((dRa * dRa) + (dDec * dDec));
                        if (distDeg <= matchRadiusDeg)
                        {
                            hasCatalogStar = true;
                            break;
                        }
                    }
                }

                // 2. Query Hipparcos catalogue fallback
                if (!hasCatalogStar)
                {
                    var hipStars = HipparcosCatalog.QueryRegion(
                        eq.RaDegrees,
                        eq.DecDegrees,
                        radiusDeg: matchRadiusDeg * 1.5,
                        maxMag: 12.5,
                        maxResults: 8);

                    foreach (var s in hipStars)
                    {
                        double dRa = (eq.RaDegrees - s.RaDeg) * Math.Cos(eq.DecDegrees * Math.PI / 180.0);
                        double dDec = eq.DecDegrees - s.DecDeg;
                        double distDeg = Math.Sqrt((dRa * dRa) + (dDec * dDec));
                        if (distDeg <= matchRadiusDeg)
                        {
                            hasCatalogStar = true;
                            break;
                        }
                    }
                }

                if (!hasCatalogStar)
                {
                    // No known catalog star exists at this location: ANOMALY CANDIDATE!
                    string anomalyType = CataloguePlateSolver.UseTurkishLanguage ? "Optik Transient / Bilinmeyen Cisim" : "Optical Transient / Uncataloged Source";

                    // Check if located in proximity to a known galaxy
                    foreach (var target in CataloguePlateSolver.KnownCatalogues)
                    {
                        if (target.Id.StartsWith("M") || target.Id.StartsWith("NGC"))
                        {
                            double dRa = (eq.RaDegrees - target.Coordinates.RaDegrees) * Math.Cos(eq.DecDegrees * Math.PI / 180.0);
                            double dDec = eq.DecDegrees - target.Coordinates.DecDegrees;
                            double distDeg = Math.Sqrt((dRa * dRa) + (dDec * dDec));
                            if (distDeg <= (target.TypicalFovDegrees * 1.5))
                            {
                                anomalyType = CataloguePlateSolver.UseTurkishLanguage
                                    ? $"Süpernova Adayı ({target.Id})"
                                    : $"Supernova Candidate ({target.Id})";
                                break;
                            }
                        }
                    }

                    float score = obj.Snr * obj.Confidence;
                    candidateList.Add((i, score, anomalyType));
                }
            }
        }

        // Strictly take at most maxAnomalies (1 or 2 max) highest-significance candidates
        var topCandidates = candidateList
            .OrderByDescending(c => c.Score)
            .Take(maxAnomalies)
            .ToDictionary(c => c.Index, c => c.AnomalyType);

        var result = new List<AstrophotographyObject>(detections.Count);
        for (int i = 0; i < detections.Count; i++)
        {
            var obj = detections[i];
            if (topCandidates.TryGetValue(i, out var aType))
            {
                result.Add(obj with
                {
                    IsAnomaly = true,
                    AnomalyType = aType
                });
            }
            else
            {
                result.Add(obj);
            }
        }

        return result;
    }
}
