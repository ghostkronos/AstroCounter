namespace AstroCounter.UI;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
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

        DrawRegions(dc, inv);

        if (ShowBoxes)
        {
            DrawObjects(dc, inv, width, height);
        }

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
            DrawHint(dc, inv, $"Etiketler için yakınlaştırın (görünür yıldız: {labelObjects.Count})");
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

    private static SolidColorBrush MakeBrush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
