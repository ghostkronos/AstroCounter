using AstroCounter.Core.Processing;
namespace AstroCounter.UI;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using AstroCounter.Core.Astrometry;
using AstroCounter.Core.Models;

/// <summary>
/// A non-clipping container Grid that disables WPF's automatic layout slot clipping,
/// allowing 2D astrophotography mosaic tiles, constellation lines, and reference stars
/// to render seamlessly across the entire infinite viewport without being cropped by a bounding box.
/// </summary>
public class InfiniteCanvasPanel : System.Windows.Controls.Grid
{
    public InfiniteCanvasPanel()
    {
        ClipToBounds = false;
    }

    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => null;
}

/// <summary>
/// Lightweight overlay that draws detections, scan regions and the region being drawn directly
/// through a DrawingContext (instead of tens of thousands of WPF elements).
/// Marker size and stroke thickness are expressed in screen pixels, so small stars stay visible at any zoom.
/// </summary>
public sealed class DetectionOverlay : FrameworkElement
{
    public DetectionOverlay()
    {
        ClipToBounds = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);
    }

    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => null;

    private const int MaxLabels = 1000;
    private const int HaloLimit = 8000;
    private const double MinMarkerScreenPx = 10.0;
    private const double StrokeScreenPx = 1.6;
    private const double LabelFontScreenPx = 12.0;

    public static readonly DependencyProperty ObjectsProperty = DependencyProperty.Register(
        nameof(Objects), typeof(IReadOnlyList<AstrophotographyObject>), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnObjectsChanged));

    private static void OnObjectsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DetectionOverlay overlay)
        {
            overlay.RebuildSpatialIndex(e.NewValue as IReadOnlyList<AstrophotographyObject>);
        }
    }

    public static readonly DependencyProperty RegionsProperty = DependencyProperty.Register(
        nameof(Regions), typeof(IReadOnlyList<SelectionRegion>), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowBoxesProperty = DependencyProperty.Register(
        nameof(ShowBoxes), typeof(bool), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowLabelsProperty = DependencyProperty.Register(
        nameof(ShowLabels), typeof(bool), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowSphericalGridProperty = DependencyProperty.Register(
        nameof(ShowSphericalGrid), typeof(bool), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowHipparcosStarsProperty = DependencyProperty.Register(
        nameof(ShowHipparcosStars), typeof(bool), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowConstellationsProperty = DependencyProperty.Register(
        nameof(ShowConstellations), typeof(bool), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowDsoMarkersProperty = DependencyProperty.Register(
        nameof(ShowDsoMarkers), typeof(bool), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ActiveFilePathProperty = DependencyProperty.Register(
        nameof(ActiveFilePath), typeof(string), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MosaicPanelsProperty = DependencyProperty.Register(
        nameof(MosaicPanels), typeof(IReadOnlyList<MosaicPanel>), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WcsProperty = DependencyProperty.Register(
        nameof(Wcs), typeof(WcsMatrix), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TargetMarkerPixelProperty = DependencyProperty.Register(
        nameof(TargetMarkerPixel), typeof(Point?), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TargetMarkerLabelProperty = DependencyProperty.Register(
        nameof(TargetMarkerLabel), typeof(string), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UseFeatheredBlendingProperty = DependencyProperty.Register(
        nameof(UseFeatheredBlending), typeof(bool), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool UseFeatheredBlending
    {
        get => (bool)GetValue(UseFeatheredBlendingProperty);
        set => SetValue(UseFeatheredBlendingProperty, value);
    }

    public static readonly DependencyProperty MosaicEquirectangularProjectionProperty = DependencyProperty.Register(
        nameof(MosaicEquirectangularProjection), typeof(EquirectangularProjection), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public EquirectangularProjection? MosaicEquirectangularProjection
    {
        get => (EquirectangularProjection?)GetValue(MosaicEquirectangularProjectionProperty);
        set => SetValue(MosaicEquirectangularProjectionProperty, value);
    }

    public static readonly DependencyProperty MosaicCompositeBitmapProperty = DependencyProperty.Register(
        nameof(MosaicCompositeBitmap), typeof(System.Windows.Media.Imaging.BitmapSource), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public System.Windows.Media.Imaging.BitmapSource? MosaicCompositeBitmap
    {
        get => (System.Windows.Media.Imaging.BitmapSource?)GetValue(MosaicCompositeBitmapProperty);
        set => SetValue(MosaicCompositeBitmapProperty, value);
    }

    public static readonly DependencyProperty CompositeOpacityProperty = DependencyProperty.Register(
        nameof(CompositeOpacity), typeof(double), typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double CompositeOpacity
    {
        get => (double)GetValue(CompositeOpacityProperty);
        set => SetValue(CompositeOpacityProperty, value);
    }


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

    private static readonly Brush ConstellationLineBrush = MakeBrush(56, 189, 248, 175);
    private static readonly Brush ConstellationTextBrush = MakeBrush(125, 211, 252, 245);
    private static readonly Brush ConstellationBackground = MakeBrush(15, 23, 42, 215);

    private static readonly Brush DsoCircleBrush = MakeBrush(250, 204, 21, 210);
    private static readonly Brush DsoFillBrush = MakeBrush(250, 204, 21, 30);
    private static readonly Brush DsoTextBrush = MakeBrush(254, 240, 138, 250);
    private static readonly Brush DsoLabelBackground = MakeBrush(15, 23, 42, 220);

    private static readonly Brush StarLabelBrush = MakeBrush(226, 232, 240, 235);
    private static readonly Brush StarLabelBackground = MakeBrush(15, 23, 42, 205);

    private static readonly Brush StarBlueBrush = MakeBrush(147, 197, 253, 255);
    private static readonly Brush StarWhiteBrush = MakeBrush(248, 250, 252, 255);
    private static readonly Brush StarYellowBrush = MakeBrush(254, 240, 138, 255);
    private static readonly Brush StarOrangeBrush = MakeBrush(253, 186, 116, 255);
    private static readonly Brush StarRedBrush = MakeBrush(248, 113, 113, 255);

    private static readonly Brush StarBlueHaloBrush = MakeBrush(147, 197, 253, 55);
    private static readonly Brush StarWhiteHaloBrush = MakeBrush(248, 250, 252, 55);
    private static readonly Brush StarYellowHaloBrush = MakeBrush(254, 240, 138, 55);
    private static readonly Brush StarOrangeHaloBrush = MakeBrush(253, 186, 116, 55);
    private static readonly Brush StarRedHaloBrush = MakeBrush(248, 113, 113, 55);

    private static readonly Brush MosaicBorderBrush = MakeBrush(168, 85, 247, 210);
    private static readonly Brush MosaicFillBrush = MakeBrush(168, 85, 247, 28);
    private static readonly Brush MosaicTextBrush = MakeBrush(216, 180, 254, 250);

    private double _zoom = 1.0;
    private Rect _viewport = Rect.Empty;

    private SpatialQuadTree? _spatialQuadTree;
    private readonly List<AstrophotographyObject> _visibleQueryBuffer = new(4096);
    private readonly List<Rect> _allVisibleRects = new(4096);
    private readonly List<Rect>[] _tierRects = new List<Rect>[4]
    {
        new(2048),
        new(2048),
        new(2048),
        new(2048)
    };
    private readonly List<Point>[] _tierDots = new List<Point>[4]
    {
        new(2048),
        new(2048),
        new(2048),
        new(2048)
    };
    private WcsMatrix? _lastHipparcosWcs;
    private EquirectangularProjection? _lastHipparcosProj;
    private List<HipparcosStar>? _cachedHipparcosStars;
    private static readonly Brush EquirectangularSkyBrush = CreateEquirectangularSkyBrush();

    private static Brush CreateEquirectangularSkyBrush()
    {
        var brush = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromRgb(4, 7, 16), 0.0),
                new GradientStop(Color.FromRgb(8, 15, 34), 0.5),
                new GradientStop(Color.FromRgb(4, 7, 16), 1.0)
            },
            new Point(0.5, 0.0),
            new Point(0.5, 1.0));
        brush.Freeze();
        return brush;
    }
    private readonly Dictionary<(int Pct, int Tier, int SizeKey), FormattedText> _labelTextCache = new(512);
    private static readonly Typeface LabelTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private long _spatialIndexVersion = 0;

    private void RebuildSpatialIndex(IReadOnlyList<AstrophotographyObject>? objects)
    {
        long currentVersion = Interlocked.Increment(ref _spatialIndexVersion);
        _labelTextCache.Clear();

        if (objects == null || objects.Count == 0)
        {
            Volatile.Write(ref _spatialQuadTree, null);
            return;
        }

        // Fast synchronous build for small scenes (< 5,000 objects)
        if (objects.Count < 5000)
        {
            var quickTree = new SpatialQuadTree(objects);
            Volatile.Write(ref _spatialQuadTree, quickTree);
            return;
        }

        // Background asynchronous QuadTree build for massive datasets (10,000 to 600,000+ objects)
        Task.Run(() =>
        {
            var newTree = new SpatialQuadTree(objects);
            if (Interlocked.Read(ref _spatialIndexVersion) == currentVersion)
            {
                Volatile.Write(ref _spatialQuadTree, newTree);
                Dispatcher.InvokeAsync(InvalidateVisual);
            }
        });
    }

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

    public bool ShowHipparcosStars
    {
        get => (bool)GetValue(ShowHipparcosStarsProperty);
        set => SetValue(ShowHipparcosStarsProperty, value);
    }

    public bool ShowConstellations
    {
        get => (bool)GetValue(ShowConstellationsProperty);
        set => SetValue(ShowConstellationsProperty, value);
    }

    public bool ShowDsoMarkers
    {
        get => (bool)GetValue(ShowDsoMarkersProperty);
        set => SetValue(ShowDsoMarkersProperty, value);
    }

    public string? ActiveFilePath
    {
        get => (string?)GetValue(ActiveFilePathProperty);
        set => SetValue(ActiveFilePathProperty, value);
    }

    public IReadOnlyList<MosaicPanel>? MosaicPanels
    {
        get => (IReadOnlyList<MosaicPanel>?)GetValue(MosaicPanelsProperty);
        set => SetValue(MosaicPanelsProperty, value);
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

        var eqProj = MosaicEquirectangularProjection;
        // Transparent fill in raw single-photo mode; deep night-sky celestial gradient in equirectangular mode.
        dc.DrawRectangle(eqProj != null ? EquirectangularSkyBrush : Brushes.Transparent, null, new Rect(0, 0, width, height));

        double inv = 1.0 / Math.Max(_zoom, 0.20);

        // Layer 0: Background Photo Mosaic Panels (Equirectangular Composite or Legacy Flat Affine)
        if (eqProj != null || (MosaicPanels != null && MosaicPanels.Count > 0))
        {
            DrawMosaicPanels(dc, inv, width, height);
        }

        // Layer 1-4: Astrometric Grid, Constellations, Stars, DSO Markers
        if (Wcs != null || eqProj != null)
        {
            var placedSkyLabels = (ShowConstellations || ShowHipparcosStars || ShowDsoMarkers)
                ? new List<Rect>(64)
                : null;

            if (ShowSphericalGrid)
            {
                DrawSphericalGrid(dc, inv, width, height);
            }

            if (ShowConstellations)
            {
                DrawConstellationLines(dc, inv, width, height, placedSkyLabels);
            }

            if (ShowHipparcosStars)
            {
                DrawHipparcosStars(dc, inv, width, height, placedSkyLabels);
            }

            if (ShowDsoMarkers)
            {
                DrawDsoMarkers(dc, inv, width, height, placedSkyLabels);
            }
        }

        // Layer 5: Regions (Scan Polygons)
        DrawRegions(dc, inv);

        // Layer 6: Object Detection Boxes & Labels & Anomalies
        if (ShowBoxes)
        {
            DrawObjects(dc, inv, width, height);
            DrawAnomalies(dc, inv, width, height);
        }

        // Layer 7: Target Marker
        DrawTargetMarker(dc, inv);

        // Layer 8: Drawing Tool Preview
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

        _visibleQueryBuffer.Clear();
        if (_spatialQuadTree != null)
        {
            _spatialQuadTree.Query((float)view.Left, (float)view.Top, (float)view.Right, (float)view.Bottom, _visibleQueryBuffer);
        }
        else
        {
            for (int i = 0; i < objects.Count; i++)
            {
                var o = objects[i];
                if (view.Contains(o.CenterX, o.CenterY))
                {
                    _visibleQueryBuffer.Add(o);
                }
            }
        }

        int visibleCount = _visibleQueryBuffer.Count;
        if (visibleCount == 0) return;

        bool isZoomedOut = _zoom < 0.40;
        bool isFarZoomedOut = _zoom < 0.20;
        bool isLODFarOut = _zoom < 0.35; // LOD threshold: exclude Tier 2 & 3 faint detections (< 35% zoom)

        // Bounded marker sizing: when zoomed out, min screen size is 5px; when zoomed in, 8px
        double screenMinPx = isZoomedOut ? 5.0 : 8.0;
        double minSize = screenMinPx * inv;
        double stroke = Math.Max(StrokeScreenPx * inv, 1.0);

        var pens = new Pen[TierBrushes.Length];
        for (int i = 0; i < pens.Length; i++)
        {
            pens[i] = new Pen(TierBrushes[i], stroke);
            pens[i].Freeze();
        }

        // LOD: Only draw halos if zoom is not far out and visible count is reasonable
        bool drawHalo = _zoom >= 0.22 && visibleCount <= HaloLimit;
        Pen? haloPen = null;
        if (drawHalo)
        {
            haloPen = new Pen(HaloBrush, stroke * 2.2);
            haloPen.Freeze();
            _allVisibleRects.Clear();
        }

        for (int i = 0; i < 4; i++)
        {
            _tierRects[i].Clear();
            _tierDots[i].Clear();
        }

        var labelObjects = ShowLabels ? new List<(AstrophotographyObject Obj, double Size, double Cx, double Cy)>(Math.Min(visibleCount, MaxLabels + 50)) : null;

        // Architectural directive: Strict LOD and Marker Budgeting to guarantee 60 FPS at any zoom level
        const int MaxBoxBudget = 2500;
        int drawnBoxCount = 0;

        bool isDense = visibleCount > 1500;
        double subpixelCell = isDense ? Math.Max(2.5 * inv, 3.0) : 0;
        HashSet<long>? visitedScreenPixels = isDense ? new HashSet<long>(Math.Min(visibleCount, 25000)) : null;

        for (int i = 0; i < visibleCount; i++)
        {
            var obj = _visibleQueryBuffer[i];
            int tierIdx = Math.Clamp((int)obj.Tier, 0, 3);

            // Phase 2 Density LOD: When zoomed out (< 35%), suppress faint Tier 2, Tier 3, and FilteredNoise
            if (isLODFarOut && tierIdx >= 1)
            {
                continue;
            }

            double cx = obj.CenterX;
            double cy = obj.CenterY;

            if (cx == 0 && cy == 0 && obj.HasCelestialCoordinates && Wcs != null)
            {
                if (Wcs.EquatorialToPixel(obj.RaDegrees!.Value, obj.DecDegrees!.Value, out double px, out double py))
                {
                    cx = px;
                    cy = py;
                }
            }

            if (isDense && visitedScreenPixels != null && tierIdx >= 2)
            {
                long sx = (long)Math.Floor((cx - view.Left) / subpixelCell);
                long sy = (long)Math.Floor((cy - view.Top) / subpixelCell);
                long cellKey = (sx << 32) ^ (sy & 0xFFFFFFFFL);
                if (!visitedScreenPixels.Add(cellKey))
                {
                    continue; // Skip redundant subpixel duplicate marker
                }
            }

            // Adaptive LOD: Cap total drawn boxes across all tiers to 2,500
            // Tier 0/1 get boxes; Tier 3 is ALWAYS rendered as subtle micro-diamond dots
            bool canDrawBox = drawnBoxCount < MaxBoxBudget;
            bool drawBox = tierIdx switch
            {
                0 => canDrawBox,
                1 => canDrawBox && !isFarZoomedOut && (visibleCount <= 3500 || _zoom >= 0.40),
                2 => canDrawBox && _zoom >= 1.5 && visibleCount <= 1200,
                _ => false // Tier 3 is ALWAYS rendered as fast micro-diamond dots
            };

            if (drawBox)
            {
                double rawW = Math.Max(obj.Box.Width, 3.0);
                double rawH = Math.Max(obj.Box.Height, 3.0);
                double size = Math.Max(Math.Max(rawW, rawH), minSize);
                if (isZoomedOut)
                {
                    size = Math.Min(size, 16.0 * inv);
                }

                var rect = new Rect(cx - (size / 2.0), cy - (size / 2.0), size, size);

                if (drawHalo && tierIdx <= 1)
                {
                    _allVisibleRects.Add(rect);
                }

                _tierRects[tierIdx].Add(rect);
                drawnBoxCount++;

                if (ShowLabels && tierIdx == 0 && (labelObjects?.Count ?? 0) < MaxLabels)
                {
                    labelObjects?.Add((obj, size, cx, cy));
                }
            }
            else
            {
                // When far zoomed out with massive star count, sub-sample lowest tier noise
                if (isFarZoomedOut && tierIdx >= 2 && (i % 2 != 0))
                {
                    continue;
                }

                _tierDots[tierIdx].Add(new Point(cx, cy));
            }
        }

        // Batched geometry draw call for halos (1 draw call instead of thousands)
        if (drawHalo && haloPen != null && _allVisibleRects.Count > 0)
        {
            var haloGeom = new StreamGeometry();
            using (var sgc = haloGeom.Open())
            {
                AppendRectangles(sgc, _allVisibleRects);
            }
            haloGeom.Freeze();
            dc.DrawGeometry(null, haloPen, haloGeom);
        }

        // Batched geometry draw call for boxes per tier (4 draw calls instead of thousands)
        for (int i = 0; i < 4; i++)
        {
            var list = _tierRects[i];
            if (list.Count == 0) continue;

            var tierGeom = new StreamGeometry();
            using (var sgc = tierGeom.Open())
            {
                AppendRectangles(sgc, list);
            }
            tierGeom.Freeze();
            dc.DrawGeometry(null, pens[i], tierGeom);
        }

        // Batched geometry draw call for subtle micro-diamond dots per tier (4 draw calls instead of thousands)
        double dotRadius = Math.Max(1.0 * inv, 1.0);
        for (int i = 0; i < 4; i++)
        {
            var dots = _tierDots[i];
            if (dots.Count == 0) continue;

            var dotGeom = new StreamGeometry();
            using (var sgc = dotGeom.Open())
            {
                AppendDiamondDots(sgc, dots, dotRadius);
            }
            dotGeom.Freeze();
            dc.DrawGeometry(TierBrushes[i], null, dotGeom);
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

    private static void AppendDiamondDots(StreamGeometryContext ctx, List<Point> dots, double radius)
    {
        for (int i = 0; i < dots.Count; i++)
        {
            var p = dots[i];
            ctx.BeginFigure(new Point(p.X, p.Y - radius), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(p.X + radius, p.Y), isStroked: false, isSmoothJoin: false);
            ctx.LineTo(new Point(p.X, p.Y + radius), isStroked: false, isSmoothJoin: false);
            ctx.LineTo(new Point(p.X - radius, p.Y), isStroked: false, isSmoothJoin: false);
        }
    }

    private static void AppendRectangles(StreamGeometryContext ctx, List<Rect> rects)
    {
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            ctx.BeginFigure(new Point(r.X, r.Y), isFilled: false, isClosed: true);
            ctx.LineTo(new Point(r.Right, r.Y), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new Point(r.Right, r.Bottom), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new Point(r.X, r.Bottom), isStroked: true, isSmoothJoin: false);
        }
    }

    private void DrawLabels(DrawingContext dc, double inv, List<(AstrophotographyObject Obj, double Size, double Cx, double Cy)> labelObjects)
    {
        if (labelObjects.Count == 0 || _zoom < 0.25) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double fontSize = Math.Clamp(LabelFontScreenPx * inv, 9.0, 24.0);

        // Pass 1: Draw all label background rounded boxes in a single batched geometry
        var bgGeom = new StreamGeometry();
        using (var bgCtx = bgGeom.Open())
        {
            for (int i = 0; i < labelObjects.Count; i++)
            {
                var (obj, size, cx, cy) = labelObjects[i];
                int tierIdx = Math.Clamp((int)obj.Tier, 0, TierBrushes.Length - 1);
                int pct = (int)Math.Round(obj.Confidence * 100);
                var formatted = GetCachedLabelText(pct, tierIdx, fontSize, TierBrushes[tierIdx], dpi);

                double x = cx + (size / 2) + (3 * inv);
                double y = cy - (formatted.Height / 2);
                var r = new Rect(x - (2 * inv), y - inv, formatted.Width + (4 * inv), formatted.Height + (2 * inv));

                bgCtx.BeginFigure(new Point(r.X, r.Y), isFilled: true, isClosed: true);
                bgCtx.LineTo(new Point(r.Right, r.Y), isStroked: false, isSmoothJoin: false);
                bgCtx.LineTo(new Point(r.Right, r.Bottom), isStroked: false, isSmoothJoin: false);
                bgCtx.LineTo(new Point(r.X, r.Bottom), isStroked: false, isSmoothJoin: false);
            }
        }
        bgGeom.Freeze();
        dc.DrawGeometry(LabelBackground, null, bgGeom);

        // Pass 2: Draw the cached text glyphs without reallocating FormattedText
        for (int i = 0; i < labelObjects.Count; i++)
        {
            var (obj, size, cx, cy) = labelObjects[i];
            int tierIdx = Math.Clamp((int)obj.Tier, 0, TierBrushes.Length - 1);
            int pct = (int)Math.Round(obj.Confidence * 100);
            var formatted = GetCachedLabelText(pct, tierIdx, fontSize, TierBrushes[tierIdx], dpi);

            double x = cx + (size / 2) + (3 * inv);
            double y = cy - (formatted.Height / 2);
            dc.DrawText(formatted, new Point(x, y));
        }
    }

    private FormattedText GetCachedLabelText(int confidencePct, int tier, double fontSize, Brush brush, double dpi)
    {
        confidencePct = Math.Clamp(confidencePct, 0, 100);
        tier = Math.Clamp(tier, 0, 3);
        int sizeKey = (int)Math.Round(fontSize * 4.0);

        var key = (confidencePct, tier, sizeKey);
        if (_labelTextCache.TryGetValue(key, out var ft))
            return ft;

        if (_labelTextCache.Count > 1500)
        {
            _labelTextCache.Clear();
        }

        string text = $"{confidencePct}%";
        ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            LabelTypeface, fontSize, brush, dpi);
        _labelTextCache[key] = ft;
        return ft;
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

    private void DrawAnomalies(DrawingContext dc, double inv, double width, double height)
    {
        var objects = Objects;
        if (objects == null || objects.Count == 0) return;

        Pen anomalyPen = new Pen(new SolidColorBrush(Color.FromRgb(244, 63, 94)), 1.8 * inv);
        anomalyPen.Freeze();
        Pen bracketPen = new Pen(new SolidColorBrush(Color.FromRgb(251, 113, 133)), 1.4 * inv);
        bracketPen.Freeze();
        Brush fillBrush = new SolidColorBrush(Color.FromArgb(45, 244, 63, 94));
        fillBrush.Freeze();
        Brush textBg = new SolidColorBrush(Color.FromArgb(230, 15, 23, 42));
        textBg.Freeze();

        Rect view = _viewport.IsEmpty ? new Rect(0, 0, width, height) : _viewport;
        view.Inflate(50 * inv, 50 * inv);

        foreach (var obj in objects)
        {
            if (!obj.IsAnomaly) continue;

            double cx = obj.CenterX;
            double cy = obj.CenterY;

            // In single image or mosaic mode, obj.CenterX and obj.CenterY are already the exact canvas pixel coordinates.
            // Only fall back to equatorial reprojection if pixel coordinates are uninitialized (0, 0).
            if (cx == 0 && cy == 0 && obj.HasCelestialCoordinates && Wcs != null)
            {
                if (Wcs.EquatorialToPixel(obj.RaDegrees!.Value, obj.DecDegrees!.Value, out double px, out double py))
                {
                    cx = px;
                    cy = py;
                }
            }

            if (!view.Contains(cx, cy)) continue;

            double r = Math.Max(obj.Box.Width / 2.0, 14.0 * inv);

            // 1. Central targeting reticle circle
            dc.DrawEllipse(fillBrush, anomalyPen, new Point(cx, cy), r, r);

            // 2. Crosshair reticle ticks
            double tickOffset = r + (3.0 * inv);
            double tickLen = 6.0 * inv;
            dc.DrawLine(bracketPen, new Point(cx - tickOffset - tickLen, cy), new Point(cx - tickOffset, cy));
            dc.DrawLine(bracketPen, new Point(cx + tickOffset, cy), new Point(cx + tickOffset + tickLen, cy));
            dc.DrawLine(bracketPen, new Point(cx, cy - tickOffset - tickLen), new Point(cx, cy - tickOffset));
            dc.DrawLine(bracketPen, new Point(cx, cy + tickOffset), new Point(cx, cy + tickOffset + tickLen));

            // 3. Anomaly Target Label
            string labelText = "🎯 " + (obj.AnomalyType ?? "ANOMALİ") + $" (SNR: {obj.Snr:F1}σ)";
            var ft = new FormattedText(
                labelText,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                11.0 * inv,
                new SolidColorBrush(Color.FromRgb(251, 113, 133)),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            double bgPad = 4.0 * inv;
            var textRect = new Rect(cx - (ft.Width / 2.0) - bgPad, cy + r + (4.0 * inv), ft.Width + (bgPad * 2), ft.Height + bgPad);
            dc.DrawRoundedRectangle(textBg, bracketPen, textRect, 4.0 * inv, 4.0 * inv);
            dc.DrawText(ft, new Point(cx - (ft.Width / 2.0), cy + r + (4.0 * inv) + (bgPad * 0.5)));
        }
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
        var eqProj = MosaicEquirectangularProjection;
        if (eqProj != null)
        {
            DrawEquirectangularGrid(dc, eqProj, inv, width, height);
            return;
        }

        var wcs = Wcs;
        if (wcs == null) return;

        var gridLines = wcs.GenerateSphericalGrid();
        if (gridLines.Count == 0) return;

        var gridPen = new Pen(MakeBrush(56, 189, 248, 100), 1.2 * inv);
        gridPen.Freeze();

        var labelBrush = MakeBrush(56, 189, 248, 220);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        double fontSize = Math.Clamp(11.0 * inv, 9.0, 22.0);

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

    private void DrawEquirectangularGrid(DrawingContext dc, EquirectangularProjection proj, double inv, double width, double height)
    {
        var gridPen = new Pen(MakeBrush(56, 189, 248, 85), 1.15 * inv);
        gridPen.Freeze();

        var labelBrush = MakeBrush(56, 189, 248, 215);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        double fontSize = Math.Clamp(11.0 * inv, 9.0, 22.0);

        static double ChooseStep(double span, double[] candidates, int targetLines)
        {
            foreach (double step in candidates)
            {
                if (span / step <= targetLines) return step;
            }
            return candidates[^1];
        }

        double raStep = ChooseStep(proj.RaSpanDeg, new[] { 0.5, 1.0, 2.0, 5.0, 10.0, 15.0, 30.0, 45.0 }, 9);
        double decStep = ChooseStep(proj.DecSpanDeg, new[] { 0.5, 1.0, 2.0, 5.0, 10.0, 15.0, 30.0 }, 7);

        // Vertical RA meridians across [-RaSpan/2 .. +RaSpan/2]
        int maxRaSteps = (int)Math.Ceiling(proj.RaSpanDeg / raStep) + 2;
        double startOffset = -Math.Ceiling((proj.RaSpanDeg * 0.5) / raStep) * raStep;
        double baseRaGrid = Math.Round(proj.CenterRaDeg / raStep) * raStep;
        for (int i = 0; i <= maxRaSteps; i++)
        {
            double raDeg = EquirectangularProjection.NormalizeRa(baseRaGrid + startOffset + (i * raStep));
            double relRa = proj.RelativeRaDeg(raDeg);
            if (Math.Abs(relRa) > (proj.RaSpanDeg * 0.5) + 0.01) continue;

            double x = (0.5 + (relRa / proj.RaSpanDeg)) * width;
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, height));

            double totalHours = raDeg / 15.0;
            int hh = (int)Math.Floor(totalHours) % 24;
            int mm = (int)Math.Round((totalHours - Math.Floor(totalHours)) * 60.0);
            if (mm >= 60) { mm = 0; hh = (hh + 1) % 24; }
            string raLabel = mm == 0 ? $"{hh}h" : $"{hh}h{mm:00}m";

            var ft = new FormattedText(raLabel, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize, labelBrush, dpi);
            double lx = Math.Clamp(x + (4 * inv), 4 * inv, Math.Max(4 * inv, width - ft.Width - (8 * inv)));
            double ly = 10 * inv;
            dc.DrawRectangle(LabelBackground, null, new Rect(lx - (3 * inv), ly - (2 * inv), ft.Width + (6 * inv), ft.Height + (4 * inv)));
            dc.DrawText(ft, new Point(lx, ly));
        }

        // Horizontal Dec parallels
        double firstDec = Math.Ceiling(proj.DecMinDeg / decStep) * decStep;
        for (double decDeg = firstDec; decDeg <= proj.DecMaxDeg + 1e-6; decDeg += decStep)
        {
            if (Math.Abs(decDeg) > 89.9) continue;
            double y = ((proj.DecMaxDeg - decDeg) / proj.DecSpanDeg) * height;
            dc.DrawLine(gridPen, new Point(0, y), new Point(width, y));

            string decLabel = decStep < 1.0 ? $"{decDeg:+0.0;-0.0;0}°" : $"{decDeg:+0;-0;0}°";
            var ft = new FormattedText(decLabel, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize, labelBrush, dpi);
            double lx = 10 * inv;
            double ly = Math.Clamp(y - (ft.Height * 0.5), 6 * inv, Math.Max(6 * inv, height - ft.Height - (6 * inv)));
            dc.DrawRectangle(LabelBackground, null, new Rect(lx - (3 * inv), ly - (2 * inv), ft.Width + (6 * inv), ft.Height + (4 * inv)));
            dc.DrawText(ft, new Point(lx, ly));
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
                Math.Clamp(13.0 * inv, 11.0, 26.0),
                MakeBrush(250, 204, 21, 255),
                dpi);

            double lx = pt.X + outerRad + (6 * inv);
            double ly = pt.Y - (ft.Height / 2.0);
            var bgRect = new Rect(lx - (6 * inv), ly - (3 * inv), ft.Width + (12 * inv), ft.Height + (6 * inv));
            dc.DrawRoundedRectangle(LabelBackground, cyanPen, bgRect, 4 * inv, 4 * inv);
            dc.DrawText(ft, new Point(lx, ly));
        }
    }

    private static bool TryReserveLabelRect(List<Rect>? occupied, Rect candidate, double padX = 4.0, double padY = 3.0)
    {
        if (occupied == null) return true;
        var padded = new Rect(candidate.X - padX, candidate.Y - padY, candidate.Width + (2.0 * padX), candidate.Height + (2.0 * padY));
        for (int i = 0; i < occupied.Count; i++)
        {
            if (occupied[i].IntersectsWith(padded))
            {
                return false;
            }
        }
        occupied.Add(padded);
        return true;
    }

    private bool IsCoordinateNearSolvedMosaic(double raDeg, double decDeg, double marginDeg)
    {
        var panels = MosaicPanels;
        if (panels == null || panels.Count == 0) return true;

        bool anySolved = false;
        var coord = new CelestialCoordinates(raDeg, decDeg);
        for (int i = 0; i < panels.Count; i++)
        {
            var p = panels[i];
            if (!p.IsVisible || !p.IsSolved || p.Wcs == null) continue;
            anySolved = true;

            double halfDiagDeg = 0.55 * Math.Sqrt((p.Wcs.FovWidthDegrees * p.Wcs.FovWidthDegrees) + (p.Wcs.FovHeightDegrees * p.Wcs.FovHeightDegrees));
            if (p.Wcs.CenterCoordinates.AngularDistanceTo(coord) <= Math.Max(3.5, halfDiagDeg + marginDeg))
            {
                return true;
            }
        }

        return !anySolved;
    }

    private void DrawHipparcosStars(DrawingContext dc, double inv, double width, double height, List<Rect>? placedLabels = null)
    {
        var eqProj = MosaicEquirectangularProjection;
        var wcs = Wcs;
        if ((wcs == null && eqProj == null) || width <= 0 || height <= 0) return;

        if (eqProj != null)
        {
            double spanDeg = Math.Sqrt((eqProj.RaSpanDeg * eqProj.RaSpanDeg) + (eqProj.DecSpanDeg * eqProj.DecSpanDeg));
            double searchRadius = Math.Clamp(spanDeg * 0.65, 10.0, 180.0);
            double midDec = Math.Clamp(0.5 * (eqProj.DecMinDeg + eqProj.DecMaxDeg), -89.9, 89.9);
            double maxMag = eqProj.RaSpanDeg switch
            {
                > 90.0 => 6.3,
                > 45.0 => 6.7,
                > 20.0 => 7.4,
                _ => 8.2
            };

            if (_cachedHipparcosStars == null || _lastHipparcosProj != eqProj)
            {
                _cachedHipparcosStars = HipparcosCatalog.QueryRegion(
                    eqProj.CenterRaDeg,
                    midDec,
                    searchRadius,
                    maxMag,
                    maxResults: 3500);
                _lastHipparcosProj = eqProj;
                _lastHipparcosWcs = wcs;
            }
        }
        else if (wcs != null)
        {
            double fovDeg = Math.Max(wcs.FovWidthDegrees, wcs.FovHeightDegrees);
            double searchRadius = Math.Max((fovDeg * 0.75) + 0.5, 6.0 / Math.Max(0.01, _zoom));
            searchRadius = Math.Clamp(searchRadius, 4.0, 85.0);

            double maxMag = fovDeg switch
            {
                > 35.0 => 5.2,
                > 20.0 => 6.2,
                > 10.0 => 7.2,
                > 4.0 => 8.2,
                _ => 9.2
            };

            if (_cachedHipparcosStars == null || _lastHipparcosWcs != wcs || _lastHipparcosProj != null)
            {
                _cachedHipparcosStars = HipparcosCatalog.QueryRegion(
                    wcs.CenterCoordinates.RaDegrees,
                    wcs.CenterCoordinates.DecDegrees,
                    searchRadius,
                    maxMag,
                    maxResults: 600);
                _lastHipparcosWcs = wcs;
                _lastHipparcosProj = null;
            }
        }

        var stars = _cachedHipparcosStars;
        if (stars == null || stars.Count == 0) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        double labelFontSize = Math.Clamp(10.0 * inv, 9.0, 22.0);

        Rect view = _viewport.IsEmpty ? new Rect(0, 0, width, height) : _viewport;
        view.Inflate(60 * inv, 60 * inv);

        foreach (var star in stars)
        {
            if (!TryProjectCelestialTo2DCanvas(star.RaDeg, star.DecDeg, out double px, out double py)) continue;
            if (!view.Contains(px, py)) continue;
            if (eqProj != null && (px < 0 || px > width || py < 0 || py > height)) continue;

            double radius = eqProj != null
                ? Math.Clamp((7.2 - star.Mag) * 0.65, 0.9, 5.2) * inv
                : Math.Max(1.0, (8.5 - star.Mag) * 0.9) * inv;
            var starBrush = GetStarBrush(star.Bv);

            if (star.Mag <= 3.5)
            {
                double haloRadius = radius * 2.8;
                var haloBrush = GetStarHaloBrush(star.Bv);
                dc.DrawEllipse(haloBrush, null, new Point(px, py), haloRadius, haloRadius);
            }

            dc.DrawEllipse(starBrush, null, new Point(px, py), radius, radius);

            bool drawLabel = eqProj != null
                ? ((_zoom > 1.35 && star.Mag <= 3.8) || (_zoom <= 1.35 && star.Mag <= 1.6))
                : (star.Mag <= 2.8 || (_zoom > 2.0 && star.Mag <= 4.2));
            if (drawLabel)
            {
                string text = $"HIP {star.HipId} ({star.Mag:F1})";
                var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, labelFontSize, StarLabelBrush, dpi);

                double lx = px + radius + (3.0 * inv);
                double ly = py - (ft.Height / 2.0);
                var bgRect = new Rect(lx - inv, ly - inv, ft.Width + (2.0 * inv), ft.Height + (2.0 * inv));
                if (TryReserveLabelRect(placedLabels, bgRect, 4.0 * inv, 3.0 * inv))
                {
                    dc.DrawRectangle(StarLabelBackground, null, bgRect);
                    dc.DrawText(ft, new Point(lx, ly));
                }
            }
        }
    }

    private void DrawConstellationLines(DrawingContext dc, double inv, double width, double height, List<Rect>? placedLabels = null)
    {
        var eqProj = MosaicEquirectangularProjection;
        var wcs = Wcs;
        if ((wcs == null && eqProj == null) || width <= 0 || height <= 0) return;

        var pen = new Pen(ConstellationLineBrush, 1.4 * inv)
        {
            DashStyle = new DashStyle(new double[] { 4, 3 }, 0)
        };
        pen.Freeze();

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        double labelFontSize = Math.Clamp(11.5 * inv, 10.0, 26.0);

        var labeledConstellations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Rect view = _viewport.IsEmpty ? new Rect(0, 0, width, height) : _viewport;
        double margin = Math.Max(width, height) * 2.0;
        Rect activeBounds = view;
        activeBounds.Inflate(margin, margin);
        Rect labelBounds = eqProj != null
            ? Rect.Intersect(view, new Rect(0, 0, width, height))
            : activeBounds;

        foreach (var line in ConstellationCatalog.Lines)
        {
            double x1, y1, x2, y2;
            if (eqProj != null)
            {
                if (!eqProj.TryProjectSegment(
                    line.Start.RaDegrees, line.Start.DecDegrees,
                    line.End.RaDegrees, line.End.DecDegrees,
                    out x1, out y1, out x2, out y2))
                {
                    continue;
                }
            }
            else
            {
                if (!TryProjectCelestialTo2DCanvas(line.Start.RaDegrees, line.Start.DecDegrees, out x1, out y1)) continue;
                if (!TryProjectCelestialTo2DCanvas(line.End.RaDegrees, line.End.DecDegrees, out x2, out y2)) continue;
            }

            bool anyNear = activeBounds.Contains(x1, y1) || activeBounds.Contains(x2, y2);
            if (!anyNear) continue;

            dc.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));

            if (!labeledConstellations.Contains(line.ConstellationName))
            {
                double mx = (x1 + x2) * 0.5;
                double my = (y1 + y2) * 0.5;

                if (!labelBounds.IsEmpty && labelBounds.Contains(mx, my))
                {
                    string text = line.DisplayName.ToUpperInvariant();
                    var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        typeface, labelFontSize, ConstellationTextBrush, dpi);

                    var rect = new Rect(mx - (ft.Width * 0.5) - (4.0 * inv),
                                        my - (ft.Height * 0.5) - (2.0 * inv),
                                        ft.Width + (8.0 * inv),
                                        ft.Height + (4.0 * inv));
                    if (TryReserveLabelRect(placedLabels, rect, 6.0 * inv, 4.0 * inv))
                    {
                        labeledConstellations.Add(line.ConstellationName);
                        dc.DrawRoundedRectangle(ConstellationBackground, null, rect, 3.0 * inv, 3.0 * inv);
                        dc.DrawText(ft, new Point(mx - (ft.Width * 0.5), my - (ft.Height * 0.5)));
                    }
                }
            }
        }
    }

    private void DrawDsoMarkers(DrawingContext dc, double inv, double width, double height, List<Rect>? placedLabels = null)
    {
        var eqProj = MosaicEquirectangularProjection;
        var wcs = Wcs;
        if ((wcs == null && eqProj == null) || width <= 0 || height <= 0) return;

        var dsoPen = new Pen(DsoCircleBrush, 1.4 * inv)
        {
            DashStyle = new DashStyle(new double[] { 3, 2 }, 0)
        };
        dsoPen.Freeze();

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        double labelFontSize = eqProj != null && _zoom < 1.35
            ? Math.Clamp(9.5 * inv, 8.5, 19.0)
            : Math.Clamp(11.0 * inv, 10.0, 24.0);
        double degPerPx = eqProj != null
            ? (1.0 / Math.Max(0.1, eqProj.PixelsPerDegreeY))
            : ((wcs!.PixelScaleArcsec) / 3600.0);

        Rect view = _viewport.IsEmpty ? new Rect(0, 0, width, height) : _viewport;
        if (eqProj != null)
        {
            view = Rect.Intersect(view, new Rect(0, 0, width, height));
            if (view.IsEmpty) return;
        }
        else
        {
            view.Inflate(80 * inv, 80 * inv);
        }

        bool overviewMode = eqProj != null && _zoom < 1.35 && eqProj.RaSpanDeg > 20.0;
        var candidates = overviewMode
            ? CataloguePlateSolver.KnownCatalogues.OrderByDescending(t => t.TypicalFovDegrees)
            : (IEnumerable<CataloguePlateSolver.AstronomicalTarget>)CataloguePlateSolver.KnownCatalogues;

        int overviewLabelsDrawn = 0;
        const int maxOverviewLabels = 10;

        foreach (var target in candidates)
        {
            if (overviewMode && overviewLabelsDrawn >= maxOverviewLabels)
            {
                break;
            }

            if (overviewMode && !IsCoordinateNearSolvedMosaic(target.Coordinates.RaDegrees, target.Coordinates.DecDegrees, marginDeg: 3.5))
            {
                continue;
            }

            if (!TryProjectCelestialTo2DCanvas(target.Coordinates.RaDegrees, target.Coordinates.DecDegrees, out double px, out double py)) continue;
            if (!view.Contains(px, py)) continue;

            double angularRadiusDeg = target.TypicalFovDegrees * 0.5;
            double pixelRadius = degPerPx > 1e-8 ? (angularRadiusDeg / degPerPx) : (20.0 * inv);
            pixelRadius = overviewMode
                ? Math.Clamp(pixelRadius, 9.0 * inv, 36.0 * inv)
                : Math.Clamp(pixelRadius, 14.0 * inv, 80.0 * inv);

            string label = target.CommonName;
            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, labelFontSize, DsoTextBrush, dpi);

            double lx = px - (ft.Width * 0.5);
            double ly = py + pixelRadius + (4.0 * inv);

            var bgRect = new Rect(lx - (4.0 * inv), ly - (2.0 * inv), ft.Width + (8.0 * inv), ft.Height + (4.0 * inv));
            bool labelFits = TryReserveLabelRect(placedLabels, bgRect, 6.0 * inv, 4.0 * inv);

            if (overviewMode && !labelFits)
            {
                continue;
            }

            dc.DrawEllipse(DsoFillBrush, dsoPen, new Point(px, py), pixelRadius, pixelRadius);

            if (labelFits)
            {
                overviewLabelsDrawn++;
                dc.DrawRoundedRectangle(DsoLabelBackground, null, bgRect, 3.0 * inv, 3.0 * inv);
                dc.DrawText(ft, new Point(lx, ly));
            }
        }
    }

    /// <summary>
    /// Projects a celestial coordinate (RA, Dec) to 2D canvas coordinates.
    /// Uses the cropped equirectangular sky projection when active, otherwise falls back to panel-local or master TAN WCS.
    /// </summary>
    private bool TryProjectCelestialTo2DCanvas(double raDeg, double decDeg, out double px, out double py)
    {
        px = 0;
        py = 0;
        var eqProj = MosaicEquirectangularProjection;
        if (eqProj != null)
        {
            return eqProj.EquatorialToCanvas(raDeg, decDeg, out px, out py, marginPx: 140.0);
        }

        var wcs = Wcs;
        if (wcs == null) return false;

        var panels = MosaicPanels;
        if (panels != null && panels.Count > 1)
        {
            var coord = new CelestialCoordinates(raDeg, decDeg);
            // Check from top-most rendered panel to bottom-most panel
            for (int pIdx = panels.Count - 1; pIdx >= 0; pIdx--)
            {
                var p = panels[pIdx];
                if (!p.IsVisible || p.Wcs == null || !p.IsSolved) continue;
                if (p.Wcs.EquatorialToPixel(coord, out double lx, out double ly) &&
                    lx >= 0 && lx <= p.Width && ly >= 0 && ly <= p.Height)
                {
                    var tr = MosaicProject.GetPanelCanvasTransform(p, wcs, pIdx);
                    px = (lx * tr.M11) + (ly * tr.M21) + tr.OffsetX;
                    py = (lx * tr.M12) + (ly * tr.M22) + tr.OffsetY;
                    return true;
                }
            }
        }

        return wcs.EquatorialToPixel(raDeg, decDeg, out px, out py);
    }

    private static readonly Dictionary<string, System.Windows.Media.Imaging.BitmapSource> _panelBitmapCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, System.Windows.Media.Imaging.BitmapSource> _featheredBitmapCache = new(StringComparer.OrdinalIgnoreCase);

    public static void CachePanelBitmap(string? path, System.Windows.Media.Imaging.BitmapSource? bmp)
    {
        if (!string.IsNullOrEmpty(path) && bmp != null)
        {
            _panelBitmapCache[path] = bmp;
            PurgeFeatheredCacheForPath(path);
        }
    }

    public static void InvalidatePanelCache(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            _panelBitmapCache.Remove(path);
            PurgeFeatheredCacheForPath(path);
        }
    }

    private static void PurgeFeatheredCacheForPath(string path)
    {
        string prefix = path + "_";
        var keysToRemove = new List<string>();
        foreach (string k in _featheredBitmapCache.Keys)
        {
            if (k.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                keysToRemove.Add(k);
            }
        }
        foreach (string k in keysToRemove)
        {
            _featheredBitmapCache.Remove(k);
        }
    }

    /// <summary>
    /// Hit-tests which mosaic panel (from top-most to bottom-most layer) contains the given 2D canvas coordinate.
    /// </summary>
    public MosaicPanel? HitTestPanelAtCanvasPoint(Point canvasPt)
    {
        var panels = MosaicPanels;
        var eqProj = MosaicEquirectangularProjection;
        if (panels == null || panels.Count == 0) return null;

        if (eqProj != null)
        {
            eqProj.CanvasToEquatorial(canvasPt.X, canvasPt.Y, out double raDeg, out double decDeg);
            var sky = new CelestialCoordinates(raDeg, decDeg);
            double decRad = sky.DecRadians;
            double raRad = sky.RaRadians;
            double sx = Math.Cos(decRad) * Math.Cos(raRad);
            double sy = Math.Cos(decRad) * Math.Sin(raRad);
            double sz = Math.Sin(decRad);

            for (int pIndex = panels.Count - 1; pIndex >= 0; pIndex--)
            {
                var p = panels[pIndex];
                if (!p.IsVisible || !p.IsSolved || p.Wcs == null || p.Width <= 0 || p.Height <= 0) continue;

                if (p.Wcs.EquatorialToPixel(sky, out double lx, out double ly) &&
                    lx >= 0 && lx <= p.Width && ly >= 0 && ly <= p.Height)
                {
                    var rt = p.Wcs.PixelToEquatorial(lx, ly);
                    double dot = (sx * Math.Cos(rt.DecRadians) * Math.Cos(rt.RaRadians))
                               + (sy * Math.Cos(rt.DecRadians) * Math.Sin(rt.RaRadians))
                               + (sz * Math.Sin(rt.DecRadians));
                    if (dot >= 0.96)
                    {
                        return p;
                    }
                }
            }
            return null;
        }

        var wcs = Wcs;
        if (panels.Count == 1)
        {
            var p0 = panels[0];
            if (p0.IsVisible && canvasPt.X >= 0 && canvasPt.X <= p0.Width && canvasPt.Y >= 0 && canvasPt.Y <= p0.Height)
            {
                return p0;
            }
            return null;
        }

        for (int pIndex = panels.Count - 1; pIndex >= 0; pIndex--)
        {
            var p = panels[pIndex];
            if (!p.IsVisible || p.Width <= 0 || p.Height <= 0) continue;

            var tr = MosaicProject.GetPanelCanvasTransform(p, wcs, pIndex);
            var matrix = new Matrix(tr.M11, tr.M12, tr.M21, tr.M22, tr.OffsetX, tr.OffsetY);
            if (!matrix.HasInverse) continue;
            matrix.Invert();

            Point localPt = matrix.Transform(canvasPt);
            if (localPt.X >= 0 && localPt.X <= p.Width && localPt.Y >= 0 && localPt.Y <= p.Height)
            {
                return p;
            }
        }

        return null;
    }

    public static System.Windows.Media.Imaging.BitmapSource? GetPanelBitmap(MosaicPanel? panel)
    {
        if (panel == null) return null;
        if (panel.CachedBitmap is System.Windows.Media.Imaging.BitmapSource bs) return bs;
        if (!string.IsNullOrEmpty(panel.FilePath))
        {
            var loaded = GetOrLoadPanelBitmap(panel.FilePath);
            if (loaded != null)
            {
                if (panel.CachedBitmap is SkiaSharp.SKBitmap sk)
                {
                    panel.CachedSkiaBitmap ??= sk;
                }
                panel.CachedBitmap = loaded;
                return loaded;
            }
        }
        return null;
    }

    public static System.Windows.Media.Imaging.BitmapSource? GetOrLoadPanelBitmap(string? path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            return null;

        if (_panelBitmapCache.TryGetValue(path, out var cached))
            return cached;

        try
        {
            string fullPath = System.IO.Path.GetFullPath(path);
            var uri = new Uri(fullPath, UriKind.Absolute);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 2048;
            bmp.EndInit();
            bmp.Freeze();
            _panelBitmapCache[path] = bmp;
            return bmp;
        }
        catch
        {
            try
            {
                using var skBitmap = AstroCounter.Core.Processing.ImagePreprocessor.LoadImage(path);
                using var converted = skBitmap.ColorType == SkiaSharp.SKColorType.Bgra8888
                    ? skBitmap.Copy()
                    : skBitmap.Copy(SkiaSharp.SKColorType.Bgra8888);

                var wb = new System.Windows.Media.Imaging.WriteableBitmap(
                    converted.Width,
                    converted.Height,
                    96, 96,
                    System.Windows.Media.PixelFormats.Bgra32,
                    null);

                wb.WritePixels(
                    new Int32Rect(0, 0, converted.Width, converted.Height),
                    converted.GetPixels(),
                    converted.ByteCount,
                    converted.RowBytes);

                wb.Freeze();
                _panelBitmapCache[path] = wb;
                return wb;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Returns a panel bitmap with soft feathered alpha borders to eliminate hard seams and Z-buffer artifacts
    /// when blending multiple mosaic panels together on the celestial sphere or 2D mosaic preview.
    /// </summary>
    public static System.Windows.Media.Imaging.BitmapSource? GetOrLoadFeatheredPanelBitmap(
        string? path,
        System.Windows.Media.Imaging.BitmapSource? fallback = null,
        double featherFraction = 0.12,
        EdgeOverlap edges = EdgeOverlap.All,
        bool[]? perimeterMask = null,
        long maskHash = 0,
        float[]? perimeterDepth = null)
    {
        var baseBmp = fallback ?? GetOrLoadPanelBitmap(path);
        if (baseBmp == null) return null;

        if (perimeterMask != null)
        {
            bool anyTrue = false;
            for (int i = 0; i < perimeterMask.Length; i++)
            {
                if (perimeterMask[i]) { anyTrue = true; break; }
            }
            if (!anyTrue) return baseBmp; // 100% solid outer borders
        }
        else if (edges == EdgeOverlap.None)
        {
            return baseBmp;
        }

        double fraction = Math.Clamp(featherFraction, 0.02, 0.35);
        long effectiveHash = maskHash != 0 ? maskHash : (long)edges;
        string key = !string.IsNullOrEmpty(path)
            ? $"{path}_{baseBmp.GetHashCode()}_{fraction:F3}_{effectiveHash}"
            : $"{baseBmp.GetHashCode()}_{fraction:F3}_{effectiveHash}";

        if (_featheredBitmapCache.TryGetValue(key, out var cached))
            return cached;

        var feathered = CreateFeatheredBitmap(baseBmp, fraction, edges, perimeterMask, perimeterDepth);
        if (_featheredBitmapCache.Count > 64)
        {
            _featheredBitmapCache.Clear();
        }
        _featheredBitmapCache[key] = feathered;
        return feathered;
    }

    public static System.Windows.Media.Imaging.BitmapSource CreateFeatheredBitmap(
        System.Windows.Media.Imaging.BitmapSource src,
        double featherFraction = 0.12,
        EdgeOverlap edges = EdgeOverlap.All,
        bool[]? perimeterMask = null,
        float[]? perimeterDepth = null)
    {
        if (src == null) return null!;

        bool hasMask = perimeterMask != null && perimeterMask.Length >= 256;
        if (hasMask)
        {
            bool anyTrue = false;
            for (int i = 0; i < 256; i++)
            {
                if (perimeterMask![i]) { anyTrue = true; break; }
            }
            if (!anyTrue) return src; // 100% solid outer borders
        }
        else if (edges == EdgeOverlap.None)
        {
            return src;
        }

        int width = src.PixelWidth;
        int height = src.PixelHeight;
        if (width <= 0 || height <= 0) return src;

        // Downsample oversized panels before feathering: 2048px is ideal for seamless display and avoids 100MB+ LOH footprint
        System.Windows.Media.Imaging.BitmapSource effectiveSrc = src;
        if (width > 2048 || height > 2048)
        {
            double scale = 2048.0 / Math.Max(width, height);
            var scaled = new System.Windows.Media.Imaging.TransformedBitmap(src, new ScaleTransform(scale, scale));
            scaled.Freeze();
            effectiveSrc = scaled;
            width = effectiveSrc.PixelWidth;
            height = effectiveSrc.PixelHeight;
            if (width <= 0 || height <= 0) return src;
        }

        var pbgraBmp = new System.Windows.Media.Imaging.FormatConvertedBitmap(effectiveSrc, PixelFormats.Pbgra32, null, 0);

        int stride = width * 4;
        int byteCount = stride * height;
        byte[] pixels = ArrayPool<byte>.Shared.Rent(byteCount);

        double fraction = Math.Clamp(featherFraction, 0.02, 0.35);
        int marginX = Math.Max(12, (int)(width * fraction));
        int marginY = Math.Max(12, (int)(height * fraction));

        float[] fadeX = ArrayPool<float>.Shared.Rent(marginX);
        float[] fadeY = ArrayPool<float>.Shared.Rent(marginY);

        float[]? overlapWeights = null;
        float[]? overlapDepths = null;
        if (hasMask)
        {
            overlapWeights = ArrayPool<float>.Shared.Rent(256);
            overlapDepths = ArrayPool<float>.Shared.Rent(256);
            bool hasDepth = perimeterDepth != null && perimeterDepth.Length >= 256;

            for (int k = 0; k < 256; k++)
            {
                int kM2 = (k + 254) % 256;
                int kM1 = (k + 255) % 256;
                int kP1 = (k + 1) % 256;
                int kP2 = (k + 2) % 256;

                overlapWeights[k] = (perimeterMask![kM2] ? 0.10f : 0f) +
                                    (perimeterMask[kM1] ? 0.20f : 0f) +
                                    (perimeterMask[k] ? 0.40f : 0f) +
                                    (perimeterMask[kP1] ? 0.20f : 0f) +
                                    (perimeterMask[kP2] ? 0.10f : 0f);

                if (hasDepth)
                {
                    float dMax = 0f;
                    int[] neighbors = { kM1, k, kP1 };
                    foreach (int nk in neighbors)
                    {
                        if (perimeterMask[nk] && perimeterDepth![nk] > 0f)
                        {
                            float clamped = Math.Clamp(perimeterDepth[nk] * 0.85f, 0.02f, (float)fraction);
                            if (dMax == 0f || clamped < dMax) dMax = clamped;
                        }
                    }
                    overlapDepths[k] = dMax > 0f ? dMax : (float)fraction;
                }
                else
                {
                    overlapDepths[k] = (float)fraction;
                }
            }
        }

        try
        {
            pbgraBmp.CopyPixels(pixels, stride, 0);

            for (int i = 0; i < marginX; i++)
            {
                fadeX[i] = (float)(0.5 - 0.5 * Math.Cos(Math.PI * i / marginX));
            }

            for (int j = 0; j < marginY; j++)
            {
                fadeY[j] = (float)(0.5 - 0.5 * Math.Cos(Math.PI * j / marginY));
            }

            static float SampleEdgeFactor(int distPx, int edgeStartIdx, float edgeCoordNorm, int maxDimPx, float[] weights, float[] depths)
            {
                float pos = Math.Clamp(edgeCoordNorm * 63.0f, 0f, 63.0f);
                int k0 = (int)pos;
                int k1 = Math.Min(63, k0 + 1);
                float t = pos - k0;

                float w = weights[edgeStartIdx + k0] * (1f - t) + weights[edgeStartIdx + k1] * t;
                if (w <= 0.001f) return 1.0f;

                float d = depths[edgeStartIdx + k0] * (1f - t) + depths[edgeStartIdx + k1] * t;
                float effMargin = Math.Max(8.0f, maxDimPx * d);
                if (distPx >= effMargin) return 1.0f;

                float norm = distPx / effMargin;
                float alphaFeather = (float)(0.5 - 0.5 * Math.Cos(Math.PI * norm));
                return 1.0f - (w * (1.0f - alphaFeather));
            }

            float invWidth = 1.0f / Math.Max(1, width - 1);
            float invHeight = 1.0f / Math.Max(1, height - 1);

            for (int y = 0; y < height; y++)
            {
                int distTop = y;
                int distBottom = height - 1 - y;
                int rowOffset = y * stride;
                float vTop = y * invHeight;
                float vBot = (height - 1 - y) * invHeight;

                for (int x = 0; x < width; x++)
                {
                    int distLeft = x;
                    int distRight = width - 1 - x;

                    if (distTop >= marginY && distBottom >= marginY && distLeft >= marginX && distRight >= marginX)
                    {
                        continue; // Center interior core: 100% opaque
                    }

                    float factor;

                    if (hasMask && overlapWeights != null && overlapDepths != null)
                    {
                        // Smooth C1-continuous separable 256-point perimeter blending (zero diagonal corner cuts or banding)
                        float fTop = distTop < marginY
                            ? SampleEdgeFactor(distTop, 0, x * invWidth, height, overlapWeights, overlapDepths)
                            : 1.0f;
                        float fRight = distRight < marginX
                            ? SampleEdgeFactor(distRight, 64, vTop, width, overlapWeights, overlapDepths)
                            : 1.0f;
                        float fBottom = distBottom < marginY
                            ? SampleEdgeFactor(distBottom, 128, (width - 1 - x) * invWidth, height, overlapWeights, overlapDepths)
                            : 1.0f;
                        float fLeft = distLeft < marginX
                            ? SampleEdgeFactor(distLeft, 192, vBot, width, overlapWeights, overlapDepths)
                            : 1.0f;

                        factor = fTop * fRight * fBottom * fLeft;
                    }
                    else
                    {
                        // Fallback 4-edge rectilinear feathering
                        float alphaY = 1.0f;
                        if (edges.HasFlag(EdgeOverlap.Top) && distTop < marginY)
                            alphaY = fadeY[distTop];
                        else if (edges.HasFlag(EdgeOverlap.Bottom) && distBottom < marginY)
                            alphaY = fadeY[distBottom];

                        float alphaX = 1.0f;
                        if (edges.HasFlag(EdgeOverlap.Left) && distLeft < marginX)
                            alphaX = fadeX[distLeft];
                        else if (edges.HasFlag(EdgeOverlap.Right) && distRight < marginX)
                            alphaX = fadeX[distRight];

                        factor = alphaX * alphaY;
                    }

                    if (factor < 0.999f)
                    {
                        int idx = rowOffset + (x * 4);
                        pixels[idx + 0] = (byte)Math.Clamp((int)(pixels[idx + 0] * factor), 0, 255);
                        pixels[idx + 1] = (byte)Math.Clamp((int)(pixels[idx + 1] * factor), 0, 255);
                        pixels[idx + 2] = (byte)Math.Clamp((int)(pixels[idx + 2] * factor), 0, 255);
                        pixels[idx + 3] = (byte)Math.Clamp((int)(pixels[idx + 3] * factor), 0, 255);
                    }
                }
            }

            var writeable = new System.Windows.Media.Imaging.WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
            writeable.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
            writeable.Freeze();
            return writeable;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
            ArrayPool<float>.Shared.Return(fadeX);
            ArrayPool<float>.Shared.Return(fadeY);
            if (overlapWeights != null)
            {
                ArrayPool<float>.Shared.Return(overlapWeights);
            }
            if (overlapDepths != null)
            {
                ArrayPool<float>.Shared.Return(overlapDepths);
            }
        }
    }

    private void DrawMosaicPanels(DrawingContext dc, double inv, double width, double height)
    {
        var eqProj = MosaicEquirectangularProjection;
        var compositeBmp = MosaicCompositeBitmap;
        var panels = MosaicPanels;

        var selectedBorderPen = new Pen(MakeBrush(56, 189, 248, 250), 2.8 * inv);
        selectedBorderPen.Freeze();
        var unsolvedBorderPen = new Pen(MakeBrush(251, 191, 36, 200), 1.8 * inv)
        {
            DashStyle = new DashStyle(new double[] { 5, 3 }, 0)
        };
        unsolvedBorderPen.Freeze();

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        double labelFontSize = Math.Clamp(11.0 * inv, 10.0, 24.0);

        if (eqProj != null)
        {
            if (compositeBmp != null)
            {
                dc.PushOpacity(Math.Clamp(CompositeOpacity, 0.05, 1.0));
                dc.DrawImage(compositeBmp, new Rect(0, 0, width, height));
                dc.Pop();
            }

            if (panels != null && panels.Count > 0)
            {
                bool trLang = CataloguePlateSolver.UseTurkishLanguage;
                int unsolvedRibbonIndex = 0;

                for (int pIndex = 0; pIndex < panels.Count; pIndex++)
                {
                    var p = panels[pIndex];
                    if (!p.IsVisible || p.Width <= 0 || p.Height <= 0) continue;

                    if (p.IsSolved && p.Wcs != null && p.IsSelected)
                    {
                        DrawEquirectangularPanelOutline(dc, eqProj, p, selectedBorderPen, width, height, inv, labelFontSize, typeface, dpi, trLang);
                    }
                    else if (!p.IsSolved && panels.Count > 1)
                    {
                        // Surface unsolved warning neatly in bottom corner without obstructing sky dome
                        string badge = trLang
                            ? $"⚠️ [{p.Name}] Hizalanamadı — Katalog/Yıldız Eşleşmesi Yok"
                            : $"⚠️ [{p.Name}] Unsolved — No Catalog/Star Match";
                        var ft = new FormattedText(badge, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                            typeface, labelFontSize, MakeBrush(251, 191, 36, 255), dpi);
                        double bx = 14.0 * inv;
                        double by = height - ((unsolvedRibbonIndex + 1) * (ft.Height + (12.0 * inv))) - (10.0 * inv);
                        var bgRect = new Rect(bx - (4.0 * inv), by - (2.0 * inv), ft.Width + (8.0 * inv), ft.Height + (4.0 * inv));
                        dc.DrawRoundedRectangle(LabelBackground, unsolvedBorderPen, bgRect, 4.0 * inv, 4.0 * inv);
                        dc.DrawText(ft, new Point(bx, by));
                        unsolvedRibbonIndex++;
                    }
                }
            }
            return;
        }

        var wcs = Wcs;
        if (panels == null || panels.Count == 0) return;

        Rect view = _viewport.IsEmpty ? new Rect(0, 0, width, height) : _viewport;
        view.Inflate(100 * inv, 100 * inv);

        for (int pIndex = 0; pIndex < panels.Count; pIndex++)
        {
            var p = panels[pIndex];
            if (!p.IsVisible || p.Width <= 0 || p.Height <= 0) continue;

            // Single-photo mode already renders the image via AstroImage, so only draw outline if selected
            bool isSingleMode = panels.Count == 1;

            var tr = MosaicProject.GetPanelCanvasTransform(p, wcs, pIndex);
            Matrix matrix = new Matrix(tr.M11, tr.M12, tr.M21, tr.M22, tr.OffsetX, tr.OffsetY);

            // Compute exact 4 corners from affine mapping so outline and image align 1:1
            var pt1 = matrix.Transform(new Point(0, 0));
            var pt2 = matrix.Transform(new Point(p.Width, 0));
            var pt3 = matrix.Transform(new Point(p.Width, p.Height));
            var pt4 = matrix.Transform(new Point(0, p.Height));

            // Viewport Frustum Culling: Calculate panel AABB
            double minX = Math.Min(Math.Min(pt1.X, pt2.X), Math.Min(pt3.X, pt4.X));
            double maxX = Math.Max(Math.Max(pt1.X, pt2.X), Math.Max(pt3.X, pt4.X));
            double minY = Math.Min(Math.Min(pt1.Y, pt2.Y), Math.Min(pt3.Y, pt4.Y));
            double maxY = Math.Max(Math.Max(pt1.Y, pt2.Y), Math.Max(pt3.Y, pt4.Y));
            var panelAabb = new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));

            bool isSelectedPanel = p.IsSelected;
            bool isVisibleInView = view.IntersectsWith(panelAabb);

            // Skip rendering panel image if outside viewport (unless selected)
            if (!isSingleMode && (isVisibleInView || isSelectedPanel))
            {
                var panelBmp = GetPanelBitmap(p);
                double featherFrac = p.FeatherFraction > 0 ? p.FeatherFraction : 0.12;
                var bmp = UseFeatheredBlending
                    ? (GetOrLoadFeatheredPanelBitmap(p.FilePath, panelBmp, featherFrac, p.OverlappingEdges, p.PerimeterOverlapMask, p.OverlapMaskHash, p.PerimeterOverlapDepth) ?? panelBmp)
                    : panelBmp;
                if (bmp != null)
                {
                    dc.PushTransform(new MatrixTransform(matrix));
                    dc.PushOpacity(Math.Clamp(p.Opacity, 0.05, 1.0));
                    dc.DrawImage(bmp, new Rect(0, 0, p.Width, p.Height));
                    dc.Pop(); // opacity
                    dc.Pop(); // transform
                }
            }

            // Only draw outline border when the photo is explicitly selected (or unsolved warning in mosaic)
            if ((isSelectedPanel || (!isSingleMode && !p.IsSolved)) && (isVisibleInView || isSelectedPanel))
            {
                Pen panelPen = isSelectedPanel ? selectedBorderPen : unsolvedBorderPen;
                var streamGeom = new StreamGeometry();
                using (var ctx = streamGeom.Open())
                {
                    ctx.BeginFigure(pt1, isFilled: false, isClosed: true);
                    ctx.LineTo(pt2, isStroked: true, isSmoothJoin: false);
                    ctx.LineTo(pt3, isStroked: true, isSmoothJoin: false);
                    ctx.LineTo(pt4, isStroked: true, isSmoothJoin: false);
                }
                streamGeom.Freeze();
                dc.DrawGeometry(null, panelPen, streamGeom);
            }

            // Show badge when selected or when unsolved in multi-panel mode
            if ((isSelectedPanel || (!isSingleMode && !p.IsSolved)) && (isVisibleInView || isSelectedPanel))
            {
                bool trLang = CataloguePlateSolver.UseTurkishLanguage;
                string badge = p.IsSolved
                    ? $"🎯 [{p.Name}] ({p.Width}x{p.Height})"
                    : (trLang
                        ? $"⚠️ [{p.Name}] Hizalanamadı — Katalog/Yıldız Eşleşmesi Yok"
                        : $"⚠️ [{p.Name}] Unsolved — No Catalog/Star Match");
                var badgeBrush = p.IsSolved ? MakeBrush(56, 189, 248, 255) : MakeBrush(251, 191, 36, 255);
                var ft = new FormattedText(badge, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, labelFontSize, badgeBrush, dpi);

                var bgRect = new Rect(pt1.X + (4.0 * inv), pt1.Y + (4.0 * inv), ft.Width + (8.0 * inv), ft.Height + (4.0 * inv));
                dc.DrawRoundedRectangle(LabelBackground, null, bgRect, 3.0 * inv, 3.0 * inv);
                dc.DrawText(ft, new Point(pt1.X + (8.0 * inv), pt1.Y + (6.0 * inv)));
            }
        }
    }

    private void DrawEquirectangularPanelOutline(
        DrawingContext dc,
        EquirectangularProjection proj,
        MosaicPanel panel,
        Pen borderPen,
        double width,
        double height,
        double inv,
        double labelFontSize,
        Typeface typeface,
        double dpi,
        bool trLang)
    {
        if (panel.Wcs == null) return;
        const int edgeSteps = 14;
        var perimeter = new List<Point>(edgeSteps * 4 + 1);

        void AddSample(double sx, double sy)
        {
            var eq = panel.Wcs.PixelToEquatorial(sx, sy);
            proj.EquatorialToCanvas(eq.RaDegrees, eq.DecDegrees, out double cx, out double cy);
            perimeter.Add(new Point(cx, cy));
        }

        double maxW = Math.Max(1.0, panel.Width - 1.0);
        double maxH = Math.Max(1.0, panel.Height - 1.0);
        for (int i = 0; i < edgeSteps; i++) AddSample((i / (double)edgeSteps) * maxW, 0);
        for (int i = 0; i < edgeSteps; i++) AddSample(maxW, (i / (double)edgeSteps) * maxH);
        for (int i = 0; i < edgeSteps; i++) AddSample((1.0 - (i / (double)edgeSteps)) * maxW, maxH);
        for (int i = 0; i < edgeSteps; i++) AddSample(0, (1.0 - (i / (double)edgeSteps)) * maxH);
        if (perimeter.Count > 0) perimeter.Add(perimeter[0]);

        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            bool openFigure = false;
            Point prev = default;
            double wrapLimit = width * 0.45;
            foreach (var pt in perimeter)
            {
                if (!openFigure || Math.Abs(pt.X - prev.X) > wrapLimit)
                {
                    ctx.BeginFigure(pt, isFilled: false, isClosed: false);
                    openFigure = true;
                }
                else
                {
                    ctx.LineTo(pt, isStroked: true, isSmoothJoin: true);
                }
                prev = pt;
            }
        }
        geom.Freeze();
        dc.DrawGeometry(null, borderPen, geom);

        var topLeftEq = panel.Wcs.PixelToEquatorial(0, 0);
        proj.EquatorialToCanvas(topLeftEq.RaDegrees, topLeftEq.DecDegrees, out double bx, out double by);
        bx = Math.Clamp(bx + (4.0 * inv), 8.0 * inv, Math.Max(8.0 * inv, width - (220.0 * inv)));
        by = Math.Clamp(by + (4.0 * inv), 8.0 * inv, Math.Max(8.0 * inv, height - (32.0 * inv)));

        string badge = $"🎯 [{panel.Name}] ({panel.Width}x{panel.Height})";
        var ft = new FormattedText(badge, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, labelFontSize, MakeBrush(56, 189, 248, 255), dpi);
        var bgRect = new Rect(bx, by, ft.Width + (8.0 * inv), ft.Height + (4.0 * inv));
        dc.DrawRoundedRectangle(LabelBackground, null, bgRect, 3.0 * inv, 3.0 * inv);
        dc.DrawText(ft, new Point(bx + (4.0 * inv), by + (2.0 * inv)));
    }

    private static Brush GetStarBrush(float bv) => bv switch
    {
        < 0.0f => StarBlueBrush,
        < 0.35f => StarWhiteBrush,
        < 0.80f => StarYellowBrush,
        < 1.30f => StarOrangeBrush,
        _ => StarRedBrush
    };

    private static Brush GetStarHaloBrush(float bv) => bv switch
    {
        < 0.0f => StarBlueHaloBrush,
        < 0.35f => StarWhiteHaloBrush,
        < 0.80f => StarYellowHaloBrush,
        < 1.30f => StarOrangeHaloBrush,
        _ => StarRedHaloBrush
    };

    private static SolidColorBrush MakeBrush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

}

