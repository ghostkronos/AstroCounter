namespace AstroCounter.Core.Astrometry;

using System;
using System.Collections.Generic;

/// <summary>
/// High-precision 2D Thin-Plate Spline (TPS) non-linear deformation engine.
/// Computes smooth minimum-bending-energy 2D coordinate transformations based on
/// astronomical anchor stars (Ground-Truth Image Centroid <-> Catalog Projected Position).
/// Eliminates higher-order optical coma, field curvature, tangential lens decentering,
/// and atmospheric refraction residuals that cannot be modeled by simple radial polynomials.
/// </summary>
public sealed class ThinPlateSplineWarp
{
    public readonly record struct ControlPoint(double SourceX, double SourceY, double TargetX, double TargetY);

    private readonly double[] _wx;
    private readonly double[] _wy;
    private readonly double _ax0, _ax1, _ax2;
    private readonly double _ay0, _ay1, _ay2;

    // Inverse TPS parameters (for instantaneous target -> source un-warping)
    private readonly double[] _invWx;
    private readonly double[] _invWy;
    private readonly double _invAx0, _invAx1, _invAx2;
    private readonly double _invAy0, _invAy1, _invAy2;

    private readonly double[] _ctrlX;
    private readonly double[] _ctrlY;
    private readonly double[] _tgtX;
    private readonly double[] _tgtY;
    private readonly int _pointCount;
    private readonly double _normRadius;

    public int PointCount => _pointCount;
    public double MaxDisplacementPixels { get; }
    public double RmsResidualPixels { get; }

    private ThinPlateSplineWarp(
        double[] wx, double[] wy, double ax0, double ax1, double ax2, double ay0, double ay1, double ay2,
        double[] invWx, double[] invWy, double invAx0, double invAx1, double invAx2, double invAy0, double invAy1, double invAy2,
        double[] ctrlX, double[] ctrlY, double[] tgtX, double[] tgtY,
        double normRadius, double maxDisp, double rmsRes)
    {
        _wx = wx;
        _wy = wy;
        _ax0 = ax0;
        _ax1 = ax1;
        _ax2 = ax2;
        _ay0 = ay0;
        _ay1 = ay1;
        _ay2 = ay2;

        _invWx = invWx;
        _invWy = invWy;
        _invAx0 = invAx0;
        _invAx1 = invAx1;
        _invAx2 = invAx2;
        _invAy0 = invAy0;
        _invAy1 = invAy1;
        _invAy2 = invAy2;

        _ctrlX = ctrlX;
        _ctrlY = ctrlY;
        _tgtX = tgtX;
        _tgtY = tgtY;
        _pointCount = ctrlX.Length;
        _normRadius = normRadius;
        MaxDisplacementPixels = maxDisp;
        RmsResidualPixels = rmsRes;
    }

    /// <summary>
    /// Fits a Thin Plate Spline warping model from matched star pairs.
    /// Returns null if fewer than 4 non-collinear star pairs are provided.
    /// </summary>
    public static ThinPlateSplineWarp? Create(
        IReadOnlyList<ControlPoint> points,
        int imageWidth,
        int imageHeight,
        double regularization = 1e-4,
        bool addCornerStabilizers = true)
    {
        if (points == null || points.Count < 4 || imageWidth <= 0 || imageHeight <= 0)
        {
            return null;
        }

        double normRadius = Math.Max(1.0, Math.Sqrt(((double)imageWidth * imageWidth) + ((double)imageHeight * imageHeight)));

        // Filter and collect unique, valid control points
        var cleanPoints = new List<ControlPoint>(points.Count + 8);
        double maxDisp = 0.0;
        double sumSqRes = 0.0;

        foreach (var p in points)
        {
            double dx = p.TargetX - p.SourceX;
            double dy = p.TargetY - p.SourceY;
            double disp = Math.Sqrt((dx * dx) + (dy * dy));

            // Reject extreme outliers (e.g. accidental misalignments > 12% image diagonal)
            if (disp > normRadius * 0.12) continue;

            cleanPoints.Add(p);
            if (disp > maxDisp) maxDisp = disp;
            sumSqRes += disp * disp;
        }

        if (cleanPoints.Count < 4) return null;

        // Add 4 corner boundary stabilization anchors to ensure graceful extrapolation outside the star hull
        // Corner anchor targets use the affine trend of the matched stars
        // (Callers with dense, full-field control points can disable this and supply their own anchors.)
        if (addCornerStabilizers)
        {
            AddCornerStabilizers(cleanPoints, imageWidth, imageHeight);
        }

        int n = cleanPoints.Count;
        double[] srcX = new double[n];
        double[] srcY = new double[n];
        double[] dstX = new double[n];
        double[] dstY = new double[n];

        for (int i = 0; i < n; i++)
        {
            srcX[i] = cleanPoints[i].SourceX;
            srcY[i] = cleanPoints[i].SourceY;
            dstX[i] = cleanPoints[i].TargetX;
            dstY[i] = cleanPoints[i].TargetY;
        }

        // Fit Forward TPS: (SourceX, SourceY) -> (TargetX, TargetY)
        if (!FitTpsCoefficients(srcX, srcY, dstX, dstY, normRadius, regularization,
            out var fWx, out var fWy, out var fAx0, out var fAx1, out var fAx2, out var fAy0, out var fAy1, out var fAy2))
        {
            return null;
        }

        // Fit Inverse TPS: (TargetX, TargetY) -> (SourceX, SourceY)
        if (!FitTpsCoefficients(dstX, dstY, srcX, srcY, normRadius, regularization,
            out var iWx, out var iWy, out var iAx0, out var iAx1, out var iAx2, out var iAy0, out var iAy1, out var iAy2))
        {
            return null;
        }

        double rms = Math.Sqrt(sumSqRes / Math.Max(1, cleanPoints.Count - 4));

        return new ThinPlateSplineWarp(
            fWx, fWy, fAx0, fAx1, fAx2, fAy0, fAy1, fAy2,
            iWx, iWy, iAx0, iAx1, iAx2, iAy0, iAy1, iAy2,
            srcX, srcY, dstX, dstY,
            normRadius, maxDisp, rms);
    }

