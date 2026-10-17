namespace AstroCounter.UI.ViewModels;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AstroCounter.Core.Export;
using AstroCounter.Core.Inference;
using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using Microsoft.Win32;
using SkiaSharp;

public enum ScanTool
{
    Pan,
    Rectangle,
    Polygon
}

public class MainViewModel : INotifyPropertyChanged
{
    private string? _selectedImagePath;
    private BitmapSource? _imageBitmap;
    private double _imageWidth;
    private double _imageHeight;
    private float _confidenceThreshold = 0.50f;
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
    private int _speculativeCount;
    private bool _showBoundingBoxes = true;
    private bool _showLabels = true;
    private string? _modelPath;

    private ScanTool _tool = ScanTool.Pan;
    private RegionKind _regionKind = RegionKind.Exclude;
    private readonly ScanMask _mask = new();
    private IReadOnlyList<SelectionRegion> _regionSnapshot = Array.Empty<SelectionRegion>();
    private IReadOnlyList<AstrophotographyObject> _visibleObjects = Array.Empty<AstrophotographyObject>();

    private DetectionResult? _lastDetectionResult;
    private IInferenceEngine? _engine;
    private CancellationTokenSource? _analysisCts;

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
        SelectCustomModelCommand = new RelayCommand(ExecuteSelectCustomModel);
        UndoRegionCommand = new RelayCommand(UndoRegion, () => !_mask.IsEmpty);
        ClearRegionsCommand = new RelayCommand(ClearRegions, () => !_mask.IsEmpty);
    }

    private bool _isPhotometricMode = true;
    public bool IsPhotometricMode
    {
        get => _isPhotometricMode;
        set
        {
            if (SetProperty(ref _isPhotometricMode, value))
            {
                OnPropertyChanged(nameof(IsYoloMode));
                if (_engine != null)
                {
                    _engine.Mode = value ? DetectionEngineMode.Photometric : DetectionEngineMode.DeepLearningYolo;
                    HardwareInfo = _engine.ExecutionProviderName;
                }
                if (!string.IsNullOrEmpty(SelectedImagePath))
                {
                    _ = ExecuteAnalyzeAsync();
                }
            }
        }
    }

    public bool IsYoloMode
    {
        get => !_isPhotometricMode;
        set => IsPhotometricMode = !value;
    }

    private void InitializeEngine()
    {
        try
        {
            _engine?.Dispose();
            var initialMode = _isPhotometricMode ? DetectionEngineMode.Photometric : DetectionEngineMode.DeepLearningYolo;
            _engine = new OnnxInferenceEngine(_modelPath, initialMode: initialMode);
            HardwareInfo = _engine.ExecutionProviderName;
        }
        catch (Exception ex)
        {
            HardwareInfo = $"Hata: {ex.Message}";
        }
    }

    private void ExecuteSelectCustomModel()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Eğitilmiş YOLO ONNX Modeli Seç",
            Filter = "ONNX Model Dosyası (*.onnx)|*.onnx|Tüm Dosyalar (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            _modelPath = dialog.FileName;
            InitializeEngine();
            IsPhotometricMode = false;
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
                    ResetForNewImage();
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
                OnPropertyChanged(nameof(ConfidenceBrush));
                OnPropertyChanged(nameof(ConfidenceStatusText));
                ApplyLiveFilter();
            }
        }
    }

    public string ConfidencePercentText => $"%{(int)Math.Round(_confidenceThreshold * 100)}";

    private static readonly SolidColorBrush CyanConfidenceBrush = new(Color.FromRgb(0x00, 0xE5, 0xFF));
    private static readonly SolidColorBrush YellowConfidenceBrush = new(Color.FromRgb(0xFF, 0xD6, 0x00));
    private static readonly SolidColorBrush RedConfidenceBrush = new(Color.FromRgb(0xEF, 0x44, 0x44));

    static MainViewModel()
    {
        CyanConfidenceBrush.Freeze();
        YellowConfidenceBrush.Freeze();
        RedConfidenceBrush.Freeze();
    }

    public SolidColorBrush ConfidenceBrush =>
        _confidenceThreshold < 0.20f ? RedConfidenceBrush :
        _confidenceThreshold <= 0.25f ? YellowConfidenceBrush :
        CyanConfidenceBrush;

    public string ConfidenceStatusText =>
        _confidenceThreshold < 0.20f ? "Yüksek Gürültü Riski (<2.4σ)" :
        _confidenceThreshold <= 0.25f ? "Düşük Eşik (2.4σ–3.0σ)" :
        "Kararlı Tespit (≥3.0σ)";

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

    public int SpeculativeCount
    {
        get => _speculativeCount;
        private set
        {
            if (SetProperty(ref _speculativeCount, value))
            {
                OnPropertyChanged(nameof(HasSpeculativeObjects));
            }
        }
    }

    public bool HasSpeculativeObjects => _speculativeCount > 0;

    public bool ShowBoundingBoxes
    {
        get => _showBoundingBoxes;
        set => SetProperty(ref _showBoundingBoxes, value);
    }

    public bool ShowLabels
    {
        get => _showLabels;
        set => SetProperty(ref _showLabels, value);
    }

    /// <summary>Detections that pass the live confidence filter and the scan mask (drawn by the overlay).</summary>
    public IReadOnlyList<AstrophotographyObject> VisibleObjects
    {
        get => _visibleObjects;
        private set => SetProperty(ref _visibleObjects, value);
    }

    // ---- Scan area (region of interest) tools ----

    public ScanTool Tool
    {
        get => _tool;
        set
        {
            if (SetProperty(ref _tool, value))
            {
                OnPropertyChanged(nameof(IsToolPan));
                OnPropertyChanged(nameof(IsToolRectangle));
                OnPropertyChanged(nameof(IsToolPolygon));
                OnPropertyChanged(nameof(ToolHint));
            }
        }
    }

    public bool IsToolPan
    {
        get => _tool == ScanTool.Pan;
        set { if (value) Tool = ScanTool.Pan; }
    }

    public bool IsToolRectangle
    {
        get => _tool == ScanTool.Rectangle;
        set { if (value) Tool = ScanTool.Rectangle; }
    }

    public bool IsToolPolygon
    {
        get => _tool == ScanTool.Polygon;
        set { if (value) Tool = ScanTool.Polygon; }
    }

    public RegionKind CurrentRegionKind => _regionKind;

    public bool IsIncludeMode
    {
        get => _regionKind == RegionKind.Include;
        set { if (value) SetRegionKind(RegionKind.Include); }
    }

    public bool IsExcludeMode
    {
        get => _regionKind == RegionKind.Exclude;
        set { if (value) SetRegionKind(RegionKind.Exclude); }
    }

    private void SetRegionKind(RegionKind kind)
    {
        if (_regionKind == kind) return;
        _regionKind = kind;
        OnPropertyChanged(nameof(IsIncludeMode));
        OnPropertyChanged(nameof(IsExcludeMode));
        OnPropertyChanged(nameof(CurrentRegionKind));
    }

    public string ToolHint => _tool switch
    {
        ScanTool.Rectangle => "Sürükleyerek dikdörtgen çizin.",
        ScanTool.Polygon => "Noktaları tıklayın · sağ tık / çift tık / Enter ile kapatın · Esc iptal.",
        _ => "Sol tuş: kaydır · Tekerlek: yakınlaştır"
    };

    public IReadOnlyList<SelectionRegion> RegionSnapshot
    {
        get => _regionSnapshot;
        private set => SetProperty(ref _regionSnapshot, value);
    }

    public string RegionSummary
    {
        get
        {
            if (_mask.IsEmpty) return "Tüm kare";
            int include = _mask.Regions.Count(r => r.Kind == RegionKind.Include);
            int exclude = _mask.Regions.Count - include;
            return $"{include} tara · {exclude} hariç";
        }
    }

    public void AddRegion(IReadOnlyList<Vector2> points)
    {
        if (points.Count < 3) return;

        _mask.Add(new SelectionRegion(_regionKind, points));
        OnMaskChanged();
    }

    private void UndoRegion()
    {
        if (_mask.RemoveLast())
        {
            OnMaskChanged();
        }
    }

    private void ClearRegions()
    {
        if (_mask.IsEmpty) return;

        _mask.Clear();
        OnMaskChanged();
    }

    private void OnMaskChanged()
    {
        RegionSnapshot = _mask.Regions.ToList();
        OnPropertyChanged(nameof(RegionSummary));
        ApplyLiveFilter();
        CommandManager.InvalidateRequerySuggested();
    }

    private void ResetForNewImage()
    {
        // Regions are expressed in pixel coordinates of one specific image.
        _analysisCts?.Cancel();
        _mask.Clear();
        _lastDetectionResult = null;
        RegionSnapshot = Array.Empty<SelectionRegion>();
        VisibleObjects = Array.Empty<AstrophotographyObject>();
        TotalCount = 0;
        Tier1Count = 0;
        Tier2Count = 0;
        Tier3Count = 0;
        SpeculativeCount = 0;
        OnPropertyChanged(nameof(RegionSummary));
        CommandManager.InvalidateRequerySuggested();
    }

    public ICommand SelectImageCommand { get; }
    public ICommand AnalyzeCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportImageCommand { get; }
    public ICommand SelectCustomModelCommand { get; }
    public ICommand UndoRegionCommand { get; }
    public ICommand ClearRegionsCommand { get; }

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

            // Pixel size (not DIP size): detections are in pixel coordinates, so the view must use the same units.
            ImageWidth = bitmap.PixelWidth;
            ImageHeight = bitmap.PixelHeight;
            ImageBitmap = bitmap;
        }
        catch
        {
            try
            {
                // Fallback using AstroCounter.Core's ImagePreprocessor (handles complex TIFF variants)
                using var skBitmap = ImagePreprocessor.LoadImage(filePath);
                var wpfBitmap = ConvertToWpfBitmap(skBitmap);
                ImageWidth = wpfBitmap.PixelWidth;
                ImageHeight = wpfBitmap.PixelHeight;
                ImageBitmap = wpfBitmap;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Görsel yüklenirken hata: {ex.Message}";
            }
        }
    }

    private static BitmapSource ConvertToWpfBitmap(SKBitmap skBitmap)
    {
        using var converted = skBitmap.ColorType == SKColorType.Bgra8888
            ? skBitmap.Copy()
            : skBitmap.Copy(SKColorType.Bgra8888);

        var wb = new WriteableBitmap(
            converted.Width,
            converted.Height,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            null);

        wb.WritePixels(
            new Int32Rect(0, 0, converted.Width, converted.Height),
            converted.GetPixels(),
            converted.RowBytes * converted.Height,
            converted.RowBytes);

        wb.Freeze();
        return wb;
    }

    public async Task ExecuteAnalyzeAsync()
    {
        if (string.IsNullOrEmpty(SelectedImagePath) || !File.Exists(SelectedImagePath))
            return;

        _analysisCts?.Cancel();
        _analysisCts = new CancellationTokenSource();
        var token = _analysisCts.Token;

        IsLoading = true;
        ProgressValue = 0;
        StatusMessage = IsPhotometricMode
            ? "Astronomik fotometri: 2D arka plan modelleniyor ve yıldızlar aranıyor..."
            : "YOLO SAHI: Görüntü 640x640 dilimleniyor ve model çıkarımı yapılıyor...";

        var progress = new Progress<float>(percent =>
        {
            if (token.IsCancellationRequested) return;

            ProgressValue = percent * 100.0;
            StatusMessage = IsPhotometricMode
                ? $"Fotometrik analiz: %{(int)(percent * 100)}"
                : $"Dilimler işleniyor: %{(int)(percent * 100)}";
        });

        try
        {
            if (_engine == null)
            {
                InitializeEngine();
            }
            else
            {
                _engine.Mode = IsPhotometricMode ? DetectionEngineMode.Photometric : DetectionEngineMode.DeepLearningYolo;
                HardwareInfo = _engine.ExecutionProviderName;
            }

            var config = new TilingConfig(DefaultConfidenceThreshold: 0.25f, MinConfidence: 0.02f);
            var result = await _engine!.ProcessImageAsync(SelectedImagePath, config, progress, token);

            if (token.IsCancellationRequested) return;

            _lastDetectionResult = result;
            ProcessingTimeText = $"{result.ElapsedTime.TotalMilliseconds:F0} ms";
            TileCountText = IsPhotometricMode ? "Fotometri (Tam Kare)" : $"{result.TileCount} Dilim";

            ApplyLiveFilter();

            StatusMessage = $"Tamamlandı! {TotalCount} obje tespit edildi ({ProcessingTimeText}).";
        }
        catch (OperationCanceledException)
        {
            // A newer analysis replaced this one.
        }
        catch (Exception ex)
        {
            StatusMessage = $"Analiz hatası: {ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
                ProgressValue = 100;
            }
        }
    }

    /// <summary>
    /// Filters detected objects in RAM according to ConfidenceThreshold slider (and the scan areas) without re-running AI.
    /// </summary>
    public void ApplyLiveFilter()
    {
        if (_lastDetectionResult == null) return;

        var filtered = _lastDetectionResult.Filter(_confidenceThreshold, _mask);

        TotalCount = filtered.TotalCount;
        Tier1Count = filtered.Tier1Count;
        Tier2Count = filtered.Tier2Count;
        Tier3Count = filtered.Tier3Count;
        SpeculativeCount = filtered.NoiseCount;
        VisibleObjects = filtered.FilteredObjects;

        CommandManager.InvalidateRequerySuggested();
    }

    private void ExecuteSelectImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Astrofotoğraf Seç",
            Filter = "Desteklenen Görseller (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp|TIFF Görselleri (*.tif;*.tiff)|*.tif;*.tiff|PNG Görselleri (*.png)|*.png|JPEG Görselleri (*.jpg;*.jpeg)|*.jpg;*.jpeg|Tüm Dosyalar (*.*)|*.*"
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
            var filtered = _lastDetectionResult.Filter(_confidenceThreshold, _mask);
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
            var filtered = _lastDetectionResult.Filter(_confidenceThreshold, _mask);
            ResultExporter.ExportAnnotatedImage(SelectedImagePath, filtered.FilteredObjects, dialog.FileName, _mask);
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
