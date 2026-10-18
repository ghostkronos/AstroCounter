namespace AstroCounter.UI;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using AstroCounter.Core.Astrometry;
using AstroCounter.Core.Models;

/// <summary>
/// Lightweight overlay that draws detections, scan regions and the region being drawn directly
/// through a DrawingContext (instead of tens of thousands of WPF elements).
/// Marker size and stroke thickness are expressed in screen pixels, so small stars stay visible at any zoom.
/// </summary>
public sealed class DetectionOverlay : FrameworkElement
{
    private const int MaxLabels = 700;
    private const int HaloLimit = 8000;
    private const double MinMarkerScreenPx = 10.0;
    private const double StrokeScreenPx = 1.6;
    private const double LabelFontScreenPx = 12.0;

    public static readonly DependencyProperty ObjectsProperty = DependencyProperty.Register(
        nameof(Objects), typeof(IReadOnlyList<AstrophotographyObject>), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RegionsProperty = DependencyProperty.Register(
        nameof(Regions), typeof(IReadOnlyList<SelectionRegion>), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowBoxesProperty = DependencyProperty.Register(
        nameof(ShowBoxes), typeof(bool), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowLabelsProperty = DependencyProperty.Register(
        nameof(ShowLabels), typeof(bool), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowSphericalGridProperty = DependencyProperty.Register(
        nameof(ShowSphericalGrid), typeof(bool), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WcsProperty = DependencyProperty.Register(
        nameof(Wcs), typeof(WcsMatrix), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TargetMarkerPixelProperty = DependencyProperty.Register(
        nameof(TargetMarkerPixel), typeof(Point?), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TargetMarkerLabelProperty = DependencyProperty.Register(
        nameof(TargetMarkerLabel), typeof(string), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush[] TierBrushes =
    {
        MakeBrush(0x00, 0xE6, 0x76), // Tier 1 High (green)
        MakeBrush(0xFF, 0xD6, 0x00), // Tier 2 Medium (yellow)
        MakeBrush(0xFF, 0x91, 0x00), // Tier 3 Low (orange)
        MakeBrush(0xEF, 0x44, 0x44)  // FilteredNoise / Speculative (red)
    };

    private static readonly Brush HaloBrush = MakeBrush(0, 0, 0, 190);
    private static readonly Brush LabelBackground = MakeBrush(10, 15, 26, 215);
    private static readonly Brush ExcludeFill = MakeBrush(220, 38, 38, 80);
    private static readonly Brush IncludeFill = MakeBrush(16, 185, 129, 28);
    private static readonly Color ExcludeColor = Color.FromRgb(248, 113, 113);
    private static readonly Color IncludeColor = Color.FromRgb(52, 211, 153);

    private double _zoom = 1.0;
    private Rect _viewport = Rect.Empty;

    private IReadOnlyList<Vector2>? _previewPoints;
    private Vector2? _previewCursor;
    private RegionKind _previewKind = RegionKind.Exclude;
    private bool _previewIsRectangle;

    public IReadOnlyList<AstrophotographyObject>? Objects
    {
        get => (IReadOnlyList<AstrophotographyObject>?)GetValue(ObjectsProperty);
        set => SetValue(ObjectsProperty, value);
    }

    public IReadOnlyList<SelectionRegion>? Regions
    {
        get => (IReadOnlyList<SelectionRegion>?)GetValue(RegionsProperty);
        set => SetValue(RegionsProperty, value);
    }

    public bool ShowBoxes
    {
        get => (bool)GetValue(ShowBoxesProperty);
        set => SetValue(ShowBoxesProperty, value);
    }

    public bool ShowLabels
    {
        get => (bool)GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    public bool ShowSphericalGrid
    {
        get => (bool)GetValue(ShowSphericalGridProperty);
        set => SetValue(ShowSphericalGridProperty, value);
    }

    public WcsMatrix? Wcs
    {
        get => (WcsMatrix?)GetValue(WcsProperty);
        set => SetValue(WcsProperty, value);
    }

    public Point? TargetMarkerPixel
    {
        get => (Point?)GetValue(TargetMarkerPixelProperty);
        set => SetValue(TargetMarkerPixelProperty, value);
    }

    public string? TargetMarkerLabel
    {
        get => (string?)GetValue(TargetMarkerLabelProperty);
        set => SetValue(TargetMarkerLabelProperty, value);
    }

    /// <summary>Current zoom factor of the surrounding scale transform.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            if (Math.Abs(_zoom - value) < 1e-9) return;
            _zoom = value;
            InvalidateVisual();
        }
    }

    /// <summary>Visible part of the image in image pixel coordinates (used for culling).</summary>
    public Rect Viewport
    {
        get => _viewport;
        set
        {
            if (_viewport == value) return;
            _viewport = value;
            InvalidateVisual();
        }
    }

    public void SetPreview(IReadOnlyList<Vector2>? points, Vector2? cursor, RegionKind kind, bool isRectangle)
    {
        _previewPoints = points;
        _previewCursor = cursor;
        _previewKind = kind;
        _previewIsRectangle = isRectangle;
        InvalidateVisual();
    }

    public void ClearPreview() => SetPreview(null, null, _previewKind, false);

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;

        // Transparent fill makes the whole overlay hit-testable (needed for drawing tools).
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        double inv = 1.0 / Math.Max(_zoom, 0.005);

        if (ShowSphericalGrid && Wcs != null)
        {
            DrawSphericalGrid(dc, inv, width, height);
        }

        DrawRegions(dc, inv);

        if (ShowBoxes)
        {
            DrawObjects(dc, inv, width, height);
        }

        DrawTargetMarker(dc, inv);

        DrawPreview(dc, inv);
    }

    private void DrawRegions(DrawingContext dc, double inv)
    {
        var regions = Regions;
        if (regions == null) return;

        foreach (var region in regions)
        {
            bool exclude = region.Kind == RegionKind.Exclude;
            var pen = new Pen(new SolidColorBrush(exclude ? ExcludeColor : IncludeColor), 2.0 * inv);
            pen.Freeze();

            dc.DrawGeometry(exclude ? ExcludeFill : IncludeFill, pen, BuildPolygon(region.Points, closed: true));
        }
    }

    private void DrawObjects(DrawingContext dc, double inv, double width, double height)
    {
        var objects = Objects;
        if (objects == null || objects.Count == 0) return;

        Rect view = _viewport.IsEmpty ? new Rect(0, 0, width, height) : _viewport;
        view.Inflate(30 * inv, 30 * inv);

        double minSize = MinMarkerScreenPx * inv;
        double stroke = StrokeScreenPx * inv;

        var pens = new Pen[TierBrushes.Length];
        for (int i = 0; i < pens.Length; i++)
        {
            pens[i] = new Pen(TierBrushes[i], stroke);
            pens[i].Freeze();
        }

        var haloPen = new Pen(HaloBrush, stroke * 2.6);
        haloPen.Freeze();
        bool drawHalo = objects.Count <= HaloLimit;

        var labelObjects = ShowLabels ? new List<(AstrophotographyObject Obj, double Size)>() : null;

        foreach (var obj in objects)
        {
            double cx = obj.CenterX;
            double cy = obj.CenterY;
            if (!view.Contains(cx, cy)) continue;

            double size = Math.Max(Math.Max(obj.Box.Width, obj.Box.Height), minSize);
            var rect = new Rect(cx - (size / 2), cy - (size / 2), size, size);

            if (drawHalo)
            {
                dc.DrawRectangle(null, haloPen, rect);
            }

            dc.DrawRectangle(null, pens[Math.Min((int)obj.Tier, pens.Length - 1)], rect);
            labelObjects?.Add((obj, size));
        }

        if (labelObjects == null) return;

        if (labelObjects.Count > MaxLabels)
        {
            string hint = CataloguePlateSolver.UseTurkishLanguage
                ? $"Etiketler için yakınlaştırın (görünür yıldız: {labelObjects.Count})"
                : $"Zoom in to display labels (visible stars: {labelObjects.Count})";
            DrawHint(dc, inv, hint);
            return;
        }

        DrawLabels(dc, inv, labelObjects);
    }

    private void DrawLabels(DrawingContext dc, double inv, List<(AstrophotographyObject Obj, double Size)> labelObjects)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        double fontSize = LabelFontScreenPx * inv;

        foreach (var (obj, size) in labelObjects)
        {
            var brush = TierBrushes[Math.Min((int)obj.Tier, TierBrushes.Length - 1)];
            string text = $"{(int)Math.Round(obj.Confidence * 100)}%";
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, fontSize, brush, dpi);

            double x = obj.CenterX + (size / 2) + (3 * inv);
            double y = obj.CenterY - (formatted.Height / 2);

            dc.DrawRectangle(LabelBackground, null,
                new Rect(x - (2 * inv), y - inv, formatted.Width + (4 * inv), formatted.Height + (2 * inv)));
            dc.DrawText(formatted, new Point(x, y));
        }
    }

    private void DrawHint(DrawingContext dc, double inv, string text)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, 12 * inv, Brushes.White, dpi);

        double x = (_viewport.IsEmpty ? 0 : _viewport.X) + (10 * inv);
        double y = (_viewport.IsEmpty ? 0 : _viewport.Y) + (10 * inv);

        dc.DrawRectangle(LabelBackground, null,
            new Rect(x - (6 * inv), y - (4 * inv), formatted.Width + (12 * inv), formatted.Height + (8 * inv)));
        dc.DrawText(formatted, new Point(x, y));
    }

    private void DrawPreview(DrawingContext dc, double inv)
    {
        var points = _previewPoints;
        if (points == null || points.Count == 0) return;

        bool exclude = _previewKind == RegionKind.Exclude;
        var color = exclude ? ExcludeColor : IncludeColor;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, 2.0 * inv) { DashStyle = DashStyles.Dash };
        pen.Freeze();

        if (_previewIsRectangle && points.Count >= 3)
        {
            dc.DrawGeometry(exclude ? ExcludeFill : IncludeFill, pen, BuildPolygon(points, closed: true));
            return;
        }

        var chain = new List<Vector2>(points);
        if (_previewCursor.HasValue) chain.Add(_previewCursor.Value);

        if (chain.Count >= 2)
        {
            dc.DrawGeometry(null, pen, BuildPolygon(chain, closed: false));
        }

        for (int i = 0; i < points.Count; i++)
        {
            double radius = (i == 0 ? 6.0 : 4.0) * inv;
            dc.DrawEllipse(brush, null, new Point(points[i].X, points[i].Y), radius, radius);
        }
    }

    private static StreamGeometry BuildPolygon(IReadOnlyList<Vector2> points, bool closed)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: closed, isClosed: closed);
            for (int i = 1; i < points.Count; i++)
            {
                ctx.LineTo(new Point(points[i].X, points[i].Y), isStroked: true, isSmoothJoin: false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private void DrawSphericalGrid(DrawingContext dc, double inv, double width, double height)
    {
        var wcs = Wcs;
        if (wcs == null) return;

        var gridLines = wcs.GenerateSphericalGrid();
        if (gridLines.Count == 0) return;

        var gridPen = new Pen(MakeBrush(56, 189, 248, 100), 1.2 * inv);
        gridPen.Freeze();

        var labelBrush = MakeBrush(56, 189, 248, 220);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        double fontSize = Math.Max(9.0, 11.0 * inv);

        foreach (var line in gridLines)
        {
            if (line.Points.Length < 2) continue;

            var streamGeom = new StreamGeometry();
            using (var ctx = streamGeom.Open())
            {
                ctx.BeginFigure(new Point(line.Points[0].X, line.Points[0].Y), isFilled: false, isClosed: false);
                for (int i = 1; i < line.Points.Length; i++)
                {
                    ctx.LineTo(new Point(line.Points[i].X, line.Points[i].Y), isStroked: true, isSmoothJoin: true);
                }
            }
            streamGeom.Freeze();
            dc.DrawGeometry(null, gridPen, streamGeom);

            // Draw coordinate label near the middle of the line
            int midIdx = line.Points.Length / 2;
            var midPt = line.Points[midIdx];
            if (midPt.X >= 0 && midPt.X <= width && midPt.Y >= 0 && midPt.Y <= height)
            {
                var ft = new FormattedText(
                    line.Label,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    labelBrush,
                    dpi);

                dc.DrawRectangle(LabelBackground, null, new Rect(midPt.X + (4 * inv), midPt.Y - (ft.Height / 2), ft.Width + (6 * inv), ft.Height));
                dc.DrawText(ft, new Point(midPt.X + (7 * inv), midPt.Y - (ft.Height / 2)));
            }
        }
    }

    private void DrawTargetMarker(DrawingContext dc, double inv)
    {
        if (!TargetMarkerPixel.HasValue) return;

        Point pt = TargetMarkerPixel.Value;
        double outerRad = 22.0 * inv;
        double innerRad = 10.0 * inv;
        double tickLen = 9.0 * inv;

        var shadowPen = new Pen(MakeBrush(0, 0, 0, 210), 4.0 * inv);
        shadowPen.Freeze();
        var cyanPen = new Pen(MakeBrush(56, 189, 248, 255), 2.2 * inv);
        cyanPen.Freeze();
        var goldPen = new Pen(MakeBrush(250, 204, 21, 240), 1.6 * inv) { DashStyle = DashStyles.Dash };
        goldPen.Freeze();

        // Outer dashed ring + inner solid reticle ring
        dc.DrawEllipse(null, shadowPen, pt, outerRad, outerRad);
        dc.DrawEllipse(null, goldPen, pt, outerRad, outerRad);
        dc.DrawEllipse(null, shadowPen, pt, innerRad, innerRad);
        dc.DrawEllipse(null, cyanPen, pt, innerRad, innerRad);

        // 4 cardinal crosshair ticks outside inner ring
        dc.DrawLine(shadowPen, new Point(pt.X - innerRad - tickLen, pt.Y), new Point(pt.X - innerRad, pt.Y));
        dc.DrawLine(shadowPen, new Point(pt.X + innerRad, pt.Y), new Point(pt.X + innerRad + tickLen, pt.Y));
        dc.DrawLine(shadowPen, new Point(pt.X, pt.Y - innerRad - tickLen), new Point(pt.X, pt.Y - innerRad));
        dc.DrawLine(shadowPen, new Point(pt.X, pt.Y + innerRad), new Point(pt.X, pt.Y + innerRad + tickLen));

        dc.DrawLine(cyanPen, new Point(pt.X - innerRad - tickLen, pt.Y), new Point(pt.X - innerRad, pt.Y));
        dc.DrawLine(cyanPen, new Point(pt.X + innerRad, pt.Y), new Point(pt.X + innerRad + tickLen, pt.Y));
        dc.DrawLine(cyanPen, new Point(pt.X, pt.Y - innerRad - tickLen), new Point(pt.X, pt.Y - innerRad));
        dc.DrawLine(cyanPen, new Point(pt.X, pt.Y + innerRad), new Point(pt.X, pt.Y + innerRad + tickLen));

        if (!string.IsNullOrWhiteSpace(TargetMarkerLabel))
        {
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var ft = new FormattedText(
                TargetMarkerLabel,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                13.0 * inv,
                MakeBrush(250, 204, 21, 255),
                dpi);

            double lx = pt.X + outerRad + (6 * inv);
            double ly = pt.Y - (ft.Height / 2.0);
            var bgRect = new Rect(lx - (6 * inv), ly - (3 * inv), ft.Width + (12 * inv), ft.Height + (6 * inv));
            dc.DrawRoundedRectangle(LabelBackground, cyanPen, bgRect, 4 * inv, 4 * inv);
            dc.DrawText(ft, new Point(lx, ly));
        }
    }

    private static SolidColorBrush MakeBrush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
