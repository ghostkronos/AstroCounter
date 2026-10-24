namespace AstroCounter.Tests;

using System;
using System.Collections.Generic;
using AstroCounter.Core.Astrometry;
using Xunit;

public class ThinPlateSplineWarpTests
{
    [Fact]
    public void ThinPlateSpline_InterpolatesAnchorPointsAccurately()
    {
        int width = 4000;
        int height = 3000;

        // Realistic anchor stars across image with optical residual deformations
        var points = new List<ThinPlateSplineWarp.ControlPoint>
        {
            new(500, 400, 503.2, 397.8),
            new(1200, 800, 1201.5, 802.1),
            new(2000, 1500, 1999.8, 1499.5), // Center
            new(3200, 600, 3205.1, 596.2),
            new(800, 2400, 796.4, 2404.3),
            new(2800, 2200, 2806.0, 2195.0),
            new(3600, 2600, 3608.2, 2593.5),
            new(1500, 2000, 1501.0, 2000.5)
        };

        var spline = ThinPlateSplineWarp.Create(points, width, height, regularization: 1e-5);
        Assert.NotNull(spline);
        Assert.True(spline.PointCount >= 8);

        // Verify that every anchor star point lands on its target within 0.1 pixel
        foreach (var p in points)
        {
            var (wx, wy) = spline.Warp(p.SourceX, p.SourceY);
            Assert.Equal(p.TargetX, wx, precision: 1);
            Assert.Equal(p.TargetY, wy, precision: 1);
        }
    }

    [Fact]
    public void ThinPlateSpline_UnwarpInvertsWarpAccurately()
    {
        int width = 3000;
        int height = 2000;

        var points = new List<ThinPlateSplineWarp.ControlPoint>
        {
            new(400, 300, 402.5, 298.0),
            new(1500, 1000, 1500.2, 1000.8),
            new(2600, 500, 2604.1, 497.2),
            new(600, 1700, 597.5, 1703.1),
            new(2400, 1600, 2405.0, 1596.0)
        };

        var spline = ThinPlateSplineWarp.Create(points, width, height, regularization: 1e-4);
        Assert.NotNull(spline);

        (double x, double y)[] testLocations =
        {
            (1500, 1000),
            (800, 600),
            (2200, 1400),
            (1000, 1500)
        };

        foreach (var (x, y) in testLocations)
        {
            var (wx, wy) = spline.Warp(x, y);
            var (unX, unY) = spline.Unwarp(wx, wy);

            Assert.Equal(x, unX, precision: 1);
            Assert.Equal(y, unY, precision: 1);
        }
    }

    [Fact]
    public void WcsMatrix_WithSplineWarp_SnapsImageStarsDirectlyToCatalogCoordinates()
    {
        var center = new CelestialCoordinates(10.6847, 41.2687);
        int width = 4000;
        int height = 3000;

        // Base WCS matrix
        var baseWcs = new WcsMatrix(
            center,
            width / 2.0,
            height / 2.0,
            pixelScaleArcsec: 1.8,
            rotationDegrees: 12.0,
            imageWidth: width,
            imageHeight: height);

        // Ground-truth star at sensor pixel (750, 620)
        double sensorX = 750.0;
        double sensorY = 620.0;

        // Suppose true catalog star position projects to (755.2, 617.8) under base WCS due to lens optical coma/tilt
        double catalogProjectedX = 755.2;
        double catalogProjectedY = 617.8;
        var trueCatalogCoord = baseWcs.PixelToEquatorial(catalogProjectedX, catalogProjectedY);

        var points = new List<ThinPlateSplineWarp.ControlPoint>
        {
            new(sensorX, sensorY, catalogProjectedX, catalogProjectedY),
            new(1200, 1100, 1202.0, 1099.0),
            new(2000, 1500, 2000.0, 1500.0),
            new(3100, 800, 3104.0, 797.0),
            new(2800, 2400, 2803.0, 2398.0)
        };

        var spline = ThinPlateSplineWarp.Create(points, width, height, regularization: 1e-5);
        Assert.NotNull(spline);

        var warpedWcs = new WcsMatrix(
            baseWcs.CenterCoordinates,
            baseWcs.ReferencePixelX,
            baseWcs.ReferencePixelY,
            baseWcs.PixelScaleArcsec,
            baseWcs.RotationDegrees,
            width,
            height,
            splineWarp: spline);

        // 1. When converting image pixel (sensorX, sensorY) to equatorial:
        // It should match trueCatalogCoord with extreme sub-arcsecond precision
        var computedCoord = warpedWcs.PixelToEquatorial(sensorX, sensorY);
        Assert.Equal(trueCatalogCoord.RaDegrees, computedCoord.RaDegrees, precision: 4);
        Assert.Equal(trueCatalogCoord.DecDegrees, computedCoord.DecDegrees, precision: 4);

        // 2. When converting trueCatalogCoord to pixel on image:
        // It should land exactly back on sensorX, sensorY
        bool success = warpedWcs.EquatorialToPixel(trueCatalogCoord, out double backX, out double backY);
        Assert.True(success);
        Assert.Equal(sensorX, backX, precision: 1);
        Assert.Equal(sensorY, backY, precision: 1);
    }
}

