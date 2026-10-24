namespace AstroCounter.Core.Processing;

using System;
using System.Collections.Generic;
using System.Linq;
using AstroCounter.Core.Models;

/// <summary>
/// Scientific Photometry and Deep Learning (YOLO) Fusion Engine.
/// Fuses high-sensitivity linear DAOStarFinder photometric extraction (for clear prominent stars)
/// with morphological deep learning (for subtle, borderline faint sources that resemble noise).
/// </summary>
public static class FusionDetectionEngine
{
    /// <summary>
    /// Fuses photometric detections with deep learning YOLO detections.
    /// Photometry reliably anchors prominent and definite stars.
    /// AI reliably detects faint, subtle, borderline objects that thresholding overlooks.
    /// Common detections receive a synergy confidence boost.
    /// </summary>
    public static List<AstrophotographyObject> FuseDetections(
        List<AstrophotographyObject> photometricStars,
        List<AstrophotographyObject>? yoloDetections,
        float matchDistancePx = 14f)
    {
        if (yoloDetections == null || yoloDetections.Count == 0)
        {
            // Pure photometric fallback
            var standaloneList = new List<AstrophotographyObject>(photometricStars.Count);
            int sid = 1;
            foreach (var p in photometricStars)
            {
                standaloneList.Add(p with { Id = sid++ });
            }
            return standaloneList;
        }

        var matchedYoloIndices = new HashSet<int>();
        var fusedResults = new List<AstrophotographyObject>(photometricStars.Count + yoloDetections.Count);
        int id = 1;
        float matchDistSq = matchDistancePx * matchDistancePx;

        // Step 1: Photometry anchors prominent stars, corroborated by AI
        foreach (var photo in photometricStars)
        {
            int bestYoloIdx = -1;
            float bestDistSq = float.MaxValue;

            for (int i = 0; i < yoloDetections.Count; i++)
            {
                var yolo = yoloDetections[i];
                float dx = photo.CenterX - yolo.CenterX;
                float dy = photo.CenterY - yolo.CenterY;
                float distSq = (dx * dx) + (dy * dy);

                if (distSq <= matchDistSq && distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestYoloIdx = i;
                }
            }

            if (bestYoloIdx >= 0)
            {
                // Corroborated by both Photometry and AI: High confidence genuine astronomical source
                matchedYoloIndices.Add(bestYoloIdx);
                var matchedYolo = yoloDetections[bestYoloIdx];

                // Fused confidence formula: synergy boost (promoted to Tier 1)
                float fusedConf = Math.Clamp(
                    Math.Max(photo.Confidence, matchedYolo.Confidence) + 0.12f,
                    0.10f, 1.0f);

                string finalClass = (matchedYolo.ClassName == "galaxy" || matchedYolo.ClassName == "nebula")
                    ? matchedYolo.ClassName
                    : photo.ClassName;

                fusedResults.Add(photo with
                {
                    Id = id++,
                    Confidence = fusedConf,
                    ClassName = finalClass,
                    Snr = Math.Max(photo.Snr, matchedYolo.Snr)
                });
            }
            else
            {
                // Hot pixel / sensor noise rejection: if SNR < 2.5 sigma and AI did not corroborate, discard
                if (photo.Snr < 2.5f)
                {
                    continue;
                }

                // Definite photometric star (not matched by AI):
                // Photometry is authoritative on clear, prominent, definite stars
                fusedResults.Add(photo with { Id = id++ });
            }
        }

        // Step 2: Add AI-detected subtle / faint objects that Photometry missed
        // AI specializes in subtle, borderline faint sources that resemble noise
        for (int i = 0; i < yoloDetections.Count; i++)
        {
            if (matchedYoloIndices.Contains(i)) continue;

            var yolo = yoloDetections[i];
            // Include subtle AI detections; lift faint candidates to baseline visible tier (>= 0.25)
            float effectiveConf = Math.Max(yolo.Confidence, 0.25f);
            fusedResults.Add(yolo with
            {
                Id = id++,
                Confidence = effectiveConf,
                Snr = yolo.Snr > 0 ? yolo.Snr : 4.5f
            });
        }

        return fusedResults.OrderByDescending(o => o.Confidence).ToList();
    }
}
