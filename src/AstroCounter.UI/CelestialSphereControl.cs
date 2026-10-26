namespace AstroCounter.UI;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using AstroCounter.Core.Astrometry;
using AstroCounter.Core.Models;

/// <summary>
/// Interactive 360° celestial sphere planetarium viewport (Stellarium-like dome mode).
/// Allows users to navigate inside the celestial dome with mouse pan and zoom,
/// projecting astrophotography photos, 117k Hipparcos stars, constellations, and DSO markers
/// directly onto the celestial sphere.
/// </summary>
public class CelestialSphereControl : Grid
{
    public static readonly DependencyProperty CurrentWcsProperty = DependencyProperty.Register(
        nameof(CurrentWcs), typeof(WcsMatrix), typeof(CelestialSphereControl),
        new PropertyMetadata(null, OnWcsOrPanelsChanged));

    public static readonly DependencyProperty MosaicPanelsProperty = DependencyProperty.Register(
        nameof(MosaicPanels), typeof(IReadOnlyList<MosaicPanel>), typeof(CelestialSphereControl),
        new PropertyMetadata(null, OnWcsOrPanelsChanged));

    public static readonly DependencyProperty UseFeatheredBlendingProperty = DependencyProperty.Register(
        nameof(UseFeatheredBlending), typeof(bool), typeof(CelestialSphereControl),
        new PropertyMetadata(true, OnWcsOrPanelsChanged));

    public bool UseFeatheredBlending
    {
        get => (bool)GetValue(UseFeatheredBlendingProperty);
        set => SetValue(UseFeatheredBlendingProperty, value);
    }


    public static readonly DependencyProperty ShowHipparcosStarsProperty = DependencyProperty.Register(
        nameof(ShowHipparcosStars), typeof(bool), typeof(CelestialSphereControl),
        new PropertyMetadata(true, OnOverlaySettingChanged));

    public static readonly DependencyProperty ShowConstellationsProperty = DependencyProperty.Register(
        nameof(ShowConstellations), typeof(bool), typeof(CelestialSphereControl),
        new PropertyMetadata(true, OnOverlaySettingChanged));

    public static readonly DependencyProperty ShowBoundingBoxesProperty = DependencyProperty.Register(
        nameof(ShowBoundingBoxes), typeof(bool), typeof(CelestialSphereControl),
        new PropertyMetadata(false, OnOverlaySettingChanged));

    public static readonly DependencyProperty ShowLabelsProperty = DependencyProperty.Register(
        nameof(ShowLabels), typeof(bool), typeof(CelestialSphereControl),
        new PropertyMetadata(false, OnOverlaySettingChanged));

    public static readonly DependencyProperty ShowDsoMarkersProperty = DependencyProperty.Register(
        nameof(ShowDsoMarkers), typeof(bool), typeof(CelestialSphereControl),
        new PropertyMetadata(true, OnOverlaySettingChanged));

    public static readonly DependencyProperty VisibleObjectsProperty = DependencyProperty.Register(
        nameof(VisibleObjects), typeof(IReadOnlyList<AstroCounter.Core.Models.AstrophotographyObject>), typeof(CelestialSphereControl),
        new PropertyMetadata(null, OnOverlaySettingChanged));

    public static readonly DependencyProperty RegionsProperty = DependencyProperty.Register(
        nameof(Regions), typeof(IReadOnlyList<AstroCounter.Core.Models.SelectionRegion>), typeof(CelestialSphereControl),
        new PropertyMetadata(null, OnOverlaySettingChanged));

    public static readonly DependencyProperty TotalCountProperty = DependencyProperty.Register(
        nameof(TotalCount), typeof(int), typeof(CelestialSphereControl),
        new PropertyMetadata(0, OnDetectionsChanged));

    public static readonly DependencyProperty Tier1CountProperty = DependencyProperty.Register(
        nameof(Tier1Count), typeof(int), typeof(CelestialSphereControl),
        new PropertyMetadata(0, OnDetectionsChanged));

    public static readonly DependencyProperty Tier2CountProperty = DependencyProperty.Register(
        nameof(Tier2Count), typeof(int), typeof(CelestialSphereControl),
        new PropertyMetadata(0, OnDetectionsChanged));

    public static readonly DependencyProperty Tier3CountProperty = DependencyProperty.Register(
        nameof(Tier3Count), typeof(int), typeof(CelestialSphereControl),
        new PropertyMetadata(0, OnDetectionsChanged));

