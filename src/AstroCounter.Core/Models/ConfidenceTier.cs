namespace AstroCounter.Core.Models;

public enum ConfidenceTier
{
    Tier1_High,     // c >= 0.80 (SNR > 10 sigma)
    Tier2_Medium,   // 0.50 <= c < 0.80 (SNR 5-10 sigma)
    Tier3_Low,      // 0.25 <= c < 0.50 (SNR 3-5 sigma)
    FilteredNoise   // c < 0.25 (SNR < 3 sigma)
}

public static class ConfidenceTierExtensions
{
    public static string GetDisplayName(this ConfidenceTier tier) => tier switch
    {
        ConfidenceTier.Tier1_High => "Tier 1: Yüksek Güvenilirlik (>10σ)",
        ConfidenceTier.Tier2_Medium => "Tier 2: Orta Güvenilirlik (5σ–10σ)",
        ConfidenceTier.Tier3_Low => "Tier 3: Düşük / Spekülatif (3σ–5σ)",
        ConfidenceTier.FilteredNoise => "Elenen Gürültü (<3σ)",
        _ => "Bilinmeyen"
    };

    public static string GetShortName(this ConfidenceTier tier) => tier switch
    {
        ConfidenceTier.Tier1_High => "Tier 1 (High)",
        ConfidenceTier.Tier2_Medium => "Tier 2 (Medium)",
        ConfidenceTier.Tier3_Low => "Tier 3 (Low)",
        ConfidenceTier.FilteredNoise => "Noise",
        _ => "Unknown"
    };

    public static string GetColorHex(this ConfidenceTier tier) => tier switch
    {
        ConfidenceTier.Tier1_High => "#00E676",    // Bright neon green / emerald
        ConfidenceTier.Tier2_Medium => "#FFD600",  // Vivid amber / yellow
        ConfidenceTier.Tier3_Low => "#FF9100",     // Bright orange
        ConfidenceTier.FilteredNoise => "#757575", // Muted gray
        _ => "#FFFFFF"
    };

    public static ConfidenceTier FromConfidence(float confidence)
    {
        if (confidence >= 0.80f) return ConfidenceTier.Tier1_High;
        if (confidence >= 0.50f) return ConfidenceTier.Tier2_Medium;
        if (confidence >= 0.25f) return ConfidenceTier.Tier3_Low;
        return ConfidenceTier.FilteredNoise;
    }
}

