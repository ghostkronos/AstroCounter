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

    private Point _lastDragPoint;
    private bool _isPanning;
    private double _currentZoom = 1.0;

    private Point? _rectStart;
    private bool _isDrawingRect;
    private readonly List<Vector2> _polygonPoints = new();

    private MainViewModel? Vm => DataContext as MainViewModel;

    public MainWindow()
    {
        InitializeComponent();

        PreviewKeyDown += Window_PreviewKeyDown;

        if (Vm != null)
        {
            Vm.PropertyChanged += Vm_PropertyChanged;
        }
        ApplyToolCursor();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ImageBitmap):
                CancelDrawing();
                // Wait for layout so the viewport size is known, then fit the new image to the window.
                Dispatcher.BeginInvoke(new Action(FitToView), DispatcherPriority.Loaded);
                break;
            case nameof(MainViewModel.Tool):
                CancelDrawing();
                ApplyToolCursor();
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
                string firstFile = files[0];
                string ext = Path.GetExtension(firstFile).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".bmp" or ".fits" or ".fit")
                {
                    if (DataContext is MainViewModel vm)
                    {
                        vm.SelectedImagePath = firstFile;
                    }
                }
                else
                {
                    MessageBox.Show("Lütfen geçerli bir görsel dosyası sürükleyin (PNG, JPG, TIFF, BMP, FITS).", "Desteklenmeyen Dosya", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
    }

    // ---------------------------------------------------------------- zoom / pan

    private void SetZoom(double zoom)
    {
        _currentZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        CanvasScale.ScaleX = _currentZoom;
        CanvasScale.ScaleY = _currentZoom;
        ZoomPercentText.Text = $"{(int)Math.Round(_currentZoom * 100)}%";
        Overlay.Zoom = _currentZoom;
        UpdateViewport();
    }

    private void UpdateViewport()
    {
        double z = _currentZoom;
        Overlay.Viewport = new Rect(
            ViewerScrollViewer.HorizontalOffset / z,
            ViewerScrollViewer.VerticalOffset / z,
            ViewerScrollViewer.ViewportWidth / z,
            ViewerScrollViewer.ViewportHeight / z);
    }

    private void FitToView()
    {
        var vm = Vm;
        if (vm == null || vm.ImageWidth <= 0 || vm.ImageHeight <= 0) return;

        double viewW = ViewerScrollViewer.ViewportWidth > 0 ? ViewerScrollViewer.ViewportWidth : ViewerScrollViewer.ActualWidth;
        double viewH = ViewerScrollViewer.ViewportHeight > 0 ? ViewerScrollViewer.ViewportHeight : ViewerScrollViewer.ActualHeight;
        if (viewW <= 0 || viewH <= 0) return;

        SetZoom(Math.Min(viewW / vm.ImageWidth, viewH / vm.ImageHeight) * 0.98);
    }

    private void ViewerScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateViewport();
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm?.ImageBitmap == null) return;

        // Zoom in or out around the mouse cursor
        double zoomFactor = e.Delta > 0 ? 1.2 : 1.0 / 1.2;
        Point anchorInViewer = e.GetPosition(ViewerScrollViewer);
        Point anchorInImage = e.GetPosition(CanvasContainer);

        SetZoom(_currentZoom * zoomFactor);
        ViewerScrollViewer.UpdateLayout();
        ViewerScrollViewer.ScrollToHorizontalOffset((anchorInImage.X * _currentZoom) - anchorInViewer.X);
        ViewerScrollViewer.ScrollToVerticalOffset((anchorInImage.Y * _currentZoom) - anchorInViewer.Y);

        e.Handled = true;
    }

    // ---------------------------------------------------------------- mouse: pan and region drawing

    private Point ClampToImage(Point p) =>
        new(Math.Clamp(p.X, 0, Overlay.ActualWidth), Math.Clamp(p.Y, 0, Overlay.ActualHeight));

    private void ScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var vm = Vm;
        if (vm?.ImageBitmap == null) return;

        bool overImage = Overlay.IsMouseOver;
        bool leftPan = e.ChangedButton == MouseButton.Left && overImage &&
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
            CancelDrawing();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _polygonPoints.Count >= 3)
        {
            FinishPolygon();
            e.Handled = true;
        }
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(1.0);
    }

    private void FitZoom_Click(object sender, RoutedEventArgs e)
    {
        FitToView();
    }
}