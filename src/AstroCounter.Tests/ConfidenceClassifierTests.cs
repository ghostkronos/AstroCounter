namespace AstroCounter.Tests;

using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using Xunit;

public class ConfidenceClassifierTests
{
    [Theory]
    [InlineData(0.95f, ConfidenceTier.Tier1_High)]
    [InlineData(0.80f, ConfidenceTier.Tier1_High)]
    [InlineData(0.79f, ConfidenceTier.Tier2_Medium)]
    [InlineData(0.50f, ConfidenceTier.Tier2_Medium)]
    [InlineData(0.49f, ConfidenceTier.Tier3_Low)]
    [InlineData(0.25f, ConfidenceTier.Tier3_Low)]
    [InlineData(0.24f, ConfidenceTier.FilteredNoise)]
    [InlineData(0.05f, ConfidenceTier.FilteredNoise)]
    public void FromConfidence_CorrectTierMapping(float score, ConfidenceTier expectedTier)
    {
        var tier = ConfidenceTierExtensions.FromConfidence(score);
        Assert.Equal(expectedTier, tier);
    }

    [Fact]
    public void Summarize_AccuratelyGroupsTiers()
    {
        var list = new[]
        {
            new AstrophotographyObject(1, new BoundingBox(0,0,5,5), 0.90f),
            new AstrophotographyObject(2, new BoundingBox(10,10,5,5), 0.60f),
            new AstrophotographyObject(3, new BoundingBox(20,20,5,5), 0.30f),
            new AstrophotographyObject(4, new BoundingBox(30,30,5,5), 0.15f)
        };

        var summary = ConfidenceClassifier.Summarize(list);

        Assert.Equal(4, summary.TotalCount);
        Assert.Equal(1, summary.Tier1HighCount);
        Assert.Equal(1, summary.Tier2MediumCount);
        Assert.Equal(1, summary.Tier3LowCount);
        Assert.Equal(1, summary.NoiseCount);
    }

    [Fact]
    public void FilteredNoise_UsesRedColorHex()
    {
        Assert.Equal("#EF4444", ConfidenceTier.FilteredNoise.GetColorHex());
    }

    [Fact]
    public void DetectionResult_FiltersBelow25PercentCorrectly()
    {
        var list = new[]
        {
            new AstrophotographyObject(1, new BoundingBox(0,0,5,5), 0.90f),
            new AstrophotographyObject(2, new BoundingBox(10,10,5,5), 0.60f),
            new AstrophotographyObject(3, new BoundingBox(20,20,5,5), 0.30f),
            new AstrophotographyObject(4, new BoundingBox(30,30,5,5), 0.22f), // 22% (between 20-25%)
            new AstrophotographyObject(5, new BoundingBox(40,40,5,5), 0.12f)  // 12% (<20%)
        };

        var result = new DetectionResult(list, 1000, 1000, 1, TimeSpan.Zero, "CPU");

        // Normal threshold at 0.25 -> only objects 1, 2, 3
        var normalFilter = result.Filter(0.25f);
        Assert.Equal(3, normalFilter.TotalCount);
        Assert.Equal(0, normalFilter.NoiseCount);

        // Low threshold at 0.20 -> includes 22% object
        var yellowFilter = result.Filter(0.20f);
        Assert.Equal(4, yellowFilter.TotalCount);
        Assert.Equal(1, yellowFilter.NoiseCount);

        // Speculative threshold at 0.10 -> includes both 22% and 12% objects
        var redFilter = result.Filter(0.10f);
        Assert.Equal(5, redFilter.TotalCount);
        Assert.Equal(2, redFilter.NoiseCount);
    }
}

