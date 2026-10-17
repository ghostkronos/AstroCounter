namespace AstroCounter.UI.ViewModels;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using AstroCounter.Core.Export;
using AstroCounter.Core.Inference;
using AstroCounter.Core.Models;
using Microsoft.Win32;

public class MainViewModel : INotifyPropertyChanged
{
    private string? _selectedImagePath;
    private BitmapSource? _imageBitmap;
    private double _imageWidth;
    private double _imageHeight;
    private float _confidenceThreshold = 0.25f;
    private bool _isLoading;
    private double _progressValue;
    private string _statusMessage = "Analiz edilecek astrofotoğrafı sürükleyip bırakın veya 'Görsel Seç'e tıklayın.";
    private string _hardwareInfo = "Hazır";
    private string _processingTimeText = "-";
    private string _tileCountText = "-";
    private int _totalCount;
    private int _tier1Count;
    private int _tier2Count;
    private int _tier3Count;
    private bool _showBoundingBoxes = true;
    private bool _showLabels = true;
    private string? _modelPath;

    private DetectionResult? _lastDetectionResult;
    private IInferenceEngine? _engine;

    public ObservableCollection<VisualBoxViewModel> VisibleBoxes { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel()
    {
        // Try locating bundled or repository ONNX model
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string candidateModel = Path.Combine(appDir, "models", "astro_yolo11n.onnx");
        if (!File.Exists(candidateModel))
        {
            // Try relative development path
            string devModel = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "models", "astro_yolo11n.onnx"));
            if (File.Exists(devModel))
            {
                candidateModel = devModel;
            }
        }

        if (File.Exists(candidateModel))
        {
            _modelPath = candidateModel;
        }

        InitializeEngine();

