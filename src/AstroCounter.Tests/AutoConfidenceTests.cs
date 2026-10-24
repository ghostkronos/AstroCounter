namespace AstroCounter.Tests;

using System.Collections.Generic;
using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using Xunit;

public class AutoConfidenceTests
{
    [Fact]
    public void ComputeOptimalThreshold_EmptyList_ReturnsDefaultSafeThreshold()
    {
        var emptyList = new List<AstrophotographyObject>();
        float thresholdTiff = AutoConfidenceCalculator.ComputeOptimalThreshold(emptyList, isCompressedFormat: false);
        float thresholdJpg = AutoConfidenceCalculator.ComputeOptimalThreshold(emptyList, isCompressedFormat: true);

        Assert.Equal(0.25f, thresholdTiff);
        Assert.Equal(0.35f, thresholdJpg);
    }

    [Fact]
    public void ComputeOptimalThreshold_CleanDeepSkyStack_SelectsSensitiveThreshold()
    {
        // Simulate clean deep sky stack: many real stars with SNR 3.5 to 12.0 sigma, few noise spikes
        var objects = new List<AstrophotographyObject>();
        int id = 1;

        // A few noise floor items
        for (int i = 0; i < 5; i++)
        {
            objects.Add(new AstrophotographyObject(id++, new BoundingBox(i * 10, 10, 4, 4), 0.18f, Snr: 2.1f));
        }

        // Many genuine faint stars (SNR 2.8 - 5.0)
        for (int i = 0; i < 40; i++)
        {
            objects.Add(new AstrophotographyObject(id++, new BoundingBox(i * 10, 50, 6, 6), 0.35f, Snr: 3.5f + (i * 0.1f)));
        }

        // Several prominent stars (SNR 6 - 12)
        for (int i = 0; i < 20; i++)
        {
            objects.Add(new AstrophotographyObject(id++, new BoundingBox(i * 15, 100, 10, 10), 0.75f, Snr: 6.0f + (i * 0.3f)));
        }

        float optimalThreshold = AutoConfidenceCalculator.ComputeOptimalThreshold(objects, isCompressedFormat: false);

        Assert.InRange(optimalThreshold, 0.15f, 0.30f);
    }

    [Fact]
    public void ComputeOptimalThreshold_CompressedJpeg_AdaptsToHigherThreshold()
    {
        // Simulate noisy compressed JPEG: heavy noise surge below 3.5 sigma
        var objects = new List<AstrophotographyObject>();
        int id = 1;

        // Heavy noise surge (JPG block artifacts)
        for (int i = 0; i < 80; i++)
        {
            objects.Add(new AstrophotographyObject(id++, new BoundingBox(i * 5, 10, 3, 3), 0.15f, Snr: 1.5f + (i % 5 * 0.3f)));
        }

        // Some bright stars
        for (int i = 0; i < 15; i++)
        {
            objects.Add(new AstrophotographyObject(id++, new BoundingBox(i * 20, 100, 10, 10), 0.85f, Snr: 7.0f + i));
        }

        float optimalThreshold = AutoConfidenceCalculator.ComputeOptimalThreshold(objects, isCompressedFormat: true);

        // Should adapt to higher threshold to reject JPEG compression noise (> 0.25)
        Assert.True(optimalThreshold >= 0.25f, $"Expected threshold >= 0.25 for noisy JPEG, but got {optimalThreshold}");
    }
}

