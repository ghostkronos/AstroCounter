namespace AstroCounter.UI;

using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using AstroCounter.UI.ViewModels;

public partial class MainWindow : Window
{
    private Point _lastDragPoint;
    private bool _isPanning;
    private double _currentZoom = 1.0;

    public MainWindow()
    {
        InitializeComponent();
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

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control || !ViewerScrollViewer.IsMouseOver)
        {
            // Zoom in or out
            double zoomFactor = e.Delta > 0 ? 1.15 : 0.85;
            _currentZoom = Math.Clamp(_currentZoom * zoomFactor, 0.1, 10.0);
            CanvasScale.ScaleX = _currentZoom;
            CanvasScale.ScaleY = _currentZoom;
            ZoomPercentText.Text = $"{(int)(_currentZoom * 100)}%";
            e.Handled = true;
        }
    }

    private void ScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.MiddleButton == MouseButtonState.Pressed || (e.LeftButton == MouseButtonState.Pressed && Keyboard.Modifiers == ModifierKeys.Alt))
        {
            _lastDragPoint = e.GetPosition(ViewerScrollViewer);
            _isPanning = true;
            ViewerScrollViewer.Cursor = Cursors.SizeAll;
            ViewerScrollViewer.CaptureMouse();
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
    }

    private void ScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            ViewerScrollViewer.Cursor = Cursors.Arrow;
            ViewerScrollViewer.ReleaseMouseCapture();
        }
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e)
    {
        _currentZoom = 1.0;
        CanvasScale.ScaleX = 1.0;
        CanvasScale.ScaleY = 1.0;
        ZoomPercentText.Text = "100%";
    }
}