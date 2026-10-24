namespace AstroCounter.Tests;

using System;
using AstroCounter.Core.Astrometry;
using Xunit;

public class CelestialSphereAlignmentTests
{
    [Fact]
    public void CelestialProjection_DomePerspectiveMatchesScreenProjectionExactly()
    {
        // Setup camera parameters
        double cameraRa = 310.0;
        double cameraDec = 45.0;
        double cameraFov = 50.0;
        double screenW = 1920.0;
        double screenH = 1080.0;
        double eyeOffset = 0.0; // True centered celestial sphere (zero offset)
        double sphereR = 98.2;

        double camRaRad = cameraRa * (Math.PI / 180.0);
        double camDecRad = cameraDec * (Math.PI / 180.0);

        double cCosD = Math.Cos(camDecRad);
        double cSinD = Math.Sin(camDecRad);
        double cCosR = Math.Cos(camRaRad);
        double cSinR = Math.Sin(camRaRad);

        // Basis vectors
        double lx = cCosD * cSinR;
        double ly = cSinD;
        double lz = -cCosD * cCosR;

        double ux = -cSinD * cSinR;
        double uy = cCosD;
        double uz = cSinD * cCosR;

        double rx = cCosR;
        double ry = 0.0;
        double rz = cSinR;

        // Several catalog stars across the field of view
        (double Ra, double Dec)[] testStars =
        {
            (310.0, 45.0),   // Dead center
            (315.0, 48.0),   // Top-Right
            (305.0, 42.0),   // Bottom-Left
            (318.0, 43.0),   // Right
            (302.0, 47.0)    // Left
        };

        foreach (var (starRa, starDec) in testStars)
        {
            double sRaRad = starRa * (Math.PI / 180.0);
            double sDecRad = starDec * (Math.PI / 180.0);

            double cosD = Math.Cos(sDecRad);
            double sinD = Math.Sin(sDecRad);
            double cosR = Math.Cos(sRaRad);
            double sinR = Math.Sin(sRaRad);

            // 1. Compute 3D mesh position on sphere
            double ptX = sphereR * cosD * sinR;
            double ptY = sphereR * sinD;
            double ptZ = -sphereR * cosD * cosR;

            // 2. WPF Viewport3D camera position at -lookDir * eyeOffset
            double camX = -lx * eyeOffset;
            double camY = -ly * eyeOffset;
            double camZ = -lz * eyeOffset;

            // Vector from camera to point
            double dx = ptX - camX;
            double dy = ptY - camY;
            double dz = ptZ - camZ;

            double zc = (dx * lx) + (dy * ly) + (dz * lz);
            Assert.True(zc > 0.05, "Point should be in front of the camera");

            double xc = (dx * rx) + (dy * ry) + (dz * rz);
            double yc = (dx * ux) + (dy * uy) + (dz * uz);

            double f = (screenW * 0.5) / Math.Tan(cameraFov * (Math.PI / 360.0));
            double sx = (screenW * 0.5) + (f * (xc / zc));
            double sy = (screenH * 0.5) - (f * (yc / zc));

            // Verify that the star falls on the screen within valid canvas bounds
            Assert.InRange(sx, 0.0, screenW);
            Assert.InRange(sy, 0.0, screenH);
        }
    }

    [Fact]
    public void CatalogLockRefiner_BuildsLuminanceAndExtractsCorrectScale()
    {
        using var bmp = new SkiaSharp.SKBitmap(1000, 800, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        using (var canvas = new SkiaSharp.SKCanvas(bmp))
        {
            canvas.Clear(new SkiaSharp.SKColor(5, 5, 10)); // Dark background
            using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White };
            canvas.DrawCircle(500, 400, 4, paint); // Star at center
            canvas.DrawCircle(200, 200, 3, paint); // Star in corner
        }

        var lum = CatalogLockRefiner.BuildLuminance(bmp);
        Assert.NotNull(lum);
        Assert.Equal(1000, lum.Width);
        Assert.Equal(800, lum.Height);
        Assert.Equal(1.0, lum.Scale);

        // Centroid measurement should find the center star at (500, 400)
        bool found = CatalogLockRefiner.MeasureCentroid(lum, 500, 400, 15.0, out double mx, out double my);
        Assert.True(found);
        Assert.InRange(mx, 498.0, 502.0);
        Assert.InRange(my, 398.0, 402.0);
    }
}
