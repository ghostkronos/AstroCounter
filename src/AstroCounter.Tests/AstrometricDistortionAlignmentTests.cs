namespace AstroCounter.Tests;

using System;
using System.Collections.Generic;
using AstroCounter.Core.Astrometry;
using AstroCounter.Core.Models;
using Xunit;

public class AstrometricDistortionAlignmentTests
{
    [Fact]
    public void WcsMatrix_NewtonRaphson_SubPixelRoundtrip_WithRadialDistortion()
    {
        // Setup WCS with non-linear barrel/pincushion radial distortion (k1 = 0.05, k2 = -0.01)
        var center = new CelestialCoordinates(180.0, 45.0);
        int w = 4000;
        int h = 3000;
        var wcs = new WcsMatrix(
            center,
            referencePixelX: w * 0.5,
            referencePixelY: h * 0.5,
            pixelScaleArcsec: 1.5,
            rotationDegrees: 35.0,
            imageWidth: w,
            imageHeight: h,
            radialDistortionK1: 0.05,
            radialDistortionK2: -0.01);

        // Test points across frame: Center, mid-frame, outer frame, and corners
        var testPixels = new (double X, double Y)[]
        {
            (2000, 1500),
            (1000, 800),
            (3000, 2200),
            (200, 150),
            (3800, 2850),
            (50, 50),
            (3950, 2950)
        };

        foreach (var (origX, origY) in testPixels)
        {
            // Forward: Pixel -> Celestial
            var eq = wcs.PixelToEquatorial(origX, origY);

            // Inverse: Celestial -> Pixel (uses bounded Newton-Raphson)
            bool success = wcs.EquatorialToPixel(eq, out double invX, out double invY);

            Assert.True(success, $"EquatorialToPixel failed for pixel ({origX}, {origY})");
            Assert.False(double.IsNaN(invX) || double.IsNaN(invY), "Inversion returned NaN");

            double errX = Math.Abs(invX - origX);
            double errY = Math.Abs(invY - origY);
            double distErr = Math.Sqrt((errX * errX) + (errY * errY));

            // Must invert with sub-pixel precision (< 0.01 pixels)
            Assert.True(distErr < 0.01, $"Inversion error {distErr:F5}px exceeded 0.01px tolerance at ({origX}, {origY})");
        }
    }

    [Fact]
    public void WcsMatrix_NewtonRaphson_ExtremePoints_NeverProducesNaNOrHangs()
    {
        var center = new CelestialCoordinates(0.0, 0.0);
        var wcs = new WcsMatrix(
            center,
            referencePixelX: 1000,
            referencePixelY: 1000,
            pixelScaleArcsec: 2.0,
            rotationDegrees: 0.0,
            imageWidth: 2000,
            imageHeight: 2000,
            radialDistortionK1: 0.20, // Extreme distortion
            radialDistortionK2: 0.10);

        // Test coordinates far away or extreme
        var farEq = new CelestialCoordinates(30.0, 30.0);
        bool res = wcs.EquatorialToPixel(farEq, out double px, out double py);

        // Even if outside or extreme, must NEVER return NaN or hang in infinite loop
        Assert.False(double.IsNaN(px), "px should never be NaN");
        Assert.False(double.IsNaN(py), "py should never be NaN");
    }

    [Fact]
    public void WcsMatrix_LinearFallback_EngagesWhenDistortionDegenerates()
    {
        var center = new CelestialCoordinates(45.0, -10.0);
        var wcs = new WcsMatrix(
            center,
            referencePixelX: 500,
            referencePixelY: 500,
            pixelScaleArcsec: 1.0,
            rotationDegrees: 15.0,
            imageWidth: 1000,
            imageHeight: 1000,
            radialDistortionK1: -0.22, // Heavy compression
            radialDistortionK2: -0.15);

        var coord = wcs.PixelToEquatorial(800, 800);
        bool ok = wcs.EquatorialToPixel(coord, out double rx, out double ry);

        Assert.True(ok);
        Assert.False(double.IsNaN(rx));
        Assert.False(double.IsNaN(ry));
    }
}

