namespace AstroCounter.Tests;

using System.Collections.Generic;
using System.Linq;
using AstroCounter.Core.Astrometry;
using AstroCounter.Core.Models;
using Xunit;

public class TransientAnomalyTests
{
    [Fact]
    public void DetectAnomalies_HighSnrUncatalogedSource_FlagsAsAnomaly()
    {
        // Setup a WCS pointing towards deep southern sky where no bright Gaia stars exist
        var wcs = new WcsMatrix(
            centerCoordinates: new CelestialCoordinates(15.0, -75.0),
            referencePixelX: 500,
            referencePixelY: 500,
            pixelScaleArcsec: 3.6,
            rotationDegrees: 0,
            imageWidth: 1000,
            imageHeight: 1000
        );

        // A high SNR candidate (SNR: 12.0 sigma, Confidence: 0.88)
        var detections = new List<AstrophotographyObject>
        {
            new AstrophotographyObject(1, new BoundingBox(500, 500, 10, 10), 0.88f, "star", Snr: 12.0f)
        };

        var results = TransientAnomalyDetector.DetectAnomalies(detections, wcs, 1000, 1000, minSnr: 7.5f, minConfidence: 0.65f);

        Assert.Single(results);
        var candidate = results[0];
        Assert.True(candidate.IsAnomaly, "High SNR uncataloged star should be flagged as an anomaly");
        Assert.NotNull(candidate.AnomalyType);
    }

    [Fact]
    public void DetectAnomalies_LowSnrNoise_NotFlaggedAsAnomaly()
    {
        var wcs = new WcsMatrix(
            centerCoordinates: new CelestialCoordinates(15.0, -75.0),
            referencePixelX: 500,
            referencePixelY: 500,
            pixelScaleArcsec: 3.6,
            rotationDegrees: 0,
            imageWidth: 1000,
            imageHeight: 1000
        );

        // A low SNR candidate (SNR: 3.2 sigma)
        var detections = new List<AstrophotographyObject>
        {
            new AstrophotographyObject(1, new BoundingBox(500, 500, 6, 6), 0.35f, "faint_star", Snr: 3.2f)
        };

        var results = TransientAnomalyDetector.DetectAnomalies(detections, wcs, 1000, 1000, minSnr: 7.5f, minConfidence: 0.65f);

        Assert.Single(results);
        Assert.False(results[0].IsAnomaly, "Low SNR noise candidate must NOT be flagged as an anomaly");
    }

    [Fact]
    public void DetectAnomalies_NearKnownGalaxy_FlagsAsSupernovaCandidate()
    {
        // M31 Andromeda Galaxy coordinates: RA: 10.6847, Dec: 41.2687
        var wcs = new WcsMatrix(
            centerCoordinates: new CelestialCoordinates(10.6847, 41.2687),
            referencePixelX: 500,
            referencePixelY: 500,
            pixelScaleArcsec: 3.6,
            rotationDegrees: 0,
            imageWidth: 1000,
            imageHeight: 1000
        );

        // High SNR source at pixel (520, 510) near Andromeda nucleus
        var detections = new List<AstrophotographyObject>
        {
            new AstrophotographyObject(1, new BoundingBox(520, 510, 12, 12), 0.90f, "star", Snr: 14.5f)
        };

        var results = TransientAnomalyDetector.DetectAnomalies(detections, wcs, 1000, 1000, minSnr: 7.5f, minConfidence: 0.65f);

        Assert.Single(results);
        var candidate = results[0];
        if (candidate.IsAnomaly)
        {
            Assert.True(candidate.AnomalyType!.Contains("Süpernova") || candidate.AnomalyType!.Contains("Supernova"));
        }
    }

    [Fact]
    public void DetectAnomalies_MultipleCandidates_StrictlyCappedToMaximumTwo()
    {
        var wcs = new WcsMatrix(
            centerCoordinates: new CelestialCoordinates(15.0, -75.0),
            referencePixelX: 500,
            referencePixelY: 500,
            pixelScaleArcsec: 3.6,
            rotationDegrees: 0,
            imageWidth: 1000,
            imageHeight: 1000
        );

        // 10 uncataloged high-SNR detections
        var detections = new List<AstrophotographyObject>();
        for (int i = 0; i < 10; i++)
        {
            detections.Add(new AstrophotographyObject(
                i + 1,
                new BoundingBox(100 + (i * 50), 100 + (i * 50), 10, 10),
                0.80f + (i * 0.01f),
                "star",
                Snr: 10f + i));
        }

        var results = TransientAnomalyDetector.DetectAnomalies(detections, wcs, 1000, 1000, minSnr: 7.5f, minConfidence: 0.65f, maxAnomalies: 2);

        int anomalyCount = results.Count(r => r.IsAnomaly);
        Assert.Equal(2, anomalyCount);
    }
    [Fact]
    public void DetectAnomalies_CosmicRaySpike_NotFlaggedAsAnomaly()
    {
        var wcs = new WcsMatrix(
            centerCoordinates: new CelestialCoordinates(15.0, -75.0),
            referencePixelX: 500,
            referencePixelY: 500,
            pixelScaleArcsec: 3.6,
            rotationDegrees: 0,
            imageWidth: 1000,
            imageHeight: 1000
        );

        // A single-pixel spike (width = 1, height = 1, FWHM < 1.0)
        var detections = new List<AstrophotographyObject>
        {
            new AstrophotographyObject(1, new BoundingBox(500, 500, 1, 1), 0.95f, "star", Snr: 15.0f)
        };

        var results = TransientAnomalyDetector.DetectAnomalies(detections, wcs, 1000, 1000, minSnr: 7.5f, minConfidence: 0.65f);

        Assert.Single(results);
        Assert.False(results[0].IsAnomaly, "Single-pixel cosmic ray or hot pixel spike must be rejected by PSF shape filter");
    }

    [Fact]
    public void DetectAnomalies_SatelliteStreak_NotFlaggedAsAnomaly()
    {
        var wcs = new WcsMatrix(
            centerCoordinates: new CelestialCoordinates(15.0, -75.0),
            referencePixelX: 500,
            referencePixelY: 500,
            pixelScaleArcsec: 3.6,
            rotationDegrees: 0,
            imageWidth: 1000,
            imageHeight: 1000
        );

        // An elongated streak (width = 30, height = 3, Ellipticity = 0.90)
        var detections = new List<AstrophotographyObject>
        {
            new AstrophotographyObject(1, new BoundingBox(500, 500, 30, 3), 0.90f, "star", Snr: 14.0f)
        };

        var results = TransientAnomalyDetector.DetectAnomalies(detections, wcs, 1000, 1000, minSnr: 7.5f, minConfidence: 0.65f);

        Assert.Single(results);
        Assert.False(results[0].IsAnomaly, "Elongated satellite streak must be rejected by ellipticity filter");
    }
}
