namespace AstroCounter.Core.Processing;

using AstroCounter.Core.Models;

public record TierSummary(
    int TotalCount,
    int Tier1HighCount,
    int Tier2MediumCount,
    int Tier3LowCount,
    int NoiseCount,
    float AverageConfidence
);

/// <summary>
/// Provides classification and statistical binning logic for astronomical detection candidates.
/// </summary>
public static class ConfidenceClassifier
{
    public static TierSummary Summarize(IEnumerable<AstrophotographyObject> objects)
    {
        var list = objects.ToList();
        if (list.Count == 0)
        {
            return new TierSummary(0, 0, 0, 0, 0, 0f);
        }

        int tier1 = 0;
        int tier2 = 0;
        int tier3 = 0;
        int noise = 0;
        float sumConf = 0f;

        foreach (var obj in list)
        {
            sumConf += obj.Confidence;
            switch (obj.Tier)
            {
                case ConfidenceTier.Tier1_High:
                    tier1++;
                    break;
                case ConfidenceTier.Tier2_Medium:
                    tier2++;
                    break;
                case ConfidenceTier.Tier3_Low:
                    tier3++;
                    break;
                case ConfidenceTier.FilteredNoise:
                    noise++;
                    break;
            }
        }

        return new TierSummary(
            TotalCount: list.Count,
            Tier1HighCount: tier1,
            Tier2MediumCount: tier2,
            Tier3LowCount: tier3,
            NoiseCount: noise,
            AverageConfidence: sumConf / list.Count
        );
    }

    /// <summary>
    /// Approximates astronomical Signal-to-Noise Ratio (SNR) in sigmas from confidence score.
    /// Based on the paper calibration: 0.80 -> 10 sigma, 0.50 -> 5 sigma, 0.25 -> 3 sigma.
    /// </summary>
    public static float EstimateEquivalentSnrSigma(float confidence)
    {
        if (confidence <= 0f) return 0f;
        // Piecewise linear interpolation matching the paper calibration
        if (confidence >= 0.80f)
        {
            return 10.0f + (confidence - 0.80f) * 25.0f; // 0.80 -> 10 sigma, 1.0 -> 15 sigma
        }
        if (confidence >= 0.50f)
        {
            return 5.0f + ((confidence - 0.50f) / 0.30f) * 5.0f; // 0.50 -> 5 sigma, 0.80 -> 10 sigma
        }
        if (confidence >= 0.25f)
        {
            return 3.0f + ((confidence - 0.25f) / 0.25f) * 2.0f; // 0.25 -> 3 sigma, 0.50 -> 5 sigma
        }
        return (confidence / 0.25f) * 3.0f; // 0.0 -> 0 sigma, 0.25 -> 3 sigma
    }
}

