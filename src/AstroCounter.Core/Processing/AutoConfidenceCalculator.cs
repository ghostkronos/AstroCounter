namespace AstroCounter.Core.Processing;

using System;
using System.Collections.Generic;
using System.Linq;
using AstroCounter.Core.Models;

/// <summary>
/// Calculates optimal detection confidence threshold based on physical noise floor knee-point (elbow)
/// analysis and AI feedback calibration.
/// </summary>
public static class AutoConfidenceCalculator
{
    /// <summary>
    /// Computes the scientifically optimal confidence threshold for an astronomical image.
    /// </summary>
    /// <param name="objects">Candidate objects extracted by the detection engine</param>
    /// <param name="isCompressedFormat">True if JPG/WebP, false if TIFF/FITS/PNG</param>
    /// <returns>Optimal confidence threshold in [0.15, 0.55] range</returns>
    public static float ComputeOptimalThreshold(
        IReadOnlyList<AstrophotographyObject> objects,
        bool isCompressedFormat)
    {
        if (objects == null || objects.Count == 0)
        {
            return isCompressedFormat ? 0.35f : 0.25f;
        }

        // 1. Collect positive SNR values
        var snrList = objects.Where(o => o.Snr > 0.5f).Select(o => o.Snr).OrderBy(s => s).ToList();
        if (snrList.Count < 10)
        {
            return isCompressedFormat ? 0.35f : 0.25f;
        }

        // 2. Histogram binning from 1.0 sigma to 12.0 sigma with step 0.25 (44 bins)
        float minBin = 1.0f;
        float maxBin = 12.0f;
        float binWidth = 0.25f;
        int numBins = (int)Math.Ceiling((maxBin - minBin) / binWidth);
        int[] counts = new int[numBins];

        foreach (float snr in snrList)
        {
            if (snr >= minBin && snr < maxBin)
            {
                int binIdx = (int)((snr - minBin) / binWidth);
                counts[binIdx]++;
            }
        }

        // 3. Find Knee / Elbow point (transition from noise surge to star signal)
        float kneeSnr = isCompressedFormat ? 4.0f : 2.8f;
        int maxCurvatureIdx = -1;
        float maxCurvature = 0f;

        // Smooth histogram with 3-point moving average
        float[] smoothed = new float[numBins];
        for (int i = 0; i < numBins; i++)
        {
            float prev = i > 0 ? counts[i - 1] : counts[i];
            float curr = counts[i];
            float next = i < numBins - 1 ? counts[i + 1] : counts[i];
            smoothed[i] = (prev + 2f * curr + next) / 4f;
        }

        for (int i = 2; i < numBins - 2; i++)
        {
            float d2 = smoothed[i - 1] - (2f * smoothed[i]) + smoothed[i + 1];
            if (d2 > maxCurvature)
            {
                float snrVal = minBin + (i * binWidth);
                // Plausible knee range: 2.2 to 5.5 sigma
                if (snrVal >= 2.2f && snrVal <= 5.5f)
                {
                    maxCurvature = d2;
                    maxCurvatureIdx = i;
                }
            }
        }

        if (maxCurvatureIdx >= 0)
        {
            kneeSnr = minBin + (maxCurvatureIdx * binWidth);
        }

        // 4. AI Feedback Calibration:
        var aiStars = objects.Where(o => o.Confidence >= 0.70f && o.Snr > 0).ToList();
        if (aiStars.Count >= 5)
        {
            var aiSnrs = aiStars.Select(o => o.Snr).OrderBy(s => s).ToList();
            int p10Idx = Math.Max(0, (int)(aiSnrs.Count * 0.10f));
            float aiP10Snr = aiSnrs[p10Idx];

            float minSafeFloor = isCompressedFormat ? 3.2f : 2.2f;
            kneeSnr = Math.Max(minSafeFloor, Math.Min(kneeSnr, aiP10Snr));
        }

        if (isCompressedFormat)
        {
            kneeSnr = Math.Max(kneeSnr, 4.0f);
        }

        // 5. Map knee SNR to Confidence Threshold:
        float optimalConfidence;
        if (kneeSnr <= 3.5f)
        {
            float t = (kneeSnr - 2.0f) / 1.5f;
            optimalConfidence = 0.15f + (Math.Clamp(t, 0f, 1f) * 0.10f);
        }
        else
        {
            float t = (kneeSnr - 3.5f) / 2.5f;
            optimalConfidence = 0.25f + (Math.Clamp(t, 0f, 1f) * 0.25f);
        }

        if (isCompressedFormat)
        {
            optimalConfidence = Math.Max(optimalConfidence, 0.30f);
        }

        return (float)Math.Round(Math.Clamp(optimalConfidence, 0.15f, 0.55f), 2);
    }
}
