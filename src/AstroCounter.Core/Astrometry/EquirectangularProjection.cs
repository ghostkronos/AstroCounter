namespace AstroCounter.Core.Astrometry;

using System;
using System.Collections.Generic;

/// <summary>
/// Cropped Plate Carrée (equirectangular cylindrical) celestial projection mapping equatorial coordinates (RA, Dec)
/// uniformly to a 2D canvas around the mosaic center (East = Left, West = Right, North = Up).
/// </summary>
public sealed record EquirectangularProjection
{
    public double CenterRaDeg { get; }
    public double RaSpanDeg { get; }
    public double DecMinDeg { get; }
    public double DecMaxDeg { get; }
    public double DecSpanDeg { get; }
    public int CanvasWidth { get; }
    public int CanvasHeight { get; }

    /// <summary>
    /// Pixels per degree along Declination (and along RA in pure Plate Carrée when unclamped).
    /// </summary>
    public double PixelsPerDegreeX => CanvasWidth / RaSpanDeg;
    public double PixelsPerDegreeY => CanvasHeight / DecSpanDeg;

    public EquirectangularProjection(
        double centerRaDeg,
        double raSpanDeg,
        double decMinDeg,
        double decMaxDeg,
        int canvasWidth,
        int canvasHeight)
    {
        CenterRaDeg = NormalizeRa(centerRaDeg);
        RaSpanDeg = Math.Clamp(raSpanDeg, 0.05, 360.0);
        DecMinDeg = Math.Clamp(decMinDeg, -90.0, 89.9);
        DecMaxDeg = Math.Clamp(decMaxDeg, DecMinDeg + 0.05, 90.0);
        DecSpanDeg = Math.Max(0.05, DecMaxDeg - DecMinDeg);
        CanvasWidth = Math.Max(64, canvasWidth);
        CanvasHeight = Math.Max(64, canvasHeight);
    }

    public static double NormalizeRa(double raDeg)
    {
        double r = raDeg % 360.0;
        return r < 0.0 ? r + 360.0 : r;
    }

    /// <summary>
    /// Signed shortest angular offset in degrees east-to-west relative to <see cref="CenterRaDeg"/>:
    /// negative = east (left side of canvas), positive = west (right side of canvas).
    /// </summary>
    public double RelativeRaDeg(double raDeg)
    {
        return ((CenterRaDeg - raDeg + 540.0) % 360.0) - 180.0;
    }

    /// <summary>
    /// Projects sky coordinates (RA, Dec in degrees) to canvas pixel coordinates (x, y).
    /// Returns true if the point falls within the canvas bounds (plus <paramref name="marginPx"/>).
    /// </summary>
    public bool EquatorialToCanvas(double raDeg, double decDeg, out double x, out double y, double marginPx = 0.0)
    {
        double relRa = RelativeRaDeg(raDeg);
        x = (0.5 + (relRa / RaSpanDeg)) * CanvasWidth;
        y = ((DecMaxDeg - Math.Clamp(decDeg, -90.0, 90.0)) / DecSpanDeg) * CanvasHeight;

        return x >= -marginPx && x <= CanvasWidth + marginPx
            && y >= -marginPx && y <= CanvasHeight + marginPx;
    }

    /// <summary>
    /// Unprojects canvas pixel coordinates (x, y) back to equatorial sky coordinates (RA, Dec in degrees).
    /// </summary>
    public void CanvasToEquatorial(double x, double y, out double raDeg, out double decDeg)
    {
        double relRa = ((x / CanvasWidth) - 0.5) * RaSpanDeg;
        raDeg = NormalizeRa(CenterRaDeg - relRa);
        decDeg = Math.Clamp(DecMaxDeg - ((y / CanvasHeight) * DecSpanDeg), -90.0, 90.0);
    }

    /// <summary>
    /// Projects a celestial segment (ra1, dec1) -> (ra2, dec2) safely, rejecting anti-meridian wrap jumps
    /// and segments lying completely outside the padded viewport.
    /// </summary>
    public bool TryProjectSegment(
        double ra1,
        double dec1,
        double ra2,
        double dec2,
        out double x1,
        out double y1,
        out double x2,
        out double y2,
        double marginPx = 80.0)
    {
        EquatorialToCanvas(ra1, dec1, out x1, out y1);
        EquatorialToCanvas(ra2, dec2, out x2, out y2);

        // Reject horizontal wrap-around jumps across +/- 180 deg from CenterRa
        if (Math.Abs(x2 - x1) > (CanvasWidth * (180.0 / Math.Max(1.0, RaSpanDeg)) * 0.85)
            || (RaSpanDeg >= 240.0 && Math.Abs(x2 - x1) > CanvasWidth * 0.48))
        {
            return false;
        }

        double minX = Math.Min(x1, x2);
        double maxX = Math.Max(x1, x2);
        double minY = Math.Min(y1, y2);
        double maxY = Math.Max(y1, y2);

        return maxX >= -marginPx && minX <= CanvasWidth + marginPx
            && maxY >= -marginPx && minY <= CanvasHeight + marginPx;
    }