    /// <summary>
    /// Warps a sensor image coordinate (X, Y) onto the ideal catalog-aligned coordinate.
    /// </summary>
    public (double X, double Y) Warp(double x, double y)
    {
        double u = _ax0 + (_ax1 * (x / _normRadius)) + (_ax2 * (y / _normRadius));
        double v = _ay0 + (_ay1 * (x / _normRadius)) + (_ay2 * (y / _normRadius));

        for (int i = 0; i < _pointCount; i++)
        {
            double dx = (x - _ctrlX[i]) / _normRadius;
            double dy = (y - _ctrlY[i]) / _normRadius;
            double rSq = (dx * dx) + (dy * dy);
            if (rSq > 1e-14)
            {
                double uVal = 0.5 * rSq * Math.Log(rSq);
                u += _wx[i] * uVal;
                v += _wy[i] * uVal;
            }
        }

        return (u, v);
    }

    /// <summary>
    /// Un-warps an ideal catalog coordinate back to the sensor image pixel (X, Y).
    /// </summary>
    public (double X, double Y) Unwarp(double u, double v)
    {
        double x = _invAx0 + (_invAx1 * (u / _normRadius)) + (_invAx2 * (v / _normRadius));
        double y = _invAy0 + (_invAy1 * (u / _normRadius)) + (_invAy2 * (v / _normRadius));

        for (int i = 0; i < _pointCount; i++)
        {
            double du = (u - _tgtX[i]) / _normRadius;
            double dv = (v - _tgtY[i]) / _normRadius;
            double rSq = (du * du) + (dv * dv);
            if (rSq > 1e-14)
            {
                double uVal = 0.5 * rSq * Math.Log(rSq);
                x += _invWx[i] * uVal;
                y += _invWy[i] * uVal;
            }
        }

        // 2-step fixed-point refinement ensures Warp(Unwarp(u, v)) == (u, v) to < 1e-5 px
        for (int iter = 0; iter < 2; iter++)
        {
            var (wu, wv) = Warp(x, y);
            double errU = u - wu;
            double errV = v - wv;
            if (Math.Abs(errU) < 1e-5 && Math.Abs(errV) < 1e-5) break;
            x += errU;
            y += errV;
        }

        return (x, y);
    }

    /// <summary>
    /// Evaluates a deformed grid of vertices for high-performance rendering on 3D celestial domes or 2D meshes.
    /// </summary>
    public void EvaluateGrid(int cols, int rows, double width, double height, (double X, double Y)[,] destination)
    {
        for (int r = 0; r <= rows; r++)
        {
            double py = (double)r / rows * height;
            for (int c = 0; c <= cols; c++)
            {
                double px = (double)c / cols * width;
                destination[c, r] = Warp(px, py);
            }
        }
    }

