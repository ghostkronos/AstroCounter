namespace AstroCounter.Core.Astrometry;

using System;

/// <summary>
/// Implements standard Astronomical World Coordinate System (WCS) gnomonic (TAN) projection.
/// Maps 2D pixel coordinates (X, Y) to celestial Right Ascension and Declination (RA, Dec),
/// and vice versa.
/// </summary>
public class WcsMatrix
{
    public CelestialCoordinates CenterCoordinates { get; }
    public double ReferencePixelX { get; }
    public double ReferencePixelY { get; }
    public double PixelScaleArcsec { get; }
    public double RotationDegrees { get; }
    public int ImageWidth { get; }
    public int ImageHeight { get; }

    // Derived CD matrix elements (degrees per pixel)
    private readonly double _cd11;
    private readonly double _cd12;
    private readonly double _cd21;
    private readonly double _cd22;
    private readonly double _detCd;

    public double FovWidthDegrees => (ImageWidth * PixelScaleArcsec) / 3600.0;
    public double FovHeightDegrees => (ImageHeight * PixelScaleArcsec) / 3600.0;

    public WcsMatrix(
        CelestialCoordinates centerCoordinates,
        double referencePixelX,
        double referencePixelY,
        double pixelScaleArcsec,
        double rotationDegrees,
        int imageWidth,
        int imageHeight)
    {
        CenterCoordinates = centerCoordinates;
        ReferencePixelX = referencePixelX;
        ReferencePixelY = referencePixelY;
        PixelScaleArcsec = Math.Max(0.001, pixelScaleArcsec);
        RotationDegrees = rotationDegrees;
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;

        // Pixel scale in degrees
        double degPerPixel = PixelScaleArcsec / 3600.0;
        double rotRad = RotationDegrees * (Math.PI / 180.0);
        double cosR = Math.Cos(rotRad);
        double sinR = Math.Sin(rotRad);

        // Standard FITS CD matrix adapted for screen coordinates (origin top-left, +X right/West, +Y down/South):
        // East (increasing RA) is left (-X), North (increasing Dec) is up (-Y).
        _cd11 = -degPerPixel * cosR;
        _cd12 = degPerPixel * sinR;
        _cd21 = -degPerPixel * sinR;
        _cd22 = -degPerPixel * cosR;

        _detCd = (_cd11 * _cd22) - (_cd12 * _cd21);
    }

    /// <summary>
    /// Returns a new WcsMatrix with offset center coordinates, scaled pixel scale, and adjusted rotation angle.
    /// Enables fine-tuning and 1-to-1 live alignment.
    /// </summary>
    public WcsMatrix WithAdjustment(double deltaRaDeg, double deltaDecDeg, double scaleMultiplier, double deltaRotationDeg)
    {
        var newCenter = new CelestialCoordinates(
            CenterCoordinates.RaDegrees + deltaRaDeg,
            Math.Clamp(CenterCoordinates.DecDegrees + deltaDecDeg, -90.0, 90.0));

        double newScale = Math.Max(0.01, PixelScaleArcsec * Math.Max(0.1, scaleMultiplier));
        double newRot = (RotationDegrees + deltaRotationDeg) % 360.0;
        if (newRot < 0) newRot += 360.0;

        return new WcsMatrix(
            newCenter,
            ReferencePixelX,
            ReferencePixelY,
            newScale,
            newRot,
            ImageWidth,
            ImageHeight);
    }

    /// <summary>
    /// Computes the 4 corners in celestial coordinates (RA, Dec) in counter-clockwise order:
    /// [0] Top-Left (0, 0), [1] Bottom-Left (0, H), [2] Bottom-Right (W, H), [3] Top-Right (W, 0).
    /// Used directly by Stellarium's core.loadSkyImage warp mapping.
    /// </summary>
    public (CelestialCoordinates TopLeft, CelestialCoordinates BottomLeft, CelestialCoordinates BottomRight, CelestialCoordinates TopRight) GetCornerCoordinates()
    {
        var tl = PixelToEquatorial(0, 0);
        var bl = PixelToEquatorial(0, ImageHeight);
        var br = PixelToEquatorial(ImageWidth, ImageHeight);
        var tr = PixelToEquatorial(ImageWidth, 0);
        return (tl, bl, br, tr);
    }

    /// <summary>
    /// Converts image pixel coordinates (X, Y) to celestial (RA, Dec) coordinates using gnomonic (TAN) projection.
    /// </summary>
    public CelestialCoordinates PixelToEquatorial(double x, double y)
    {
        double dx = x - ReferencePixelX;
        double dy = y - ReferencePixelY;

        // Intermediate celestial coordinates (xi, eta) in degrees on the tangent plane
        double xiDeg = (_cd11 * dx) + (_cd12 * dy);
        double etaDeg = (_cd21 * dx) + (_cd22 * dy);

        double xi = xiDeg * (Math.PI / 180.0);
        double eta = etaDeg * (Math.PI / 180.0);

        double a0 = CenterCoordinates.RaRadians;
        double d0 = CenterCoordinates.DecRadians;

        double sinD0 = Math.Sin(d0);
        double cosD0 = Math.Cos(d0);

        // De-project tangent plane (TAN) back to spherical celestial coordinates
        double denom = cosD0 - (eta * sinD0);
        if (Math.Abs(denom) < 1e-12) denom = 1e-12;

        double raRad = a0 + Math.Atan2(xi, denom);
        double decRad = Math.Atan2((sinD0 + (eta * cosD0)) * Math.Cos(raRad - a0), denom);

        return CelestialCoordinates.FromRadians(raRad, decRad);
    }