        SelectImageCommand = new RelayCommand(ExecuteSelectImage);
        AnalyzeCommand = new RelayCommand(async () => await ExecuteAnalyzeAsync(), () => !IsLoading && !string.IsNullOrEmpty(SelectedImagePath));
        ExportCsvCommand = new RelayCommand(ExecuteExportCsv, () => _lastDetectionResult != null && TotalCount > 0);
        ExportImageCommand = new RelayCommand(ExecuteExportImage, () => _lastDetectionResult != null && !string.IsNullOrEmpty(SelectedImagePath));
    }

    private void InitializeEngine()
    {
        try
        {
            _engine?.Dispose();
            _engine = new OnnxInferenceEngine(_modelPath);
            HardwareInfo = _engine.ExecutionProviderName;
        }
        catch (Exception ex)
        {
            HardwareInfo = $"Hata: {ex.Message}";
        }
    }

    public string? SelectedImagePath
    {
        get => _selectedImagePath;
        set
        {
            if (SetProperty(ref _selectedImagePath, value))
            {
                if (!string.IsNullOrEmpty(value) && File.Exists(value))
                {
                    LoadBitmapPreview(value);
                    _ = ExecuteAnalyzeAsync();
                }
            }
        }
    }

    public BitmapSource? ImageBitmap
    {
        get => _imageBitmap;
        private set
        {
            if (SetProperty(ref _imageBitmap, value))
            {
                OnPropertyChanged(nameof(IsPlaceholderVisible));
            }
        }
    }

    public bool IsPlaceholderVisible => ImageBitmap == null;

    public double ImageWidth
    {
        get => _imageWidth;
        private set => SetProperty(ref _imageWidth, value);
    }

    public double ImageHeight
    {
        get => _imageHeight;
        private set => SetProperty(ref _imageHeight, value);
    }

    public float ConfidenceThreshold
    {
        get => _confidenceThreshold;
        set
        {
            if (SetProperty(ref _confidenceThreshold, (float)Math.Round(value, 2)))
            {
                OnPropertyChanged(nameof(ConfidencePercentText));
                ApplyLiveFilter();
            }
        }
    }

    public string ConfidencePercentText => $"%{(int)(_confidenceThreshold * 100)}";

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public double ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string HardwareInfo
    {
        get => _hardwareInfo;
        private set => SetProperty(ref _hardwareInfo, value);
    }

    public string ProcessingTimeText
    {
        get => _processingTimeText;
        private set => SetProperty(ref _processingTimeText, value);
    }

    public string TileCountText
    {
        get => _tileCountText;
        private set => SetProperty(ref _tileCountText, value);
    }

    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    public int Tier1Count
    {
        get => _tier1Count;
        private set => SetProperty(ref _tier1Count, value);
    }

    public int Tier2Count
    {
        get => _tier2Count;
        private set => SetProperty(ref _tier2Count, value);
    }

    public int Tier3Count
    {
        get => _tier3Count;
        private set => SetProperty(ref _tier3Count, value);
    }

    public bool ShowBoundingBoxes
    {
        get => _showBoundingBoxes;
        set
        {
            if (SetProperty(ref _showBoundingBoxes, value))
            {
                ApplyLiveFilter();
            }
        }
    }

    public bool ShowLabels
    {
        get => _showLabels;
        set
        {
            if (SetProperty(ref _showLabels, value))
            {
                ApplyLiveFilter();
            }
        }
    }

    public ICommand SelectImageCommand { get; }
    public ICommand AnalyzeCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportImageCommand { get; }

    private void LoadBitmapPreview(string filePath)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            ImageBitmap = bitmap;
            ImageWidth = bitmap.PixelWidth;
            ImageHeight = bitmap.PixelHeight;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Görsel yüklenirken hata: {ex.Message}";
        }
    }

    public async Task ExecuteAnalyzeAsync()
    {
        if (string.IsNullOrEmpty(SelectedImagePath) || !File.Exists(SelectedImagePath))
            return;

        IsLoading = true;
        ProgressValue = 0;
        StatusMessage = "Görüntü 640x640 dilimleniyor ve çıkarım yapılıyor...";

        var progress = new Progress<float>(percent =>
        {
            ProgressValue = percent * 100.0;
            StatusMessage = $"Dilimler işleniyor: %{(int)(percent * 100)}";
        });

        try
        {
            _engine ??= new OnnxInferenceEngine(_modelPath);
            HardwareInfo = _engine.ExecutionProviderName;

            var config = new TilingConfig(DefaultConfidenceThreshold: 0.10f, MinConfidence: 0.10f);
            var result = await _engine.ProcessImageAsync(SelectedImagePath, config, progress);

            _lastDetectionResult = result;
            ProcessingTimeText = $"{result.ElapsedTime.TotalMilliseconds:F0} ms";
            TileCountText = $"{result.TileCount} Dilim";

            ApplyLiveFilter();

            StatusMessage = $"Tamamlandı! {TotalCount} obje tespit edildi ({ProcessingTimeText}).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Analiz hatası: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            ProgressValue = 100;
        }
    }

    /// <summary>
    /// Filters detected objects in RAM according to ConfidenceThreshold slider without re-running AI.
    /// </summary>
    public void ApplyLiveFilter()
    {
        if (_lastDetectionResult == null) return;

        var filtered = _lastDetectionResult.Filter(_confidenceThreshold);

        TotalCount = filtered.TotalCount;
        Tier1Count = filtered.Tier1Count;
        Tier2Count = filtered.Tier2Count;
        Tier3Count = filtered.Tier3Count;

        VisibleBoxes.Clear();
        if (ShowBoundingBoxes)
        {
            foreach (var obj in filtered.FilteredObjects)
            {
                string label = ShowLabels ? $"{obj.ClassName} {(int)(obj.Confidence * 100)}%" : "";
                VisibleBoxes.Add(new VisualBoxViewModel(
                    Id: obj.Id,
                    Left: obj.Box.X,
                    Top: obj.Box.Y,
                    Width: Math.Max(4, obj.Box.Width),
                    Height: Math.Max(4, obj.Box.Height),
                    ColorHex: obj.TierColorHex,
                    LabelText: label,
                    Confidence: obj.Confidence,
                    Tier: obj.Tier
                ));
            }
        }

        CommandManager.InvalidateRequerySuggested();
    }

    private void ExecuteSelectImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Astrofotoğraf Seç",
            Filter = "Desteklenen Görseller (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp|Tüm Dosyalar (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            SelectedImagePath = dialog.FileName;
        }
    }

    private void ExecuteExportCsv()
    {
        if (_lastDetectionResult == null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Sayım Sonuçlarını CSV Olarak Kaydet",
            Filter = "CSV Dosyası (*.csv)|*.csv",
            FileName = $"AstroSayim_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        if (dialog.ShowDialog() == true)
        {
            var filtered = _lastDetectionResult.Filter(_confidenceThreshold);
            ResultExporter.ExportToCsv(filtered.FilteredObjects, dialog.FileName);
            MessageBox.Show($"CSV başarıyla kaydedildi:\n{dialog.FileName}", "Dışa Aktarma Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ExecuteExportImage()
    {
        if (_lastDetectionResult == null || string.IsNullOrEmpty(SelectedImagePath)) return;

        var dialog = new SaveFileDialog
        {
            Title = "İşlenmiş Görseli Kaydet",
            Filter = "PNG Görseli (*.png)|*.png|JPEG Görseli (*.jpg)|*.jpg",
            FileName = $"AstroDetected_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };

        if (dialog.ShowDialog() == true)
        {
            var filtered = _lastDetectionResult.Filter(_confidenceThreshold);
            ResultExporter.ExportAnnotatedImage(SelectedImagePath, filtered.FilteredObjects, dialog.FileName);
            MessageBox.Show($"Etiketli görsel kaydedildi:\n{dialog.FileName}", "Dışa Aktarma Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return false;

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