    private static void OnDetectionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CelestialSphereControl csc)
        {
            csc.UpdateDetectionsHud();
        }
    }

    public static readonly DependencyProperty ShowSphericalGridProperty = DependencyProperty.Register(
        nameof(ShowSphericalGrid), typeof(bool), typeof(CelestialSphereControl),
        new PropertyMetadata(true, OnOverlaySettingChanged));

    public static readonly DependencyProperty PhotoOpacityProperty = DependencyProperty.Register(
        nameof(PhotoOpacity), typeof(double), typeof(CelestialSphereControl),
        new PropertyMetadata(1.0, OnOpacityChanged));

    public static readonly DependencyProperty ActiveFilePathProperty = DependencyProperty.Register(
        nameof(ActiveFilePath), typeof(string), typeof(CelestialSphereControl),
        new PropertyMetadata(null, OnWcsOrPanelsChanged));

    public static readonly DependencyProperty ActiveImageBitmapProperty = DependencyProperty.Register(
        nameof(ActiveImageBitmap), typeof(BitmapSource), typeof(CelestialSphereControl),
        new PropertyMetadata(null, (d, e) => (d as CelestialSphereControl)?.RebuildPhotoMeshes()));

    public static readonly DependencyProperty ExitCommandProperty = DependencyProperty.Register(
        nameof(ExitCommand), typeof(ICommand), typeof(CelestialSphereControl),
        new PropertyMetadata(null));

    public WcsMatrix? CurrentWcs
    {
        get => (WcsMatrix?)GetValue(CurrentWcsProperty);
        set => SetValue(CurrentWcsProperty, value);
    }

    public string? ActiveFilePath
    {
        get => (string?)GetValue(ActiveFilePathProperty);
        set => SetValue(ActiveFilePathProperty, value);
    }

    public BitmapSource? ActiveImageBitmap
    {
        get => (BitmapSource?)GetValue(ActiveImageBitmapProperty);
        set => SetValue(ActiveImageBitmapProperty, value);
    }

    public IReadOnlyList<MosaicPanel>? MosaicPanels
    {
        get => (IReadOnlyList<MosaicPanel>?)GetValue(MosaicPanelsProperty);
        set => SetValue(MosaicPanelsProperty, value);
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

    public bool ShowBoundingBoxes
    {
        get => (bool)GetValue(ShowBoundingBoxesProperty);
        set => SetValue(ShowBoundingBoxesProperty, value);
    }

    public bool ShowLabels
    {
        get => (bool)GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    public bool ShowDsoMarkers
    {
        get => (bool)GetValue(ShowDsoMarkersProperty);
        set => SetValue(ShowDsoMarkersProperty, value);
    }

    public IReadOnlyList<AstroCounter.Core.Models.AstrophotographyObject>? VisibleObjects
    {
        get => (IReadOnlyList<AstroCounter.Core.Models.AstrophotographyObject>?)GetValue(VisibleObjectsProperty);
        set => SetValue(VisibleObjectsProperty, value);
    }

    public IReadOnlyList<AstroCounter.Core.Models.SelectionRegion>? Regions
    {
        get => (IReadOnlyList<AstroCounter.Core.Models.SelectionRegion>?)GetValue(RegionsProperty);
        set => SetValue(RegionsProperty, value);
    }

    public int TotalCount
    {
        get => (int)GetValue(TotalCountProperty);
        set => SetValue(TotalCountProperty, value);
    }

    public int Tier1Count
    {
        get => (int)GetValue(Tier1CountProperty);
        set => SetValue(Tier1CountProperty, value);
    }

    public int Tier2Count
    {
        get => (int)GetValue(Tier2CountProperty);
        set => SetValue(Tier2CountProperty, value);
    }

    public int Tier3Count
    {
        get => (int)GetValue(Tier3CountProperty);
        set => SetValue(Tier3CountProperty, value);
    }

    public bool ShowSphericalGrid
    {
        get => (bool)GetValue(ShowSphericalGridProperty);
        set => SetValue(ShowSphericalGridProperty, value);
    }

    public double PhotoOpacity
    {
        get => (double)GetValue(PhotoOpacityProperty);
        set => SetValue(PhotoOpacityProperty, value);
    }

    public ICommand? ExitCommand
    {
        get => (ICommand?)GetValue(ExitCommandProperty);
        set => SetValue(ExitCommandProperty, value);
    }

    // 3D Viewport components
    private readonly Viewport3D _viewport;
    private readonly PerspectiveCamera _camera;
    private readonly ModelVisual3D _sceneVisual;
    private readonly Model3DGroup _sceneGroup;
    private readonly Model3DGroup _panelsGroup;

    // 2D Overlay element
    private readonly SphereOverlayCanvas _overlayCanvas;

    // Camera celestial orientation
    public double CameraRa { get; private set; } = 310.0;
    public double CameraDec { get; private set; } = 45.0;
    public double CameraFov { get; private set; } = 50.0;
    public bool IsGlobeView { get; set; } = false;
    public double GlobeDistance { get; set; } = 220.0;

    /// <summary>
    /// Raised when the user pans or zooms the 3D sphere interactively, passing (RaDegrees, DecDegrees, FovDegrees).
    /// </summary>
    public event Action<double, double, double>? CameraMoved;

    /// <summary>
    /// Raised when the user double-clicks a photo projected onto the 3D celestial sphere (or null when double-clicking empty space).
    /// </summary>
    public event Action<MosaicPanel?>? PanelDoubleClicked;

    // Viewport hardware 3D-to-2D projection transform cache
    private GeneralTransform3DTo2D? _cachedTransform3D;

    internal void UpdateProjectionTransform()
    {
        try
        {
            if (_sceneVisual != null && _viewport != null && _viewport.ActualWidth > 0 && _viewport.ActualHeight > 0)
            {
                _cachedTransform3D = _sceneVisual.TransformToAncestor(_viewport) as GeneralTransform3DTo2D;
            }
        }
        catch
        {
            _cachedTransform3D = null;
        }
    }

    // Interaction state
    private Point _lastMousePos;
    private bool _isDragging;

    // 3D sky dome model reference
    private GeometryModel3D? _skyDomeModel;

    // HUD TextBlocks & Buttons
    private readonly Border _topCard;
    private readonly TextBlock _hudTitle;
    private readonly TextBlock _hudCoordinates;
    private readonly TextBlock _hudPanelsInfo;
    private readonly TextBlock _hudNavHints;
    private readonly TextBlock _hudDetections;
    private readonly TextBlock _hudObjectsInFrame;
    private readonly Button _centerPhotoBtn;

    private double _currentNudgeStep = 0.05;
    private bool _suppressCameraAutoCenter;
    private bool _isUserCameraActive;
    private double _cumulativeNudgeRa;
    private double _cumulativeNudgeDec;
    private double _cumulativeNudgeRot;
    private double _cumulativeScaleMult = 1.0;
    private WcsMatrix? _originalUnnudgedWcs;

    public CelestialSphereControl()
    {
        Focusable = true;
        Background = new SolidColorBrush(Color.FromRgb(4, 7, 16));
        ClipToBounds = true;

        // 1. Setup 3D Viewport
        _camera = new PerspectiveCamera
        {
            Position = new Point3D(0, 0, 0),
            NearPlaneDistance = 0.5,
            FarPlaneDistance = 1500.0,
            FieldOfView = CameraFov
        };

        _sceneGroup = new Model3DGroup();
        _panelsGroup = new Model3DGroup();

        // Add celestial ambient white lighting for true color fidelity and non-additive linear alpha crossfading (v1.3.2)
        _sceneGroup.Children.Add(new AmbientLight(Colors.White));

        // Build celestial sphere globe / dome
        BuildCelestialSkyDome(IsGlobeView ? 97.0 : 150.0);

        _sceneGroup.Children.Add(_panelsGroup);

        _sceneVisual = new ModelVisual3D { Content = _sceneGroup };

        _viewport = new Viewport3D
        {
            Camera = _camera,
            IsHitTestVisible = false
        };
        _viewport.Children.Add(_sceneVisual);
        Children.Add(_viewport);

        // 2. Setup 2D Overlay Canvas on top of 3D Viewport
        _overlayCanvas = new SphereOverlayCanvas(this);
        Children.Add(_overlayCanvas);

        // 3. Setup HUD Overlay (Note: do NOT set IsHitTestVisible = false on hudGrid so buttons receive clicks!)
        var hudGrid = new Grid { Margin = new Thickness(16) };
        hudGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        hudGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        hudGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Top bar HUD card (Draggable within the 3D view)
        var topCardTransform = new TranslateTransform();
        _topCard = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(225, 11, 17, 32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(140, 56, 189, 248)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.SizeAll,
            RenderTransform = topCardTransform,
            ToolTip = CataloguePlateSolver.UseTurkishLanguage ? "Sürükleyerek taşıyabilirsiniz" : "Drag to move"
        };

        bool isDraggingHud = false;
        Point hudDragStart = default;
        double hudStartX = 0, hudStartY = 0;

        _topCard.MouseLeftButtonDown += (s, e) =>
        {
            isDraggingHud = true;
            hudDragStart = e.GetPosition(this);
            hudStartX = topCardTransform.X;
            hudStartY = topCardTransform.Y;
            _topCard.CaptureMouse();
            e.Handled = true;
        };
        _topCard.MouseMove += (s, e) =>
        {
            if (!isDraggingHud) return;
            Point cur = e.GetPosition(this);
            double nx = hudStartX + (cur.X - hudDragStart.X);
            double ny = hudStartY + (cur.Y - hudDragStart.Y);
            double maxX = Math.Max(0, ActualWidth - _topCard.ActualWidth - 32);
            double maxY = Math.Max(0, ActualHeight - _topCard.ActualHeight - 32);
            topCardTransform.X = Math.Clamp(nx, 0, maxX);
            topCardTransform.Y = Math.Clamp(ny, 0, maxY);
            e.Handled = true;
        };
        _topCard.MouseLeftButtonUp += (s, e) =>
        {
            if (!isDraggingHud) return;
            isDraggingHud = false;
            _topCard.ReleaseMouseCapture();
            e.Handled = true;
        };

        var topStack = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = "⋮⋮ ",
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        _hudTitle = new TextBlock
        {
            Text = CataloguePlateSolver.UseTurkishLanguage ? "🌌 3D GÖK KUBBE PLANETARYUM" : "🌌 3D CELESTIAL DOME PLANETARIUM",
            Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleRow.Children.Add(_hudTitle);
        topStack.Children.Add(titleRow);

        _hudCoordinates = new TextBlock
        {
            Text = CataloguePlateSolver.UseTurkishLanguage ? "Merkez: RA -- · Dec --" : "Center: RA -- · Dec --",
            Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            FontSize = 11.5,
            Margin = new Thickness(0, 4, 0, 0)
        };
        topStack.Children.Add(_hudCoordinates);

        _hudPanelsInfo = new TextBlock
        {
            Text = CataloguePlateSolver.UseTurkishLanguage ? "Kubbeye Giydirilen: 0 Panel" : "Projected Panels: 0",
            Foreground = new SolidColorBrush(Color.FromRgb(168, 85, 247)),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 0)
        };
        topStack.Children.Add(_hudPanelsInfo);

        _hudDetections = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153)),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 0),
            Visibility = Visibility.Collapsed
        };
        topStack.Children.Add(_hudDetections);

        _hudObjectsInFrame = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(250, 204, 21)),
            FontSize = 10.5,
            Margin = new Thickness(0, 2, 0, 0),
            Visibility = Visibility.Collapsed
        };
        topStack.Children.Add(_hudObjectsInFrame);

        _topCard.Child = topStack;
        Grid.SetRow(_topCard, 0);
        hudGrid.Children.Add(_topCard);

        // Top-right quick controls
        var rightStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };

        _centerPhotoBtn = new Button
        {
            Content = CataloguePlateSolver.UseTurkishLanguage ? "🎯 Fotoğrafa Odaklan" : "🎯 Center on Photo",
            Background = new SolidColorBrush(Color.FromArgb(220, 30, 41, 59)),
            Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 56, 189, 248)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6, 12, 6),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 4, 0),
            Cursor = Cursors.Hand,
            ToolTip = CataloguePlateSolver.UseTurkishLanguage ? "Kamerayı seçili fotoğrafın gökyüzündeki merkezine odaklar" : "Focuses the camera on the celestial center of the active photo"
        };
        _centerPhotoBtn.Click += (s, e) =>
        {
            _isUserCameraActive = false;
            CenterOnActivePhoto();
        };
        rightStack.Children.Add(_centerPhotoBtn);

        Grid.SetRow(rightStack, 0);
        hudGrid.Children.Add(rightStack);

        // Bottom navigation hints
        var bottomCard = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(190, 15, 23, 42)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 56, 189, 248)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        _hudNavHints = new TextBlock
        {
            Text = "🖱️ Sol Tık + Sürükle: 3D Küreyi Döndür   |   📜 Fare Tekerleği: Yakınlaştır / Uzaklaştır",
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            FontSize = 11
        };
        bottomCard.Child = _hudNavHints;
        Grid.SetRow(bottomCard, 2);
        hudGrid.Children.Add(bottomCard);

        Children.Add(hudGrid);

        IsVisibleChanged += (s, e) =>
        {
            if (IsVisible)
            {
                RebuildPhotoMeshes();
                CenterOnActivePhoto();
            }
        };

        UpdateNavHints();
        UpdateCameraVectors();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (CurrentWcs == null) return;

        double step = _currentNudgeStep;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (shift)
        {
            if (e.Key == Key.Left) { NudgeAlignment(-step, 0, 1.0, 0); e.Handled = true; }
            else if (e.Key == Key.Right) { NudgeAlignment(step, 0, 1.0, 0); e.Handled = true; }
            else if (e.Key == Key.Up) { NudgeAlignment(0, step, 1.0, 0); e.Handled = true; }
            else if (e.Key == Key.Down) { NudgeAlignment(0, -step, 1.0, 0); e.Handled = true; }
        }
        else if (ctrl)
        {
            if (e.Key == Key.Left) { NudgeAlignment(0, 0, 1.0, -0.25); e.Handled = true; }
            else if (e.Key == Key.Right) { NudgeAlignment(0, 0, 1.0, 0.25); e.Handled = true; }
            else if (e.Key == Key.Up) { NudgeAlignment(0, 0, 1.005, 0); e.Handled = true; }
            else if (e.Key == Key.Down) { NudgeAlignment(0, 0, 0.995, 0); e.Handled = true; }
        }
        else if (e.Key == Key.M || e.Key == Key.F)
        {
            NudgeAlignment(0, 0, 1.0, 0, toggleFlip: !CurrentWcs.IsFlippedHorizontally);
            e.Handled = true;
        }
        else if (e.Key == Key.L)
        {
            ExecuteAutoLockAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.None)
        {
            ResetAlignment();
            e.Handled = true;
        }
    }

    private static Button CreateHudButton(string content, string toolTip, Action onClick, Brush? bg = null, Brush? fg = null, Thickness? margin = null)
    {
        var btn = new Button
        {
            Content = content,
            ToolTip = toolTip,
            Background = bg ?? new SolidColorBrush(Color.FromArgb(220, 30, 41, 59)),
            Foreground = fg ?? new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(140, 56, 189, 248)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 3, 7, 3),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Margin = margin ?? new Thickness(2, 2, 2, 2),
            Cursor = Cursors.Hand
        };
        btn.Click += (s, e) => onClick();
        return btn;
    }

    public void NudgeAlignment(double deltaRaDeg, double deltaDecDeg, double scaleMult, double deltaRotDeg, bool? toggleFlip = null)
    {
        if (CurrentWcs == null) return;
        _originalUnnudgedWcs ??= CurrentWcs;

        _cumulativeNudgeRa += deltaRaDeg;
        _cumulativeNudgeDec += deltaDecDeg;
        _cumulativeNudgeRot += deltaRotDeg;
        _cumulativeScaleMult *= scaleMult;

        _suppressCameraAutoCenter = true;
        try
        {
            var updatedWcs = CurrentWcs.WithAdjustment(deltaRaDeg, deltaDecDeg, scaleMult, deltaRotDeg, toggleFlip);
            CurrentWcs = updatedWcs;
            if (MosaicPanels != null)
            {
                foreach (var p in MosaicPanels)
                {
                    if (!string.IsNullOrEmpty(ActiveFilePath) && string.Equals(p.FilePath, ActiveFilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Wcs = updatedWcs;
                    }
                }
            }
            RebuildPhotoMeshes();
            _overlayCanvas.InvalidateVisual();
            UpdateAlignmentHud();
        }
        finally
        {
            _suppressCameraAutoCenter = false;
        }
    }

    public void ResetAlignment()
    {
        if (_originalUnnudgedWcs != null)
        {
            _suppressCameraAutoCenter = true;
            try
            {
                CurrentWcs = _originalUnnudgedWcs;
                _cumulativeNudgeRa = 0;
                _cumulativeNudgeDec = 0;
                _cumulativeNudgeRot = 0;
                _cumulativeScaleMult = 1.0;
                RebuildPhotoMeshes();
                _overlayCanvas.InvalidateVisual();
                UpdateAlignmentHud();
            }
            finally
            {
                _suppressCameraAutoCenter = false;
            }
        }
    }

    private void UpdateAlignmentHud()
    {
    }

    private async void ExecuteAutoLockAsync()
    {
        if (CurrentWcs == null) return;

        var bmp = ActiveImageBitmap;
        if (bmp == null) return;

        try
        {
            var lum = BuildLuminanceFromBitmapSource(bmp);
            if (lum == null) return;

            var wcsSnapshot = CurrentWcs;
            var (refined, report) = await System.Threading.Tasks.Task.Run(() =>
            {
                var refWcs = CatalogLockRefiner.Refine(lum, wcsSnapshot, out var rep);
                return (refWcs, rep);
            });

            if (report != null && !ReferenceEquals(refined, CurrentWcs))
            {
                _suppressCameraAutoCenter = true;
                try
                {
                    CurrentWcs = refined;
                    if (MosaicPanels != null)
                    {
                        foreach (var p in MosaicPanels)
                        {
                            if (!string.IsNullOrEmpty(ActiveFilePath) && string.Equals(p.FilePath, ActiveFilePath, StringComparison.OrdinalIgnoreCase))
                            {
                                p.Wcs = refined;
                            }
                        }
                    }
                    RebuildPhotoMeshes();
                    _overlayCanvas.InvalidateVisual();
                }
                finally
                {
                    _suppressCameraAutoCenter = false;
                }
            }
        }
        catch { }
    }

    private static CatalogLockRefiner.LuminanceImage? BuildLuminanceFromBitmapSource(BitmapSource src)
    {
        if (src == null || src.PixelWidth < 16 || src.PixelHeight < 16) return null;
        int w = src.PixelWidth;
        int h = src.PixelHeight;

        FormatConvertedBitmap converted = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        int stride = w * 4;
        byte[] pixels = new byte[h * stride];
        converted.CopyPixels(pixels, stride, 0);

        byte[] lumData = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            int o = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + (x * 4);
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                lumData[o + x] = (byte)Math.Min(255, (int)((0.299 * r) + (0.587 * g) + (0.114 * b) + 0.5));
            }
        }
        return new CatalogLockRefiner.LuminanceImage(lumData, w, h, 1.0);
    }

    private static void OnWcsOrPanelsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CelestialSphereControl csc)
        {
            csc.RebuildPhotoMeshes();
            // Preserve user camera state on panel selection or property updates (v1.3.2 / Target 4)
            if (!csc._suppressCameraAutoCenter && !csc._isUserCameraActive)
            {
                csc.CenterOnActivePhoto();
            }
            csc.UpdateAlignmentHud();
        }
    }

    private static void OnOverlaySettingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CelestialSphereControl csc)
        {
            csc._overlayCanvas.InvalidateVisual();
        }
    }

    private static void OnOpacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CelestialSphereControl csc)
        {
            csc.RebuildPhotoMeshes();
        }
    }

    public void CenterOnActivePhoto()
    {
        if (MosaicPanels != null && MosaicPanels.Count > 1)
        {
            var solvedPanels = MosaicPanels.Where(p => p.IsVisible && p.IsSolved && p.Wcs != null).ToList();
            var panelsToCenter = solvedPanels.Count > 0 ? solvedPanels : MosaicPanels.Where(p => p.IsVisible && p.Wcs != null).ToList();
            if (panelsToCenter.Count == 0) return;

            double sumX = 0, sumY = 0, sumZ = 0;
            double maxSpan = 5.0;
            foreach (var p in panelsToCenter)
            {
                var c = p.Wcs!.CenterCoordinates;
                double cosD = Math.Cos(c.DecRadians);
                sumX += cosD * Math.Cos(c.RaRadians);
                sumY += cosD * Math.Sin(c.RaRadians);
                sumZ += Math.Sin(c.DecRadians);

                double span = Math.Max(p.Wcs.FovWidthDegrees, p.Wcs.FovHeightDegrees);
                if (span > maxSpan) maxSpan = span;
            }
            double norm = Math.Max(1e-9, Math.Sqrt((sumX * sumX) + (sumY * sumY) + (sumZ * sumZ)));
            var avgCoord = CelestialCoordinates.FromRadians(
                Math.Atan2(sumY, sumX),
                Math.Asin(Math.Clamp(sumZ / norm, -1.0, 1.0)));

            CameraRa = avgCoord.RaDegrees;
            CameraDec = Math.Clamp(avgCoord.DecDegrees, -89.0, 89.0);
            CameraFov = Math.Clamp(maxSpan * 1.45, 12.0, 85.0);
            GlobeDistance = Math.Clamp(200.0 + (maxSpan * 3.0), 160.0, 320.0);
            UpdateCameraVectors();
        }
        else if (CurrentWcs != null)
        {
            CameraRa = CurrentWcs.CenterCoordinates.RaDegrees;
            CameraDec = CurrentWcs.CenterCoordinates.DecDegrees;
            double span = Math.Max(CurrentWcs.FovWidthDegrees, CurrentWcs.FovHeightDegrees);
            CameraFov = Math.Clamp(span * 2.2, 12.0, 75.0);
            GlobeDistance = 220.0;
            UpdateCameraVectors();
        }
    }

    public void SetCamera(double raDeg, double decDeg, double fovDeg)
    {
        CameraRa = ((raDeg % 360.0) + 360.0) % 360.0;
        CameraDec = Math.Clamp(decDeg, -89.0, 89.0);
        CameraFov = Math.Clamp(fovDeg, 2.0, 100.0);
        UpdateCameraVectors();
    }

    public void RefreshLocalization()
    {
        bool tr = CataloguePlateSolver.UseTurkishLanguage;
        if (_topCard != null)
        {
            _topCard.ToolTip = tr ? "Sürükleyerek taşıyabilirsiniz" : "Drag to move";
        }
        if (_centerPhotoBtn != null)
        {
            _centerPhotoBtn.Content = tr ? "🎯 Fotoğrafa Odaklan" : "🎯 Center on Photo";
            _centerPhotoBtn.ToolTip = tr ? "Kamerayı seçili fotoğrafın gökyüzündeki merkezine odaklar" : "Focuses the camera on the celestial center of the active photo";
        }
        UpdateNavHints();
        UpdateCameraVectors();
        UpdateDetectionsHud();
        UpdateObjectsInFrame();
        RebuildPhotoMeshes();
    }

    private void UpdateNavHints()
    {
        bool tr = CataloguePlateSolver.UseTurkishLanguage;
        if (_hudNavHints != null)
        {
            if (tr)
            {
                _hudNavHints.Text = IsGlobeView
                    ? "🖱️ Sol Tık + Sürükle: 3D Küreyi Döndür   |   📜 Fare Tekerleği: Yaklaş / Uzaklaş   |   🖱️ Çift Tık: Fotoğrafı Seç"
                    : "🖱️ Sol Tık + Sürükle: Gök Kubbede Dolaş   |   📜 Fare Tekerleği: Görüş Alanı (FOV)   |   🖱️ Çift Tık: Fotoğrafı Seç";
            }
            else
            {
                _hudNavHints.Text = IsGlobeView
                    ? "🖱️ Left Drag: Rotate 3D Globe   |   📜 Mouse Wheel: Zoom Distance   |   🖱️ Double-Click: Select Photo"
                    : "🖱️ Left Drag: Pan Celestial Dome   |   📜 Mouse Wheel: Field of View (FOV)   |   🖱️ Double-Click: Select Photo";
            }
        }
        if (_hudTitle != null)
        {
            if (tr)
            {
                _hudTitle.Text = IsGlobeView
                    ? "🌐 3D GÖK KÜRESİ"
                    : "🌌 3D GÖK KUBBE PLANETARYUM";
            }
            else
            {
                _hudTitle.Text = IsGlobeView
                    ? "🌐 3D CELESTIAL GLOBE"
                    : "🌌 3D CELESTIAL DOME PLANETARIUM";
            }
        }
    }

    private void UpdateCameraVectors()
    {
        double raRad = CameraRa * (Math.PI / 180.0);
        double decRad = CameraDec * (Math.PI / 180.0);

        double cosD = Math.Cos(decRad);
        double sinD = Math.Sin(decRad);
        double cosR = Math.Cos(raRad);
        double sinR = Math.Sin(raRad);

        // Forward unit vector pointing towards (CameraRa, CameraDec)
        var lookDir = new Vector3D(cosD * sinR, sinD, -cosD * cosR);

        // Up vector pointing towards celestial North (+Dec)
        var upDir = new Vector3D(-sinD * sinR, cosD, sinD * cosR);

        var coord = new CelestialCoordinates(CameraRa, CameraDec);
        bool tr = CataloguePlateSolver.UseTurkishLanguage;

        if (IsGlobeView)
        {
            // 3D Globe (Dış Görünüm): Camera is positioned outside at distance GlobeDistance looking at center (0, 0, 0)
            _camera.Position = new Point3D(lookDir.X * GlobeDistance, lookDir.Y * GlobeDistance, lookDir.Z * GlobeDistance);
            _camera.LookDirection = new Vector3D(-lookDir.X * GlobeDistance, -lookDir.Y * GlobeDistance, -lookDir.Z * GlobeDistance);
            _camera.UpDirection = upDir;
            _camera.FieldOfView = 45.0;

            _hudCoordinates.Text = tr
                ? $"🌐 Dış Gök Küresi | Merkez: RA {coord.FormatRa()} ({CameraRa:F1}°) · Dec {coord.FormatDec()} · Mesafe {GlobeDistance:F0}"
                : $"🌐 Outer Globe | Center: RA {coord.FormatRa()} ({CameraRa:F1}°) · Dec {coord.FormatDec()} · Dist {GlobeDistance:F0}";
        }
        else
        {
            // 3D Dome (İç Görünüm - Stellarium): Observer is at the exact center of the celestial sphere
            _camera.Position = new Point3D(0, 0, 0);
            _camera.LookDirection = lookDir;
            _camera.UpDirection = upDir;
            _camera.FieldOfView = CameraFov;

            _hudCoordinates.Text = tr
                ? $"🌌 İç Gök Kubbe | Merkez: RA {coord.FormatRa()} ({CameraRa:F1}°) · Dec {coord.FormatDec()} · FOV {CameraFov:F1}°"
                : $"🌌 Inner Dome | Center: RA {coord.FormatRa()} ({CameraRa:F1}°) · Dec {coord.FormatDec()} · FOV {CameraFov:F1}°";
        }

        UpdateProjectionTransform();
        _overlayCanvas.InvalidateVisual();
    }

    private void UpdateObjectsInFrame()
    {
        if (_hudObjectsInFrame == null) return;

        var inFrame = new List<string>();
        double maxSpan = Math.Max(CameraFov * 0.7, 4.0);

        foreach (var target in CataloguePlateSolver.KnownCatalogues)
        {
            double dRa = (CameraRa - target.Coordinates.RaDegrees) * Math.Cos(CameraDec * Math.PI / 180.0);
            double dDec = CameraDec - target.Coordinates.DecDegrees;
            double angDist = Math.Sqrt((dRa * dRa) + (dDec * dDec));
            if (angDist <= maxSpan)
            {
                inFrame.Add(target.CommonName);
                if (inFrame.Count >= 4) break;
            }
        }

        if (inFrame.Count > 0)
        {
            _hudObjectsInFrame.Visibility = Visibility.Visible;
            _hudObjectsInFrame.Text = (CataloguePlateSolver.UseTurkishLanguage ? "🔭 Alandaki Objeler: " : "🔭 Objects in Frame: ") + string.Join(", ", inFrame);
        }
        else
        {
            _hudObjectsInFrame.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateDetectionsHud()
    {
        if (_hudDetections == null) return;
        if (TotalCount > 0)
        {
            _hudDetections.Visibility = Visibility.Visible;
            _hudDetections.Text = CataloguePlateSolver.UseTurkishLanguage
                ? $"⭐ Tespitler: {TotalCount:N0} Obje (Tier 1: {Tier1Count} · Tier 2: {Tier2Count} · Tier 3: {Tier3Count})"
                : $"⭐ Detections: {TotalCount:N0} Objects (Tier 1: {Tier1Count} · Tier 2: {Tier2Count} · Tier 3: {Tier3Count})";
        }
        else
        {
            _hudDetections.Visibility = Visibility.Collapsed;
        }
    }

    private static BitmapSource GetOrCreate3DProxyBitmap(BitmapSource src)
    {
        if (src.PixelWidth <= 2048 && src.PixelHeight <= 2048)
        {
            return src;
        }

        double scale = 2048.0 / Math.Max(src.PixelWidth, src.PixelHeight);
        var transformed = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
    }

    private void BuildCelestialSkyDome(double radius)
    {
        if (_skyDomeModel != null)
        {
            _sceneGroup.Children.Remove(_skyDomeModel);
            _skyDomeModel = null;
        }

        var mesh = new MeshGeometry3D();
        int latBands = 36;
        int lonBands = 72;

        for (int lat = 0; lat <= latBands; lat++)
        {
            double theta = (double)lat * Math.PI / latBands;
            double sinTheta = Math.Sin(theta);
            double cosTheta = Math.Cos(theta);

            for (int lon = 0; lon <= lonBands; lon++)
            {
                double phi = (double)lon * 2.0 * Math.PI / lonBands;
                double sinPhi = Math.Sin(phi);
                double cosPhi = Math.Cos(phi);

                double x = radius * cosPhi * sinTheta;
                double y = radius * cosTheta;
                double z = radius * sinPhi * sinTheta;

                mesh.Positions.Add(new Point3D(x, y, z));
            }
        }

        // Both inside and outside visible triangles
        for (int lat = 0; lat < latBands; lat++)
        {
            for (int lon = 0; lon < lonBands; lon++)
            {
                int first = (lat * (lonBands + 1)) + lon;
                int second = first + lonBands + 1;

                mesh.TriangleIndices.Add(first);
                mesh.TriangleIndices.Add(second);
                mesh.TriangleIndices.Add(first + 1);

                mesh.TriangleIndices.Add(second);
                mesh.TriangleIndices.Add(second + 1);
                mesh.TriangleIndices.Add(first + 1);
            }
        }

        var darkBrush = new SolidColorBrush(Color.FromRgb(6, 10, 20));
        darkBrush.Freeze();
        var darkMat = new EmissiveMaterial(darkBrush);
        darkMat.Freeze();

        _skyDomeModel = new GeometryModel3D(mesh, darkMat)
        {
            Material = darkMat,
            BackMaterial = darkMat
        };
        _sceneGroup.Children.Insert(0, _skyDomeModel);
    }

    public void RebuildPhotoMeshes()
    {
        _panelsGroup.Children.Clear();

        var panelsToRender = new List<MosaicPanel>();
        if (MosaicPanels != null && MosaicPanels.Count > 0)
        {
            panelsToRender.AddRange(MosaicPanels);
        }

        // Single photo fallback: only synthesize "active" panel if no panels exist in mosaic
        if (panelsToRender.Count == 0 && CurrentWcs != null && CurrentWcs.ImageWidth > 0 && CurrentWcs.ImageHeight > 0)
        {
            var activePanel = new MosaicPanel(
                "active",
                "Aktif Fotoğraf",
                CurrentWcs.ImageWidth,
                CurrentWcs.ImageHeight,
                CurrentWcs,
                ActiveFilePath)
            {
                CachedBitmap = ActiveImageBitmap
            };
            panelsToRender.Add(activePanel);
        }

        double baseRadius = 98.2;
        double opacity = Math.Clamp(PhotoOpacity, 0.05, 1.0);
        int renderedCount = 0;

        for (int pIndex = 0; pIndex < panelsToRender.Count; pIndex++)
        {
            var p = panelsToRender[pIndex];
            if (!p.IsVisible || p.Width <= 0 || p.Height <= 0) continue;

            var bmp = UseFeatheredBlending
                ? (DetectionOverlay.GetOrLoadFeatheredPanelBitmap(p.FilePath, DetectionOverlay.GetPanelBitmap(p)) ?? DetectionOverlay.GetPanelBitmap(p))
                : DetectionOverlay.GetPanelBitmap(p);
            if (bmp == null && (p.Id == "active" || (!string.IsNullOrEmpty(ActiveFilePath) && string.Equals(p.FilePath, ActiveFilePath, StringComparison.OrdinalIgnoreCase))))
            {
                bmp = ActiveImageBitmap;
            }
            if (bmp == null || p.Wcs == null || (panelsToRender.Count > 1 && !p.IsSolved)) continue;

            renderedCount++;

            // Strict depth layering: microscopic depth offset avoids Z-fighting between overlapping mosaic panels
            // while preserving exact mathematical radius consistency with catalog stars.
            double layerStep = 0.08;
            double panelRadius = IsGlobeView
                ? (baseRadius + (pIndex * layerStep))
                : (baseRadius - (pIndex * layerStep));

            // 32x32 spherical mesh warp (v1.3.2 / Target 2): Curves each photo accurately onto the celestial dome
            // and faithfully snaps overlapping stars using non-linear Thin Plate Spline and optical radial lens distortion.
            int divX = 32;
            int divY = 32;
            int vertexCount = (divX + 1) * (divY + 1);
            var positions = new Point3D[vertexCount];
            var texCoords = new Point[vertexCount];

            var panelWcs = p.Wcs;
            double pWidth = p.Width;
            double pHeight = p.Height;

            Parallel.For(0, divY + 1, j =>
            {
                double v = (double)j / divY;
                int rowOffset = j * (divX + 1);

                for (int i = 0; i <= divX; i++)
                {
                    double u = (double)i / divX;
                    double px = u * pWidth;
                    double py = v * pHeight;

                    // Direct 1:1 astrometric mapping with non-linear TPS & distortion correction:
                    // Snaps overlapping stars across adjacent panels to identical 3D celestial sphere coordinates!
                    var coord = panelWcs.PixelToEquatorial(px, py);
                    double raRad = coord.RaRadians;
                    double decRad = coord.DecRadians;

                    double cosD = Math.Cos(decRad);
                    double sinD = Math.Sin(decRad);
                    double cosR = Math.Cos(raRad);
                    double sinR = Math.Sin(raRad);

                    int idx = rowOffset + i;
                    positions[idx] = new Point3D(
                        panelRadius * cosD * sinR,
                        panelRadius * sinD,
                        -panelRadius * cosD * cosR);
                    texCoords[idx] = new Point(u, v);
                }
            });

            var mesh = new MeshGeometry3D();
            mesh.Positions = new Point3DCollection(positions);
            mesh.TextureCoordinates = new PointCollection(texCoords);

            for (int j = 0; j < divY; j++)
            {
                for (int i = 0; i < divX; i++)
                {
                    int p0 = (j * (divX + 1)) + i;
                    int p1 = p0 + 1;
                    int p2 = p0 + (divX + 1);
                    int p3 = p2 + 1;

                    // Inside facing triangles
                    mesh.TriangleIndices.Add(p0);
                    mesh.TriangleIndices.Add(p3);
                    mesh.TriangleIndices.Add(p1);

                    mesh.TriangleIndices.Add(p0);
                    mesh.TriangleIndices.Add(p2);
                    mesh.TriangleIndices.Add(p3);
                }
            }

            mesh.Freeze();

            // Mipmap Proxy Texture - ensure 3D spherical mesh texture is bounded to 2048px
            bmp = GetOrCreate3DProxyBitmap(bmp);

            var imageBrush = new ImageBrush(bmp)
            {
                Stretch = Stretch.Fill,
                Opacity = opacity * p.Opacity
            };
            imageBrush.Freeze();

            // v1.3.2 Fix 1: Use DiffuseMaterial with white ambient light for linear alpha crossfade blending without additive blowout
            var diffMat = new DiffuseMaterial(imageBrush);
            diffMat.Freeze();

            var geomModel = new GeometryModel3D(mesh, diffMat)
            {
                Material = diffMat,
                BackMaterial = diffMat
            };

            _panelsGroup.Children.Add(geomModel);
        }

        if (CataloguePlateSolver.UseTurkishLanguage)
        {
            _hudPanelsInfo.Text = renderedCount > 1
                ? $"Kubbeye Giydirilen: {renderedCount} Panel Birleştirildi"
                : (renderedCount == 1 ? "Kubbeye Giydirilen: 1 Panel Yerleştirildi" : "Kubbeye Giydirilen: 0 Panel");
        }
        else
        {
            _hudPanelsInfo.Text = renderedCount > 1
                ? $"Projected: {renderedCount} Panels Stitched"
                : (renderedCount == 1 ? "Projected: 1 Panel Placed" : "Projected: 0 Panels");
        }

        _overlayCanvas.InvalidateVisual();
    }

    private MosaicPanel? HitTestPanelAtScreenPoint(Point screenPt)
    {
        var panels = MosaicPanels;
        if (panels == null || panels.Count == 0) return null;

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return null;

        // Check from top-most layer (last in list) to bottom-most layer (first in list)
        for (int i = panels.Count - 1; i >= 0; i--)
        {
            var p = panels[i];
            if (!p.IsVisible || p.Wcs == null) continue;

            var (tl, tr, br, bl) = p.GetFootprintCorners();
            if (!CelestialToScreen(tl.RaDegrees, tl.DecDegrees, w, h, out double x0, out double y0)) continue;
            if (!CelestialToScreen(tr.RaDegrees, tr.DecDegrees, w, h, out double x1, out double y1)) continue;
            if (!CelestialToScreen(br.RaDegrees, br.DecDegrees, w, h, out double x2, out double y2)) continue;
            if (!CelestialToScreen(bl.RaDegrees, bl.DecDegrees, w, h, out double x3, out double y3)) continue;

            Point[] quad = { new Point(x0, y0), new Point(x1, y1), new Point(x2, y2), new Point(x3, y3) };
            if (IsPointInPolygon(screenPt, quad))
            {
                return p;
            }
        }

        return null;
    }

    private static bool IsPointInPolygon(Point pt, Point[] poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            if (((poly[i].Y > pt.Y) != (poly[j].Y > pt.Y)) &&
                (pt.X < (poly[j].X - poly[i].X) * (pt.Y - poly[i].Y) / (poly[j].Y - poly[i].Y + 1e-12) + poly[i].X))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            if (e.ClickCount == 2)
            {
                var hitPanel = HitTestPanelAtScreenPoint(e.GetPosition(this));
                PanelDoubleClicked?.Invoke(hitPanel);
                _overlayCanvas.InvalidateVisual();
                e.Handled = true;
                return;
            }

            _isDragging = true;
            _lastMousePos = e.GetPosition(this);
            CaptureMouse();
            Cursor = Cursors.SizeAll;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            _isUserCameraActive = true;
            var curPos = e.GetPosition(this);
            double dx = curPos.X - _lastMousePos.X;
            double dy = curPos.Y - _lastMousePos.Y;
            _lastMousePos = curPos;

            double w = Math.Max(ActualWidth, 100.0);
            double h = Math.Max(ActualHeight, 100.0);

            if (IsGlobeView)
            {
                // Dragging in 3D globe view smoothly spins the globe
                double dRa = -(dx / w) * 90.0;
                double dDec = (dy / h) * 90.0;

                CameraRa = ((CameraRa + dRa) % 360.0 + 360.0) % 360.0;
                CameraDec = Math.Clamp(CameraDec + dDec, -89.0, 89.0);
            }
            else
            {
                // Dragging in dome view pans around the celestial dome (Stellarium navigation)
                double cosDec = Math.Max(0.05, Math.Cos(CameraDec * (Math.PI / 180.0)));
                double dRa = -(dx / w) * CameraFov * (1.0 / cosDec);
                double dDec = (dy / h) * CameraFov;

                CameraRa = ((CameraRa + dRa) % 360.0 + 360.0) % 360.0;
                CameraDec = Math.Clamp(CameraDec + dDec, -89.0, 89.0);
            }

            UpdateCameraVectors();
            CameraMoved?.Invoke(CameraRa, CameraDec, CameraFov);
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_isDragging)
        {
            _isDragging = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _isUserCameraActive = true;
        double factor = e.Delta > 0 ? 0.85 : 1.18;

        if (IsGlobeView)
        {
            // Zoom distance to 3D globe
            GlobeDistance = Math.Clamp(GlobeDistance * factor, 110.0, 500.0);
        }
        else
        {
            // Zoom field of view in dome
            CameraFov = Math.Clamp(CameraFov * factor, 2.0, 100.0);
        }

        UpdateCameraVectors();
        CameraMoved?.Invoke(CameraRa, CameraDec, CameraFov);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateProjectionTransform();
        _overlayCanvas.InvalidateVisual();
    }

    /// <summary>
    /// Projects a celestial coordinate (RA, Dec) onto 2D screen pixels based on current 3D camera orientation.
    /// Returns false if point lies behind the camera plane or occluded by the globe.
    /// </summary>
    public bool CelestialToScreen(double raDeg, double decDeg, double screenW, double screenH, out double sx, out double sy)
    {
        double raRad = raDeg * (Math.PI / 180.0);
        double decRad = decDeg * (Math.PI / 180.0);

        double camRaRad = CameraRa * (Math.PI / 180.0);
        double camDecRad = CameraDec * (Math.PI / 180.0);

        double cosD = Math.Cos(decRad);
        double sinD = Math.Sin(decRad);
        double cosR = Math.Cos(raRad);
        double sinR = Math.Sin(raRad);

        // Point on unit sphere
        double px = cosD * sinR;
        double py = sinD;
        double pz = -cosD * cosR;

        double cCosD = Math.Cos(camDecRad);
        double cSinD = Math.Sin(camDecRad);
        double cCosR = Math.Cos(camRaRad);
        double cSinR = Math.Sin(camRaRad);

        // Direction vector from origin towards center
        double lx = cCosD * cSinR;
        double ly = cSinD;
        double lz = -cCosD * cCosR;

        // Up basis (+Dec)
        double ux = -cSinD * cSinR;
        double uy = cCosD;
        double uz = cSinD * cCosR;

        // Right basis
        double rx = cCosR;
        double ry = 0.0;
        double rz = cSinR;

        double sphereR = 98.2;
        double ptX = px * sphereR;
        double ptY = py * sphereR;
        double ptZ = pz * sphereR;

        if (IsGlobeView)
        {
            // In globe view, point must be on the visible hemisphere facing camera
            double dotFacing = (px * lx) + (py * ly) + (pz * lz);
            if (dotFacing < 0.05)
            {
                sx = 0; sy = 0;
                return false;
            }
        }
        else
        {
            // Inside view: Camera at center (0, 0, 0) looking towards +lookDir (matching _camera.Position exactly)
            double zc = (ptX * lx) + (ptY * ly) + (ptZ * lz);
            if (zc <= 0.05)
            {
                sx = 0; sy = 0;
                return false;
            }
        }

        // 1. Primary Hardware Projection: Direct 1:1 synchronization with Viewport3D rendering matrix
        var xform = _cachedTransform3D ?? (_sceneVisual.TransformToAncestor(_viewport) as GeneralTransform3DTo2D);
        if (xform != null && xform.TryTransform(new Point3D(ptX, ptY, ptZ), out Point p2d))
        {
            sx = p2d.X;
            sy = p2d.Y;
            return true;
        }

        // 2. Analytical Fallback Projection (Exact right-handed view matrix)
        if (IsGlobeView)
        {
            double camX = lx * GlobeDistance;
            double camY = ly * GlobeDistance;
            double camZ = lz * GlobeDistance;

            double dx = ptX - camX;
            double dy = ptY - camY;
            double dz = ptZ - camZ;

            // Camera forward vector is towards (0, 0, 0)
            double zc = (dx * -lx) + (dy * -ly) + (dz * -lz);
            if (zc <= 1.0)
            {
                sx = 0; sy = 0;
                return false;
            }

            // In right-handed coordinates looking towards -L with up U, X-axis is U x -L = -R
            double xc = -((dx * rx) + (dy * ry) + (dz * rz));
            double yc = (dx * ux) + (dy * uy) + (dz * uz);

            double f = (screenW * 0.5) / Math.Tan(45.0 * (Math.PI / 360.0));
            sx = (screenW * 0.5) + (f * (xc / zc));
            sy = (screenH * 0.5) - (f * (yc / zc));
            return true;
        }
        else
        {
            double zc = (ptX * lx) + (ptY * ly) + (ptZ * lz);
            if (zc <= 0.05)
            {
                sx = 0; sy = 0;
                return false;
            }

            double xc = (ptX * rx) + (ptY * ry) + (ptZ * rz);
            double yc = (ptX * ux) + (ptY * uy) + (ptZ * uz);

            double f = (screenW * 0.5) / Math.Tan(CameraFov * (Math.PI / 360.0));
            sx = (screenW * 0.5) + (f * (xc / zc));
            sy = (screenH * 0.5) - (f * (yc / zc));
            return true;
        }
    }

    /// <summary>
    /// Lightweight 2D DrawingContext overlay rendering stars, constellations, DSOs, and coordinate grids
    /// on top of the 3D celestial sphere.
    /// </summary>
    private sealed class SphereOverlayCanvas : FrameworkElement
    {
        private readonly CelestialSphereControl _owner;

        private static readonly Brush ConstellationPenBrush = MakeBrush(56, 189, 248, 160);
        private static readonly Brush ConstellationTextBrush = MakeBrush(125, 211, 252, 230);
        private static readonly Brush GridBrush = MakeBrush(56, 189, 248, 75);
        private static readonly Brush DsoBrush = MakeBrush(250, 204, 21, 220);
        private static readonly Brush DsoTextBrush = MakeBrush(254, 240, 138, 240);
        private static readonly Brush StarLabelBrush = MakeBrush(203, 213, 225, 210);
        private static readonly Brush BadgeBackground = MakeBrush(15, 23, 42, 210);

        private static readonly Brush StarBlue = MakeBrush(147, 197, 253);
        private static readonly Brush StarWhite = MakeBrush(248, 250, 252);
        private static readonly Brush StarYellow = MakeBrush(254, 240, 138);
        private static readonly Brush StarOrange = MakeBrush(253, 186, 116);
        private static readonly Brush StarRed = MakeBrush(248, 113, 113);

        private static readonly Brush Tier1Brush = MakeBrush(0, 230, 118);
        private static readonly Brush Tier2Brush = MakeBrush(255, 214, 0);
        private static readonly Brush Tier3Brush = MakeBrush(255, 145, 0);
        private static readonly Brush FilteredNoiseBrush = MakeBrush(239, 68, 68);

        private static readonly Pen Tier1Pen = MakePen(0, 230, 118, 1.2);
        private static readonly Pen Tier2Pen = MakePen(255, 214, 0, 1.1);
        private static readonly Pen Tier3Pen = MakePen(255, 145, 0, 1.0);
        private static readonly Pen FilteredPen = MakePen(239, 68, 68, 0.9);


        public SphereOverlayCanvas(CelestialSphereControl owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            _owner.UpdateProjectionTransform();

            if (_owner.ShowSphericalGrid)
            {
                DrawSphericalGrid(dc, w, h);
            }

            if (_owner.ShowConstellations)
            {
                DrawConstellations(dc, w, h);
            }

            if (_owner.ShowHipparcosStars)
            {
                DrawHipparcosStars(dc, w, h);
            }

            if (_owner.ShowDsoMarkers)
            {
                DrawDsoMarkers(dc, w, h);
            }

            DrawRoiRegions(dc, w, h);
            if (_owner.ShowBoundingBoxes || _owner.ShowLabels)
            {
                DrawDetections3D(dc, w, h);
            }
            DrawAnomalyReticles3D(dc, w, h);
            DrawMosaicPanelTags(dc, w, h);

            DrawCenterCrosshair(dc, w, h);
        }

        private void DrawSphericalGrid(DrawingContext dc, double w, double h)
        {
            var pen = new Pen(GridBrush, 1.0);
            pen.Freeze();

            double step = _owner.CameraFov > 50.0 ? 15.0 : (_owner.CameraFov > 20.0 ? 10.0 : 5.0);

            // RA meridians
            for (double ra = 0; ra < 360.0; ra += step)
            {
                Point? prev = null;
                for (double dec = -85; dec <= 85; dec += 5)
                {
                    if (_owner.CelestialToScreen(ra, dec, w, h, out double sx, out double sy))
                    {
                        var cur = new Point(sx, sy);
                        if (prev.HasValue)
                        {
                            dc.DrawLine(pen, prev.Value, cur);
                        }
                        prev = cur;
                    }
                    else
                    {
                        prev = null;
                    }
                }
            }

            // Dec parallels
            for (double dec = -80; dec <= 80; dec += step)
            {
                Point? prev = null;
                for (double ra = 0; ra <= 360.0; ra += 5)
                {
                    if (_owner.CelestialToScreen(ra, dec, w, h, out double sx, out double sy))
                    {
                        var cur = new Point(sx, sy);
                        if (prev.HasValue)
                        {
                            dc.DrawLine(pen, prev.Value, cur);
                        }
                        prev = cur;
                    }
                    else
                    {
                        prev = null;
                    }
                }
            }
        }

        private void DrawConstellations(DrawingContext dc, double w, double h)
        {
            var pen = new Pen(ConstellationPenBrush, 1.4)
            {
                DashStyle = new DashStyle(new double[] { 4, 3 }, 0)
            };
            pen.Freeze();

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var labeled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in ConstellationCatalog.Lines)
            {
                bool p1Ok = _owner.CelestialToScreen(line.Start.RaDegrees, line.Start.DecDegrees, w, h, out double x1, out double y1);
                bool p2Ok = _owner.CelestialToScreen(line.End.RaDegrees, line.End.DecDegrees, w, h, out double x2, out double y2);

                if (p1Ok && p2Ok)
                {
                    dc.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));

                    if (!labeled.Contains(line.ConstellationName))
                    {
                        double mx = (x1 + x2) * 0.5;
                        double my = (y1 + y2) * 0.5;
                        if (mx >= 40 && mx <= w - 40 && my >= 40 && my <= h - 40)
                        {
                            labeled.Add(line.ConstellationName);
                            var ft = new FormattedText(line.DisplayName.ToUpperInvariant(), CultureInfo.InvariantCulture,
                                FlowDirection.LeftToRight, typeface, 11.5, ConstellationTextBrush, dpi);

                            var rect = new Rect(mx - (ft.Width * 0.5) - 4, my - (ft.Height * 0.5) - 2, ft.Width + 8, ft.Height + 4);
                            dc.DrawRoundedRectangle(BadgeBackground, null, rect, 3, 3);
                            dc.DrawText(ft, new Point(mx - (ft.Width * 0.5), my - (ft.Height * 0.5)));
                        }
                    }
                }
            }
        }

        private void DrawHipparcosStars(DrawingContext dc, double w, double h)
        {
            double searchRadius = (_owner.CameraFov * 0.75) + 2.0;
            double maxMag = _owner.CameraFov switch
            {
                > 60.0 => 5.2,
                > 35.0 => 6.2,
                > 15.0 => 7.5,
                _ => 8.8
            };

            var stars = HipparcosCatalog.QueryRegion(_owner.CameraRa, _owner.CameraDec, searchRadius, maxMag, maxResults: 350);
            if (stars.Count == 0) return;

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

            foreach (var s in stars)
            {
                if (!_owner.CelestialToScreen(s.RaDeg, s.DecDeg, w, h, out double sx, out double sy)) continue;
                if (sx < -10 || sx > w + 10 || sy < -10 || sy > h + 10) continue;

                double radius = Math.Max(1.2, (8.5 - s.Mag) * 0.95);
                var brush = s.Bv switch
                {
                    < 0.0f => StarBlue,
                    < 0.35f => StarWhite,
                    < 0.80f => StarYellow,
                    < 1.30f => StarOrange,
                    _ => StarRed
                };

                dc.DrawEllipse(brush, null, new Point(sx, sy), radius, radius);

                if (s.Mag <= 2.8 || (_owner.CameraFov < 15.0 && s.Mag <= 4.5))
                {
                    string txt = $"HIP {s.HipId} ({s.Mag:F1})";
                    var ft = new FormattedText(txt, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        typeface, 10.0, StarLabelBrush, dpi);

                    dc.DrawRectangle(BadgeBackground, null, new Rect(sx + radius + 2, sy - (ft.Height * 0.5), ft.Width + 4, ft.Height));
                    dc.DrawText(ft, new Point(sx + radius + 4, sy - (ft.Height * 0.5)));
                }
            }
        }

        private void DrawDsoMarkers(DrawingContext dc, double w, double h)
        {
            var pen = new Pen(DsoBrush, 1.4)
            {
                DashStyle = new DashStyle(new double[] { 3, 2 }, 0)
            };
            pen.Freeze();

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

            foreach (var target in CataloguePlateSolver.KnownCatalogues)
            {
                if (!_owner.CelestialToScreen(target.Coordinates.RaDegrees, target.Coordinates.DecDegrees, w, h, out double sx, out double sy)) continue;
                if (sx < -40 || sx > w + 40 || sy < -40 || sy > h + 40) continue;

                double radius = Math.Clamp(18.0 * (45.0 / _owner.CameraFov), 12.0, 50.0);
                dc.DrawEllipse(null, pen, new Point(sx, sy), radius, radius);

                var ft = new FormattedText(target.CommonName, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 11.0, DsoTextBrush, dpi);

                double lx = sx - (ft.Width * 0.5);
                double ly = sy + radius + 3;
                dc.DrawRoundedRectangle(BadgeBackground, null, new Rect(lx - 4, ly - 2, ft.Width + 8, ft.Height + 4), 3, 3);
                dc.DrawText(ft, new Point(lx, ly));
            }
        }

        private void DrawMosaicPanelTags(DrawingContext dc, double w, double h)
        {
            var panels = _owner.MosaicPanels;
            if (panels == null || panels.Count == 0) return;

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var purpleBrush = MakeBrush(216, 180, 254);
            var purpleBorder = MakeBrush(168, 85, 247, 180);
            var selectedBrush = MakeBrush(56, 189, 248);
            var selectedBorderPen = new Pen(MakeBrush(56, 189, 248, 235), 2.2);
            selectedBorderPen.Freeze();

            foreach (var p in panels)
            {
                if (!p.IsVisible || p.Wcs == null || !p.IsSelected) continue;
                var (tl, tr, br, bl) = p.GetFootprintCorners();

                bool c0 = _owner.CelestialToScreen(tl.RaDegrees, tl.DecDegrees, w, h, out double x0, out double y0);
                bool c1 = _owner.CelestialToScreen(tr.RaDegrees, tr.DecDegrees, w, h, out double x1, out double y1);
                bool c2 = _owner.CelestialToScreen(br.RaDegrees, br.DecDegrees, w, h, out double x2, out double y2);
                bool c3 = _owner.CelestialToScreen(bl.RaDegrees, bl.DecDegrees, w, h, out double x3, out double y3);
                if (c0 && c1) dc.DrawLine(selectedBorderPen, new Point(x0, y0), new Point(x1, y1));
                if (c1 && c2) dc.DrawLine(selectedBorderPen, new Point(x1, y1), new Point(x2, y2));
                if (c2 && c3) dc.DrawLine(selectedBorderPen, new Point(x2, y2), new Point(x3, y3));
                if (c3 && c0) dc.DrawLine(selectedBorderPen, new Point(x3, y3), new Point(x0, y0));

                if (!c0) continue;
                if (x0 < -40 || x0 > w + 40 || y0 < -40 || y0 > h + 40) continue;

                string badge = $"🎯 {p.Name}";
                var ft = new FormattedText(badge, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, 11.0, selectedBrush, dpi);

                var rect = new Rect(x0 + 6, y0 + 6, ft.Width + 8, ft.Height + 4);
                dc.DrawRoundedRectangle(BadgeBackground, selectedBorderPen, rect, 3, 3);
                dc.DrawText(ft, new Point(x0 + 10, y0 + 8));
            }
        }

        private void DrawRoiRegions(DrawingContext dc, double w, double h)
        {
            var regions = _owner.Regions;
            var wcs = _owner.CurrentWcs;
            if (regions == null || regions.Count == 0 || wcs == null) return;

            foreach (var region in regions)
            {
                if (region.Points.Count < 3) continue;

                bool exclude = region.Kind == AstroCounter.Core.Models.RegionKind.Exclude;
                var fillBrush = new SolidColorBrush(exclude ? Color.FromArgb(70, 239, 68, 68) : Color.FromArgb(45, 52, 211, 153));
                fillBrush.Freeze();
                var strokePen = new Pen(new SolidColorBrush(exclude ? Color.FromRgb(248, 113, 113) : Color.FromRgb(52, 211, 153)), 1.8);
                strokePen.Freeze();

                var streamGeom = new StreamGeometry();
                bool anyVisible = false;
                using (var ctx = streamGeom.Open())
                {
                    var firstEq = wcs.PixelToEquatorial(region.Points[0].X, region.Points[0].Y);
                    if (_owner.CelestialToScreen(firstEq.RaDegrees, firstEq.DecDegrees, w, h, out double sx0, out double sy0))
                    {
                        anyVisible = true;
                    }
                    ctx.BeginFigure(new Point(sx0, sy0), isFilled: true, isClosed: true);

                    for (int i = 1; i < region.Points.Count; i++)
                    {
                        var eq = wcs.PixelToEquatorial(region.Points[i].X, region.Points[i].Y);
                        if (_owner.CelestialToScreen(eq.RaDegrees, eq.DecDegrees, w, h, out double sx, out double sy))
                        {
                            anyVisible = true;
                        }
                        ctx.LineTo(new Point(sx, sy), isStroked: true, isSmoothJoin: false);
                    }
                }

                if (anyVisible)
                {
                    streamGeom.Freeze();
                    dc.DrawGeometry(fillBrush, strokePen, streamGeom);
                }
            }
        }

        private void DrawAnomalyReticles3D(DrawingContext dc, double w, double h)
        {
            var wcs = _owner.CurrentWcs;
            var objects = _owner.VisibleObjects;
            if (wcs == null || objects == null || objects.Count == 0) return;

            Pen anomalyPen = new Pen(new SolidColorBrush(Color.FromRgb(244, 63, 94)), 1.8);
            anomalyPen.Freeze();
            Pen tickPen = new Pen(new SolidColorBrush(Color.FromRgb(251, 113, 133)), 1.4);
            tickPen.Freeze();
            Brush fillBrush = new SolidColorBrush(Color.FromArgb(50, 244, 63, 94));
            fillBrush.Freeze();
            Brush textBg = new SolidColorBrush(Color.FromArgb(230, 15, 23, 42));
            textBg.Freeze();

            foreach (var obj in objects)
            {
                if (!obj.IsAnomaly) continue;

                double targetRa = obj.RaDegrees ?? (wcs != null ? wcs.PixelToEquatorial(obj.CenterX, obj.CenterY).RaDegrees : 0);
                double targetDec = obj.DecDegrees ?? (wcs != null ? wcs.PixelToEquatorial(obj.CenterX, obj.CenterY).DecDegrees : 0);
                if (_owner.CelestialToScreen(targetRa, targetDec, w, h, out double sx, out double sy))
                {
                    double r = 16.0;
                    dc.DrawEllipse(fillBrush, anomalyPen, new Point(sx, sy), r, r);

                    dc.DrawLine(tickPen, new Point(sx - r - 6, sy), new Point(sx - r - 2, sy));
                    dc.DrawLine(tickPen, new Point(sx + r + 2, sy), new Point(sx + r + 6, sy));
                    dc.DrawLine(tickPen, new Point(sx, sy - r - 6), new Point(sx, sy - r - 2));
                    dc.DrawLine(tickPen, new Point(sx, sy + r + 2), new Point(sx, sy + r + 6));

                    string labelText = "🎯 " + (obj.AnomalyType ?? "ANOMALİ");
                    var ft = new FormattedText(
                        labelText,
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                        11.0,
                        new SolidColorBrush(Color.FromRgb(251, 113, 133)),
                        VisualTreeHelper.GetDpi(_owner).PixelsPerDip);

                    var textRect = new Rect(sx - (ft.Width / 2.0) - 4, sy + r + 4, ft.Width + 8, ft.Height + 4);
                    dc.DrawRoundedRectangle(textBg, tickPen, textRect, 4, 4);
                    dc.DrawText(ft, new Point(sx - (ft.Width / 2.0), sy + r + 6));
                }
            }
        }

        private void DrawCenterCrosshair(DrawingContext dc, double w, double h)
        {
            double cx = w * 0.5;
            double cy = h * 0.5;
            double size = 8.0;

            var pen = new Pen(MakeBrush(56, 189, 248, 120), 1.0);
            pen.Freeze();

            dc.DrawLine(pen, new Point(cx - size, cy), new Point(cx - 2, cy));
            dc.DrawLine(pen, new Point(cx + 2, cy), new Point(cx + size, cy));
            dc.DrawLine(pen, new Point(cx, cy - size), new Point(cx, cy - 2));
            dc.DrawLine(pen, new Point(cx, cy + 2), new Point(cx, cy + size));
        }

        private void DrawDetections3D(DrawingContext dc, double w, double h)
        {
            var objects = _owner.VisibleObjects;
            var wcs = _owner.CurrentWcs;
            if (objects == null || objects.Count == 0 || wcs == null) return;

            bool showBoxes = _owner.ShowBoundingBoxes;
            bool showLabels = _owner.ShowLabels;
            if (!showBoxes && !showLabels) return;

            double dpi = VisualTreeHelper.GetDpi(_owner).PixelsPerDip;
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

            double scale = Math.Clamp(45.0 / Math.Max(_owner.CameraFov, 5.0), 0.35, 4.0);
            bool drawDenseLabels = _owner.CameraFov < 30.0;
            int drawnLabelsCount = 0;
            int drawnBoxesCount = 0;
            const int Max3DBoxesBudget = 10000;

            for (int i = 0; i < objects.Count; i++)
            {
                var obj = objects[i];
                if (obj.IsAnomaly) continue;

                if (_owner.CameraFov > 35.0 && obj.Tier >= ConfidenceTier.Tier3_Low) continue;
                if (_owner.CameraFov > 55.0 && obj.Tier == ConfidenceTier.FilteredNoise) continue;

                if (_owner.MosaicPanels != null && _owner.MosaicPanels.Count > 1 && !string.IsNullOrEmpty(obj.PanelId))
                {
                    var parentPanel = _owner.MosaicPanels.FirstOrDefault(p => p.Id == obj.PanelId);
                    if (parentPanel != null && parentPanel.TargetName == null) continue;
                }

                double targetRa = obj.RaDegrees ?? (wcs != null ? wcs.PixelToEquatorial(obj.CenterX, obj.CenterY).RaDegrees : 0);
                double targetDec = obj.DecDegrees ?? (wcs != null ? wcs.PixelToEquatorial(obj.CenterX, obj.CenterY).DecDegrees : 0);
                if (!_owner.CelestialToScreen(targetRa, targetDec, w, h, out double sx, out double sy)) continue;
                if (sx < -20 || sx > w + 20 || sy < -20 || sy > h + 20) continue;

                var (pen, brush) = obj.Tier switch
                {
                    ConfidenceTier.Tier1_High => (Tier1Pen, Tier1Brush),
                    ConfidenceTier.Tier2_Medium => (Tier2Pen, Tier2Brush),
                    ConfidenceTier.Tier3_Low => (Tier3Pen, Tier3Brush),
                    _ => (FilteredPen, FilteredNoiseBrush)
                };

                double halfW = Math.Max(3.0, (obj.Box.Width * 0.5) * scale);
                double halfH = Math.Max(3.0, (obj.Box.Height * 0.5) * scale);

                if (showBoxes && drawnBoxesCount < Max3DBoxesBudget)
                {
                    dc.DrawRectangle(null, pen, new Rect(sx - halfW, sy - halfH, halfW * 2.0, halfH * 2.0));
                    drawnBoxesCount++;
                }

                if (showLabels && (drawDenseLabels || obj.Tier == ConfidenceTier.Tier1_High || (obj.Tier == ConfidenceTier.Tier2_Medium && _owner.CameraFov < 45.0)))
                {
                    if (drawnLabelsCount < 250)
                    {
                        string text = $"{obj.ClassName} {(int)(obj.Confidence * 100)}%";
                        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 9.5, brush, dpi);

                        var bgRect = new Rect(sx - halfW, sy - halfH - ft.Height - 1, ft.Width + 4, ft.Height);
                        dc.DrawRoundedRectangle(BadgeBackground, null, bgRect, 2, 2);
                        dc.DrawText(ft, new Point(sx - halfW + 2, sy - halfH - ft.Height));
                        drawnLabelsCount++;
                    }
                }
            }
        }

        private static Pen MakePen(byte r, byte g, byte b, double thickness, byte a = 255)
        {
            var pen = new Pen(MakeBrush(r, g, b, a), thickness);
            pen.Freeze();
            return pen;
        }

        private static SolidColorBrush MakeBrush(byte r, byte g, byte b, byte a = 255)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}

