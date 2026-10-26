namespace AstroCounter.UI;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AstroCounter.UI.ViewModels;

public partial class MainWindow : Window
{
    private const double MinZoom = 0.02;
    private const double MaxZoom = 16.0;
    private const double InfiniteCanvasMargin = 25000.0;

    private Point _lastDragPoint;
    private bool _isPanning;
    private double _currentZoom = 1.0;
    private bool _isUpdatingZoomOrRotation;
    private Point _lastCenterPixel;

    private Point? _rectStart;
    private bool _isDrawingRect;
    private readonly List<Vector2> _polygonPoints = new();

    // Floating panel drag state
    private Border? _draggingPanel;
    private UIElement? _draggingHeader;
    private Point _panelDragStartMouse;
    private double _panelDragStartX;
    private double _panelDragStartY;
    private int _topFloatingZIndex = 130;

    // Drag-and-drop photo layer reordering state
    private Point _photoCardDragStart;
    private AstroCounter.Core.Astrometry.MosaicPanel? _draggedPhotoPanel;

    private MainViewModel? Vm => DataContext as MainViewModel;

    public MainWindow()
    {
        InitializeComponent();

        PreviewKeyDown += Window_PreviewKeyDown;
        Closed += (s, e) => Vm?.Dispose();

        if (Vm != null)
        {
            Vm.PropertyChanged += Vm_PropertyChanged;
            Vm.RequestCenterOnPixel += CenterOnPixel;
            Vm.RequestSetVisiblePixelsHeight += SetVisiblePixelsHeightFromStellarium;
            Vm.RequestSet3DCamera += (ra, dec, fov) => Dispatcher.Invoke(() => SphereControl.SetCamera(ra, dec, fov));
            Vm.RequestScrollToMosaicPanel += ScrollToMosaicPanel;
            Vm.RequestInvalidateOverlay += () =>
            {
                Overlay.InvalidateVisual();
                SphereControl.RebuildPhotoMeshes();
            };
        }

        SphereControl.CameraMoved += (ra, dec, fov) => Vm?.OnSphereCameraMoved(ra, dec, fov);
        SphereControl.PanelDoubleClicked += panel =>
        {
            if (panel != null)
            {
                bool multi = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
                Vm?.SelectAndHighlightPanel(panel, openDrawer: true, toggleMultiSelect: multi);
            }
            else
            {
                Vm?.ClearPanelSelection();
            }
        };

        ApplyToolCursor();
    }