    /// <summary>
    /// Computes an appropriately cropped Plate Carrée equirectangular projection framing all solved mosaic panels
    /// (or a single solved frame's WCS) with balanced night-sky margins around curved footprints.
    /// Returns null if no solved panel or WCS is available.
    /// </summary>
    public static EquirectangularProjection? CreateForPanels(
        IEnumerable<MosaicPanel>? panels,
        WcsMatrix? fallbackWcs = null,
        int targetMaxDim = 3200)
    {
        var solvedWcsList = new List<(WcsMatrix Wcs, int W, int H)>();
        bool hasAnyPanel = false;
        if (panels != null)
        {
            foreach (var p in panels)
            {
                hasAnyPanel = true;
                if (!p.IsVisible || !p.IsSolved || p.Wcs == null) continue;
                int pw = p.Width > 0 ? p.Width : p.Wcs.ImageWidth;
                int ph = p.Height > 0 ? p.Height : p.Wcs.ImageHeight;
                if (pw > 0 && ph > 0)
                {
                    solvedWcsList.Add((p.Wcs, pw, ph));
                }
            }
        }

        if (solvedWcsList.Count == 0 &&
            !hasAnyPanel &&
            fallbackWcs != null &&
            fallbackWcs.ImageWidth > 0 &&
            fallbackWcs.ImageHeight > 0 &&
            (Math.Abs(fallbackWcs.CenterCoordinates.RaDegrees - 180.0) > 1e-3 ||
             Math.Abs(fallbackWcs.CenterCoordinates.DecDegrees - 30.0) > 1e-3 ||
             Math.Abs(fallbackWcs.PixelScaleArcsec - 2.5) > 1e-3))
        {
            solvedWcsList.Add((fallbackWcs, fallbackWcs.ImageWidth, fallbackWcs.ImageHeight));
        }

        if (solvedWcsList.Count == 0)
        {
            return null;
        }

        // 1. Initial circular mean RA
        double sumSin = 0.0, sumCos = 0.0;
        foreach (var item in solvedWcsList)
        {
            double raRad = item.Wcs.CenterCoordinates.RaRadians;
            sumSin += Math.Sin(raRad);
            sumCos += Math.Cos(raRad);
        }

        double initCenterRa = (Math.Abs(sumSin) < 1e-9 && Math.Abs(sumCos) < 1e-9)
            ? solvedWcsList[0].Wcs.CenterCoordinates.RaDegrees
            : NormalizeRa(Math.Atan2(sumSin, sumCos) * (180.0 / Math.PI));

        // 2. Sample 9x9 grid per solved panel to capture true curved spherical + distortion + TPS bounds
        double minRelRa = double.PositiveInfinity;
        double maxRelRa = double.NegativeInfinity;
        double minDec = double.PositiveInfinity;
        double maxDec = double.NegativeInfinity;

        foreach (var item in solvedWcsList)
        {
            for (int gy = 0; gy <= 8; gy++)
            {
                double py = (gy / 8.0) * (item.H - 1);
                for (int gx = 0; gx <= 8; gx++)
                {
                    double px = (gx / 8.0) * (item.W - 1);
                    var eq = item.Wcs.PixelToEquatorial(px, py);
                    double rel = ((initCenterRa - eq.RaDegrees + 540.0) % 360.0) - 180.0;
                    if (rel < minRelRa) minRelRa = rel;
                    if (rel > maxRelRa) maxRelRa = rel;
                    if (eq.DecDegrees < minDec) minDec = eq.DecDegrees;
                    if (eq.DecDegrees > maxDec) maxDec = eq.DecDegrees;
                }
            }
        }

        if (!double.IsFinite(minRelRa) || !double.IsFinite(minDec))
        {
            return null;
        }

        // Re-center RA symmetrically on actual sampled span
        double midRelRa = 0.5 * (minRelRa + maxRelRa);
        double centerRa = NormalizeRa(initCenterRa - midRelRa);
        double rawRaSpan = Math.Max(0.25, maxRelRa - minRelRa);
        double rawDecSpan = Math.Max(0.20, maxDec - minDec);

        // Generous dark starfield padding matching curved celestial canvas aesthetic
        double padRa = Math.Clamp(rawRaSpan * 0.14, 0.20, 22.0);
        double padDec = Math.Clamp(rawDecSpan * 0.16, 0.15, 14.0);

        double raSpan = rawRaSpan + (2.0 * padRa);
        if (raSpan >= 300.0)
        {
            raSpan = 360.0;
        }

        double decMin = Math.Max(-90.0, minDec - padDec);
        double decMax = Math.Min(90.0, maxDec + padDec);
        double decSpan = Math.Max(0.25, decMax - decMin);

        // Prevent extreme needle-thin canvas aspect ratios (keep comfortable Plate Carrée framing)
        double aspect = raSpan / decSpan;
        if (aspect > 4.2)
        {
            double desiredDecSpan = Math.Min(180.0, raSpan / 4.2);
            double decMid = 0.5 * (decMin + decMax);
            decMin = Math.Max(-90.0, decMid - (0.5 * desiredDecSpan));
            decMax = Math.Min(90.0, decMid + (0.5 * desiredDecSpan));
            decSpan = Math.Max(0.25, decMax - decMin);
        }
        else if (aspect < 0.75)
        {
            double desiredRaSpan = Math.Min(360.0, decSpan * 0.85);
            raSpan = Math.Max(raSpan, desiredRaSpan);
        }

        double finalAspect = raSpan / decSpan;
        int maxDim = Math.Clamp(targetMaxDim, 640, 8192);
        int width, height;
        if (finalAspect >= 1.0)
        {
            width = maxDim;
            height = Math.Max(320, (int)Math.Round(maxDim / finalAspect));
        }
        else
        {
            height = maxDim;
            width = Math.Max(320, (int)Math.Round(maxDim * finalAspect));
        }

        return new EquirectangularProjection(centerRa, raSpan, decMin, decMax, width, height);
    }
}