    /// <summary>
    /// Projects celestial (RA, Dec) coordinates onto the image plane to find pixel (X, Y).
    /// Returns false if the coordinates lie behind the tangent plane.
    /// </summary>
    public bool EquatorialToPixel(CelestialCoordinates coord, out double x, out double y)
    {
        double a = coord.RaRadians;
        double d = coord.DecRadians;
        double a0 = CenterCoordinates.RaRadians;
        double d0 = CenterCoordinates.DecRadians;

        double sinD = Math.Sin(d);
        double cosD = Math.Cos(d);
        double sinD0 = Math.Sin(d0);
        double cosD0 = Math.Cos(d0);
        double dRa = a - a0;

        double cosC = (sinD0 * sinD) + (cosD0 * cosD * Math.Cos(dRa));
        if (cosC <= 0)
        {
            // Point is on the opposite side of the celestial sphere
            x = 0;
            y = 0;
            return false;
        }

        // Tangent plane projection (xi, eta) in radians
        double xi = (cosD * Math.Sin(dRa)) / cosC;
        double eta = ((cosD0 * sinD) - (sinD0 * cosD * Math.Cos(dRa))) / cosC;

        // Convert radians to degrees
        double xiDeg = xi * (180.0 / Math.PI);
        double etaDeg = eta * (180.0 / Math.PI);

        if (Math.Abs(_detCd) < 1e-15)
        {
            x = ReferencePixelX;
            y = ReferencePixelY;
            return false;
        }

        // Inverse CD matrix transformation
        double dx = ((_cd22 * xiDeg) - (_cd12 * etaDeg)) / _detCd;
        double dy = ((-_cd21 * xiDeg) + (_cd11 * etaDeg)) / _detCd;

        x = ReferencePixelX + dx;
        y = ReferencePixelY + dy;
        return true;
    }

    public record SphericalGridLine(
        bool IsRightAscension,
        double AngleDegrees,
        string Label,
        System.Numerics.Vector2[] Points);

    /// <summary>
    /// Generates curved spherical celestial coordinate grid lines (RA meridians and Dec parallels)
    /// projected onto the image sensor plane using the gnomonic TAN matrix.
    /// </summary>
    public IReadOnlyList<SphericalGridLine> GenerateSphericalGrid()
    {
        var lines = new List<SphericalGridLine>();

        double fov = Math.Max(FovWidthDegrees, FovHeightDegrees);
        if (fov <= 0 || ImageWidth <= 0 || ImageHeight <= 0) return lines;

        double stepDeg = fov switch
        {
            > 25.0 => 5.0,
            > 10.0 => 2.0,
            > 4.0 => 1.0,
            > 1.5 => 0.5,
            > 0.8 => 0.25,
            _ => 0.1
        };

        // Determine RA / Dec span around center
        double centerRa = CenterCoordinates.RaDegrees;
        double centerDec = CenterCoordinates.DecDegrees;
        double halfFov = (fov * 0.8) + stepDeg;

        double minDec = Math.Max(-89.0, centerDec - halfFov);
        double maxDec = Math.Min(89.0, centerDec + halfFov);

        // 1. Generate Right Ascension Meridians (lines of constant RA)
        double startRa = (Math.Floor((centerRa - halfFov) / stepDeg) * stepDeg);
        double endRa = (Math.Ceiling((centerRa + halfFov) / stepDeg) * stepDeg);

        for (double ra = startRa; ra <= endRa; ra += stepDeg)
        {
            double normalizedRa = ((ra % 360.0) + 360.0) % 360.0;
            var pts = new List<System.Numerics.Vector2>();
            int samples = 35;

            for (int i = 0; i <= samples; i++)
            {
                double d = minDec + (i * (maxDec - minDec) / samples);
                if (EquatorialToPixel(new CelestialCoordinates(normalizedRa, d), out double px, out double py))
                {
                    pts.Add(new System.Numerics.Vector2((float)px, (float)py));
                }
            }

            if (pts.Count >= 2 && AnyInBounds(pts))
            {
                var coord = new CelestialCoordinates(normalizedRa, centerDec);
                lines.Add(new SphericalGridLine(true, normalizedRa, coord.FormatRa(), pts.ToArray()));
            }
        }

        // 2. Generate Declination Parallels (curved arcs of constant Dec)
        double startDec = (Math.Floor(minDec / stepDeg) * stepDeg);
        double endDec = (Math.Ceiling(maxDec / stepDeg) * stepDeg);

        for (double dec = startDec; dec <= endDec; dec += stepDeg)
        {
            if (dec < -89.5 || dec > 89.5) continue;

            var pts = new List<System.Numerics.Vector2>();
            int samples = 45;

            for (int i = 0; i <= samples; i++)
            {
                double r = (centerRa - halfFov) + (i * (2.0 * halfFov) / samples);
                double normalizedRa = ((r % 360.0) + 360.0) % 360.0;

                if (EquatorialToPixel(new CelestialCoordinates(normalizedRa, dec), out double px, out double py))
                {
                    pts.Add(new System.Numerics.Vector2((float)px, (float)py));
                }
            }

            if (pts.Count >= 2 && AnyInBounds(pts))
            {
                var coord = new CelestialCoordinates(centerRa, dec);
                lines.Add(new SphericalGridLine(false, dec, coord.FormatDec(), pts.ToArray()));
            }
        }

        return lines;
    }

    private bool AnyInBounds(List<System.Numerics.Vector2> pts)
    {
        double margin = Math.Max(ImageWidth, ImageHeight) * 0.2;
        foreach (var p in pts)
        {
            if (p.X >= -margin && p.X <= ImageWidth + margin &&
                p.Y >= -margin && p.Y <= ImageHeight + margin)
            {
                return true;
            }
        }
        return false;
    }
}

