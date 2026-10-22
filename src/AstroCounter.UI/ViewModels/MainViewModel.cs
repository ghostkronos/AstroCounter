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
using System.Windows.Threading;
using AstroCounter.Core.Astrometry;
using AstroCounter.Core.Export;
using AstroCounter.Core.Inference;
using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using AstroCounter.Core.Stellarium;
using Microsoft.Win32;
using SkiaSharp;

public enum ScanTool
{
    Pan,
    Rectangle,
    Polygon
}

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private string? _selectedImagePath;
    private BitmapSource? _imageBitmap;
    private double _imageWidth;
    private double _imageHeight;
    private float _confidenceThreshold = 0.25f;
    private bool _isLoading;
    private double _progressValue;
    private string _statusMessage = string.Empty;
    private string _hardwareInfo = string.Empty;
    private string _processingTimeText = "-";
    private string _tileCountText = "-";
    private int _totalCount;
    private int _tier1Count;
    private int _tier2Count;
    private int _tier3Count;
    private int _speculativeCount;
    private bool _showBoundingBoxes = true;
    private bool _showLabels = true;
    private bool _isAutoBrightnessEnabled = true;
    private string _brightnessStatusText = string.Empty;
    private string? _modelPath;

    private ScanTool _tool = ScanTool.Pan;
    private RegionKind _regionKind = RegionKind.Exclude;
    private readonly ScanMask _mask = new();
    private IReadOnlyList<SelectionRegion> _regionSnapshot = Array.Empty<SelectionRegion>();
    private IReadOnlyList<AstrophotographyObject> _visibleObjects = Array.Empty<AstrophotographyObject>();

    private DetectionResult? _lastDetectionResult;
    private IInferenceEngine? _engine;
    private CancellationTokenSource? _analysisCts;

    // Astrometry & Stellarium integration
    private readonly IStellariumClient _stellariumClient;
    private readonly IPlateSolver _plateSolver;
    private WcsMatrix? _currentWcs;
    private bool _isStellariumConnected;
    private bool _isStellariumSyncEnabled = true;
    private bool _isStellariumZoomSyncEnabled = true;
    private bool _showSphericalGrid = false;
    private double _imageRotationAngle = 0.0;
    private double _stellariumSkyAngle = 0.0;
    private double _solvedCameraAngle = 0.0;
    private double _userManualAngleOffset = 0.0;
    private bool _isStarPatternSolved = false;
    private bool _isAutoRotating = false;
    private double _lastVisiblePixelsHeight = 0.0;
    private CelestialCoordinates _lastPolledStellariumCoords;
    private double _lastPolledStellariumFov;
    private CataloguePlateSolver.AstronomicalTarget? _selectedCatalogueTarget;
    private IReadOnlyList<CataloguePlateSolver.AstronomicalTarget> _availableTargetsInPhoto = Array.Empty<CataloguePlateSolver.AstronomicalTarget>();
    private Point? _targetMarkerPixel;
    private string? _targetMarkerLabel;
    private bool _isPlateSolving;
    private string _stellariumStatusText = string.Empty;
    private string _solvedCoordinatesText = "-";
    private string _targetDetailsText = "";
    private string _cursorCelestialText = "-";
    private readonly DispatcherTimer _stellariumPollTimer;
    private long _lastSyncTicks;
    private bool _isSyncingFromStellarium;

    public event Action<double, double>? RequestCenterOnPixel;
    public event Action<double>? RequestSetVisiblePixelsHeight;
    public event Action? RequestInvalidateOverlay;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsTurkish => CataloguePlateSolver.UseTurkishLanguage;
    public string LanguageButtonText => IsTurkish ? "TR | EN" : "EN | TR";
    public string L_LanguageToolTip => IsTurkish ? "Uygulama dilini değiştir (Türkçe / İngilizce)" : "Switch application language (English / Turkish)";

    // Localized UI Strings
    public string L_WindowTitle => IsTurkish
        ? "AstroCounter v1.1.2 - Astrofotoğraf Nesne Tespiti ve Stellarium 1:1 Senkronizasyonu"
        : "AstroCounter v1.1.2 - Astrophotography Object Detection & Stellarium 1:1 Synchronization";
    public string L_HeaderSubtitle => IsTurkish
        ? "v1.1.2 • Fotometri + YOLO/SAHI + Stellarium 1:1"
        : "v1.1.2 • Photometry + YOLO/SAHI + Stellarium 1:1";
    public string L_SelectImage => IsTurkish ? "Görsel Seç" : "Select Image";
    public string L_Reanalyze => IsTurkish ? "Yeniden Analiz Et" : "Re-Analyze";
    public string L_ExportCsv => IsTurkish ? "CSV Dışa Aktar" : "Export CSV";
    public string L_ExportImage => IsTurkish ? "Görseli Kaydet" : "Save Image";

    public string L_EngineGroup => IsTurkish ? "MOTOR" : "ENGINE";
    public string L_Photometry => IsTurkish ? "Fotometri" : "Photometry";
    public string L_Yolo => "YOLO";
    public string L_SelectModel => IsTurkish ? "Model Seç..." : "Select Model...";
    public string L_AutoBrightnessToolTip => IsTurkish
        ? "Çok karanlık veya çok parlak fotoğrafların parlaklık ve arka plan dengesini algoritmanın yıldızları en net seçebileceği seviyeye otomatik getirir"
        : "Automatically balances exposure and background of underexposed or light-polluted images for optimal star detection";

    public string L_ScanAreaGroup => IsTurkish ? "TARAMA ALANI" : "SCAN AREA";
    public string L_Pan => IsTurkish ? "Gezin" : "Pan";
    public string L_Rectangle => IsTurkish ? "Dikdörtgen" : "Rectangle";
    public string L_Polygon => IsTurkish ? "Çokgen" : "Polygon";
    public string L_Exclude => IsTurkish ? "Hariç tut" : "Exclude";
    public string L_Include => IsTurkish ? "Sadece tara" : "Scan only";
    public string L_Undo => IsTurkish ? "Geri Al" : "Undo";
    public string L_Clear => IsTurkish ? "Temizle" : "Clear";

    public string L_StellariumGroup => "STELLARIUM 1:1";
    public string L_PlateSolveButton => IsTurkish ? "Yıldızlardan Eşle" : "Solve Star Pattern";
    public string L_PlateSolveToolTip => IsTurkish
        ? "Fotoğraftaki yıldızların konumlarını ve üçgen geometrisini analiz ederek gökyüzündeki gerçek koordinatları, ölçeği ve açıyı otomatik bulur"
        : "Analyzes star positions and triangle geometry in the image to automatically determine celestial coordinates, scale, and roll angle";
    public string L_TargetComboToolTip => IsTurkish
        ? "Fotoğrafın kapsama alanı içinde tespit edilen gök cisimlerini listeler; seçtiğinizde doğrudan o cisme odaklanır"
        : "Lists celestial objects detected within the image field of view; selecting an object centers directly on it";
    public string L_TwoWaySync => IsTurkish ? "Çift Yönlü 1:1" : "Two-Way 1:1";
    public string L_TwoWaySyncToolTip => IsTurkish
        ? "Fotoğraf ve Stellarium çift yönlü birbirine bağlanır: Fotoğrafı veya Stellarium'u kaydırıp yakınlaştırdığınızda diğeri bire bir hareket eder"
        : "Links the image and Stellarium bidirectionally: panning or zooming either view moves the other 1:1";
    public string L_SphericalGrid => IsTurkish ? "Küresel Izgara" : "Spherical Grid";
    public string L_SphericalGridToolTip => IsTurkish
        ? "Fotoğrafın üzerine küresel RA/Dec gök koordinat eksenlerini çizer"
        : "Draws spherical RA/Dec equatorial coordinate grid lines over the image";
    public string L_AngleLabel => IsTurkish ? "Açı:" : "Angle:";
    public string L_AngleToolTip => IsTurkish
        ? "Fotoğraf Stellarium açısına göre otomatik döner; ayrıca buradan 0°–360° elle döndürebilirsiniz"
        : "The image rotates automatically with Stellarium's sky angle; you can also adjust rotation manually from 0° to 360°";
    public string L_ResetAngleToolTip => IsTurkish
        ? "Açıyı Stellarium'un doğal gökyüzü açısına sıfırla"
        : "Reset rotation to Stellarium's natural sky angle";

    public string L_TotalObjects => IsTurkish ? "TOPLAM OBJE" : "TOTAL OBJECTS";
    public string L_Speculative => IsTurkish ? "SPEKÜLATİF (<3σ)" : "SPECULATIVE (<3σ)";
    public string L_ProcessingTime => IsTurkish ? "İŞLEM SÜRESİ" : "PROCESSING TIME";
    public string L_ConfidenceLabel => IsTurkish ? "Güven Eşiği: " : "Confidence: ";
    public string L_Boxes => IsTurkish ? "Kutular" : "Boxes";
    public string L_Labels => IsTurkish ? "Etiketler" : "Labels";

    public string L_PlaceholderTitle => IsTurkish
        ? "Astrofotoğrafınızı Buraya Sürükleyip Bırakın"
        : "Drag and Drop Your Astrophotograph Here";
    public string L_PlaceholderFormats => IsTurkish
        ? "TIFF, PNG, JPG, BMP veya 16-bit FITS formatları desteklenir"
        : "Supports TIFF, PNG, JPG, BMP, or 16-bit FITS formats";
    public string L_PlaceholderBrowse => IsTurkish
        ? "Gözat ve Görsel Seç"
        : "Browse and Select Image";

    public string L_CursorLabel => IsTurkish ? "İmleç: " : "Cursor: ";
    public string L_ZoomLabel => IsTurkish ? "Yakınlaştırma: " : "Zoom: ";
    public string L_FitButton => IsTurkish ? "Sığdır" : "Fit";

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

        _statusMessage = IsTurkish
            ? "Analiz edilecek astrofotoğrafı sürükleyip bırakın veya 'Görsel Seç'e tıklayın."
            : "Drag and drop an astrophotograph to analyze, or click 'Select Image'.";
        _brightnessStatusText = IsTurkish ? "Oto Parlaklık" : "Auto Brightness";
        _stellariumStatusText = IsTurkish ? "Stellarium: Bağlan" : "Stellarium: Connect";

        InitializeEngine();

        _stellariumClient = new StellariumClient();
        _plateSolver = new HybridPlateSolver();

        _stellariumPollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _stellariumPollTimer.Tick += StellariumPollTimer_Tick;

        SelectImageCommand = new RelayCommand(ExecuteSelectImage);
        AnalyzeCommand = new RelayCommand(async () => await ExecuteAnalyzeAsync(), () => !IsLoading && !string.IsNullOrEmpty(SelectedImagePath));
        ExportCsvCommand = new RelayCommand(ExecuteExportCsv, () => _lastDetectionResult != null && TotalCount > 0);
        ExportImageCommand = new RelayCommand(ExecuteExportImage, () => _lastDetectionResult != null && !string.IsNullOrEmpty(SelectedImagePath));
        SelectCustomModelCommand = new RelayCommand(ExecuteSelectCustomModel);
        UndoRegionCommand = new RelayCommand(UndoRegion, () => !_mask.IsEmpty);
        ClearRegionsCommand = new RelayCommand(ClearRegions, () => !_mask.IsEmpty);
        ToggleStellariumCommand = new RelayCommand(async () => await ToggleStellariumAsync());
        PlateSolveCommand = new RelayCommand(async () => await ExecutePlateSolveAsync(), () => !IsLoading && !IsPlateSolving && !string.IsNullOrEmpty(SelectedImagePath));
        SyncToStellariumCommand = new RelayCommand(async () => await SyncCurrentCenterToStellariumAsync(), () => CanSyncToStellarium);
        ResetRotationCommand = new RelayCommand(ResetRotationToStellarium);
        ToggleLanguageCommand = new RelayCommand(ExecuteToggleLanguage);
    }

    private void ExecuteToggleLanguage()
    {
        CataloguePlateSolver.UseTurkishLanguage = !CataloguePlateSolver.UseTurkishLanguage;

        if (_engine != null)
        {
            HardwareInfo = _engine.ExecutionProviderName;
        }

        if (_lastDetectionResult != null)
        {
            TileCountText = IsPhotometricMode
                ? (IsTurkish ? "Fotometri - Tam Kare" : "Photometry - Full Frame")
                : (IsTurkish ? $"{_lastDetectionResult.TileCount} Dilim" : $"{_lastDetectionResult.TileCount} Tiles");
        }

        if (!IsStellariumConnected)
        {
            StellariumStatusText = IsTurkish ? "Stellarium: Bağlan" : "Stellarium: Connect";
        }
        else
        {
            StellariumStatusText = IsTurkish ? "Stellarium Bağlı" : "Stellarium Connected";
        }

        if (!IsAutoBrightnessEnabled)
        {
            BrightnessStatusText = IsTurkish ? "Oto Parlaklık: Kapalı" : "Auto Brightness: Off";
        }
        else if (string.IsNullOrEmpty(SelectedImagePath))
        {
            BrightnessStatusText = IsTurkish ? "Oto Parlaklık" : "Auto Brightness";
        }
        else if (File.Exists(SelectedImagePath))
        {
            LoadBitmapPreview(SelectedImagePath);
        }

        if ( string.IsNullOrEmpty(SelectedImagePath))
        {
            StatusMessage = IsTurkish
                ? "Analiz edilecek astrofotoğrafı sürükleyip bırakın veya 'Görsel Seç'e tıklayın."
                : "Drag and drop an astrophotograph to analyze, or click 'Select Image'.";
        }

        // Re-populate ComboBox targets so their CommonName updates immediately
        if (_currentWcs != null && ImageWidth > 0 && ImageHeight > 0)
        {
            var prevNameEn = _selectedCatalogueTarget?.NameEn;
            var refreshed = CataloguePlateSolver.GetTargetsInField(
                _currentWcs,
                (int)ImageWidth,
                (int)ImageHeight,
                IsPixelInsideValidPhotoSky);
            AvailableTargetsInPhoto = Array.Empty<CataloguePlateSolver.AstronomicalTarget>();
            AvailableTargetsInPhoto = refreshed;
            if (prevNameEn != null)
            {
                _selectedCatalogueTarget = refreshed.FirstOrDefault(t => t.NameEn == prevNameEn);
                OnPropertyChanged(nameof(SelectedCatalogueTarget));
                if (_selectedCatalogueTarget != null)
                {
                    TargetMarkerLabel = _selectedCatalogueTarget.CommonName;
                }
            }
        }

        // Notify WPF to refresh all bindings on MainViewModel
        OnPropertyChanged(string.Empty);
        RequestInvalidateOverlay?.Invoke();
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
            HardwareInfo = IsTurkish ? $"Hata: {ex.Message}" : $"Error: {ex.Message}";
        }
    }

    private void ExecuteSelectCustomModel()
    {
        var dialog = new OpenFileDialog
        {
            Title = IsTurkish ? "Eğitilmiş YOLO ONNX Modeli Seç" : "Select Trained YOLO ONNX Model",
            Filter = IsTurkish
                ? "ONNX Model Dosyası (*.onnx)|*.onnx|Tüm Dosyalar (*.*)|*.*"
                : "ONNX Model File (*.onnx)|*.onnx|All Files (*.*)|*.*"
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
                    _ = AnalyzeAndPlateSolveSequentiallyAsync();
                }
            }
        }
    }

    private async Task AnalyzeAndPlateSolveSequentiallyAsync()
    {
        await ExecuteAnalyzeAsync();
        await ExecutePlateSolveAsync();
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

    public string ConfidencePercentText => IsTurkish
        ? $"%{(int)Math.Round(_confidenceThreshold * 100)}"
        : $"{(int)Math.Round(_confidenceThreshold * 100)}%";

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
        _confidenceThreshold < 0.15f ? RedConfidenceBrush :
        _confidenceThreshold < 0.25f ? YellowConfidenceBrush :
        CyanConfidenceBrush;

    public string ConfidenceStatusText =>
        _confidenceThreshold < 0.15f
            ? (IsTurkish ? "Aşırı Duyarlı / Gürültü Riski (<2.0σ)" : "Ultra-Sensitive / Noise Risk (<2.0σ)")
            : _confidenceThreshold < 0.25f
                ? (IsTurkish ? "Derin Alan / Çok Sönük (2.0σ–3.0σ)" : "Deep Field / Very Faint (2.0σ–3.0σ)")
                : _confidenceThreshold < 0.50f
                    ? (IsTurkish ? "Standart Astronomik Tespit (≥3.0σ)" : "Standard Astronomical Detection (≥3.0σ)")
                    : _confidenceThreshold < 0.80f
                        ? (IsTurkish ? "Belirgin Yıldızlar (≥5.0σ)" : "Prominent Stars (≥5.0σ)")
                        : (IsTurkish ? "Parlak Yıldızlar (≥10.0σ)" : "Bright Stars (≥10.0σ)");

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
        ScanTool.Rectangle => IsTurkish ? "Sürükleyerek dikdörtgen çizin." : "Drag to draw a rectangle.",
        ScanTool.Polygon => IsTurkish
            ? "Noktaları tıklayın · sağ tık / çift tık / Enter ile kapatın · Esc iptal."
            : "Click vertices · right-click / double-click / Enter to close · Esc to cancel.",
        _ => IsTurkish ? "Sol tuş: kaydır · Tekerlek: yakınlaştır" : "Left-drag: pan · Wheel: zoom"
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
            if (_mask.IsEmpty) return IsTurkish ? "Tüm kare" : "Full frame";
            int include = _mask.Regions.Count(r => r.Kind == RegionKind.Include);
            int exclude = _mask.Regions.Count - include;
            return IsTurkish ? $"{include} tara · {exclude} hariç" : $"{include} scan · {exclude} exclude";
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
        CurrentWcs = null;
        SolvedCoordinatesText = "-";
        DiagnosticWarningText = null;
        StellariumGuideText = null;
        TargetDetailsText = "";
        CursorCelestialText = "-";
        _isStarPatternSolved = false;
        _solvedCameraAngle = 0.0;
        _userManualAngleOffset = 0.0;
        _isAutoRotating = true;
        ImageRotationAngle = Math.Round(_stellariumSkyAngle % 360.0, 1);
        _isAutoRotating = false;
        _selectedCatalogueTarget = null;
        AvailableTargetsInPhoto = Array.Empty<CataloguePlateSolver.AstronomicalTarget>();
        TargetMarkerPixel = null;
        TargetMarkerLabel = null;
        OnPropertyChanged(nameof(SelectedCatalogueTarget));
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
    public ICommand ToggleStellariumCommand { get; }
    public ICommand PlateSolveCommand { get; }
    public ICommand SyncToStellariumCommand { get; }
    public ICommand ResetRotationCommand { get; }
    public ICommand ToggleLanguageCommand { get; }

    public bool IsAutoBrightnessEnabled
    {
        get => _isAutoBrightnessEnabled;
        set
        {
            if (SetProperty(ref _isAutoBrightnessEnabled, value))
            {
                if (!string.IsNullOrEmpty(SelectedImagePath) && File.Exists(SelectedImagePath))
                {
                    LoadBitmapPreview(SelectedImagePath);
                }
                else
                {
                    BrightnessStatusText = value
                        ? (IsTurkish ? "Oto Parlaklık" : "Auto Brightness")
                        : (IsTurkish ? "Oto Parlaklık: Kapalı" : "Auto Brightness: Off");
                }
            }
        }
    }

    public string BrightnessStatusText
    {
        get => _brightnessStatusText;
        private set => SetProperty(ref _brightnessStatusText, value);
    }

    private void LoadBitmapPreview(string filePath)
    {
        try
        {
            if (IsAutoBrightnessEnabled)
            {
                using var skBitmap = ImagePreprocessor.LoadImage(filePath);
                using var balanced = ImagePreprocessor.AutoBalanceBrightness(skBitmap, out var info);
                BrightnessStatusText = info.WasAdjusted
                    ? info.SummaryText
                    : (IsTurkish ? "Oto Parlaklık: Optimal" : "Auto Brightness: Optimal");

                if (info.WasAdjusted)
                {
                    var wpfBitmap = ConvertToWpfBitmap(balanced);
                    ImageWidth = wpfBitmap.PixelWidth;
                    ImageHeight = wpfBitmap.PixelHeight;
                    ImageBitmap = wpfBitmap;
                    StatusMessage = IsTurkish
                        ? $"Görsel yüklendi ({info.SummaryText})."
                        : $"Image loaded ({info.SummaryText}).";
                    return;
                }
            }
            else
            {
                BrightnessStatusText = IsTurkish ? "Oto Parlaklık: Kapalı" : "Auto Brightness: Off";
            }

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
                using var balanced = IsAutoBrightnessEnabled
                    ? ImagePreprocessor.AutoBalanceBrightness(skBitmap, out var info)
                    : skBitmap.Copy();
                var wpfBitmap = ConvertToWpfBitmap(balanced);
                ImageWidth = wpfBitmap.PixelWidth;
                ImageHeight = wpfBitmap.PixelHeight;
                ImageBitmap = wpfBitmap;
            }
            catch (Exception ex)
            {
                StatusMessage = IsTurkish
                    ? $"Görsel yüklenirken hata: {ex.Message}"
                    : $"Error loading image: {ex.Message}";
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
            ? (IsTurkish
                ? "Astronomik fotometri: 2D arka plan modelleniyor ve yıldızlar aranıyor..."
                : "Astronomical photometry: modeling 2D background and extracting star centroids...")
            : (IsTurkish
                ? "YOLO SAHI: Görüntü 640x640 dilimleniyor ve model çıkarımı yapılıyor..."
                : "YOLO SAHI: slicing image into 640x640 tiles and running inference...");

        var progress = new Progress<float>(percent =>
        {
            if (token.IsCancellationRequested) return;

            ProgressValue = percent * 100.0;
            int pct = (int)(percent * 100);
            StatusMessage = IsPhotometricMode
                ? (IsTurkish ? $"Fotometrik analiz: %{pct}" : $"Photometric analysis: {pct}%")
                : (IsTurkish ? $"Dilimler işleniyor: %{pct}" : $"Processing tiles: {pct}%");
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
            TileCountText = IsPhotometricMode
                ? (IsTurkish ? "Fotometri - Tam Kare" : "Photometry - Full Frame")
                : (IsTurkish ? $"{result.TileCount} Dilim" : $"{result.TileCount} Tiles");

            ApplyLiveFilter();

            StatusMessage = IsTurkish
                ? $"Tamamlandı! {TotalCount} obje tespit edildi ({ProcessingTimeText})."
                : $"Completed! {TotalCount} objects detected ({ProcessingTimeText}).";
        }
        catch (OperationCanceledException)
        {
            // A newer analysis replaced this one.
        }
        catch (Exception ex)
        {
            StatusMessage = IsTurkish ? $"Analiz hatası: {ex.Message}" : $"Analysis error: {ex.Message}";
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
            Title = IsTurkish ? "Astrofotoğraf Seç" : "Select Astrophotograph",
            Filter = IsTurkish
                ? "Desteklenen Görseller (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp|TIFF Görselleri (*.tif;*.tiff)|*.tif;*.tiff|PNG Görselleri (*.png)|*.png|JPEG Görselleri (*.jpg;*.jpeg)|*.jpg;*.jpeg|Tüm Dosyalar (*.*)|*.*"
                : "Supported Images (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp|TIFF Images (*.tif;*.tiff)|*.tif;*.tiff|PNG Images (*.png)|*.png|JPEG Images (*.jpg;*.jpeg)|*.jpg;*.jpeg|All Files (*.*)|*.*"
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
            Title = IsTurkish ? "Sayım Sonuçlarını CSV Olarak Kaydet" : "Save Detection Results as CSV",
            Filter = IsTurkish ? "CSV Dosyası (*.csv)|*.csv" : "CSV File (*.csv)|*.csv",
            FileName = $"AstroCount_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        if (dialog.ShowDialog() == true)
        {
            var filtered = _lastDetectionResult.Filter(_confidenceThreshold, _mask);
            ResultExporter.ExportToCsv(filtered.FilteredObjects, dialog.FileName);
            MessageBox.Show(
                IsTurkish ? $"CSV başarıyla kaydedildi:\n{dialog.FileName}" : $"CSV saved successfully:\n{dialog.FileName}",
                IsTurkish ? "Dışa Aktarma Başarılı" : "Export Successful",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void ExecuteExportImage()
    {
        if (_lastDetectionResult == null || string.IsNullOrEmpty(SelectedImagePath)) return;

        var dialog = new SaveFileDialog
        {
            Title = IsTurkish ? "İşlenmiş Görseli Kaydet" : "Save Annotated Image",
            Filter = IsTurkish
                ? "PNG Görseli (*.png)|*.png|JPEG Görseli (*.jpg)|*.jpg"
                : "PNG Image (*.png)|*.png|JPEG Image (*.jpg)|*.jpg",
            FileName = $"AstroDetected_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };

        if (dialog.ShowDialog() == true)
        {
            var filtered = _lastDetectionResult.Filter(_confidenceThreshold, _mask);
            ResultExporter.ExportAnnotatedImage(SelectedImagePath, filtered.FilteredObjects, dialog.FileName, _mask);
            MessageBox.Show(
                IsTurkish ? $"Etiketli görsel kaydedildi:\n{dialog.FileName}" : $"Annotated image saved:\n{dialog.FileName}",
                IsTurkish ? "Dışa Aktarma Başarılı" : "Export Successful",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    public bool IsStellariumConnected
    {
        get => _isStellariumConnected;
        private set
        {
            if (SetProperty(ref _isStellariumConnected, value))
            {
                OnPropertyChanged(nameof(CanSyncToStellarium));
            }
        }
    }

    public bool IsStellariumSyncEnabled
    {
        get => _isStellariumSyncEnabled;
        set
        {
            if (SetProperty(ref _isStellariumSyncEnabled, value))
            {
                _isStellariumZoomSyncEnabled = value;
            }
        }
    }

    public bool IsStellariumZoomSyncEnabled
    {
        get => _isStellariumZoomSyncEnabled;
        set => SetProperty(ref _isStellariumZoomSyncEnabled, value);
    }

    public bool ShowSphericalGrid
    {
        get => _showSphericalGrid;
        set => SetProperty(ref _showSphericalGrid, value);
    }

    public double ImageRotationAngle
    {
        get => _imageRotationAngle;
        set
        {
            double normalized = ((value % 360.0) + 360.0) % 360.0;
            if (Math.Abs(value - 360.0) < 0.01) normalized = 360.0;
            if (SetProperty(ref _imageRotationAngle, Math.Round(normalized, 1)))
            {
                if (!_isAutoRotating)
                {
                    _userManualAngleOffset = ((_imageRotationAngle - (_solvedCameraAngle + _stellariumSkyAngle)) % 360.0 + 360.0) % 360.0;

                    if (!_isStarPatternSolved && _currentWcs != null)
                    {
                        CurrentWcs = new WcsMatrix(
                            _currentWcs.CenterCoordinates,
                            _currentWcs.ReferencePixelX,
                            _currentWcs.ReferencePixelY,
                            _currentWcs.PixelScaleArcsec,
                            _userManualAngleOffset,
                            (int)ImageWidth,
                            (int)ImageHeight);
                    }
                }
            }
        }
    }

    private void ResetRotationToStellarium()
    {
        _userManualAngleOffset = 0.0;
        ApplyStellariumSkyAngle(_stellariumSkyAngle);
    }

    private void ApplyStellariumSkyAngle(double skyRollAngleDegrees)
    {
        _stellariumSkyAngle = ((skyRollAngleDegrees % 360.0) + 360.0) % 360.0;
        double targetVisualAngle = Math.Round(((_solvedCameraAngle + _stellariumSkyAngle + _userManualAngleOffset) % 360.0 + 360.0) % 360.0, 1);
        if (Math.Abs(_imageRotationAngle - targetVisualAngle) >= 0.3)
        {
            _isAutoRotating = true;
            ImageRotationAngle = targetVisualAngle;
            _isAutoRotating = false;
        }
    }

    public IReadOnlyList<CataloguePlateSolver.AstronomicalTarget> AvailableTargetsInPhoto
    {
        get => _availableTargetsInPhoto;
        private set
        {
            if (SetProperty(ref _availableTargetsInPhoto, value))
            {
                OnPropertyChanged(nameof(HasTargetsInPhoto));
                OnPropertyChanged(nameof(TargetComboPlaceholderText));
            }
        }
    }

    public bool HasTargetsInPhoto => _availableTargetsInPhoto.Count > 0;

    public string TargetComboPlaceholderText =>
        _availableTargetsInPhoto.Count > 0
            ? (IsTurkish ? $"Fotoğraftaki Cisimler ({_availableTargetsInPhoto.Count})" : $"Objects in Frame ({_availableTargetsInPhoto.Count})")
            : (IsTurkish ? "Kadrajda Katalog Cismi Yok" : "No Catalog Objects in Frame");

    public CataloguePlateSolver.AstronomicalTarget? SelectedCatalogueTarget
    {
        get => _selectedCatalogueTarget;
        set
        {
            if (SetProperty(ref _selectedCatalogueTarget, value) && value != null)
            {
                SetTarget(value);
            }
        }
    }

    public Point? TargetMarkerPixel
    {
        get => _targetMarkerPixel;
        private set => SetProperty(ref _targetMarkerPixel, value);
    }

    public string? TargetMarkerLabel
    {
        get => _targetMarkerLabel;
        private set => SetProperty(ref _targetMarkerLabel, value);
    }

    public bool IsPlateSolving
    {
        get => _isPlateSolving;
        private set
        {
            if (SetProperty(ref _isPlateSolving, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string StellariumStatusText
    {
        get => _stellariumStatusText;
        private set => SetProperty(ref _stellariumStatusText, value);
    }

    public string SolvedCoordinatesText
    {
        get => _solvedCoordinatesText;
        private set => SetProperty(ref _solvedCoordinatesText, value);
    }

    public string TargetDetailsText
    {
        get => _targetDetailsText;
        private set => SetProperty(ref _targetDetailsText, value);
    }

    public string CursorCelestialText
    {
        get => _cursorCelestialText;
        set => SetProperty(ref _cursorCelestialText, value);
    }

    private string? _diagnosticWarningText;
    private string? _stellariumGuideText;

    public string? DiagnosticWarningText
    {
        get => _diagnosticWarningText;
        private set
        {
            if (SetProperty(ref _diagnosticWarningText, value))
            {
                OnPropertyChanged(nameof(HasDiagnosticWarning));
            }
        }
    }

    public bool HasDiagnosticWarning => !string.IsNullOrEmpty(DiagnosticWarningText);

    public string? StellariumGuideText
    {
        get => _stellariumGuideText;
        private set => SetProperty(ref _stellariumGuideText, value);
    }

    public WcsMatrix? CurrentWcs
    {
        get => _currentWcs;
        private set
        {
            if (SetProperty(ref _currentWcs, value))
            {
                OnPropertyChanged(nameof(IsWcsAvailable));
                OnPropertyChanged(nameof(CanSyncToStellarium));
                RefreshAvailableTargetsInPhoto();
            }
        }
    }

    public bool IsWcsAvailable => CurrentWcs != null;
    public bool CanSyncToStellarium => IsStellariumConnected && CurrentWcs != null;

    private void RefreshAvailableTargetsInPhoto()
    {
        _selectedCatalogueTarget = null;
        OnPropertyChanged(nameof(SelectedCatalogueTarget));
        TargetMarkerPixel = null;
        TargetMarkerLabel = null;

        if (_currentWcs == null || ImageWidth <= 0 || ImageHeight <= 0)
        {
            AvailableTargetsInPhoto = Array.Empty<CataloguePlateSolver.AstronomicalTarget>();
            return;
        }

        AvailableTargetsInPhoto = CataloguePlateSolver.GetTargetsInField(
            _currentWcs,
            (int)ImageWidth,
            (int)ImageHeight,
            IsPixelInsideValidPhotoSky);
    }

    public void OnMouseMoveOnImage(double pixelX, double pixelY)
    {
        if (_currentWcs != null)
        {
            var coords = _currentWcs.PixelToEquatorial(pixelX, pixelY);
            CursorCelestialText = $"RA: {coords.FormatRa()} | Dec: {coords.FormatDec()}";
        }
        else
        {
            CursorCelestialText = $"X: {pixelX:F0}, Y: {pixelY:F0} px";
        }
    }

    public void OnViewportMoved(double centerPixelX, double centerPixelY, double visiblePixelsHeight, bool notifyStellarium = true)
    {
        if (visiblePixelsHeight > 10)
        {
            _lastVisiblePixelsHeight = visiblePixelsHeight;
        }

        if (!notifyStellarium) return;
        if (!_isStellariumConnected || !_isStellariumSyncEnabled || _currentWcs == null) return;
        if (_isSyncingFromStellarium) return;

        long now = Environment.TickCount64;
        if (now - _lastSyncTicks < 60) return;
        _lastSyncTicks = now;

        var wcs = _currentWcs;
        double refHeight = visiblePixelsHeight > 10 ? visiblePixelsHeight : ImageHeight;

        _ = Task.Run(async () =>
        {
            try
            {
                var coord = wcs.PixelToEquatorial(centerPixelX, centerPixelY);
                double fov = Math.Clamp((refHeight * wcs.PixelScaleArcsec) / 3600.0, 0.05, 130.0);

                _lastPolledStellariumCoords = coord;
                _lastPolledStellariumFov = fov;

                await _stellariumClient.FocusCoordinatesAsync(coord, fov, 0.0);
            }
            catch
            {
                // Ignore transient network errors
            }
        });
    }

    public async void SetTarget(CataloguePlateSolver.AstronomicalTarget target)
    {
        if (_currentWcs == null || ImageWidth <= 0 || ImageHeight <= 0)
        {
            return;
        }

        if (!_currentWcs.EquatorialToPixel(target.Coordinates, out double px, out double py))
        {
            return;
        }

        // Highlight and jump directly to (px, py) on the photo
        TargetMarkerPixel = new Point(px, py);
        TargetMarkerLabel = target.CommonName;

        double maxFov = Math.Max(0.1, _currentWcs.FovHeightDegrees * 0.75);
        double minFov = Math.Min(0.15, maxFov * 0.2);
        double targetFovDeg = Math.Clamp(
            Math.Min(target.TypicalFovDegrees * 2.5, _currentWcs.FovHeightDegrees * 0.5),
            minFov,
            maxFov);
        double targetVisiblePixelsH = Math.Clamp(
            (targetFovDeg * 3600.0) / Math.Max(0.05, _currentWcs.PixelScaleArcsec),
            80.0,
            ImageHeight);

        _isSyncingFromStellarium = true;
        RequestCenterOnPixel?.Invoke(px, py);
        RequestSetVisiblePixelsHeight?.Invoke(targetVisiblePixelsH);
        RequestCenterOnPixel?.Invoke(px, py);
        _isSyncingFromStellarium = false;

        SolvedCoordinatesText = $"{target.Coordinates.FormatRa()}  {target.Coordinates.FormatDec()}";
        TargetDetailsText = IsTurkish
            ? $"{target.CommonName} — Fotoğraftaki Konum: X={px:F0}, Y={py:F0} px (FOV: {targetFovDeg:F1}°)"
            : $"{target.CommonName} — Position in Image: X={px:F0}, Y={py:F0} px (FOV: {targetFovDeg:F1}°)";
        StatusMessage = IsTurkish
            ? $"'{target.CommonName}' üzerine odaklandı (X:{px:F0}, Y:{py:F0} px)."
            : $"Centered on '{target.CommonName}' (X:{px:F0}, Y:{py:F0} px).";

        if (IsStellariumConnected)
        {
            _lastPolledStellariumCoords = target.Coordinates;
            _lastPolledStellariumFov = targetFovDeg;
            _lastSyncTicks = Environment.TickCount64;

            await _stellariumClient.FocusCoordinatesAsync(target.Coordinates, targetFovDeg, 0.0);
            var status = await _stellariumClient.GetStatusAsync();
            if (status.IsConnected)
            {
                ApplyStellariumSkyAngle(status.SkyRollAngleDegrees);
            }
        }
    }

    /// <summary>
    /// Verifies that pixel (px, py) is inside actual photographed sky pixels and not inside a pure-black (0,0,0) panorama border void.
    /// </summary>
    private bool IsPixelInsideValidPhotoSky(double px, double py)
    {
        if (_imageBitmap == null) return true;
        try
        {
            int ix = Math.Clamp((int)Math.Round(px), 0, _imageBitmap.PixelWidth - 1);
            int iy = Math.Clamp((int)Math.Round(py), 0, _imageBitmap.PixelHeight - 1);
            int x0 = Math.Max(0, ix - 4);
            int y0 = Math.Max(0, iy - 4);
            int w = Math.Min(_imageBitmap.PixelWidth - x0, 9);
            int h = Math.Min(_imageBitmap.PixelHeight - y0, 9);
            if (w <= 0 || h <= 0) return true;

            int bpp = Math.Max(1, (_imageBitmap.Format.BitsPerPixel + 7) / 8);
            int stride = w * bpp;
            byte[] buf = new byte[stride * h];
            _imageBitmap.CopyPixels(new Int32Rect(x0, y0, w, h), buf, stride, 0);

            for (int i = 0; i < buf.Length; i += bpp)
            {
                if (buf[i] > 0 || (i + 1 < buf.Length && buf[i + 1] > 0) || (i + 2 < buf.Length && buf[i + 2] > 0))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private async void StellariumPollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isStellariumConnected || !_isStellariumSyncEnabled || _currentWcs == null) return;
        if (Environment.TickCount64 - _lastSyncTicks < 450) return;

        try
        {
            var status = await _stellariumClient.GetStatusAsync();
            if (status.IsConnected)
            {
                ApplyStellariumSkyAngle(status.SkyRollAngleDegrees);

                bool fovChanged = false;
                if (_isStellariumZoomSyncEnabled && status.CurrentFovDegrees > 0.01 && _currentWcs.PixelScaleArcsec > 0.01)
                {
                    double fovRelDiff = Math.Abs(status.CurrentFovDegrees - _lastPolledStellariumFov) / Math.Max(0.05, _lastPolledStellariumFov);
                    if (fovRelDiff > 0.015)
                    {
                        _lastPolledStellariumFov = status.CurrentFovDegrees;
                        fovChanged = true;
                        double targetVisiblePixelsH = (status.CurrentFovDegrees * 3600.0) / _currentWcs.PixelScaleArcsec;
                        _isSyncingFromStellarium = true;
                        RequestSetVisiblePixelsHeight?.Invoke(targetVisiblePixelsH);
                        _isSyncingFromStellarium = false;
                    }
                }

                double dRa = Math.Abs(status.CurrentCoordinates.RaDegrees - _lastPolledStellariumCoords.RaDegrees);
                if (dRa > 180.0) dRa = 360.0 - dRa;
                double dDec = Math.Abs(status.CurrentCoordinates.DecDegrees - _lastPolledStellariumCoords.DecDegrees);

                double moveThresholdDeg = Math.Max(0.001, status.CurrentFovDegrees * 0.002);
                if (dRa > moveThresholdDeg || dDec > moveThresholdDeg || fovChanged)
                {
                    _lastPolledStellariumCoords = status.CurrentCoordinates;
                    if (_currentWcs.EquatorialToPixel(status.CurrentCoordinates, out double px, out double py))
                    {
                        if (px >= -ImageWidth * 0.5 && px <= ImageWidth * 1.5 &&
                            py >= -ImageHeight * 0.5 && py <= ImageHeight * 1.5)
                        {
                            _isSyncingFromStellarium = true;
                            RequestCenterOnPixel?.Invoke(px, py);
                            _isSyncingFromStellarium = false;
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore background polling issues
        }
    }

    public async Task ToggleStellariumAsync()
    {
        if (IsStellariumConnected)
        {
            _stellariumPollTimer.Stop();
            IsStellariumConnected = false;
            StellariumStatusText = IsTurkish ? "Stellarium: Bağlan" : "Stellarium: Connect";
            StatusMessage = IsTurkish ? "Stellarium bağlantısı kesildi." : "Stellarium disconnected.";
            return;
        }

        StatusMessage = IsTurkish ? "Stellarium kontrol ediliyor..." : "Checking Stellarium connection...";
        bool ok = await _stellariumClient.CheckConnectionAsync();

        if (!ok)
        {
            // Stellarium is not responding. Check if installed and launch in windowed mode.
            string? exe = StellariumLauncher.FindStellariumExecutable();
            if (exe != null)
            {
                StatusMessage = IsTurkish
                    ? "Stellarium pencereli modda başlatılıyor, lütfen bekleyin..."
                    : "Launching Stellarium in windowed mode, please wait...";
                bool launched = StellariumLauncher.LaunchWindowed(exe);
                if (launched)
                {
                    // Wait for Stellarium to start and initialize HTTP server (up to 12 seconds)
                    for (int i = 0; i < 12; i++)
                    {
                        await Task.Delay(1000);
                        ok = await _stellariumClient.CheckConnectionAsync();
                        if (ok) break;
                    }
                }
            }
        }

        if (ok)
        {
            IsStellariumConnected = true;
            StellariumStatusText = IsTurkish ? "Stellarium Bağlı" : "Stellarium Connected";
            _stellariumPollTimer.Start();

            // Ensure windowed mode, dark sky, and paused clock via API
            await _stellariumClient.EnsureWindowedModeAsync();

            if (_currentWcs != null)
            {
                StatusMessage = IsTurkish
                    ? "Stellarium pencereli modda bağlandı. 1:1 odaklanılıyor..."
                    : "Connected to Stellarium in windowed mode. Synchronizing 1:1...";
                await SyncCurrentCenterToStellariumAsync();
            }
            else if (!string.IsNullOrEmpty(SelectedImagePath))
            {
                StatusMessage = IsTurkish
                    ? "Stellarium pencereli modda bağlandı. Görsel 1:1 eşitleniyor..."
                    : "Connected to Stellarium in windowed mode. Solving and synchronizing image 1:1...";
                await ExecutePlateSolveAsync();
            }
            else
            {
                var status = await _stellariumClient.GetStatusAsync();
                if (status.IsConnected)
                {
                    ApplyStellariumSkyAngle(status.SkyRollAngleDegrees);
                }
                StatusMessage = IsTurkish
                    ? "Stellarium pencereli modda başarıyla bağlandı. Çift yönlü 1:1 senkronizasyon aktif."
                    : "Connected to Stellarium in windowed mode. Bidirectional 1:1 synchronization is active.";
            }
        }
        else
        {
            IsStellariumConnected = false;
            StellariumStatusText = IsTurkish ? "Stellarium Bulunamadı" : "Stellarium Not Found";
            StatusMessage = IsTurkish
                ? "Stellarium bulunamadı veya açılamadı! Lütfen Stellarium'un kurulu olduğundan ve Ayarlar > Eklentiler > Uzaktan Denetim eklentisinin açık olduğundan (Port 8090) emin olun."
                : "Stellarium was not found or could not be started. Please ensure Stellarium is installed and the Remote Control plugin is enabled on Port 8090.";
        }
    }

    public async Task ExecutePlateSolveAsync()
    {
        if (string.IsNullOrEmpty(SelectedImagePath) || ImageWidth <= 0 || ImageHeight <= 0)
        {
            StatusMessage = IsTurkish ? "Lütfen önce bir görsel yükleyin." : "Please load an image first.";
            return;
        }

        try
        {
            IsPlateSolving = true;
            StatusMessage = IsTurkish
                ? "Fotoğraftaki yıldız konumları analiz ediliyor (RANSAC Üçgen Eşleme)..."
                : "Analyzing star positions in the image (RANSAC Triangle Matching)...";

            var result = await _plateSolver.SolveAsync(
                SelectedImagePath,
                (int)ImageWidth,
                (int)ImageHeight,
                VisibleObjects);

            if (result.IsSuccess && result.Wcs != null)
            {
                _isStarPatternSolved = true;
                _solvedCameraAngle = result.Wcs.RotationDegrees;
                _userManualAngleOffset = 0.0;
                CurrentWcs = result.Wcs;
                DiagnosticWarningText = result.WarningMessage;
                StellariumGuideText = result.StellariumGuide;

                // Immediately orient photo to North-Up (plus Stellarium sky roll if connected)
                ApplyStellariumSkyAngle(_stellariumSkyAngle);
                RequestCenterOnPixel?.Invoke(ImageWidth / 2.0, ImageHeight / 2.0);

                var coords = result.Wcs.CenterCoordinates;
                double refHeight = _lastVisiblePixelsHeight > 10 ? _lastVisiblePixelsHeight : ImageHeight;
                double fov = Math.Clamp((refHeight * result.Wcs.PixelScaleArcsec) / 3600.0, 0.05, 130.0);

                SolvedCoordinatesText = $"{coords.FormatRa()}  {coords.FormatDec()}";
                TargetDetailsText = IsTurkish
                    ? $"{result.TargetName} ({result.MatchedStarCount} yıldız eşleşti, Kadrajda {AvailableTargetsInPhoto.Count} gök cismi bulundu, FOV: {result.Wcs.FovWidthDegrees:F1}°x{result.Wcs.FovHeightDegrees:F1}°)"
                    : $"{result.TargetName} ({result.MatchedStarCount} stars matched, {AvailableTargetsInPhoto.Count} catalog objects in frame, FOV: {result.Wcs.FovWidthDegrees:F1}°x{result.Wcs.FovHeightDegrees:F1}°)";
                
                if (!string.IsNullOrEmpty(result.WarningMessage))
                {
                    StatusMessage = $"⚠️ {result.WarningMessage}";
                }
                else
                {
                    StatusMessage = IsTurkish
                        ? $"Yıldızlardan çözümlendi: {TargetDetailsText}"
                        : $"Solved from star pattern: {TargetDetailsText}";
                }

                if (IsStellariumConnected)
                {
                    _lastPolledStellariumCoords = coords;
                    _lastPolledStellariumFov = fov;
                    _lastSyncTicks = Environment.TickCount64;

                    await _stellariumClient.FocusCoordinatesAsync(coords, fov, 0.0);
                    var status = await _stellariumClient.GetStatusAsync();
                    if (status.IsConnected)
                    {
                        ApplyStellariumSkyAngle(status.SkyRollAngleDegrees);
                    }
                }
            }
            else
            {
                CurrentWcs = null;
                SolvedCoordinatesText = IsTurkish ? "Eşleşme Yok" : "No Match";
                DiagnosticWarningText = result.DiagnosticReason ?? (IsTurkish ? "Yıldız üçgen deseni katalogda bulunamadı." : "Star triangle pattern not found in catalog.");
                TargetDetailsText = DiagnosticWarningText;
                StatusMessage = $"⚠️ {DiagnosticWarningText}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = IsTurkish ? $"Plate solving hatası: {ex.Message}" : $"Plate solving error: {ex.Message}";
        }
        finally
        {
            IsPlateSolving = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public async Task SyncCurrentCenterToStellariumAsync()
    {
        if (!IsStellariumConnected || _currentWcs == null) return;
        try
        {
            RequestCenterOnPixel?.Invoke(ImageWidth / 2.0, ImageHeight / 2.0);
            double refHeight = _lastVisiblePixelsHeight > 10 ? _lastVisiblePixelsHeight : ImageHeight;
            double fov = Math.Clamp((refHeight * _currentWcs.PixelScaleArcsec) / 3600.0, 0.05, 130.0);
            _lastPolledStellariumCoords = _currentWcs.CenterCoordinates;
            _lastPolledStellariumFov = fov;
            _lastSyncTicks = Environment.TickCount64;

            await _stellariumClient.FocusCoordinatesAsync(_currentWcs.CenterCoordinates, fov, 0.0);
            var status = await _stellariumClient.GetStatusAsync();
            if (status.IsConnected)
            {
                ApplyStellariumSkyAngle(status.SkyRollAngleDegrees);
            }
            StatusMessage = IsTurkish
                ? $"Stellarium görsel merkezine, büyüklüğüne ({fov:F1}°) ve açısına 1:1 odaklandı."
                : $"Stellarium synchronized 1:1 to image center, FOV ({fov:F1}°), and roll angle.";
        }
        catch (Exception ex)
        {
            StatusMessage = IsTurkish ? $"Stellarium eşitleme hatası: {ex.Message}" : $"Stellarium sync error: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _stellariumPollTimer.Stop();
        _stellariumClient.Dispose();
        _engine?.Dispose();
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