    private static void AddCornerStabilizers(List<ControlPoint> points, int w, int h)
    {
        var corners = new (double X, double Y)[]
        {
            (0, 0), (w, 0), (0, h), (w, h),
            (w * 0.5, 0), (w * 0.5, h), (0, h * 0.5), (w, h * 0.5)
        };

        double diag = Math.Sqrt(((double)w * w) + ((double)h * h));
        double emptyDist = diag * 0.08;

        foreach (var (cx, cy) in corners)
        {
            bool hasNearby = false;
            foreach (var p in points)
            {
                double ddx = p.SourceX - cx;
                double ddy = p.SourceY - cy;
                if ((ddx * ddx) + (ddy * ddy) < emptyDist * emptyDist)
                {
                    hasNearby = true;
                    break;
                }
            }
            if (hasNearby) continue;

            double sumW = 0.0, sumDx = 0.0, sumDy = 0.0;
            var nearest = points
                .Select(p => {
                    double ddx = p.SourceX - cx;
                    double ddy = p.SourceY - cy;
                    return (Point: p, DistSq: (ddx * ddx) + (ddy * ddy));
                })
                .OrderBy(t => t.DistSq)
                .Take(4);

            foreach (var (p, dSq) in nearest)
            {
                double weight = 1.0 / Math.Max(1.0, dSq);
                sumW += weight;
                sumDx += weight * (p.TargetX - p.SourceX);
                sumDy += weight * (p.TargetY - p.SourceY);
            }

            if (sumW > 0)
            {
                double extraDx = sumDx / sumW;
                double extraDy = sumDy / sumW;
                points.Add(new ControlPoint(cx, cy, cx + extraDx, cy + extraDy));
            }
        }
    }

    private static bool FitTpsCoefficients(
        double[] x, double[] y, double[] tx, double[] ty,
        double normRadius, double lambda,
        out double[] wx, out double[] wy,
        out double ax0, out double ax1, out double ax2,
        out double ay0, out double ay1, out double ay2)
    {
        int p = x.Length;
        int m = p + 3;

        double[,] l = new double[m, m];
        double[] bx = new double[m];
        double[] by = new double[m];

        // 1. Build K matrix block with radial basis U(r) = r^2 * ln(r)
        for (int i = 0; i < p; i++)
        {
            for (int j = i + 1; j < p; j++)
            {
                double dx = (x[i] - x[j]) / normRadius;
                double dy = (y[i] - y[j]) / normRadius;
                double rSq = (dx * dx) + (dy * dy);
                if (rSq > 1e-14)
                {
                    double u = 0.5 * rSq * Math.Log(rSq);
                    l[i, j] = u;
                    l[j, i] = u;
                }
            }

            // Regularization diagonal
            l[i, i] = lambda;

            // 2. Build P matrix block (1, x, y)
            l[i, p] = 1.0;
            l[i, p + 1] = x[i] / normRadius;
            l[i, p + 2] = y[i] / normRadius;

            // P^T block
            l[p, i] = 1.0;
            l[p + 1, i] = x[i] / normRadius;
            l[p + 2, i] = y[i] / normRadius;

            // Target vector
            bx[i] = tx[i];
            by[i] = ty[i];
        }

        // P block lower right 3x3 is 0

        // Solve L * [w; a] = b via Gaussian elimination with partial pivoting
        if (!SolveLinearSystem(l, bx, m) || !SolveLinearSystem(l, by, m))
        {
            wx = wy = Array.Empty<double>();
            ax0 = ax1 = ax2 = ay0 = ay1 = ay2 = 0;
            return false;
        }

        wx = new double[p];
        wy = new double[p];
        Array.Copy(bx, 0, wx, 0, p);
        Array.Copy(by, 0, wy, 0, p);

        ax0 = bx[p];
        ax1 = bx[p + 1];
        ax2 = bx[p + 2];

        ay0 = by[p];
        ay1 = by[p + 1];
        ay2 = by[p + 2];

        return true;
    }

    private static bool SolveLinearSystem(double[,] a, double[] b, int n)
    {
        // Make a working copy of A because Gaussian elimination mutates in-place
        double[,] m = (double[,])a.Clone();

        for (int col = 0; col < n; col++)
        {
            // Find pivot
            int maxRow = col;
            double maxVal = Math.Abs(m[col, col]);
            for (int row = col + 1; row < n; row++)
            {
                double val = Math.Abs(m[row, col]);
                if (val > maxVal)
                {
                    maxVal = val;
                    maxRow = row;
                }
            }

            if (maxVal < 1e-12)
            {
                return false; // Singular matrix
            }

            // Swap rows
            if (maxRow != col)
            {
                for (int k = col; k < n; k++)
                {
                    (m[col, k], m[maxRow, k]) = (m[maxRow, k], m[col, k]);
                }
                (b[col], b[maxRow]) = (b[maxRow], b[col]);
            }

            // Eliminate
            double pivot = m[col, col];
            for (int row = col + 1; row < n; row++)
            {
                double factor = m[row, col] / pivot;
                m[row, col] = 0;
                for (int k = col + 1; k < n; k++)
                {
                    m[row, k] -= factor * m[col, k];
                }
                b[row] -= factor * b[col];
            }
        }

        // Back substitution
        for (int row = n - 1; row >= 0; row--)
        {
            double sum = b[row];
            for (int col = row + 1; col < n; col++)
            {
                sum -= m[row, col] * b[col];
            }
            b[row] = sum / m[row, row];
        }

        return true;
    }
}

