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
    public double RadialDistortionK1 { get; }
    public double RadialDistortionK2 { get; }
    public ThinPlateSplineWarp? SplineWarp { get; }
    public bool IsFlippedHorizontally { get; }

    // Derived CD matrix elements (degrees per pixel)
    private readonly double _cd11;
    private readonly double _cd12;
    private readonly double _cd21;
    private readonly double _cd22;
    private readonly double _detCd;
    private readonly double _normRadius;

    public double FovWidthDegrees => (ImageWidth * PixelScaleArcsec) / 3600.0;
    public double FovHeightDegrees => (ImageHeight * PixelScaleArcsec) / 3600.0;

    public WcsMatrix(
        CelestialCoordinates centerCoordinates,
        double referencePixelX,
        double referencePixelY,
        double pixelScaleArcsec,
        double rotationDegrees,
        int imageWidth,
        int imageHeight,
        double radialDistortionK1 = 0.0,
        double radialDistortionK2 = 0.0,
        ThinPlateSplineWarp? splineWarp = null,
        bool isFlippedHorizontally = false)
    {
        CenterCoordinates = centerCoordinates;
        ReferencePixelX = referencePixelX;
        ReferencePixelY = referencePixelY;
        PixelScaleArcsec = Math.Max(0.001, pixelScaleArcsec);
        RotationDegrees = rotationDegrees;
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        RadialDistortionK1 = radialDistortionK1;
        RadialDistortionK2 = radialDistortionK2;
        SplineWarp = splineWarp;
        IsFlippedHorizontally = isFlippedHorizontally;
        _normRadius = Math.Max(1.0, 0.5 * Math.Sqrt(((double)imageWidth * imageWidth) + ((double)imageHeight * imageHeight)));

        // Pixel scale in degrees
        double degPerPixel = PixelScaleArcsec / 3600.0;
        double rotRad = RotationDegrees * (Math.PI / 180.0);
        double cosR = Math.Cos(rotRad);
        double sinR = Math.Sin(rotRad);

        // Standard FITS CD matrix adapted for screen coordinates (origin top-left, +X right/West, +Y down/South):
        // East (increasing RA) is left (-X), North (increasing Dec) is up (-Y).
        // If flipped horizontally (mirrored parity, e.g. diagonal prism/mirror), X axis sign is inverted.
        double parityX = IsFlippedHorizontally ? 1.0 : -1.0;
        _cd11 = parityX * degPerPixel * cosR;
        _cd12 = -parityX * degPerPixel * sinR;
        _cd21 = -degPerPixel * sinR;
        _cd22 = -degPerPixel * cosR;

        _detCd = (_cd11 * _cd22) - (_cd12 * _cd21);
    }

    /// <summary>
    /// Returns a new WcsMatrix with offset center coordinates, scaled pixel scale, and adjusted rotation angle.
    /// Enables fine-tuning and 1-to-1 live alignment.
    /// </summary>
    public WcsMatrix WithAdjustment(
        double deltaRaDeg,
        double deltaDecDeg,
        double scaleMultiplier,
        double deltaRotationDeg,
        bool? toggleFlip = null,
        bool resetSpline = false)
    {
        double newRa = ((CenterCoordinates.RaDegrees + deltaRaDeg) % 360.0 + 360.0) % 360.0;
        double newDec = Math.Clamp(CenterCoordinates.DecDegrees + deltaDecDeg, -89.9, 89.9);
        var newCenter = new CelestialCoordinates(newRa, newDec);

        double newScale = Math.Max(0.01, PixelScaleArcsec * Math.Max(0.1, scaleMultiplier));
        double newRot = (RotationDegrees + deltaRotationDeg) % 360.0;
        if (newRot < 0) newRot += 360.0;
        bool flipped = toggleFlip.HasValue ? toggleFlip.Value : IsFlippedHorizontally;
        var spline = resetSpline ? null : SplineWarp;

        return new WcsMatrix(
            newCenter,
            ReferencePixelX,
            ReferencePixelY,
            newScale,
            newRot,
            ImageWidth,
            ImageHeight,
            RadialDistortionK1,
            RadialDistortionK2,
            spline,
            flipped);
    }

    /// <summary>
    /// Returns a new WcsMatrix with inverted horizontal parity (optical mirror flip).
    /// </summary>
    public WcsMatrix WithFlip() => WithAdjustment(0, 0, 1.0, 0, toggleFlip: !IsFlippedHorizontally);

    /// <summary>
    /// Shifts the center coordinates by a screen pixel offset (dx, dy).
    /// </summary>
    public WcsMatrix WithPixelShift(double deltaPx, double deltaPy)
    {
        // Shifting the predicted position of celestial objects by (deltaPx, deltaPy) on the sensor plane
        // means the center pixel (ReferencePixelX, ReferencePixelY) aligns with the celestial coordinate
        // at (ReferencePixelX - deltaPx, ReferencePixelY - deltaPy).
        var newCenter = PixelToEquatorial(ReferencePixelX - deltaPx, ReferencePixelY - deltaPy);
        return new WcsMatrix(
            newCenter,
            ReferencePixelX,
            ReferencePixelY,
            PixelScaleArcsec,
            RotationDegrees,
            ImageWidth,
            ImageHeight,
            RadialDistortionK1,
            RadialDistortionK2,
            SplineWarp,
            IsFlippedHorizontally);
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
        if (SplineWarp != null)
        {
            // Symmetrically bound TPS warping within sensor frame (+10% margin) and clamp maximum displacement
            // to prevent extrapolation divergence outside the matched star hull.
            double margin = Math.Max(ImageWidth, ImageHeight) * 0.10;
            if (x >= -margin && x <= ImageWidth + margin &&
                y >= -margin && y <= ImageHeight + margin)
            {
                var (warpedX, warpedY) = SplineWarp.Warp(x, y);
                double dispX = warpedX - x;
                double dispY = warpedY - y;
                double maxDisp = Math.Max(25.0, SplineWarp.MaxDisplacementPixels * 3.0);
                double dist = Math.Sqrt((dispX * dispX) + (dispY * dispY));
                if (dist > maxDisp && dist > 1e-6)
                {
                    double factor = maxDisp / dist;
                    dispX *= factor;
                    dispY *= factor;
                }
                x += dispX;
                y += dispY;
            }
        }

        double dx = x - ReferencePixelX;
        double dy = y - ReferencePixelY;

        double rSq = (dx * dx) + (dy * dy);
        if ((Math.Abs(RadialDistortionK1) > 1e-7 || Math.Abs(RadialDistortionK2) > 1e-7) && rSq > 1e-6)
        {
            // Radial un-distortion: map distorted sensor coordinates back to rectilinear TAN plane
            double rNormSq = rSq / (_normRadius * _normRadius);
            double factor = 1.0 + (RadialDistortionK1 * rNormSq) + (RadialDistortionK2 * rNormSq * rNormSq);
            dx *= factor;
            dy *= factor;
        }

        // Intermediate celestial coordinates (xi, eta) in degrees on the tangent plane
        double xiDeg = (_cd11 * dx) + (_cd12 * dy);
        double etaDeg = (_cd21 * dx) + (_cd22 * dy);

        double xi = xiDeg * (Math.PI / 180.0);
        double eta = etaDeg * (Math.PI / 180.0);

        double a0 = CenterCoordinates.RaRadians;
        double d0 = CenterCoordinates.DecRadians;

        double sinD0 = Math.Sin(d0);
        double cosD0 = Math.Cos(d0);
        double sinA0 = Math.Sin(a0);
        double cosA0 = Math.Cos(a0);

        // Standard gnomonic (TAN) projection 3D unit vector mapping:
        // Center direction r0 = (cosD0 * cosA0, cosD0 * sinA0, sinD0)
        // Tangent East basis uXi = (-sinA0, cosA0, 0)
        // Tangent North basis uEta = (-sinD0 * cosA0, -sinD0 * sinA0, cosD0)
        // V = r0 + xi * uXi + eta * uEta
        // Singularity-free across celestial poles (Dec -> +/-90 deg).
        double vx = (cosD0 * cosA0) - (xi * sinA0) - (eta * sinD0 * cosA0);
        double vy = (cosD0 * sinA0) + (xi * cosA0) - (eta * sinD0 * sinA0);
        double vz = sinD0 + (eta * cosD0);

        double raRad = Math.Atan2(vy, vx);
        double hyp = Math.Sqrt((vx * vx) + (vy * vy));
        double decRad = Math.Atan2(vz, hyp);

        return CelestialCoordinates.FromRadians(raRad, decRad);
    }

    /// <summary>
    /// Projects celestial (RA, Dec) coordinates onto the image plane to find pixel (X, Y).
    /// Returns false if the coordinates lie behind the tangent plane.
    /// </summary>
    public bool EquatorialToPixel(double raDegrees, double decDegrees, out double x, out double y)
        => EquatorialToPixel(new CelestialCoordinates(raDegrees, decDegrees), out x, out y);

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
        if (cosC <= 0.01)
        {
            // Point is on or behind the horizon of the tangent plane
            x = 0;
            y = 0;
            return false;
        }

        // Tangent plane projection (xi, eta) in radians.
        // For ultra-wide fields (c > 40 deg, cosC < 0.766) when no lens radial polynomial is fitted,
        // smoothly transition from Gnomonic (1/cosC) toward Stereographic (2/(1+cosC)) to prevent
        // hyperbolic horizon blow-up on 2D mosaic canvases while keeping narrow/medium fields 100% exact TAN.
        double denom = cosC;
        if (cosC < 0.766 && Math.Abs(RadialDistortionK1) < 1e-7 && Math.Abs(RadialDistortionK2) < 1e-7 && SplineWarp == null)
        {
            double t = Math.Clamp((0.766 - cosC) / 0.566, 0.0, 1.0);
            double stgDenom = 0.5 * (1.0 + cosC);
            denom = ((1.0 - t) * cosC) + (t * stgDenom);
        }

        double xi = (cosD * Math.Sin(dRa)) / denom;
        double eta = ((cosD0 * sinD) - (sinD0 * cosD * Math.Cos(dRa))) / denom;

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

        double linearDx = dx;
        double linearDy = dy;
        double ruSq = (dx * dx) + (dy * dy);
        if ((Math.Abs(RadialDistortionK1) > 1e-7 || Math.Abs(RadialDistortionK2) > 1e-7) && ruSq > 1e-6)
        {
            // Robust Bounded Inversion (Sprint 2 / Architect Directive 1):
            // Inverts ru = rd * (1 + k1*rd^2 + k2*rd^4) with max 10 iterations, 1e-7 normalized tol (~1e-4 px),
            // and immediate fallback to Linear TAN WCS if derivative vanishes or divergence occurs.
            double ruNorm = Math.Sqrt(ruSq) / _normRadius;
            if (ruNorm > 1e-8)
            {
                double rd = ruNorm;
                bool converged = false;
                const double tol = 1e-7;

                for (int iter = 0; iter < 10; iter++)
                {
                    double rd2 = rd * rd;
                    double rd4 = rd2 * rd2;
                    double f = (rd * (1.0 + (RadialDistortionK1 * rd2) + (RadialDistortionK2 * rd4))) - ruNorm;
                    if (Math.Abs(f) < tol)
                    {
                        converged = true;
                        break;
                    }
                    double fPrime = 1.0 + (3.0 * RadialDistortionK1 * rd2) + (5.0 * RadialDistortionK2 * rd4);
                    if (Math.Abs(fPrime) <= 1e-9 || double.IsNaN(fPrime))
                    {
                        break;
                    }
                    double step = f / fPrime;
                    rd -= step;
                    if (double.IsNaN(rd) || double.IsInfinity(rd) || rd < 0.0)
                    {
                        break;
                    }
                }

                if (converged || (rd > 0.0 && !double.IsNaN(rd) && !double.IsInfinity(rd) && rd < ruNorm * 3.0))
                {
                    double factor = rd / ruNorm;
                    dx *= factor;
                    dy *= factor;
                }
                else
                {
                    // Linear TAN Fallback on divergence (Architect Directive Risk A)
                    dx = linearDx;
                    dy = linearDy;
                }
            }
        }

        double px = ReferencePixelX + dx;
        double py = ReferencePixelY + dy;

        if (SplineWarp != null)
        {
            // TPS unwarping is strictly an internal sensor lens correction.
            // Extrapolating far beyond sensor borders causes polynomial divergence (r^2 ln(r)).
            // Only apply within sensor frame (+10% margin), and clamp maximum displacement.
            double margin = Math.Max(ImageWidth, ImageHeight) * 0.10;
            if (px >= -margin && px <= ImageWidth + margin &&
                py >= -margin && py <= ImageHeight + margin)
            {
                var (unwX, unwY) = SplineWarp.Unwarp(px, py);
                double dispX = unwX - px;
                double dispY = unwY - py;
                double maxDisp = Math.Max(25.0, SplineWarp.MaxDisplacementPixels * 3.0);
                double dist = Math.Sqrt((dispX * dispX) + (dispY * dispY));
                if (dist > maxDisp && dist > 1e-6)
                {
                    double factor = maxDisp / dist;
                    dispX *= factor;
                    dispY *= factor;
                }
                px += dispX;
                py += dispY;
            }
        }

        x = px;
        y = py;
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

        double minDec = Math.Max(-89.85, centerDec - halfFov);
        double maxDec = Math.Min(89.85, centerDec + halfFov);

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
            if (dec < -89.85 || dec > 89.85) continue;

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
        double margin = Math.Max(ImageWidth, ImageHeight) * 1.5;
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