    private void ScrollToMosaicPanel(AstroCounter.Core.Astrometry.MosaicPanel panel)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                MosaicCardsItemsControl.UpdateLayout();
                var container = MosaicCardsItemsControl.ItemContainerGenerator.ContainerFromItem(panel) as FrameworkElement;
                container?.BringIntoView();
            }
            catch { }
        }), DispatcherPriority.Loaded);
    }

    private void SetVisiblePixelsHeightFromStellarium(double targetVisiblePixelsHeight)
    {
        Dispatcher.Invoke(() =>
        {
            if (Vm?.ImageBitmap == null || targetVisiblePixelsHeight <= 1.0) return;
            double viewH = ViewerScrollViewer.ViewportHeight > 0 ? ViewerScrollViewer.ViewportHeight : ViewerScrollViewer.ActualHeight;
            if (viewH <= 10.0) return;

            double targetZoom = Math.Clamp(viewH / targetVisiblePixelsHeight, MinZoom, MaxZoom);
            if (Math.Abs(targetZoom - _currentZoom) / Math.Max(0.01, _currentZoom) < 0.01) return;

            Point keepCenter = _lastCenterPixel;
            _isUpdatingZoomOrRotation = true;
            try
            {
                SetZoom(targetZoom, updateViewport: false);
                ViewerScrollViewer.UpdateLayout();
                if (keepCenter.X != 0 || keepCenter.Y != 0)
                {
                    double viewW = ViewerScrollViewer.ViewportWidth > 0 ? ViewerScrollViewer.ViewportWidth : ViewerScrollViewer.ActualWidth;
                    Point targetInViewer = Overlay.TranslatePoint(keepCenter, ViewerScrollViewer);
                    ViewerScrollViewer.ScrollToHorizontalOffset(ViewerScrollViewer.HorizontalOffset + (targetInViewer.X - (viewW / 2.0)));
                    ViewerScrollViewer.ScrollToVerticalOffset(ViewerScrollViewer.VerticalOffset + (targetInViewer.Y - (viewH / 2.0)));
                }
            }
            finally
            {
                _isUpdatingZoomOrRotation = false;
            }
            UpdateViewport(notifyStellarium: false);
        });
    }

    private void CenterOnPixel(double px, double py)
    {
        Dispatcher.Invoke(() =>
        {
            if (Vm == null || (Vm.ImageBitmap == null && Vm.MosaicProject.Count == 0)) return;
            _isUpdatingZoomOrRotation = true;
            try
            {
                ViewerScrollViewer.UpdateLayout();
                double viewW = ViewerScrollViewer.ViewportWidth > 0 ? ViewerScrollViewer.ViewportWidth : ViewerScrollViewer.ActualWidth;
                double viewH = ViewerScrollViewer.ViewportHeight > 0 ? ViewerScrollViewer.ViewportHeight : ViewerScrollViewer.ActualHeight;
                Point targetInViewer = Overlay.TranslatePoint(new Point(px, py), ViewerScrollViewer);
                double deltaX = targetInViewer.X - (viewW / 2.0);
                double deltaY = targetInViewer.Y - (viewH / 2.0);
                ViewerScrollViewer.ScrollToHorizontalOffset(ViewerScrollViewer.HorizontalOffset + deltaX);
                ViewerScrollViewer.ScrollToVerticalOffset(ViewerScrollViewer.VerticalOffset + deltaY);
                _lastCenterPixel = new Point(px, py);
            }
            finally
            {
                _isUpdatingZoomOrRotation = false;
            }
            UpdateViewport(notifyStellarium: false);
        });
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName))
        {
            SphereControl.RefreshLocalization();
            return;
        }
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ImageBitmap):
                CancelDrawing();
                if (Vm != null && (Vm.ImageBitmap != null || Vm.MosaicProject.Count > 0))
                {
                    CanvasContainer.Margin = new Thickness(InfiniteCanvasMargin);
                    _lastCenterPixel = new Point(Vm.CanvasWidth / 2.0, Vm.CanvasHeight / 2.0);
                }
                else
                {
                    CanvasContainer.Margin = new Thickness(0);
                }
                // Wait for layout so the viewport size is known, then fit the new image to the window.
                Dispatcher.BeginInvoke(new Action(FitToView), DispatcherPriority.Loaded);
                break;
            case nameof(MainViewModel.ImageRotationAngle):
                if (Vm?.ImageBitmap != null)
                {
                    Point keepCenter = _lastCenterPixel;
                    Dispatcher.BeginInvoke(new Action(() => CenterOnPixel(keepCenter.X, keepCenter.Y)), DispatcherPriority.Loaded);
                }
                break;
            case nameof(MainViewModel.Tool):
                CancelDrawing();
                ApplyToolCursor();
                break;
            case nameof(MainViewModel.Is2DViewActive):
                if (Vm != null && Vm.Is2DViewActive)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        FitToView();
                        Overlay.InvalidateVisual();
                    }), DispatcherPriority.Loaded);
                }
                break;
            case nameof(MainViewModel.CanvasWidth):
            case nameof(MainViewModel.CanvasHeight):
            case nameof(MainViewModel.MosaicPanels):
                if (Vm != null)
                {
                    CanvasContainer.Margin = (Vm.ImageBitmap != null || Vm.MosaicProject.Count > 0)
                        ? new Thickness(InfiniteCanvasMargin)
                        : new Thickness(0);
                }
                if (Vm != null && Vm.Is2DViewActive)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (Vm.MosaicProject.Count > 1)
                        {
                            FitToView();
                        }
                        Overlay.InvalidateVisual();
                    }), DispatcherPriority.Loaded);
                }
                break;
        }
    }

    private void ApplyToolCursor()
    {
        ViewerScrollViewer.Cursor = Vm?.Tool == ScanTool.Pan || Vm == null ? Cursors.Arrow : Cursors.Cross;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0)
            {
                var validFiles = files.Where(f => {
                    string x = Path.GetExtension(f).ToLowerInvariant();
                    return x is ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".bmp" or ".fits" or ".fit";
                }).ToList();

                if (validFiles.Count > 0)
                {
                    if (DataContext is MainViewModel vm)
                    {
                        _ = vm.LoadImagesAsync(validFiles);
                    }
                }
                else
                {
                    bool tr = AstroCounter.Core.Astrometry.CataloguePlateSolver.UseTurkishLanguage;
                    MessageBox.Show(
                        tr
                            ? "Lütfen geçerli bir görsel dosyası sürükleyin (PNG, JPG, TIFF, BMP, FITS)."
                            : "Please drop a valid image file (PNG, JPG, TIFF, BMP, FITS).",
                        tr ? "Desteklenmeyen Dosya" : "Unsupported File",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
        }
    }

    // ---------------------------------------------------------------- zoom / pan

    private void SetZoom(double zoom, bool updateViewport = true)
    {
        _currentZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        CanvasScale.ScaleX = _currentZoom;
        CanvasScale.ScaleY = _currentZoom;
        ZoomPercentText.Text = $"{(int)Math.Round(_currentZoom * 100)}%";
        Overlay.Zoom = _currentZoom;
        if (updateViewport)
        {
            UpdateViewport(notifyStellarium: false);
        }
    }

    private void UpdateViewport(bool notifyStellarium = false)
    {
        if (_isUpdatingZoomOrRotation) return;

        double z = _currentZoom;
        double viewW = ViewerScrollViewer.ViewportWidth > 0 ? ViewerScrollViewer.ViewportWidth : ViewerScrollViewer.ActualWidth;
        double viewH = ViewerScrollViewer.ViewportHeight > 0 ? ViewerScrollViewer.ViewportHeight : ViewerScrollViewer.ActualHeight;

        double maxW = Vm != null && Vm.CanvasWidth > 10 ? Vm.CanvasWidth : (Vm?.ImageWidth ?? 0);
        double maxH = Vm != null && Vm.CanvasHeight > 10 ? Vm.CanvasHeight : (Vm?.ImageHeight ?? 0);

        if (Vm != null && maxW > 0 && maxH > 0 && viewW > 0 && viewH > 0)
        {
            Point viewCenter = new(viewW / 2.0, viewH / 2.0);
            Point centerInImage = ViewerScrollViewer.TranslatePoint(viewCenter, Overlay);
            double centerPxX = centerInImage.X;
            double centerPxY = centerInImage.Y;
            _lastCenterPixel = new Point(centerPxX, centerPxY);

            double visibleW = viewW / z;
            double visibleH = viewH / z;
            double diag = Math.Sqrt((visibleW * visibleW) + (visibleH * visibleH));
            Overlay.Viewport = new Rect(
                centerPxX - (diag / 2.0),
                centerPxY - (diag / 2.0),
                diag,
                diag);

            Vm.OnViewportMoved(centerPxX, centerPxY, visibleH, notifyStellarium);
        }
    }

    private void FitToView()
    {
        var vm = Vm;
        if (vm == null) return;
        double targetW = vm.MosaicProject.Count > 1 && vm.CanvasWidth > 10 ? vm.CanvasWidth : vm.ImageWidth;
        double targetH = vm.MosaicProject.Count > 1 && vm.CanvasHeight > 10 ? vm.CanvasHeight : vm.ImageHeight;
        if (targetW <= 0 || targetH <= 0) return;

        double viewW = ViewerScrollViewer.ViewportWidth > 0 ? ViewerScrollViewer.ViewportWidth : ViewerScrollViewer.ActualWidth;
        double viewH = ViewerScrollViewer.ViewportHeight > 0 ? ViewerScrollViewer.ViewportHeight : ViewerScrollViewer.ActualHeight;
        if (viewW <= 0 || viewH <= 0) return;

        _lastCenterPixel = new Point(targetW / 2.0, targetH / 2.0);
        SetZoom(Math.Min(viewW / targetW, viewH / targetH) * 0.92, updateViewport: false);
        CenterOnPixel(_lastCenterPixel.X, _lastCenterPixel.Y);
        UpdateViewport(notifyStellarium: false);
    }

    private void ViewerScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isUpdatingZoomOrRotation) return;
        UpdateViewport(notifyStellarium: _isPanning);
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm?.ImageBitmap == null && (Vm?.MosaicProject == null || Vm.MosaicProject.Count == 0)) return;

        double zoomFactor = e.Delta > 0 ? 1.18 : 1.0 / 1.18;
        double newZoom = Math.Clamp(_currentZoom * zoomFactor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _currentZoom) < 1e-6)
        {
            e.Handled = true;
            return;
        }

        Point mouseInViewer = e.GetPosition(ViewerScrollViewer);
        Point pixelUnderMouse = e.GetPosition(Overlay);

        _isUpdatingZoomOrRotation = true;
        try
        {
            SetZoom(newZoom, updateViewport: false);

            double targetHOffset = (pixelUnderMouse.X * newZoom) - mouseInViewer.X;
            double targetVOffset = (pixelUnderMouse.Y * newZoom) - mouseInViewer.Y;

            ViewerScrollViewer.ScrollToHorizontalOffset(Math.Max(0, targetHOffset));
            ViewerScrollViewer.ScrollToVerticalOffset(Math.Max(0, targetVOffset));
        }
        finally
        {
            _isUpdatingZoomOrRotation = false;
        }

        UpdateViewport(notifyStellarium: false);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- mouse: pan and region drawing

    private Point ClampToImage(Point p) =>
        new(Math.Clamp(p.X, 0, Overlay.ActualWidth), Math.Clamp(p.Y, 0, Overlay.ActualHeight));

    private void ScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var vm = Vm;
        if (vm?.ImageBitmap == null && (vm?.MosaicProject == null || vm.MosaicProject.Count == 0)) return;

        bool overImage = Overlay.IsMouseOver;

        // Double-click in Pan mode selects/toggles photo in the Photos drawer, or clears selection when double-clicking empty space
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2 && vm.Tool == ScanTool.Pan)
        {
            Point canvasPt = e.GetPosition(Overlay);
            var hitPanel = Overlay.HitTestPanelAtCanvasPoint(canvasPt);
            if (hitPanel != null)
            {
                bool multi = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
                vm.SelectAndHighlightPanel(hitPanel, openDrawer: true, toggleMultiSelect: multi);
            }
            else
            {
                vm.ClearPanelSelection();
            }
            e.Handled = true;
            return;
        }

        bool leftPan = e.ChangedButton == MouseButton.Left &&
                       (vm.Tool == ScanTool.Pan || Keyboard.Modifiers == ModifierKeys.Alt);

        if (e.ChangedButton == MouseButton.Middle || leftPan)
        {
            _lastDragPoint = e.GetPosition(ViewerScrollViewer);
            _isPanning = true;
            ViewerScrollViewer.Cursor = Cursors.SizeAll;
            ViewerScrollViewer.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (!overImage) return;

        if (vm.Tool == ScanTool.Rectangle && e.ChangedButton == MouseButton.Left)
        {
            _rectStart = ClampToImage(e.GetPosition(Overlay));
            _isDrawingRect = true;
            Overlay.CaptureMouse();
            UpdateRectanglePreview(_rectStart.Value);
            e.Handled = true;
        }
        else if (vm.Tool == ScanTool.Polygon)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                Point p = ClampToImage(e.GetPosition(Overlay));

                if (e.ClickCount >= 2)
                {
                    if (_polygonPoints.Count >= 3) FinishPolygon();
                }
                else if (_polygonPoints.Count >= 3 && Distance(_polygonPoints[0], p) < 10.0 / _currentZoom)
                {
                    FinishPolygon();
                }
                else
                {
                    _polygonPoints.Add(new Vector2((float)p.X, (float)p.Y));
                    UpdatePolygonPreview(p);
                }
                e.Handled = true;
            }
            else if (e.ChangedButton == MouseButton.Right)
            {
                if (_polygonPoints.Count >= 3) FinishPolygon();
                else CancelDrawing();
                e.Handled = true;
            }
        }
    }

    private void ScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning && Vm != null && Vm.CanvasWidth > 0 && Vm.CanvasHeight > 0)
        {
            Point p = e.GetPosition(Overlay);
            if (p.X >= 0 && p.X <= Vm.CanvasWidth && p.Y >= 0 && p.Y <= Vm.CanvasHeight)
            {
                Vm.OnMouseMoveOnImage(p.X, p.Y);
            }
        }

        if (_isPanning)
        {
            var currentPoint = e.GetPosition(ViewerScrollViewer);
            var delta = _lastDragPoint - currentPoint;

            ViewerScrollViewer.ScrollToHorizontalOffset(ViewerScrollViewer.HorizontalOffset + delta.X);
            ViewerScrollViewer.ScrollToVerticalOffset(ViewerScrollViewer.VerticalOffset + delta.Y);

            _lastDragPoint = currentPoint;
        }
        else if (_isDrawingRect)
        {
            UpdateRectanglePreview(ClampToImage(e.GetPosition(Overlay)));
        }
        else if (Vm?.Tool == ScanTool.Polygon && _polygonPoints.Count > 0)
        {
            UpdatePolygonPreview(ClampToImage(e.GetPosition(Overlay)));
        }
    }

    private void ScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            ApplyToolCursor();
            ViewerScrollViewer.ReleaseMouseCapture();
            return;
        }

        if (_isDrawingRect && e.ChangedButton == MouseButton.Left && _rectStart.HasValue)
        {
            Point end = ClampToImage(e.GetPosition(Overlay));
            Point start = _rectStart.Value;

            Overlay.ReleaseMouseCapture();
            _isDrawingRect = false;
            _rectStart = null;
            Overlay.ClearPreview();

            double x0 = Math.Min(start.X, end.X);
            double y0 = Math.Min(start.Y, end.Y);
            double x1 = Math.Max(start.X, end.X);
            double y1 = Math.Max(start.Y, end.Y);

            if (x1 - x0 >= 3 && y1 - y0 >= 3)
            {
                Vm?.AddRegion(new[]
                {
                    new Vector2((float)x0, (float)y0),
                    new Vector2((float)x1, (float)y0),
                    new Vector2((float)x1, (float)y1),
                    new Vector2((float)x0, (float)y1)
                });
            }
            e.Handled = true;
        }
    }

    private void UpdateRectanglePreview(Point current)
    {
        if (!_rectStart.HasValue || Vm == null) return;

        Point s = _rectStart.Value;
        var corners = new[]
        {
            new Vector2((float)s.X, (float)s.Y),
            new Vector2((float)current.X, (float)s.Y),
            new Vector2((float)current.X, (float)current.Y),
            new Vector2((float)s.X, (float)current.Y)
        };
        Overlay.SetPreview(corners, null, Vm.CurrentRegionKind, isRectangle: true);
    }

    private void UpdatePolygonPreview(Point cursor)
    {
        if (Vm == null) return;

        Overlay.SetPreview(_polygonPoints.ToArray(), new Vector2((float)cursor.X, (float)cursor.Y), Vm.CurrentRegionKind, isRectangle: false);
    }

    private void FinishPolygon()
    {
        if (_polygonPoints.Count >= 3)
        {
            Vm?.AddRegion(_polygonPoints.ToArray());
        }
        _polygonPoints.Clear();
        Overlay.ClearPreview();
    }

    private void CancelDrawing()
    {
        _polygonPoints.Clear();
        _rectStart = null;
        if (_isDrawingRect)
        {
            _isDrawingRect = false;
            Overlay.ReleaseMouseCapture();
        }
        Overlay.ClearPreview();
    }

    private static double Distance(Vector2 a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (Vm != null && Vm.IsHelpPanelOpen)
            {
                Vm.IsHelpPanelOpen = false;
                e.Handled = true;
                return;
            }
            if (Vm != null && Vm.IsExportPanelOpen)
            {
                Vm.IsExportPanelOpen = false;
                e.Handled = true;
                return;
            }
            CancelDrawing();
            e.Handled = true;
        }
        else if (e.Key == Key.F1)
        {
            if (Vm != null)
            {
                Vm.IsHelpPanelOpen = !Vm.IsHelpPanelOpen;
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Enter && _polygonPoints.Count >= 3)
        {
            FinishPolygon();
            e.Handled = true;
        }
        else if (e.Key == Key.B && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (Vm != null)
            {
                Vm.ToggleInspectorCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.O && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (Vm != null && Vm.SelectImageCommand.CanExecute(null))
            {
                Vm.SelectImageCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.E && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (Vm != null && Vm.ToggleExportPanelCommand.CanExecute(null))
            {
                Vm.ToggleExportPanelCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.F11)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
        }
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e)
    {
        Point keepCenter = _lastCenterPixel;
        SetZoom(1.0, updateViewport: false);
        if (keepCenter.X > 0 && keepCenter.Y > 0)
        {
            CenterOnPixel(keepCenter.X, keepCenter.Y);
        }
        UpdateViewport(notifyStellarium: true);
    }

    private void FitZoom_Click(object sender, RoutedEventArgs e)
    {
        FitToView();
        UpdateViewport(notifyStellarium: true);
    }

    // ---------------------------------------------------------------- draggable floating panels

    private static Border? FindParentFloatingBorder(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is Border b && b.RenderTransform is System.Windows.Media.TranslateTransform)
            {
                return b;
            }
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void FloatingPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border panelBorder)
        {
            Panel.SetZIndex(panelBorder, ++_topFloatingZIndex);
        }
    }

    private void FloatingPanelHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement header) return;
        var panelBorder = FindParentFloatingBorder(header);
        if (panelBorder == null || panelBorder.RenderTransform is not System.Windows.Media.TranslateTransform tt) return;

        Panel.SetZIndex(panelBorder, ++_topFloatingZIndex);
        _draggingPanel = panelBorder;
        _draggingHeader = header;
        _panelDragStartMouse = e.GetPosition(this);
        _panelDragStartX = tt.X;
        _panelDragStartY = tt.Y;
        header.CaptureMouse();
        e.Handled = true;
    }

    private void FloatingPanelHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingPanel == null || _draggingHeader == null || e.LeftButton != MouseButtonState.Pressed)
            return;

        if (_draggingPanel.RenderTransform is not System.Windows.Media.TranslateTransform tt)
            return;

        Point currentMouse = e.GetPosition(this);
        double deltaX = currentMouse.X - _panelDragStartMouse.X;
        double deltaY = currentMouse.Y - _panelDragStartMouse.Y;

        double newX = _panelDragStartX + deltaX;
        double newY = _panelDragStartY + deltaY;

        if (_draggingPanel.Parent is FrameworkElement parentContainer && parentContainer.ActualWidth > 50 && parentContainer.ActualHeight > 50)
        {
            double panelW = _draggingPanel.ActualWidth > 10 ? _draggingPanel.ActualWidth : _draggingPanel.Width;
            double panelH = _draggingPanel.ActualHeight > 10 ? _draggingPanel.ActualHeight : 260;

            double baseLeft = _draggingPanel.HorizontalAlignment switch
            {
                HorizontalAlignment.Right => parentContainer.ActualWidth - panelW - _draggingPanel.Margin.Right,
                HorizontalAlignment.Center => (parentContainer.ActualWidth - panelW) / 2.0,
                _ => _draggingPanel.Margin.Left
            };

            double baseTop = _draggingPanel.VerticalAlignment switch
            {
                VerticalAlignment.Bottom => parentContainer.ActualHeight - panelH - _draggingPanel.Margin.Bottom,
                VerticalAlignment.Center => (parentContainer.ActualHeight - panelH) / 2.0,
                _ => _draggingPanel.Margin.Top
            };

            double minX = -baseLeft + 6;
            double maxX = Math.Max(minX, parentContainer.ActualWidth - panelW - baseLeft - 6);
            double minY = -baseTop + 6;
            double maxY = Math.Max(minY, parentContainer.ActualHeight - Math.Min(panelH, parentContainer.ActualHeight - 12) - baseTop - 6);

            newX = Math.Clamp(newX, minX, maxX);
            newY = Math.Clamp(newY, minY, maxY);
        }

        tt.X = newX;
        tt.Y = newY;
        e.Handled = true;
    }

    private void FloatingPanelHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingHeader != null)
        {
            _draggingHeader.ReleaseMouseCapture();
            _draggingHeader = null;
            _draggingPanel = null;
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- drag-and-drop photo layer reordering

    private void PhotoCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep)
        {
            var cur = dep;
            while (cur != null && cur != sender)
            {
                if (cur is Slider || cur is System.Windows.Controls.Primitives.Thumb || cur is CheckBox)
                {
                    _draggedPhotoPanel = null;
                    return;
                }
                cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
            }
        }

        if (sender is Border border && border.Tag is AstroCounter.Core.Astrometry.MosaicPanel panel)
        {
            _photoCardDragStart = e.GetPosition(null);
            _draggedPhotoPanel = panel;
        }
    }

    private void PhotoCard_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && _draggedPhotoPanel != null && sender is Border border)
        {
            Point curPos = e.GetPosition(null);
            System.Windows.Vector diff = _photoCardDragStart - curPos;
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                var panelToDrag = _draggedPhotoPanel;
                _draggedPhotoPanel = null;
                DragDrop.DoDragDrop(border, new DataObject("AstroMosaicPanel", panelToDrag), DragDropEffects.Move);
            }
        }
    }

    private void PhotoCard_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("AstroMosaicPanel"))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void PhotoCard_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("AstroMosaicPanel") &&
            e.Data.GetData("AstroMosaicPanel") is AstroCounter.Core.Astrometry.MosaicPanel sourcePanel &&
            sender is Border targetBorder &&
            targetBorder.Tag is AstroCounter.Core.Astrometry.MosaicPanel targetPanel)
        {
            if (!ReferenceEquals(sourcePanel, targetPanel))
            {
                Vm?.MoveMosaicPanelTo(sourcePanel, targetPanel);
            }
            e.Handled = true;
        }
    }
}