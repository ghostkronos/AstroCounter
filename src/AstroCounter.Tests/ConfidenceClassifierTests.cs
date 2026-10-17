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
}
