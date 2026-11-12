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

public enum InspectorTab
{
    TelemetryEngine,
    Astrometry,
    ImageAdjustments,
    Mosaic
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
    private bool _showBoundingBoxes = false;
    private bool _showLabels = false;
    private bool _isAutoBrightnessEnabled = false;
    private string _brightnessStatusText = string.Empty;
    private SkiaSharp.SKBitmap? _originalSkBitmap;
    private double _displayBrightness = 0.0;
    private double _displayExposure = 1.0;
    private double _displayContrast = 1.0;
    private bool _isLightPanelOpen = false;
    private bool _isMosaicDrawerOpen = false;
    private bool _isExportPanelOpen = false;
    private bool _isHelpPanelOpen = false;
    private string _exportFormat = "PNG";
    private string _exportProjectionMode = "Canvas2D";
    private bool _exportIncludeBoxes = true;
    private bool _exportIncludeLabels = true;
    private bool _exportIncludeGrid = false;
    private bool _exportIncludeConstellations = false;
    private int _exportScale = 1;
    private MosaicPanel? _selectedMosaicPanel;
    private bool _suppressConfidenceSync;
    private bool _suppressDisplaySync;
    private double _sphereCameraRa = 310.0;
    private double _sphereCameraDec = 45.0;
    private double _sphereCameraFov = 50.0;

    private string? _formatBadgeText;
    private string? _formatWarningToolTip;
    private string _formatBadgeIcon = "ℹ️";
    private Brush _formatBadgeBackground = new SolidColorBrush(Color.FromRgb(0x16, 0x20, 0x32));
    private Brush _formatBadgeBorder = new SolidColorBrush(Color.FromRgb(0x2A, 0x36, 0x4D));
    private Brush _formatBadgeForeground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

    public bool HasFormatBadge => !string.IsNullOrEmpty(_formatBadgeText);
    public string? FormatBadgeText => _formatBadgeText;
    public string? FormatWarningToolTip => _formatWarningToolTip;
    public string FormatBadgeIcon => _formatBadgeIcon;
    public Brush FormatBadgeBackground => _formatBadgeBackground;
    public Brush FormatBadgeBorder => _formatBadgeBorder;
    public Brush FormatBadgeForeground => _formatBadgeForeground;
    private string? _modelPath;

    private ScanTool _tool = ScanTool.Pan;
    private RegionKind _regionKind = RegionKind.Exclude;
    private readonly ScanMask _mask = new();
    private IReadOnlyList<SelectionRegion> _regionSnapshot = Array.Empty<SelectionRegion>();
    private IReadOnlyList<AstrophotographyObject> _visibleObjects = Array.Empty<AstrophotographyObject>();

    private DetectionResult? _lastDetectionResult;
    private float _optimalConfidenceThreshold = 0.25f;

    public float OptimalConfidenceThreshold
    {
        get => _optimalConfidenceThreshold;
        set => SetProperty(ref _optimalConfidenceThreshold, value);
    }

    public string L_ConfidencePanelTitle => IsTurkish ? "Hassasiyet & Güven Eşiği" : "Sensitivity & Threshold";
    public string L_ConfidencePanelToolTip => IsTurkish
        ? "Kaç yıldızın işaretleneceğini (sönüklerden en parlaklara kadar) ve fotoğraf bazlı hassasiyeti ayarlar"
        : "Adjusts how faint stars are detected and sets per-photo confidence sensitivity";
    public string L_AutoConfidenceToggle => IsTurkish ? "Otomatik Hassasiyet (Format Duyarlı)" : "Auto Sensitivity (Format-Aware)";
    public string L_AutoConfidenceDesc => IsTurkish
        ? "Fotoğrafın gürültü seviyesine ve dosya türüne (sıkıştırılmış JPG vs kayıpsız TIFF/RAW/FITS) göre en temiz eşiği otomatik belirler."
        : "Automatically selects the cleanest detection threshold per photo based on sky noise and file format (compressed JPG vs lossless TIFF/RAW/FITS).";
    public string L_RecalculateAuto => IsTurkish ? "⚡ Otomatik Eşiği Yeniden Hesapla" : "⚡ Recalculate Optimal Threshold";
    public string L_QuickPresets => IsTurkish ? "HIZLI HAZIR AYARLAR:" : "QUICK PRESETS:";
    public string L_PerPhotoConfidenceHeader => IsTurkish ? "FOTOĞRAF BAZLI HASSASİYET:" : "PER-PHOTO SENSITIVITY:";
    public string L_ActivePhotoLabel => IsTurkish ? "Hedef Fotoğraflar:" : "Target Photos:";
    public string L_DragReorderHint => IsTurkish ? "💡 Katman sırasını değiştirmek için kartları sürükleyin · Çoklu seçim için tıklayın" : "💡 Drag cards to reorder layers · Click cards to multi-select";
    public string L_AnomaliesBadgeLabel => IsTurkish ? "Sıradışı / Anomali:" : "Anomalies:";
    public string L_SelectAllPhotosButton => IsTurkish ? "☑ Tümü" : "☑ All";
    public string L_ClearSelectionButton => IsTurkish ? "☐ Seçimi Temizle" : "☐ Clear Selection";
    public string L_AiModelBadge => IsTurkish ? "🧠 YOLO11n Yapay Zekâ" : "🧠 YOLO11n AI";
    public string L_AiModelBadgeToolTip => IsTurkish
        ? "SDSS DR16 derin uzay görüntüleriyle eğitilmiş YOLO11n ONNX modeli (DirectML GPU destekli)"
        : "YOLO11n ONNX neural network trained on SDSS DR16 deep-sky imagery (DirectML GPU accelerated)";

    // Help Panel Localized Strings (Beginner-Friendly & v1.4.0 Updated)
    public string L_HelpButton => IsTurkish ? "❓ Rehber" : "❓ Guide";
    public string L_HelpToolTip => IsTurkish ? "Adım adım başlangıç rehberi ve kısayollar (F1)" : "Open beginner-friendly user guide and shortcuts (F1)";
    public string L_HelpPanelTitle => IsTurkish ? "AstroCounter v1.4.0 • Hızlı Başlangıç Rehberi" : "AstroCounter v1.4.0 • Quick Start Guide";
    public string L_HelpSec1Title => IsTurkish ? "🖱️ 1. Fotoğrafında Gezinme (2D & 3D Gök Kubbe)" : "🖱️ 1. Navigating Your Sky (2D & 3D Dome)";
    public string L_HelpSec1Body => IsTurkish
        ? "• 2D Görünüm: Sol tuşa basılı tutup sürükleyerek fotoğrafı kaydırın, fare tekerleğiyle yıldızlara istediğiniz kadar yakınlaşın.\n• 3D Gök Kubbe: Fotoğrafınızı gerçek gökyüzü küresi üzerine yerleştirir; sürükleyerek 360° etrafınıza bakabilirsiniz."
        : "• 2D View: Left-drag to pan freely across your sky canvas and use the scroll wheel to zoom smoothly.\n• 3D Celestial Dome: Projects your photos onto an interactive 360° sky sphere—drag to look around, scroll to adjust Field of View.";
    public string L_HelpSec2Title => IsTurkish ? "🧩 2. Çoklu Fotoğraf & Otomatik Mozaik Birleştirme" : "🧩 2. Multi-Photo Import & Seamless Mosaics";
    public string L_HelpSec2Body => IsTurkish
        ? "• Tek tıkla klasör veya birden çok fotoğraf sürükleyip bırakın; AstroCounter hepsini tek gökyüzü atlasında birleştirir.\n• Fotoğrafa çift tıklayarak seçebilirsiniz (Ctrl/Shift ile çoklu seçim). 'Fotoğraflar' panelinde kartları sürükleyerek üst/alt sırasını değiştirin."
        : "• Drag & drop multiple photos or an entire folder to automatically stitch them into one seamless sky atlas.\n• Double-click any photo to select it (hold Ctrl/Shift for multi-select), or drag cards inside 'Photos' to reorder layers.";
    public string L_HelpSec3Title => IsTurkish ? "☀ 3. Görüntü Işığı & Hassasiyet (Sönük Yıldız Ayarı)" : "☀ 3. Light Tuning & Sensitivity Controls";
    public string L_HelpSec3Body => IsTurkish
        ? "• Işık (☀): Yıldızları daha belirgin görmek için Parlaklık, Pozlama ve Otomatik MTF Dengeleme uygulayın (orijinal dosyanız bozulmaz).\n• Hassasiyet (⚡): Fazla gürültü varsa eşiği yükseltin; çok sönük yıldızları yakalamak istiyorsanız düşürün. Sıkıştırılmış JPEG ve kayıpsız TIFF/RAW için akıllı koruma aktiftir."
        : "• Light (☀): Boost faint nebulosity and stars safely with Brightness, Exposure, and Auto MTF Stretch without altering your original files.\n• Sensitivity (⚡): Raise the threshold to filter camera noise, or lower it to reveal fainter stars. Smart compressed JPEG & lossless TIFF/RAW calibration runs automatically.";
    public string L_HelpSec4Title => IsTurkish ? "✨ 4. Sayım Motorları & Odak Alanı (ROI)" : "✨ 4. Counting Engines & Focus Area (ROI)";
    public string L_HelpSec4Body => IsTurkish
        ? "• Hibrit Füzyon (Önerilen): Keskin yıldızlar ile yumuşak odaklı/geniş açılı gök cisimlerini DirectML yapay zekâ desteğiyle birlikte sayar.\n• Odak Alanı: Ağaç, ufuk çizgisi veya parlak şehir ışıklarını tarama dışında bırakmak için Kutu/Çokgen aracını kullanın."
        : "• Hybrid Fusion (Recommended): Combines multi-scale astronomical photometry with DirectML AI to count both pinpoint and soft stars.\n• Focus Area: Use Box or Polygon tools to include only your target region or exclude horizon silhouettes.";
    public string L_HelpSec5Title => IsTurkish ? "🔭 5. Gökyüzünde Eşle, Stellarium & Dışa Aktarma" : "🔭 5. Sky Matching, Stellarium & Export";
    public string L_HelpSec5Body => IsTurkish
        ? "• Gökyüzünde Bul: Fotoğrafınızdaki yıldız desenlerini 117k Hipparcos + Gaia/Tycho kataloğu ile eşleyerek takımyıldızları ve galaksileri işaretler.\n• Dışa Aktar (🚀): Tüm yıldız listesini CSV/JSON olarak kaydedin ya da 2D/360° yüksek çözünürlüklü gök haritası çıkarın."
        : "• Find in Sky: Plate-solves your star pattern against 117k Hipparcos + dense Gaia/Tycho catalogs to overlay constellations and deep-sky targets.\n• Export (🚀): Export star catalogs as CSV/JSON or render high-resolution 2D mosaics and 360° panoramas.";
    public string L_HelpSec6Title => IsTurkish ? "⌨️ 6. Pratik Kısayollar" : "⌨️ 6. Handy Shortcuts";
    public string L_HelpSec6Body => IsTurkish
        ? "• Ctrl + O : Fotoğraf Seç   |   Ctrl + E : Dışa Aktar\n• F1 : Rehberi Aç / Kapat   |   F11 : Tam Ekran   |   Esc : Paneli / Çizimi Kapat"
        : "• Ctrl + O : Open Photo   |   Ctrl + E : Export\n• F1 : Toggle Guide   |   F11 : Fullscreen   |   Esc : Cancel / Close Panel";

    public string L_AutoConfidenceToolTip => IsTurkish
        ? "Görselin fiziksel gürültü tabanı (knee-point) ve yapay zekâ analizine göre dinamik güven eşiğini kilitler"
        : "Automatically locks confidence threshold based on physical noise floor knee-point and AI feedback";
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
    private bool _showHipparcosStars = true;
    private bool _showConstellations = true;
    private bool _showDsoMarkers = true;
    private double _photoOpacity = 1.0;
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
    public event Action<double, double, double>? RequestSet3DCamera;
    public event Action<MosaicPanel>? RequestScrollToMosaicPanel;
    public event Action? RequestInvalidateOverlay;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsTurkish => CataloguePlateSolver.UseTurkishLanguage;
    public string LanguageButtonText => IsTurkish ? "TR | EN" : "EN | TR";
    public string L_LanguageToolTip => IsTurkish ? "Uygulama dilini değiştir (Türkçe / İngilizce)" : "Switch application language (English / Turkish)";

    // Localized UI Strings (v1.4.0 — Beginner-Friendly TR & EN)
    public string L_WindowTitle => IsTurkish
        ? "AstroCounter v1.4.0 — Astrofotoğraf Yıldız Sayacı ve 3D Gök Kubbe"
        : "AstroCounter v1.4.0 — Astrophotography Star Counter & 3D Sky Dome";
    public string L_HeaderSubtitle => IsTurkish
        ? "v1.4.0 • Akıllı Yıldız Sayımı, Mozaik Atlası ve 3D Planetaryum"
        : "v1.4.0 • Smart Star Counting, Mosaic Atlas & 3D Planetarium";
    public string L_VersionBadge => "v1.4.0";
    public string L_SplashTitle => "AstroCounter";
    public string L_SplashSubtitle => IsTurkish
        ? "Yıldızları Keşfet • Akıllı Sayım & 3D Gök Kubbe"
        : "Explore the Night Sky • Smart Counting & 3D Dome";

    public string L_PhotosButtonText => MosaicProject.Count > 0
        ? (IsTurkish ? $"Fotoğraflar ({MosaicProject.Count})" : $"Photos ({MosaicProject.Count})")
        : (IsTurkish ? "Fotoğraflar" : "Photos");
    public string L_PhotosButtonToolTip => IsTurkish
        ? "Yüklü fotoğrafları yönetin, kartları sürükleyerek sırayı değiştirin veya otomatik mozaik hizalaması yapın"
        : "Manage imported photos, drag cards to reorder layers, or align a multi-panel sky mosaic";
    public string L_AddPhotosButton => IsTurkish ? "＋ Foto Ekle" : "＋ Add Photos";
    public string L_AddPhotosToolTip => IsTurkish
        ? "Tek veya birden çok astrofotoğraf ekleyin"
        : "Import single or multiple astrophotographs";
    public string L_AlignAllButton => IsTurkish ? "🔭 Tümünü Hizala" : "🔭 Align All";
    public string L_AlignAllToolTip => IsTurkish
        ? "Tüm fotoğrafları yıldız desenlerinden gökyüzüne yerleştirip tek mozaik atlasında birleştirir"
        : "Plate-solves and aligns all loaded photos into one unified sky mosaic";
    public string L_ClearAllButton => IsTurkish ? "🗑 Temizle" : "🗑 Clear";
    public string L_ClearAllToolTip => IsTurkish
        ? "Yüklü tüm fotoğrafları ve mozaik katmanlarını kaldırır"
        : "Clear all loaded photos and reset the current mosaic session";
    public string L_SelectImage => IsTurkish ? "Fotoğraf Aç" : "Open Photo";
    public string L_SelectImageToolTip => IsTurkish
        ? "Bilgisayarınızdan bir veya daha fazla astrofotoğraf seçin (Ctrl+O)"
        : "Browse and open one or more astrophotographs from your computer (Ctrl+O)";
    public string L_ExportButtonToolTip => IsTurkish
        ? "Yıldız sayım verilerini (CSV/JSON) veya yüksek çözünürlüklü 2D/360° gök haritasını kaydedin (Ctrl+E)"
        : "Export star catalog tables (CSV/JSON) or high-res 2D & 360° sky maps (Ctrl+E)";
    public string L_ZoomFitToolTip => IsTurkish
        ? "Fotoğrafı ekrana sığdır"
        : "Fit full image into viewport";
    public string L_Zoom100ToolTip => IsTurkish
        ? "Fotoğrafı %100 gerçek piksel boyutunda göster"
        : "View image at 100% native pixel scale";
    public string L_Reanalyze => IsTurkish ? "Yeniden Analiz Et" : "Re-Analyze";
    public string L_ExportCsv => IsTurkish ? "CSV Dışa Aktar" : "Export CSV";
    public string L_ExportImage => IsTurkish ? "Görseli Kaydet" : "Save Image";

    public string L_EngineGroup => IsTurkish ? "MOTOR" : "ENGINE";
    public string L_HybridFusion => IsTurkish ? "🔬 Hibrit Füzyon" : "🔬 Hybrid Fusion";
    public string L_Photometry => IsTurkish ? "Fotometri" : "Photometry";
    public string L_Yolo => "DirectML";
    public string L_SelectModel => IsTurkish ? "Model Seç..." : "Select Model...";
    public string L_LightPanelButton => IsTurkish ? "Işık" : "Light";
    public string L_LightPanelTitle => IsTurkish ? "Görüntü & Işık Ayarları" : "Display & Light Settings";
    public string L_LightPanelToolTip => IsTurkish
        ? "Sönük yıldızları ve bulutsuları daha net görmek için parlaklık, pozlama ve kontrastı ayarlayın"
        : "Adjust brightness, exposure, and contrast to reveal faint stars and nebulosity";
    public string L_AutoBalanceLabel => IsTurkish ? "Otomatik Işık & Renk Dengeleme (MTF)" : "Auto Exposure & Color Stretch (MTF)";
    public string L_AutoBalanceNow => IsTurkish ? "⚡ Şimdi Dengele" : "⚡ Stretch Now";
    public string L_BrightnessLabel => IsTurkish ? "Parlaklık:" : "Brightness:";
    public string L_ExposureLabel => IsTurkish ? "Pozlama (Gama):" : "Exposure (Gamma):";
    public string L_ContrastLabel => IsTurkish ? "Kontrast:" : "Contrast:";
    public string L_ResetAdjustments => IsTurkish ? "↺ Ayarları Sıfırla" : "↺ Reset Defaults";

    public string L_AutoBrightnessToolTip => IsTurkish
        ? "Çok karanlık veya ışık kirlilikli kareleri astronomik MTF eğrisiyle otomatik dengeler (orijinal dosyayı değiştirmez)"
        : "Automatically balances dark or light-polluted frames with an astronomical MTF stretch without altering your original files";

    public string L_ScanAreaGroup => IsTurkish ? "ODAK ALANI" : "FOCUS AREA";
    public string L_Pan => IsTurkish ? "Gezin" : "Pan";
    public string L_Rectangle => IsTurkish ? "Dikdörtgen" : "Box";
    public string L_Polygon => IsTurkish ? "Çokgen" : "Polygon";
    public string L_Exclude => IsTurkish ? "Hariç tut" : "Exclude";
    public string L_Include => IsTurkish ? "Sadece tara" : "Scan only";
    public string L_Undo => IsTurkish ? "Geri Al" : "Undo";
    public string L_Clear => IsTurkish ? "Temizle" : "Clear";

    public string L_StellariumGroup => IsTurkish ? "GÖK KUBBE / PLANETARYUM" : "CELESTIAL PLANETARIUM";
    public string L_PlateSolveButton => IsTurkish ? "Gökyüzünde Bul" : "Find in Sky";
    public string L_PlateSolveToolTip => IsTurkish
        ? "Fotoğrafınızdaki yıldız desenlerini kataloglarla eşleyip gökyüzündeki gerçek konumunu, ölçeğini ve açısını bulur"
        : "Matches your star pattern against sky catalogs to automatically pinpoint coordinates, scale, and camera roll";
    public string L_TargetComboToolTip => IsTurkish
        ? "Fotoğrafınızdaki bilinen takımyıldız ve derin uzay nesnelerini listeler; seçtiğiniz cisme odaklanır"
        : "Lists known constellations and deep-sky targets inside your frame; select any target to focus directly on it";
    public string L_TwoWaySync => IsTurkish ? "Çift Yönlü 1:1" : "Two-Way 1:1";
    public string L_TwoWaySyncToolTip => IsTurkish
        ? "2D fotoğraf ve 3D gök kubbe senkronize çalışır: Birini kaydırıp yakınlaştırdığınızda diğeri bire bir takip eder"
        : "Links the 2D canvas and 3D dome bidirectionally: panning or zooming either view syncs the other 1:1";
    public string L_SphericalGrid => IsTurkish ? "Gökyüzü Izgarası" : "Sky Coord Grid";
    public string L_HipparcosStars => IsTurkish ? "Yıldızlar (HIP)" : "Stars (HIP)";
    public string L_HipparcosStarsToolTip => IsTurkish
        ? "117.955 Hipparcos yıldız kataloğunu gerçek spektral renkleri ve parlaklıklarıyla gök haritasına yansıtır"
        : "Projects 117,955 ESA Hipparcos catalog stars with authentic spectral colors and magnitudes onto the sky";
    public string L_Constellations => IsTurkish ? "Takımyıldızlar" : "Constellations";
    public string L_ConstellationsToolTip => IsTurkish
        ? "Takımyıldız çizgilerini ve adlarını gökyüzü koordinatlarına göre hizalayarak gösterir"
        : "Draws constellation stick figures and names aligned accurately to celestial coordinates";
    public string L_DsoMarkers => IsTurkish ? "Derin Uzay (DSO)" : "Deep Sky (DSO)";
    public string L_DsoMarkersToolTip => IsTurkish
        ? "Messier ve NGC derin uzay nesnelerinin (galaksi, bulutsu, yıldız kümesi) işaretçilerini gösterir"
        : "Displays markers and labels for Messier & NGC deep-sky objects (galaxies, nebulae, clusters)";
    public string L_PhotoOpacityLabel => IsTurkish ? "Saydamlık:" : "Opacity:";
    public string L_PhotoOpacityToolTip => IsTurkish
        ? "Fotoğraf saydamlığını azaltarak alttaki Hipparcos yıldız haritası ve takımyıldızlarla bire bir karşılaştırın"
        : "Lower photo opacity to compare your stars side-by-side with the underlying Hipparcos map and constellations";
    public string L_SphericalGridToolTip => IsTurkish
        ? "Fotoğrafın üzerine küresel RA/Dec gökyüzü koordinat ağını çizer"
        : "Draws spherical RA/Dec equatorial coordinate grid lines over the sky";
    public string L_AngleLabel => IsTurkish ? "Açı:" : "Angle:";
    public string L_AngleToolTip => IsTurkish
        ? "Fotoğraf yıldız eşleşmesine göre otomatik döner; dilerseniz buradan 0°–360° elle ayarlayabilirsiniz"
        : "Rotates automatically to sky north after plate-solving, or adjust manually from 0° to 360°";
    public string L_MosaicDrawerButton => IsTurkish ? "Mozaik Paneli" : "Mosaic Manager";
    public string L_MosaicDrawerTitle => IsTurkish ? "Mozaik Kareleri Yönetimi" : "Mosaic Tiles Manager";
    public string L_RemovePanel => IsTurkish ? "Kaldır" : "Remove";
    public string L_TileOpacity => IsTurkish ? "Opaklık" : "Opacity";
    public string L_EmptyMosaic => IsTurkish ? "Henüz mozaik atlasına eklenmiş kare yok." : "No mosaic tiles added yet.";

    public string L_AddToMosaic => IsTurkish ? "Mozaik Atlasına Ekle" : "Add to Mosaic";
    public string L_AddToMosaicToolTip => IsTurkish
        ? "Çözülen fotoğrafı mozaik gökyüzü atlasına ekler; birden çok kareyi tek panoramada birleştirebilirsiniz"
        : "Adds the solved frame to the mosaic atlas so multiple panels stitch into one sky view";
    public string L_ClearMosaic => IsTurkish ? "Mozaiği Sıfırla" : "Reset Mosaic";
    public string L_FeatheredBlending => IsTurkish ? "Yumuşak Dikiş Geçişi (Feathered)" : "Feathered Alpha Blending";
    public string L_MoveUp => IsTurkish ? "Öne Taşı" : "Move Forward";
    public string L_MoveDown => IsTurkish ? "Arkaya Taşı" : "Move Back";
    public string L_BringToFront => IsTurkish ? "En Öne Getir" : "Bring to Front";
    public string L_SendToBack => IsTurkish ? "En Arkaya Gönder" : "Send to Back";

    public string L_SphereMode => IsTurkish ? "🌌 3D Gök Kubbe / Planetaryum" : "🌌 3D Celestial Dome";
    public string L_SphereModeToolTip => IsTurkish
        ? "Fotoğraflarınızı ve 117k Hipparcos yıldızını 360° dönebilen etkileşimli gök kubbe içinde birleştirir"
        : "Places your astrophotographs inside an interactive 360° celestial sphere with 117k Hipparcos stars";
    public string L_ResetAngleToolTip => IsTurkish
        ? "Açıyı gökyüzünün doğal kuzey yönelimine sıfırla"
        : "Reset rotation to the solved celestial north orientation";

    public string L_TotalObjects => IsTurkish ? "TOPLAM NESNE" : "TOTAL OBJECTS";
    public string L_Tier1Label => IsTurkish ? "Parlak (>10σ)" : "Bright (>10σ)";
    public string L_Tier1ToolTip => IsTurkish
        ? "Kademe 1: En belirgin, yüksek sinyal-gürültü oranlı parlak yıldızlar (>10σ)"
        : "Tier 1: High-confidence prominent stars (SNR > 10σ)";
    public string L_Tier2Label => IsTurkish ? "Orta (5-10σ)" : "Medium (5-10σ)";
    public string L_Tier2ToolTip => IsTurkish
        ? "Kademe 2: Net seçilen standart gök cisimleri (5–10σ)"
        : "Tier 2: Clear standard field stars (SNR 5–10σ)";
    public string L_Tier3Label => IsTurkish ? "Sönük (3-5σ)" : "Faint (3-5σ)";
    public string L_Tier3ToolTip => IsTurkish
        ? "Kademe 3: Çıplak gözle zor görülen sönük yıldızlar (3–5σ)"
        : "Tier 3: Faint stars near the visual limit (SNR 3–5σ)";
    public string L_Speculative => IsTurkish ? "ADAY (<3σ)" : "CANDIDATE (<3σ)";
    public string L_SpeculativeToolTip => IsTurkish
        ? "Arka plan gürültüsü sınırındaki çok sönük aday tespitler (<3σ)"
        : "Ultra-faint candidates near the sky background noise floor (<3σ)";
    public string L_ProcessingTime => IsTurkish ? "SÜRE" : "DURATION";
    public string L_ConfidenceLabel => IsTurkish ? "Eşik:" : "Threshold:";
    public string L_Boxes => IsTurkish ? "Kutular" : "Boxes";
    public string L_Labels => IsTurkish ? "Etiketler" : "Labels";
    public string L_LabelsPanelTitle => IsTurkish ? "Görünüm & Katmanlar" : "View & Layers";
    public string L_LabelsPanelButton => IsTurkish ? "Katmanlar" : "Layers";
    public string L_LabelsPanelToolTip => IsTurkish
        ? "Tespit kutularını, yıldız etiketlerini, takımyıldızları ve gök haritası katmanlarını yönetin"
        : "Toggle detection boxes, labels, constellation lines, and sky atlas overlays";
    public string L_DetectionsCategory => IsTurkish ? "Nesne Sayımı Katmanları" : "Detection Overlays";
    public string L_AtlasCategory => IsTurkish ? "Gökyüzü Atlası Katmanları" : "Celestial Atlas Layers";
    public string L_CheckAll => IsTurkish ? "Tümünü Aç" : "Enable All";
    public string L_UncheckAll => IsTurkish ? "Tümünü Kapat" : "Disable All";

    public string L_PlaceholderTitle => IsTurkish
        ? "İlk Astrofotoğrafınızı Keşfetmeye Hazır mısınız?"
        : "Ready to Explore Your First Astrophotograph?";
    public string L_PlaceholderFormats => IsTurkish
        ? "Tek fotoğraf veya klasörü buraya bırakın • TIFF, DNG/RAW, FITS, PNG ve JPG desteklenir"
        : "Drop photos or folders right here • Supports TIFF, DNG/RAW, FITS, PNG & JPG";
    public string L_PlaceholderBrowse => IsTurkish
        ? "Fotoğraf Seç ve Başla"
        : "Open Photo to Begin";
    public string L_PlaceholderDropHint => IsTurkish
        ? "💡 İpucu: Birden fazla kare bırakarak otomatik gökyüzü mozaiği oluşturabilirsiniz"
        : "💡 Tip: Drop multiple overlapping frames to auto-stitch a seamless sky mosaic";
    public string L_OnboardStep1Title => IsTurkish ? "1. Fotoğraf Yükle" : "1. Drop Photos";
    public string L_OnboardStep1Desc => IsTurkish ? "Tek kare ya da çoklu çekim bırakın" : "Single shots or multi-panel sets";
    public string L_OnboardStep2Title => IsTurkish ? "2. Akıllı Sayım" : "2. Smart Count";
    public string L_OnboardStep2Desc => IsTurkish ? "Parlak ve sönük yıldızları sayın" : "Pinpoint & faint stars counted cleanly";
    public string L_OnboardStep3Title => IsTurkish ? "3. 3D Gök Kubbe" : "3. 3D Sky Dome";
    public string L_OnboardStep3Desc => IsTurkish ? "Takımyıldız ve galaksileri keşfedin" : "Explore constellations & galaxies live";

    public string L_CursorLabel => IsTurkish ? "İmleç: " : "Cursor: ";
    public string L_ZoomLabel => IsTurkish ? "Yakınlaştırma: " : "Zoom: ";
    public string L_FitButton => IsTurkish ? "Sığdır" : "Fit";

    public string L_Mode2D => IsTurkish ? "2D Görünüm" : "2D Canvas";
    public string L_Mode3D => IsTurkish ? "3D Gök Kubbe" : "3D Dome";
    public string L_TabDetection => IsTurkish ? "Nesne Sayımı" : "Object Detection";
    public string L_TabAstrometry => IsTurkish ? "Gökyüzü Konumu" : "Astrometry";
    public string L_TabImage => IsTurkish ? "Işık & Renk" : "Image Tuning";
    public string L_TabMosaic => IsTurkish ? "Mozaik Atlası" : "Mosaic Atlas";

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
            ? "Analiz etmek için bir veya birden fazla astrofotoğraf sürükleyip bırakın ya da 'Fotoğraf Aç'a tıklayın."
            : "Drag and drop one or more astrophotographs to explore, or click 'Open Photo'.";
        _brightnessStatusText = IsTurkish ? "Oto Parlaklık" : "Auto Brightness";
        _stellariumStatusText = IsTurkish ? "Canlı Senk: Bağlan" : "Live Sync: Connect";

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
        PlateSolveCommand = new RelayCommand(async () => await ExecuteSmartPlateSolveAsync(), () => !IsLoading && !IsPlateSolving && (!string.IsNullOrEmpty(SelectedImagePath) || MosaicProject.Count > 0));
        SyncToStellariumCommand = new RelayCommand(async () => await SyncCurrentCenterToStellariumAsync(), () => CanSyncToStellarium);
        ResetRotationCommand = new RelayCommand(ResetRotationToStellarium);
        AddToMosaicCommand = new RelayCommand(ExecuteAddToMosaic, () => CurrentWcs != null);
        ClearMosaicCommand = new RelayCommand(ExecuteClearMosaic, () => MosaicProject.Count > 0);
        ToggleSphereModeCommand = new RelayCommand(() => IsSphereModeActive = !IsSphereModeActive);
        Select2DViewCommand = new RelayCommand(() => IsSphereModeActive = false);
        Select3DViewCommand = new RelayCommand(() => IsSphereModeActive = true);
        ToggleLanguageCommand = new RelayCommand(ExecuteToggleLanguage);
        ToggleInspectorCommand = new RelayCommand(() => IsInspectorOpen = !IsInspectorOpen);
        SelectInspectorTabCommand = new RelayCommand(p =>
        {
            if (p is InspectorTab t)
            {
                CurrentInspectorTab = t;
                IsInspectorOpen = true;
            }
            else if (p is string s && Enum.TryParse<InspectorTab>(s, true, out var parsed))
            {
                CurrentInspectorTab = parsed;
                IsInspectorOpen = true;
            }
        });
        ToggleLabelsPanelCommand = new RelayCommand(() => IsLabelsPanelOpen = !IsLabelsPanelOpen);
        CloseLabelsPanelCommand = new RelayCommand(() => IsLabelsPanelOpen = false);
        CheckAllOverlaysCommand = new RelayCommand(() =>
        {
            ShowBoundingBoxes = true;
            ShowLabels = true;
            ShowHipparcosStars = true;
            ShowConstellations = true;
            ShowDsoMarkers = true;
            ShowSphericalGrid = true;
        });
        UncheckAllOverlaysCommand = new RelayCommand(() =>
        {
            ShowBoundingBoxes = false;
            ShowLabels = false;
            ShowHipparcosStars = false;
            ShowConstellations = false;
            ShowDsoMarkers = false;
            ShowSphericalGrid = false;
        });
        ToggleConfidencePanelCommand = new RelayCommand(() => IsConfidencePanelOpen = !IsConfidencePanelOpen);
        CloseConfidencePanelCommand = new RelayCommand(() => IsConfidencePanelOpen = false);
        PresetConfidenceCommand = new RelayCommand(p =>
        {
            if (p is string s && float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float val))
            {
                IsAutoConfidenceEnabled = false;
                ConfidenceThreshold = val;
            }
            else if (p is double d)
            {
                IsAutoConfidenceEnabled = false;
                ConfidenceThreshold = (float)d;
            }
        });
        ToggleLightPanelCommand = new RelayCommand(() => IsLightPanelOpen = !IsLightPanelOpen);
        ToggleMosaicDrawerCommand = new RelayCommand(() => IsMosaicDrawerOpen = !IsMosaicDrawerOpen);
        ApplyAutoConfidenceCommand = new RelayCommand(ExecuteApplyAutoConfidence, () => _lastDetectionResult != null);
        ToggleExportPanelCommand = new RelayCommand(() => IsExportPanelOpen = !IsExportPanelOpen, () => HasLoadedImage || _lastDetectionResult != null);
        CloseExportPanelCommand = new RelayCommand(() => IsExportPanelOpen = false);
        PerformExportCommand = new RelayCommand(ExecutePerformExport);
        CloseMosaicDrawerCommand = new RelayCommand(() => IsMosaicDrawerOpen = false);
        RemoveMosaicPanelCommand = new RelayCommand(p => ExecuteRemoveMosaicPanel(p as MosaicPanel));
        MoveMosaicPanelUpCommand = new RelayCommand(p => ExecuteMoveMosaicPanelUp(p as MosaicPanel));
        MoveMosaicPanelDownCommand = new RelayCommand(p => ExecuteMoveMosaicPanelDown(p as MosaicPanel));
        BringMosaicPanelToFrontCommand = new RelayCommand(p => ExecuteBringMosaicPanelToFront(p as MosaicPanel));
        SendMosaicPanelToBackCommand = new RelayCommand(p => ExecuteSendMosaicPanelToBack(p as MosaicPanel));
        SelectMosaicPanelCommand = new RelayCommand(p => ExecuteSelectMosaicPanel(p as MosaicPanel));
        CountMosaicAtlasCommand = new RelayCommand(async () => await ExecuteAnalyzeAsync(), () => MosaicProject.Count > 0 && !IsLoading);
        AddPhotosCommand = new RelayCommand(ExecuteAddPhotos);
        ClearAllPanelsCommand = new RelayCommand(ExecuteClearMosaic);
        PlateSolveAllPanelsCommand = new RelayCommand(async () => await ExecutePlateSolveAllPanelsAsync(), () => MosaicProject.Count > 0 && !IsLoading);
        ToggleFeatheredBlendingCommand = new RelayCommand(() => UseFeatheredBlending = !UseFeatheredBlending);
        CloseLightPanelCommand = new RelayCommand(() => IsLightPanelOpen = false);
        ResetDisplayAdjustmentsCommand = new RelayCommand(ResetDisplayAdjustments);
        AutoBalanceNowCommand = new RelayCommand(AutoBalanceNow);
        ToggleHelpPanelCommand = new RelayCommand(() => IsHelpPanelOpen = !IsHelpPanelOpen);
        CloseHelpPanelCommand = new RelayCommand(() => IsHelpPanelOpen = false);
        SelectAllPanelsCommand = new RelayCommand(SelectAllPanels, () => MosaicProject.Count > 0);
        ClearPanelSelectionCommand = new RelayCommand(ClearPanelSelection, () => MosaicProject.Panels.Any(p => p.IsSelected));
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
            StellariumStatusText = IsTurkish ? "Canlı Senk: Bağlan" : "Live Sync: Connect";
        }
        else
        {
            StellariumStatusText = IsTurkish ? "Canlı Senk: Bağlı" : "Live Sync: Active";
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

        if (string.IsNullOrEmpty(SelectedImagePath))
        {
            StatusMessage = IsTurkish
                ? "Analiz etmek için bir veya birden fazla astrofotoğraf sürükleyip bırakın ya da 'Fotoğraf Aç'a tıklayın."
                : "Drag and drop one or more astrophotographs to explore, or click 'Open Photo'.";
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

        foreach (var p in MosaicProject.Panels)
        {
            p.NotifyLocalizationChanged();
        }

        // Notify WPF to refresh all bindings on MainViewModel
        OnPropertyChanged(string.Empty);
        RequestInvalidateOverlay?.Invoke();
    }

    private DetectionEngineMode _selectedEngineMode = DetectionEngineMode.HybridFusion;
    public DetectionEngineMode SelectedEngineMode
    {
        get => _selectedEngineMode;
        set
        {
            if (SetProperty(ref _selectedEngineMode, value))
            {
                OnPropertyChanged(nameof(IsHybridFusionMode));
                OnPropertyChanged(nameof(IsPhotometricMode));
                OnPropertyChanged(nameof(IsYoloMode));
                if (_engine != null)
                {
                    _engine.Mode = value;
                    HardwareInfo = _engine.ExecutionProviderName;
                }
                if (!string.IsNullOrEmpty(SelectedImagePath))
                {
                    _ = ExecuteAnalyzeAsync();
                }
            }
        }
    }

    public bool IsHybridFusionMode
    {
        get => _selectedEngineMode == DetectionEngineMode.HybridFusion;
        set { if (value) SelectedEngineMode = DetectionEngineMode.HybridFusion; }
    }

    public bool IsPhotometricMode
    {
        get => _selectedEngineMode == DetectionEngineMode.Photometric;
        set { if (value) SelectedEngineMode = DetectionEngineMode.Photometric; }
    }

    public bool IsYoloMode
    {
        get => _selectedEngineMode == DetectionEngineMode.DeepLearningYolo;
        set { if (value) SelectedEngineMode = DetectionEngineMode.DeepLearningYolo; }
    }

    private void InitializeEngine()
    {
        try
        {
            _engine?.Dispose();
            _engine = new OnnxInferenceEngine(_modelPath, initialMode: _selectedEngineMode);
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
                OnPropertyChanged(nameof(ImageFileName));
                OnPropertyChanged(nameof(HasLoadedImage));
                if (!string.IsNullOrEmpty(value) && File.Exists(value))
                {
                    if (MosaicProject.Count <= 1)
                    {
                        ResetForNewImage();
                        LoadBitmapPreview(value);
                        _ = AnalyzeAndPlateSolveSequentiallyAsync();
                    }
                    else
                    {
                        LoadBitmapPreview(value);
                        RequestInvalidateOverlay?.Invoke();
                    }
                }
            }
        }
    }

    public string? ImageFileName => string.IsNullOrEmpty(SelectedImagePath) ? null : Path.GetFileName(SelectedImagePath);
    public string ImageDimensionText => ImageWidth > 0 && ImageHeight > 0 ? $"{(int)ImageWidth} × {(int)ImageHeight} px" : string.Empty;
    public bool HasLoadedImage => ImageBitmap != null || !string.IsNullOrEmpty(SelectedImagePath) || MosaicProject.Count > 0;

    public double CanvasWidth
    {
        get
        {
            if (MosaicProject.Count > 1)
            {
                if (CurrentWcs != null) return CurrentWcs.ImageWidth;
                double sumW = MosaicProject.Panels.Sum(p => (double)p.Width);
                return sumW > 10 ? sumW : Math.Max(1, ImageWidth);
            }
            return Math.Max(1, ImageWidth);
        }
    }

    public double CanvasHeight
    {
        get
        {
            if (MosaicProject.Count > 1)
            {
                if (CurrentWcs != null) return CurrentWcs.ImageHeight;
                double maxH = MosaicProject.Panels.Count > 0 ? MosaicProject.Panels.Max(p => (double)p.Height) : 0;
                return maxH > 10 ? maxH : Math.Max(1, ImageHeight);
            }
            return Math.Max(1, ImageHeight);
        }
    }
    public bool IsSingleImageActive => MosaicProject.Count <= 1;

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
                OnPropertyChanged(nameof(HasLoadedImage));
                OnPropertyChanged(nameof(ImageDimensionText));
            }
        }
    }

    public bool IsPlaceholderVisible => ImageBitmap == null && MosaicProject.Count == 0;

    public double ImageWidth
    {
        get => _imageWidth;
        private set
        {
            if (SetProperty(ref _imageWidth, value))
            {
                OnPropertyChanged(nameof(CanvasWidth));
                OnPropertyChanged(nameof(ImageDimensionText));
            }
        }
    }

    public double ImageHeight
    {
        get => _imageHeight;
        private set
        {
            if (SetProperty(ref _imageHeight, value))
            {
                OnPropertyChanged(nameof(CanvasHeight));
                OnPropertyChanged(nameof(ImageDimensionText));
            }
        }
    }

    public MosaicPanel? SelectedMosaicPanel
    {
        get => _selectedMosaicPanel;
        private set
        {
            if (SetProperty(ref _selectedMosaicPanel, value))
            {
                OnPropertyChanged(nameof(ActivePanelDisplayName));
            }
        }
    }

    public string ActivePanelDisplayName
    {
        get
        {
            var selected = MosaicProject.Panels.Where(p => p.IsSelected).ToList();
            if (selected.Count > 1)
            {
                return IsTurkish ? $"{selected.Count} Fotoğraf Seçili" : $"{selected.Count} Photos Selected";
            }
            if (selected.Count == 1)
            {
                return $"{selected[0].Name} ({selected[0].FormatBadgeText})";
            }
            if (MosaicProject.Count > 0)
            {
                return IsTurkish
                    ? $"Tüm Fotoğraflar ({MosaicProject.Count})"
                    : $"All Photos ({MosaicProject.Count})";
            }
            return !string.IsNullOrEmpty(ImageFileName)
                ? ImageFileName
                : (IsTurkish ? "Fotoğraf Seçilmedi" : "No Photo Selected");
        }
    }

    public float ConfidenceThreshold
    {
        get => _confidenceThreshold;
        set
        {
            float rounded = (float)Math.Round(Math.Clamp(value, 0.02f, 0.99f), 2);
            if (SetProperty(ref _confidenceThreshold, rounded))
            {
                if (!_suppressConfidenceSync)
                {
                    var activePanel = GetActiveMosaicPanel();
                    if (activePanel != null)
                    {
                        activePanel.IsAutoConfidenceEnabled = _isAutoConfidenceEnabled;
                        activePanel.ConfidenceThreshold = rounded;
                    }
                }
                OnPropertyChanged(nameof(ConfidencePercentText));
                OnPropertyChanged(nameof(ConfidenceBrush));
                OnPropertyChanged(nameof(ConfidenceStatusText));
                OnPropertyChanged(nameof(ConfidenceButtonText));
                ApplyLiveFilter();
            }
        }
    }

    private MosaicPanel? GetActiveMosaicPanel()
    {
        if (_selectedMosaicPanel != null && MosaicProject.Panels.Contains(_selectedMosaicPanel))
        {
            return _selectedMosaicPanel;
        }
        if (!string.IsNullOrEmpty(SelectedImagePath))
        {
            return MosaicProject.Panels.FirstOrDefault(p =>
                !string.IsNullOrEmpty(p.FilePath) &&
                string.Equals(p.FilePath, SelectedImagePath, StringComparison.OrdinalIgnoreCase));
        }
        return MosaicProject.Panels.FirstOrDefault();
    }

    public string ConfidencePercentText => IsTurkish
        ? $"%{(int)Math.Round(_confidenceThreshold * 100)}"
        : $"{(int)Math.Round(_confidenceThreshold * 100)}%";

    public string ConfidenceButtonText => _isAutoConfidenceEnabled
        ? (IsTurkish ? $"⚡ Oto Hassasiyet ({ConfidencePercentText})" : $"⚡ Auto Sensitivity ({ConfidencePercentText})")
        : (IsTurkish ? $"⚡ Hassasiyet ({ConfidencePercentText})" : $"⚡ Sensitivity ({ConfidencePercentText})");

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

    private int _anomalyCount;
    public int AnomalyCount
    {
        get => _anomalyCount;
        private set
        {
            if (SetProperty(ref _anomalyCount, value))
            {
                OnPropertyChanged(nameof(HasAnomalies));
            }
        }
    }

    public bool HasAnomalies => _anomalyCount > 0;


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

    private void ExecuteApplyAutoConfidence()
    {
        if (_lastDetectionResult == null) return;
        var activePanel = GetActiveMosaicPanel();
        if (activePanel != null)
        {
            activePanel.IsAutoConfidenceEnabled = true;
            activePanel.ConfidenceThreshold = activePanel.AutoConfidenceThreshold;
            ConfidenceThreshold = activePanel.AutoConfidenceThreshold;
        }
        else
        {
            ConfidenceThreshold = _lastDetectionResult.RecommendedConfidence;
        }
        StatusMessage = IsTurkish
            ? $"⚡ Dinamik güven eşiği kilitlendi: %{(int)Math.Round(ConfidenceThreshold * 100)} (Format duyarlı gürültü tabanı)"
            : $"⚡ Dynamic confidence locked: {(int)Math.Round(ConfidenceThreshold * 100)}% (Format-aware noise floor knee-point)";
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
        AnomalyCount = 0;
        Tier1Count = 0;
        Tier2Count = 0;
        Tier3Count = 0;
        SpeculativeCount = 0;
        var existingMosaicPanel = !string.IsNullOrEmpty(SelectedImagePath)
            ? MosaicProject.Panels.FirstOrDefault(p =>
                !string.IsNullOrEmpty(p.FilePath) &&
                string.Equals(p.FilePath, SelectedImagePath, StringComparison.OrdinalIgnoreCase))
            : null;
        if (MosaicProject.Count <= 1)
        {
            CurrentWcs = existingMosaicPanel?.Wcs;
        }
        else
        {
            var masterWcs = MosaicProject.ComputeMasterWcs();
            CurrentWcs = masterWcs ?? existingMosaicPanel?.Wcs;
        }
        SolvedCoordinatesText = existingMosaicPanel?.Wcs != null ? existingMosaicPanel.Wcs.CenterCoordinates.ToString() : "-";
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

    public ICommand ApplyAutoConfidenceCommand { get; }
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
    public ICommand AddToMosaicCommand { get; }
    public ICommand ClearMosaicCommand { get; }
    public ICommand AddPhotosCommand { get; }
    public ICommand ClearAllPanelsCommand { get; }
    public ICommand PlateSolveAllPanelsCommand { get; }
    public ICommand ToggleSphereModeCommand { get; }
    public ICommand Select2DViewCommand { get; }
    public ICommand Select3DViewCommand { get; }

    private bool _isSphereModeActive = false;
    public bool IsSphereModeActive
    {
        get => _isSphereModeActive;
        set
        {
            if (SetProperty(ref _isSphereModeActive, value))
            {
                OnPropertyChanged(nameof(Is2DViewActive));
            }
        }
    }

    public bool Is2DViewActive
    {
        get => !_isSphereModeActive;
        set
        {
            if (value)
            {
                IsSphereModeActive = false;
            }
            else
            {
                IsSphereModeActive = true;
            }
        }
    }

    public bool IsExportPanelOpen
    {
        get => _isExportPanelOpen;
        set => SetProperty(ref _isExportPanelOpen, value);
    }

    public string ExportFormat
    {
        get => _exportFormat;
        set
        {
            if (SetProperty(ref _exportFormat, value))
            {
                OnPropertyChanged(nameof(IsFormatPng));
                OnPropertyChanged(nameof(IsFormatTiff));
                OnPropertyChanged(nameof(IsFormatJpg));
                OnPropertyChanged(nameof(IsFormatCsv));
                OnPropertyChanged(nameof(IsFormatJson));
            }
        }
    }

    public bool IsFormatPng { get => _exportFormat == "PNG"; set { if (value) ExportFormat = "PNG"; } }
    public bool IsFormatTiff { get => _exportFormat == "TIFF"; set { if (value) ExportFormat = "TIFF"; } }
    public bool IsFormatJpg { get => _exportFormat == "JPG"; set { if (value) ExportFormat = "JPG"; } }
    public bool IsFormatCsv { get => _exportFormat == "CSV"; set { if (value) ExportFormat = "CSV"; } }
    public bool IsFormatJson { get => _exportFormat == "JSON"; set { if (value) ExportFormat = "JSON"; } }

    public bool ExportIncludeBoxes
    {
        get => _exportIncludeBoxes;
        set => SetProperty(ref _exportIncludeBoxes, value);
    }

    public bool ExportIncludeLabels
    {
        get => _exportIncludeLabels;
        set => SetProperty(ref _exportIncludeLabels, value);
    }

    public bool ExportIncludeGrid
    {
        get => _exportIncludeGrid;
        set => SetProperty(ref _exportIncludeGrid, value);
    }

    public bool ExportIncludeConstellations
    {
        get => _exportIncludeConstellations;
        set => SetProperty(ref _exportIncludeConstellations, value);
    }

    public int ExportScale
    {
        get => _exportScale;
        set
        {
            if (SetProperty(ref _exportScale, value))
            {
                OnPropertyChanged(nameof(IsScale1x));
                OnPropertyChanged(nameof(IsScale2x));
            }
        }
    }

    public bool IsScale1x { get => _exportScale == 1; set { if (value) ExportScale = 1; } }
    public bool IsScale2x { get => _exportScale == 2; set { if (value) ExportScale = 2; } }

    public string L_ExportPanelTitle => IsTurkish ? "Gelişmiş Dışa Aktarma" : "Export Sky Atlas & Data";
    public string L_ExportButton => IsTurkish ? "🚀 Dışa Aktar..." : "🚀 Export...";
    public string L_PerformExport => IsTurkish ? "💾 Dosyayı Kaydet" : "💾 Save File";
    public string L_ExportProjectionLabel => IsTurkish ? "Çıktı Türü:" : "Output Mode:";
    public string L_ExportProj2D => IsTurkish ? "🖼️ 2D Görsel / Mozaik" : "🖼️ 2D Canvas / Mosaic";
    public string L_ExportProj360 => IsTurkish ? "🌐 360° Panoramik Gök Haritası" : "🌐 360° Panoramic Sky Map";
    public string L_ExportFormatLabel => IsTurkish ? "Format:" : "Format:";
    public string L_ExportLayersLabel => IsTurkish ? "Katmanlar:" : "Layers:";
    public string L_ExportScaleLabel => IsTurkish ? "Çözünürlük:" : "Resolution:";
    public string L_FormatTiffLossless => IsTurkish ? "TIFF (Kayıpsız)" : "TIFF (Lossless)";
    public string L_ExportBoxes => IsTurkish ? "Tespit Kutuları" : "Detection Boxes";
    public string L_ExportLabels => IsTurkish ? "Yıldız Etiketleri" : "Star Labels";
    public string L_ExportGrid => IsTurkish ? "Gökyüzü Izgarası (RA/Dec)" : "Sky Grid (RA/Dec)";
    public string L_ExportConstellations => IsTurkish ? "Takımyıldızlar & HIP Yıldızları" : "Constellations & HIP Stars";
    public string L_Scale1x => IsTurkish ? "1x (Orijinal / 2K)" : "1x (Original / 2K)";
    public string L_Scale2x => IsTurkish ? "2x (Yüksek Çözünürlük / 4K)" : "2x (Super-Res / 4K)";
    public string L_Cancel => IsTurkish ? "Vazgeç" : "Cancel";
    public string L_PanelDragHint => IsTurkish ? "Başlıktan tutup taşıyın" : "Drag header to move";

    public string ExportProjectionMode
    {
        get => _exportProjectionMode;
        set
        {
            if (SetProperty(ref _exportProjectionMode, value))
            {
                OnPropertyChanged(nameof(IsExportCanvas2D));
                OnPropertyChanged(nameof(IsExportPanorama360));
            }
        }
    }

    public bool IsExportCanvas2D
    {
        get => _exportProjectionMode != "Panorama360";
        set { if (value) ExportProjectionMode = "Canvas2D"; }
    }

    public bool IsExportPanorama360
    {
        get => _exportProjectionMode == "Panorama360";
        set { if (value) ExportProjectionMode = "Panorama360"; }
    }

    public bool IsHelpPanelOpen
    {
        get => _isHelpPanelOpen;
        set => SetProperty(ref _isHelpPanelOpen, value);
    }

    public ICommand ToggleHelpPanelCommand { get; }
    public ICommand CloseHelpPanelCommand { get; }
    public ICommand SelectAllPanelsCommand { get; }
    public ICommand ClearPanelSelectionCommand { get; }

    public ICommand ToggleExportPanelCommand { get; }
    public ICommand CloseExportPanelCommand { get; }
    public ICommand PerformExportCommand { get; }

    public bool IsMosaicDrawerOpen
    {
        get => _isMosaicDrawerOpen;
        set => SetProperty(ref _isMosaicDrawerOpen, value);
    }

    public ICommand ToggleMosaicDrawerCommand { get; }
    public ICommand CloseMosaicDrawerCommand { get; }
    public ICommand RemoveMosaicPanelCommand { get; }
    public ICommand MoveMosaicPanelUpCommand { get; }
    public ICommand MoveMosaicPanelDownCommand { get; }
    public ICommand BringMosaicPanelToFrontCommand { get; }
    public ICommand SendMosaicPanelToBackCommand { get; }
    public ICommand SelectMosaicPanelCommand { get; }
    public ICommand CountMosaicAtlasCommand { get; }
    public ICommand ToggleFeatheredBlendingCommand { get; }


    private void ExecuteRemoveMosaicPanel(MosaicPanel? panel)
    {
        if (panel == null) return;
        panel.PropertyChanged -= OnMosaicPanelPropertyChanged;
        MosaicProject.RemovePanel(panel);
        RefreshMosaicProperties();

        if (MosaicProject.Count == 0)
        {
            ExecuteClearMosaic();
            return;
        }

        var masterWcs = MosaicProject.ComputeMasterWcs();
        CurrentWcs = masterWcs ?? MosaicProject.Panels[0].Wcs;
        if (SelectedImagePath == panel.FilePath)
        {
            var remaining = MosaicProject.Panels.FirstOrDefault();
            if (remaining != null)
            {
                ExecuteSelectMosaicPanel(remaining);
            }
        }
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
        _ = ExecuteAnalyzeAsync();

        StatusMessage = IsTurkish ? $"'{panel.Name}' kaldırıldı." : $"'{panel.Name}' removed.";
    }

    private void OnMosaicPanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is MosaicPanel p)
        {
            if (e.PropertyName == nameof(MosaicPanel.ConfidenceThreshold) || e.PropertyName == nameof(MosaicPanel.IsAutoConfidenceEnabled))
            {
                if (p == GetActiveMosaicPanel() && !_suppressConfidenceSync)
                {
                    _suppressConfidenceSync = true;
                    _confidenceThreshold = p.ConfidenceThreshold;
                    _isAutoConfidenceEnabled = p.IsAutoConfidenceEnabled;
                    OnPropertyChanged(nameof(ConfidenceThreshold));
                    OnPropertyChanged(nameof(IsAutoConfidenceEnabled));
                    OnPropertyChanged(nameof(ConfidencePercentText));
                    OnPropertyChanged(nameof(ConfidenceBrush));
                    OnPropertyChanged(nameof(ConfidenceStatusText));
                    OnPropertyChanged(nameof(ConfidenceButtonText));
                    _suppressConfidenceSync = false;
                }
                ApplyLiveFilter();
                return;
            }
        }
        OnPropertyChanged(nameof(MosaicPanels));
        RequestInvalidateOverlay?.Invoke();
    }

    public bool UseFeatheredBlending
    {
        get => MosaicProject.UseFeatheredBlending;
        set
        {
            if (MosaicProject.UseFeatheredBlending != value)
            {
                MosaicProject.UseFeatheredBlending = value;
                OnPropertyChanged(nameof(UseFeatheredBlending));
                RefreshMosaicProperties();
                StatusMessage = value
                    ? (IsTurkish ? "Yumuşak dikiş geçişi (feathered blending) etkinleştirildi." : "Feathered alpha blending enabled.")
                    : (IsTurkish ? "Yumuşak dikiş geçişi kapatıldı." : "Feathered blending disabled.");
            }
        }
    }

    private bool _isMosaicCounting;
    public bool IsMosaicCounting
    {
        get => _isMosaicCounting;
        set => SetProperty(ref _isMosaicCounting, value);
    }

    private int _mosaicUniqueStarsCount;
    public int MosaicUniqueStarsCount
    {
        get => _mosaicUniqueStarsCount;
        set
        {
            if (SetProperty(ref _mosaicUniqueStarsCount, value))
            {
                OnPropertyChanged(nameof(HasMosaicCountResult));
            }
        }
    }

    public bool HasMosaicCountResult => _mosaicUniqueStarsCount > 0;

    private int _mosaicRawStarsCount;
    public int MosaicRawStarsCount
    {
        get => _mosaicRawStarsCount;
        set => SetProperty(ref _mosaicRawStarsCount, value);
    }

    private int _mosaicOverlapStarsCount;
    public int MosaicOverlapStarsCount
    {
        get => _mosaicOverlapStarsCount;
        set => SetProperty(ref _mosaicOverlapStarsCount, value);
    }

    private string? _mosaicCountingProgressText;
    public string? MosaicCountingProgressText
    {
        get => _mosaicCountingProgressText;
        set => SetProperty(ref _mosaicCountingProgressText, value);
    }

    private void ExecuteMoveMosaicPanelUp(MosaicPanel? panel)
    {
        if (panel == null) return;
        if (MosaicProject.MoveUp(panel))
        {
            RefreshMosaicProperties();
            StatusMessage = IsTurkish ? $"'{panel.Name}' öne taşındı (Katman #{MosaicProject.GetLayerIndex(panel)})." : $"'{panel.Name}' moved up (Layer #{MosaicProject.GetLayerIndex(panel)}).";
        }
    }

    private void ExecuteMoveMosaicPanelDown(MosaicPanel? panel)
    {
        if (panel == null) return;
        if (MosaicProject.MoveDown(panel))
        {
            RefreshMosaicProperties();
            StatusMessage = IsTurkish ? $"'{panel.Name}' arkaya taşındı (Katman #{MosaicProject.GetLayerIndex(panel)})." : $"'{panel.Name}' moved down (Layer #{MosaicProject.GetLayerIndex(panel)}).";
        }
    }

    private void ExecuteBringMosaicPanelToFront(MosaicPanel? panel)
    {
        if (panel == null) return;
        if (MosaicProject.BringToFront(panel))
        {
            RefreshMosaicProperties();
            StatusMessage = IsTurkish ? $"'{panel.Name}' en öne getirildi." : $"'{panel.Name}' brought to front.";
        }
    }

    private void ExecuteSendMosaicPanelToBack(MosaicPanel? panel)
    {
        if (panel == null) return;
        if (MosaicProject.SendToBack(panel))
        {
            RefreshMosaicProperties();
            StatusMessage = IsTurkish ? $"'{panel.Name}' en arkaya gönderildi." : $"'{panel.Name}' sent to back.";
        }
    }

    public void MoveMosaicPanelTo(MosaicPanel source, MosaicPanel target)
    {
        if (source == null || target == null || ReferenceEquals(source, target)) return;
        int oldIndex = MosaicProject.GetLayerIndex(source) - 1;
        int targetIndex = MosaicProject.GetLayerIndex(target) - 1;
        if (oldIndex < 0 || targetIndex < 0) return;
        if (MosaicProject.Move(oldIndex, targetIndex))
        {
            RefreshMosaicProperties();
            RequestInvalidateOverlay?.Invoke();
            StatusMessage = IsTurkish
                ? $"'{source.Name}' katmanı #{MosaicProject.GetLayerIndex(source)} sırasına taşındı."
                : $"Moved '{source.Name}' to Layer #{MosaicProject.GetLayerIndex(source)}.";
        }
    }

    public void ClearPanelSelection()
    {
        foreach (var p in MosaicProject.Panels)
        {
            p.IsSelected = false;
        }
        SelectedMosaicPanel = null;
        OnPropertyChanged(nameof(ActivePanelDisplayName));
        OnPropertyChanged(nameof(MosaicPanels));
        CommandManager.InvalidateRequerySuggested();
        RequestInvalidateOverlay?.Invoke();
        StatusMessage = IsTurkish ? "Fotoğraf seçimi sıfırlandı." : "Photo selection cleared.";
    }

    public void SelectAllPanels()
    {
        foreach (var p in MosaicProject.Panels)
        {
            p.IsSelected = true;
        }
        SelectedMosaicPanel = MosaicProject.Panels.FirstOrDefault();
        OnPropertyChanged(nameof(ActivePanelDisplayName));
        OnPropertyChanged(nameof(MosaicPanels));
        CommandManager.InvalidateRequerySuggested();
        RequestInvalidateOverlay?.Invoke();
        StatusMessage = IsTurkish
            ? $"Tüm fotoğraflar seçildi ({MosaicProject.Count} fotoğraf)."
            : $"Selected all photos ({MosaicProject.Count} photos).";
    }

    public void SelectAndHighlightPanel(MosaicPanel? panel, bool openDrawer = false, bool toggleMultiSelect = false)
    {
        if (panel == null)
        {
            ClearPanelSelection();
            return;
        }

        if (toggleMultiSelect)
        {
            panel.IsSelected = !panel.IsSelected;
            SelectedMosaicPanel = panel.IsSelected ? panel : MosaicProject.Panels.FirstOrDefault(p => p.IsSelected);
        }
        else
        {
            foreach (var p in MosaicProject.Panels)
            {
                p.IsSelected = ReferenceEquals(p, panel);
            }
            SelectedMosaicPanel = panel;
        }

        var syncPanel = SelectedMosaicPanel ?? panel;
        _suppressConfidenceSync = true;
        _confidenceThreshold = syncPanel.ConfidenceThreshold;
        _isAutoConfidenceEnabled = syncPanel.IsAutoConfidenceEnabled;
        OnPropertyChanged(nameof(ConfidenceThreshold));
        OnPropertyChanged(nameof(IsAutoConfidenceEnabled));
        OnPropertyChanged(nameof(ConfidencePercentText));
        OnPropertyChanged(nameof(ConfidenceBrush));
        OnPropertyChanged(nameof(ConfidenceStatusText));
        OnPropertyChanged(nameof(ConfidenceButtonText));
        _suppressConfidenceSync = false;

        _suppressDisplaySync = true;
        _displayBrightness = syncPanel.Brightness;
        _displayExposure = syncPanel.Exposure;
        _displayContrast = syncPanel.Contrast;
        _isAutoBrightnessEnabled = syncPanel.IsAutoBrightnessEnabled;
        OnPropertyChanged(nameof(DisplayBrightness));
        OnPropertyChanged(nameof(DisplayExposure));
        OnPropertyChanged(nameof(DisplayContrast));
        OnPropertyChanged(nameof(DisplayBrightnessText));
        OnPropertyChanged(nameof(DisplayExposureText));
        OnPropertyChanged(nameof(DisplayContrastText));
        OnPropertyChanged(nameof(IsAutoBrightnessEnabled));
        _suppressDisplaySync = false;

        if (!string.IsNullOrEmpty(syncPanel.FilePath) && File.Exists(syncPanel.FilePath))
        {
            _selectedImagePath = syncPanel.FilePath;
            LoadBitmapPreview(syncPanel.FilePath);
            OnPropertyChanged(nameof(SelectedImagePath));
            OnPropertyChanged(nameof(ImageFileName));
            OnPropertyChanged(nameof(HasLoadedImage));
            OnPropertyChanged(nameof(ImageDimensionText));
        }

        OnPropertyChanged(nameof(ActivePanelDisplayName));
        CommandManager.InvalidateRequerySuggested();

        if (openDrawer)
        {
            IsMosaicDrawerOpen = true;
            if (panel.IsSelected)
            {
                RequestScrollToMosaicPanel?.Invoke(panel);
            }
            int selCount = MosaicProject.Panels.Count(p => p.IsSelected);
            StatusMessage = selCount > 1
                ? (IsTurkish ? $"🎯 {selCount} fotoğraf seçili." : $"🎯 {selCount} photos selected.")
                : (IsTurkish
                    ? $"🎯 '{panel.Name}' seçildi (Katman #{MosaicProject.GetLayerIndex(panel)})."
                    : $"🎯 Selected '{panel.Name}' (Layer #{MosaicProject.GetLayerIndex(panel)}).");
        }

        OnPropertyChanged(nameof(MosaicPanels));
        RequestInvalidateOverlay?.Invoke();
    }

    private void ExecuteSelectMosaicPanel(MosaicPanel? panel)
    {
        if (panel == null) return;
        bool multi = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0 ||
                     MosaicProject.Panels.Any(p => p.IsSelected);
        SelectAndHighlightPanel(panel, openDrawer: false, toggleMultiSelect: multi);
        if (panel.IsSelected && MosaicProject.Count > 1 && CurrentWcs != null && panel.Wcs != null)
        {
            var centerSky = panel.Wcs.CenterCoordinates;
            if (CurrentWcs.EquatorialToPixel(centerSky, out double px, out double py))
            {
                RequestCenterOnPixel?.Invoke(px, py);
            }
            if (IsSphereModeActive)
            {
                double span = Math.Max(panel.Wcs.FovWidthDegrees, panel.Wcs.FovHeightDegrees);
                RequestSet3DCamera?.Invoke(centerSky.RaDegrees, centerSky.DecDegrees, Math.Clamp(span * 1.8, 10.0, 80.0));
            }
        }
    }

    public void RefreshMosaicProperties()
    {
        MosaicProject.UpdatePanelEdgeOverlaps(CurrentWcs);
        OnPropertyChanged(nameof(MosaicPanels));
        OnPropertyChanged(nameof(MosaicPanelCount));
        OnPropertyChanged(nameof(HasPanels));
        OnPropertyChanged(nameof(IsMosaicEmpty));
        OnPropertyChanged(nameof(HasMultipleMosaicPanels));
        OnPropertyChanged(nameof(MosaicPanelCountText));
        OnPropertyChanged(nameof(L_PhotosButtonText));
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
        OnPropertyChanged(nameof(IsSingleImageActive));
        OnPropertyChanged(nameof(HasLoadedImage));
        OnPropertyChanged(nameof(IsPlaceholderVisible));
    }

    public MosaicProject MosaicProject { get; } = new();
    public IReadOnlyList<MosaicPanel> MosaicPanels => MosaicProject.Panels.ToList();
    public int MosaicPanelCount => MosaicProject.Count;
    public bool HasPanels => MosaicProject.Count > 0;
    public bool IsMosaicEmpty => MosaicProject.Count == 0;
    public bool HasMultipleMosaicPanels => MosaicProject.Count > 1;
    public string MosaicPanelCountText => IsTurkish
        ? $"Mozaik: {MosaicProject.Count} Panel"
        : $"Mosaic: {MosaicProject.Count} Panels";

    public void AddOrUpdateMosaicPanel(string? filePath, int width, int height, WcsMatrix wcs, string? targetName)
    {
        if (wcs == null || width <= 0 || height <= 0) return;

        string name = !string.IsNullOrWhiteSpace(filePath)
            ? Path.GetFileNameWithoutExtension(filePath)
            : $"Panel {MosaicProject.Count + 1}";

        var panel = new MosaicPanel(
            Guid.NewGuid().ToString("N"),
            name,
            width,
            height,
            wcs,
            filePath,
            targetName);

        panel.PropertyChanged -= OnMosaicPanelPropertyChanged;
        panel.PropertyChanged += OnMosaicPanelPropertyChanged;
        MosaicProject.AddOrUpdatePanel(panel);
        OnPropertyChanged(nameof(MosaicPanels));
        OnPropertyChanged(nameof(MosaicPanelCount));
        OnPropertyChanged(nameof(HasMultipleMosaicPanels));
        OnPropertyChanged(nameof(MosaicPanelCountText));
        OnPropertyChanged(nameof(L_PhotosButtonText));
    }

    private void ExecuteAddToMosaic()
    {
        if (CurrentWcs == null || ImageWidth <= 0 || ImageHeight <= 0) return;

        if (!string.IsNullOrEmpty(SelectedImagePath) && ImageBitmap != null)
        {
            DetectionOverlay.CachePanelBitmap(SelectedImagePath, ImageBitmap);
        }

        AddOrUpdateMosaicPanel(
            SelectedImagePath,
            (int)Math.Round(ImageWidth),
            (int)Math.Round(ImageHeight),
            CurrentWcs,
            SelectedCatalogueTarget?.CommonName);

        StatusMessage = IsTurkish
            ? $"'{Path.GetFileNameWithoutExtension(SelectedImagePath) ?? "Panel"}' mozaik atlasına eklendi (Toplam panel: {MosaicProject.Count})."
            : $"Added to mosaic atlas (Total panels: {MosaicProject.Count}).";
    }

    private void ExecuteClearMosaic()
    {
        MosaicProject.Clear();
        _lastDetectionResult = null;
        VisibleObjects = Array.Empty<AstrophotographyObject>();
        TotalCount = 0;
        Tier1Count = 0;
        Tier2Count = 0;
        Tier3Count = 0;
        SpeculativeCount = 0;
        AnomalyCount = 0;
        ImageBitmap = null;
        _selectedImagePath = null;
        CurrentWcs = null;
        SolvedCoordinatesText = "-";
        OnPropertyChanged(nameof(SelectedImagePath));
        OnPropertyChanged(nameof(ImageFileName));
        RefreshMosaicProperties();
        RequestInvalidateOverlay?.Invoke();

        StatusMessage = IsTurkish ? "Tüm fotoğraflar ve mozaik temizlendi." : "All photos and mosaic cleared.";
    }
    public ICommand ToggleLanguageCommand { get; }
    private bool _isInspectorOpen = true;
    public bool IsInspectorOpen
    {
        get => _isInspectorOpen;
        set => SetProperty(ref _isInspectorOpen, value);
    }

    private InspectorTab _currentInspectorTab = InspectorTab.TelemetryEngine;
    public InspectorTab CurrentInspectorTab
    {
        get => _currentInspectorTab;
        set
        {
            if (SetProperty(ref _currentInspectorTab, value))
            {
                OnPropertyChanged(nameof(IsTabTelemetryEngine));
                OnPropertyChanged(nameof(IsTabAstrometry));
                OnPropertyChanged(nameof(IsTabImageAdjustments));
                OnPropertyChanged(nameof(IsTabMosaic));
            }
        }
    }

    public bool IsTabTelemetryEngine
    {
        get => _currentInspectorTab == InspectorTab.TelemetryEngine;
        set { if (value) CurrentInspectorTab = InspectorTab.TelemetryEngine; }
    }

    public bool IsTabAstrometry
    {
        get => _currentInspectorTab == InspectorTab.Astrometry;
        set { if (value) CurrentInspectorTab = InspectorTab.Astrometry; }
    }

    public bool IsTabImageAdjustments
    {
        get => _currentInspectorTab == InspectorTab.ImageAdjustments;
        set { if (value) CurrentInspectorTab = InspectorTab.ImageAdjustments; }
    }

    public bool IsTabMosaic
    {
        get => _currentInspectorTab == InspectorTab.Mosaic;
        set { if (value) CurrentInspectorTab = InspectorTab.Mosaic; }
    }

    public string L_InspectorTitle => IsTurkish ? "Denetçi Paneli" : "Inspector";
    public string L_ToggleInspectorToolTip => IsTurkish ? "Denetçi Panelini Aç/Kapat (Ctrl+B)" : "Toggle Inspector Sidebar (Ctrl+B)";

    public ICommand ToggleInspectorCommand { get; }
    public ICommand SelectInspectorTabCommand { get; }


    public ICommand ToggleLabelsPanelCommand { get; }
    public ICommand CloseLabelsPanelCommand { get; }
    public ICommand CheckAllOverlaysCommand { get; }
    public ICommand UncheckAllOverlaysCommand { get; }
    public ICommand ToggleLightPanelCommand { get; }
    public ICommand CloseLightPanelCommand { get; }
    public ICommand ResetDisplayAdjustmentsCommand { get; }
    public ICommand AutoBalanceNowCommand { get; }

    private bool _isConfidencePanelOpen;
    public bool IsConfidencePanelOpen
    {
        get => _isConfidencePanelOpen;
        set => SetProperty(ref _isConfidencePanelOpen, value);
    }

    private bool _isAutoConfidenceEnabled = true;
    public bool IsAutoConfidenceEnabled
    {
        get => _isAutoConfidenceEnabled;
        set
        {
            if (SetProperty(ref _isAutoConfidenceEnabled, value))
            {
                var activePanel = GetActiveMosaicPanel();
                if (activePanel != null)
                {
                    activePanel.IsAutoConfidenceEnabled = value;
                }
                OnPropertyChanged(nameof(ConfidenceButtonText));
                if (value && _lastDetectionResult != null)
                {
                    ExecuteApplyAutoConfidence();
                }
            }
        }
    }

    public ICommand ToggleConfidencePanelCommand { get; }
    public ICommand CloseConfidencePanelCommand { get; }
    public ICommand PresetConfidenceCommand { get; }

    private bool _isLabelsPanelOpen = false;
    public bool IsLabelsPanelOpen
    {
        get => _isLabelsPanelOpen;
        set => SetProperty(ref _isLabelsPanelOpen, value);
    }

    public bool IsLightPanelOpen
    {
        get => _isLightPanelOpen;
        set => SetProperty(ref _isLightPanelOpen, value);
    }

    public double DisplayBrightness
    {
        get => _displayBrightness;
        set
        {
            if (SetProperty(ref _displayBrightness, value))
            {
                OnPropertyChanged(nameof(DisplayBrightnessText));
                ApplyDisplayAdjustments();
            }
        }
    }

    public double DisplayExposure
    {
        get => _displayExposure;
        set
        {
            if (SetProperty(ref _displayExposure, value))
            {
                OnPropertyChanged(nameof(DisplayExposureText));
                ApplyDisplayAdjustments();
            }
        }
    }

    public double DisplayContrast
    {
        get => _displayContrast;
        set
        {
            if (SetProperty(ref _displayContrast, value))
            {
                OnPropertyChanged(nameof(DisplayContrastText));
                ApplyDisplayAdjustments();
            }
        }
    }

    public string DisplayBrightnessText => $"{_displayBrightness:+#0;-#0;0}";
    public string DisplayExposureText => $"{_displayExposure:F2}x";
    public string DisplayContrastText => $"{_displayContrast:F2}x";

    public bool IsAutoBrightnessEnabled
    {
        get => _isAutoBrightnessEnabled;
        set
        {
            if (SetProperty(ref _isAutoBrightnessEnabled, value))
            {
                ApplyDisplayAdjustments();
            }
        }
    }

    public string BrightnessStatusText
    {
        get => _brightnessStatusText;
        private set => SetProperty(ref _brightnessStatusText, value);
    }

    private void ResetDisplayAdjustments()
    {
        _displayBrightness = 0.0;
        _displayExposure = 1.0;
        _displayContrast = 1.0;
        _isAutoBrightnessEnabled = false;
        OnPropertyChanged(nameof(DisplayBrightness));
        OnPropertyChanged(nameof(DisplayExposure));
        OnPropertyChanged(nameof(DisplayContrast));
        OnPropertyChanged(nameof(IsAutoBrightnessEnabled));
        OnPropertyChanged(nameof(DisplayBrightnessText));
        OnPropertyChanged(nameof(DisplayExposureText));
        OnPropertyChanged(nameof(DisplayContrastText));
        ApplyDisplayAdjustments();
    }

    private void AutoBalanceNow()
    {
        IsAutoBrightnessEnabled = true;
        ApplyDisplayAdjustments();
        StatusMessage = IsTurkish ? "Görsel için otomatik ışık ve renk dengelendi." : "Image exposure and color automatically balanced.";
    }

    private void ApplyDisplayAdjustments()
    {
        if (_originalSkBitmap == null && MosaicProject.Count == 0) return;

        try
        {
            var selectedPanels = MosaicProject.Panels.Where(p => p.IsSelected).ToList();
            var targetPanels = selectedPanels.Count > 0 ? selectedPanels : MosaicProject.Panels.ToList();

            if (!_suppressDisplaySync && targetPanels.Count > 0)
            {
                foreach (var tp in targetPanels)
                {
                    tp.Brightness = _displayBrightness;
                    tp.Exposure = _displayExposure;
                    tp.Contrast = _displayContrast;
                    tp.IsAutoBrightnessEnabled = _isAutoBrightnessEnabled;

                    SkiaSharp.SKBitmap? panelSkBmp = tp.CachedSkiaBitmap as SkiaSharp.SKBitmap;
                    if (panelSkBmp == null && !string.IsNullOrEmpty(tp.FilePath) && File.Exists(tp.FilePath))
                    {
                        panelSkBmp = ImagePreprocessor.LoadImage(tp.FilePath);
                        tp.CachedSkiaBitmap = panelSkBmp;
                    }

                    if (panelSkBmp != null)
                    {
                        SkiaSharp.SKBitmap panelBase;
                        if (_isAutoBrightnessEnabled)
                        {
                            panelBase = ImagePreprocessor.AutoBalanceBrightness(panelSkBmp, out var pInfo);
                            BrightnessStatusText = pInfo.WasAdjusted ? pInfo.SummaryText : (IsTurkish ? "Oto Parlaklık: Optimal" : "Auto Brightness: Optimal");
                        }
                        else
                        {
                            panelBase = panelSkBmp.Copy();
                            BrightnessStatusText = IsTurkish ? "Oto Parlaklık: Kapalı" : "Auto Brightness: Off";
                        }

                        using (panelBase)
                        {
                            using var panelAdjusted = ImagePreprocessor.ApplyDisplayToneAdjustment(
                                panelBase, _displayBrightness, _displayExposure, _displayContrast);
                            var panelWpfBmp = ConvertToWpfBitmap(panelAdjusted);
                            tp.CachedBitmap = panelWpfBmp;
                            if (!string.IsNullOrEmpty(tp.FilePath))
                            {
                                DetectionOverlay.CachePanelBitmap(tp.FilePath, panelWpfBmp);
                            }
                            if (!string.IsNullOrEmpty(SelectedImagePath) &&
                                !string.IsNullOrEmpty(tp.FilePath) &&
                                string.Equals(tp.FilePath, SelectedImagePath, StringComparison.OrdinalIgnoreCase))
                            {
                                ImageWidth = panelWpfBmp.PixelWidth;
                                ImageHeight = panelWpfBmp.PixelHeight;
                                ImageBitmap = panelWpfBmp;
                            }
                        }
                    }
                }

                OnPropertyChanged(nameof(MosaicPanels));
                RequestInvalidateOverlay?.Invoke();
                return;
            }

            if (_originalSkBitmap == null) return;

            SkiaSharp.SKBitmap baseBmp;
            if (IsAutoBrightnessEnabled)
            {
                baseBmp = ImagePreprocessor.AutoBalanceBrightness(_originalSkBitmap, out var bInfo);
                BrightnessStatusText = bInfo.WasAdjusted ? bInfo.SummaryText : (IsTurkish ? "Oto Parlaklık: Optimal" : "Auto Brightness: Optimal");
            }
            else
            {
                baseBmp = _originalSkBitmap.Copy();
                BrightnessStatusText = IsTurkish ? "Oto Parlaklık: Kapalı" : "Auto Brightness: Off";
            }

            using (baseBmp)
            {
                using var adjusted = ImagePreprocessor.ApplyDisplayToneAdjustment(
                    baseBmp, _displayBrightness, _displayExposure, _displayContrast);

                var wpfBitmap = ConvertToWpfBitmap(adjusted);
                ImageWidth = wpfBitmap.PixelWidth;
                ImageHeight = wpfBitmap.PixelHeight;
                ImageBitmap = wpfBitmap;

                var activePanel = GetActiveMosaicPanel();
                if (activePanel != null)
                {
                    activePanel.CachedBitmap = wpfBitmap;
                }

                if (!string.IsNullOrEmpty(SelectedImagePath))
                {
                    DetectionOverlay.CachePanelBitmap(SelectedImagePath, wpfBitmap);
                }

                OnPropertyChanged(nameof(MosaicPanels));
                RequestInvalidateOverlay?.Invoke();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = IsTurkish ? $"Görsel işleme hatası: {ex.Message}" : $"Display adjustment error: {ex.Message}";
        }
    }

    private void UpdateFormatAccuracyBadge(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        bool isCompressed = ext is ".jpg" or ".jpeg" or ".webp";
        bool isLossless = ext is ".tif" or ".tiff" or ".fits" or ".fit" or ".png";

        if (isCompressed)
        {
            _formatBadgeIcon = "⚠️";
            _formatBadgeText = IsTurkish ? $"Sıkıştırılmış {ext.ToUpperInvariant().TrimStart('.')}" : $"Compressed {ext.ToUpperInvariant().TrimStart('.')}";
            _formatWarningToolTip = IsTurkish
                ? $"Sıkıştırılmış format ({ext.ToUpperInvariant()}): Bloklama ve DCT sıkıştırma artefaktları sönük yıldız tespit doğruluğunu ve fotometrik duyarlılığı etkileyebilir. Bilimsel hassasiyet için 16-bit TIFF veya FITS önerilir."
                : $"Compressed format ({ext.ToUpperInvariant()}): Lossy compression artifacts may affect faint star detection and photometric accuracy. 16-bit TIFF or FITS is recommended for scientific precision.";
            _formatBadgeBackground = new SolidColorBrush(Color.FromRgb(0x2D, 0x1B, 0x04));
            _formatBadgeBorder = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
            _formatBadgeForeground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
        }
        else if (isLossless)
        {
            _formatBadgeIcon = "✨";
            _formatBadgeText = IsTurkish ? $"Kayıpsız {ext.ToUpperInvariant().TrimStart('.')}" : $"Lossless {ext.ToUpperInvariant().TrimStart('.')}";
            _formatWarningToolTip = IsTurkish
                ? $"Kayıpsız Astronomik Format ({ext.ToUpperInvariant()}): Maksimum piksel doğruluğu ve doğrusal fotometrik duyarlılık."
                : $"Lossless Astronomical Format ({ext.ToUpperInvariant()}): Maximum pixel fidelity and linear photometric accuracy.";
            _formatBadgeBackground = new SolidColorBrush(Color.FromRgb(0x06, 0x2A, 0x1F));
            _formatBadgeBorder = new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69));
            _formatBadgeForeground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
        }
        else
        {
            _formatBadgeText = null;
            _formatWarningToolTip = null;
        }

        _formatBadgeBackground.Freeze();
        _formatBadgeBorder.Freeze();
        _formatBadgeForeground.Freeze();

        OnPropertyChanged(nameof(HasFormatBadge));
        OnPropertyChanged(nameof(FormatBadgeText));
        OnPropertyChanged(nameof(FormatWarningToolTip));
        OnPropertyChanged(nameof(FormatBadgeIcon));
        OnPropertyChanged(nameof(FormatBadgeBackground));
        OnPropertyChanged(nameof(FormatBadgeBorder));
        OnPropertyChanged(nameof(FormatBadgeForeground));
    }

    private void LoadBitmapPreview(string filePath)
    {
        try
        {
            _originalSkBitmap?.Dispose();
            _originalSkBitmap = ImagePreprocessor.LoadImage(filePath);

            UpdateFormatAccuracyBadge(filePath);

            ApplyDisplayAdjustments();

            StatusMessage = IsTurkish
                ? $"Görsel yüklendi: {Path.GetFileName(filePath)} ({ImageWidth}x{ImageHeight})"
                : $"Image loaded: {Path.GetFileName(filePath)} ({ImageWidth}x{ImageHeight})";
        }
        catch (Exception ex)
        {
            StatusMessage = IsTurkish ? $"Görsel yüklenirken hata: {ex.Message}" : $"Error loading image: {ex.Message}";
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

    private static BitmapSource ConvertToPreviewWpfBitmap(SKBitmap skBitmap, int maxDimension = 2048)
    {
        if (skBitmap.Width <= maxDimension && skBitmap.Height <= maxDimension)
        {
            return ConvertToWpfBitmap(skBitmap);
        }

        float scale = (float)maxDimension / Math.Max(skBitmap.Width, skBitmap.Height);
        int newW = Math.Max(64, (int)Math.Round(skBitmap.Width * scale));
        int newH = Math.Max(64, (int)Math.Round(skBitmap.Height * scale));

        using var resized = skBitmap.Resize(new SKImageInfo(newW, newH, skBitmap.ColorType, skBitmap.AlphaType), SKSamplingOptions.Default);
        return resized != null ? ConvertToWpfBitmap(resized) : ConvertToWpfBitmap(skBitmap);
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
        StatusMessage = IsHybridFusionMode
            ? (IsTurkish
                ? "🔬 Hibrit Füzyon: Fotometri + Yapay Zekâ analizi yapılıyor..."
                : "🔬 Hybrid Fusion: Photometry + Deep Learning analysis running...")
            : (IsPhotometricMode
                ? (IsTurkish
                    ? "Astronomik fotometri: 2D arka plan modelleniyor ve yıldızlar aranıyor..."
                    : "Astronomical photometry: modeling 2D background and extracting star centroids...")
                : (IsTurkish
                    ? "YOLO SAHI: Görüntü 640x640 dilimleniyor ve model çıkarımı yapılıyor..."
                    : "YOLO SAHI: slicing image into 640x640 tiles and running inference..."));

        var progress = new Progress<float>(percent =>
        {
            if (token.IsCancellationRequested) return;

            ProgressValue = percent * 100.0;
            int pct = (int)(percent * 100);
            StatusMessage = IsHybridFusionMode
                ? (IsTurkish ? $"🔬 Füzyon analizi: %{pct}" : $"🔬 Fusion analysis: {pct}%")
                : (IsPhotometricMode
                    ? (IsTurkish ? $"Fotometrik analiz: %{pct}" : $"Photometric analysis: {pct}%")
                    : (IsTurkish ? $"Dilimler işleniyor: %{pct}" : $"Processing tiles: {pct}%"));
        });

        try
        {
            if (_engine == null)
            {
                InitializeEngine();
            }
            else
            {
                _engine.Mode = _selectedEngineMode;
                HardwareInfo = _engine.ExecutionProviderName;
            }

        if (MosaicProject.Count > 1)
        {
            ProgressValue = 40.0;
            StatusMessage = IsTurkish
                ? $"🔬 [Aşama 3/3 - Yıldız Sayımı] {MosaicProject.Count} fotoğraftaki binlerce yıldız tam çözünürlükte sayılıyor ve tekilleştiriliyor..."
                : $"🔬 [Stage 3/3 - Star Counting] Counting & deduplicating thousands of stars across {MosaicProject.Count} photos at full resolution...";
            await Task.Yield();

            var mosaicProgress = new Progress<MosaicCountingProgress>(p =>
            {
                if (token.IsCancellationRequested) return;
                ProgressValue = 40.0 + (p.OverallPercent * 60.0);
                StatusMessage = IsTurkish
                    ? $"⭐ [Aşama 3/3 - Yıldız Sayımı {p.CompletedPanels}/{p.TotalPanels}] {p.CurrentPanelName} — {p.TotalRawStars:N0} ham yıldız tespit edildi..."
                    : $"⭐ [Stage 3/3 - Star Counting {p.CompletedPanels}/{p.TotalPanels}] {p.CurrentPanelName} — {p.TotalRawStars:N0} raw stars detected...";
            });

            try
            {
                // In mosaic mode, CurrentWcs MUST ALWAYS be the Master WCS representing the continuous master canvas
                var masterWcs = MosaicProject.ComputeMasterWcs() ?? MosaicProject.Panels.FirstOrDefault(p => p.Wcs != null)?.Wcs;
                if (masterWcs != null)
                {
                    CurrentWcs = masterWcs;
                }
                OnPropertyChanged(nameof(CanvasWidth));
                OnPropertyChanged(nameof(CanvasHeight));

                var mosaicConfig = new TilingConfig(DefaultConfidenceThreshold: _confidenceThreshold, MinConfidence: Math.Max(0.05f, _confidenceThreshold * 0.35f));
                var mosaicResult = await MosaicStarCounter.CountMosaicStarsAsync(
                    MosaicProject,
                    mosaicConfig,
                    matchToleranceArcsec: 3.0,
                    progress: mosaicProgress,
                    targetWcs: masterWcs ?? _currentWcs,
                    engine: _engine,
                    cancellationToken: token);

                if (token.IsCancellationRequested) return;

                MosaicUniqueStarsCount = mosaicResult.TotalUniqueStars;
                MosaicRawStarsCount = mosaicResult.RawDetectionsCount;
                MosaicOverlapStarsCount = mosaicResult.OverlapDetectionsCount;

                var mosaicDetectionResult = new DetectionResult(
                    mosaicResult.AllObjects,
                    (int)Math.Max(1, CanvasWidth),
                    (int)Math.Max(1, CanvasHeight),
                    MosaicProject.Count,
                    mosaicResult.ElapsedTime,
                    HardwareInfo,
                    _confidenceThreshold);

                _lastDetectionResult = mosaicDetectionResult;
                OptimalConfidenceThreshold = mosaicDetectionResult.RecommendedConfidence;
                var activePanel = GetActiveMosaicPanel();
                if (activePanel != null)
                {
                    _suppressConfidenceSync = true;
                    _confidenceThreshold = activePanel.ConfidenceThreshold;
                    _isAutoConfidenceEnabled = activePanel.IsAutoConfidenceEnabled;
                    OnPropertyChanged(nameof(ConfidenceThreshold));
                    OnPropertyChanged(nameof(IsAutoConfidenceEnabled));
                    OnPropertyChanged(nameof(ConfidencePercentText));
                    OnPropertyChanged(nameof(ConfidenceBrush));
                    OnPropertyChanged(nameof(ConfidenceStatusText));
                    OnPropertyChanged(nameof(ConfidenceButtonText));
                    _suppressConfidenceSync = false;
                }
                ProcessingTimeText = $"{mosaicResult.ElapsedTime.TotalSeconds:F1} s";
                TileCountText = IsTurkish ? $"{MosaicProject.Count} Panel" : $"{MosaicProject.Count} Panels";

                ApplyLiveFilter();

                StatusMessage = IsTurkish
                    ? $"🌌 Mozaik tamamlandı! {MosaicProject.Count} panelde {mosaicResult.TotalUniqueStars:N0} benzersiz gök cismi sayıldı ({mosaicResult.OverlapDetectionsCount:N0} örtüşme birleştirildi, {ProcessingTimeText})."
                    : $"🌌 Mosaic completed! Counted {mosaicResult.TotalUniqueStars:N0} unique objects across {MosaicProject.Count} panels ({mosaicResult.OverlapDetectionsCount:N0} overlaps merged, {ProcessingTimeText}).";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                StatusMessage = IsTurkish ? $"Mozaik analiz hatası: {ex.Message}" : $"Mosaic analysis error: {ex.Message}";
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    IsLoading = false;
                    ProgressValue = 100;
                }
            }
            return;
        }

        var config = new TilingConfig(DefaultConfidenceThreshold: 0.25f, MinConfidence: 0.02f);
        var result = await _engine!.ProcessImageAsync(SelectedImagePath, config, progress, token);

            if (token.IsCancellationRequested) return;

            var allObjects = result.AllObjects;
            if (_currentWcs != null)
            {
                var anomalyChecked = AstroCounter.Core.Astrometry.TransientAnomalyDetector.DetectAnomalies(
                    allObjects,
                    _currentWcs,
                    (int)ImageWidth,
                    (int)ImageHeight);

                string? pId = MosaicProject.Panels.FirstOrDefault()?.Id;
                var withSky = new List<AstrophotographyObject>(anomalyChecked.Count);
                foreach (var o in anomalyChecked)
                {
                    var sky = _currentWcs.PixelToEquatorial(o.CenterX, o.CenterY);
                    withSky.Add(o.WithCelestialCoordinates(sky.RaDegrees, sky.DecDegrees, pId));
                }
                allObjects = withSky;
            }

            result = new DetectionResult(
                allObjects,
                result.ImageWidth,
                result.ImageHeight,
                result.TileCount,
                result.ElapsedTime,
                result.ExecutionProvider,
                result.RecommendedConfidence);

            _lastDetectionResult = result;
            var singlePanel = GetActiveMosaicPanel();
            float formatAwareAuto = AutoConfidenceCalculator.ComputeOptimalThreshold(
                allObjects,
                singlePanel?.IsLossyFormat ?? false);
            OptimalConfidenceThreshold = formatAwareAuto;
            if (singlePanel != null)
            {
                singlePanel.AutoConfidenceThreshold = formatAwareAuto;
            }
            if (IsAutoConfidenceEnabled)
            {
                _suppressConfidenceSync = true;
                _confidenceThreshold = formatAwareAuto;
                if (singlePanel != null)
                {
                    singlePanel.ConfidenceThreshold = formatAwareAuto;
                }
                OnPropertyChanged(nameof(ConfidenceThreshold));
                OnPropertyChanged(nameof(ConfidencePercentText));
                OnPropertyChanged(nameof(ConfidenceBrush));
                OnPropertyChanged(nameof(ConfidenceStatusText));
                OnPropertyChanged(nameof(ConfidenceButtonText));
                _suppressConfidenceSync = false;
            }
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
    /// Filters detected objects in RAM according to per-photo ConfidenceThreshold sliders AND the scan area mask without re-running AI.
    /// </summary>
    public void ApplyLiveFilter()
    {
        if (_lastDetectionResult == null) return;

        IReadOnlyDictionary<string, float>? perPanelMap = null;
        if (MosaicProject.Count > 0)
        {
            var dict = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var p in MosaicProject.Panels)
            {
                dict[p.Id] = p.ConfidenceThreshold;
            }
            perPanelMap = dict;
        }

        var filtered = _lastDetectionResult.Filter(_confidenceThreshold, _mask, perPanelMap);

        TotalCount = filtered.TotalCount;
        Tier1Count = filtered.Tier1Count;
        Tier2Count = filtered.Tier2Count;
        Tier3Count = filtered.Tier3Count;
        SpeculativeCount = filtered.NoiseCount;
        VisibleObjects = filtered.FilteredObjects;
        AnomalyCount = filtered.FilteredObjects.Count(o => o.IsAnomaly);

        CommandManager.InvalidateRequerySuggested();
    }

    private bool _isBatchImporting;

    public async Task LoadImagesAsync(IReadOnlyList<string> filePaths, bool append = false)
    {
        if (filePaths == null || filePaths.Count == 0) return;
        if (_isBatchImporting) return;

        var valid = filePaths
            .Where(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (valid.Count == 0) return;

        _isBatchImporting = true;
        IsLoading = true;
        ProgressValue = 2.0;
        StatusMessage = IsTurkish
            ? $"🌌 {valid.Count} fotoğraf yükleniyor ve hazırlanıyor..."
            : $"🌌 Loading {valid.Count} photos...";

        try
        {
            if (!append)
            {
                foreach (var oldPanel in MosaicProject.Panels)
                {
                    if (oldPanel.CachedSkiaBitmap is SKBitmap oldSk && !ReferenceEquals(oldSk, _originalSkBitmap))
                    {
                        oldSk.Dispose();
                    }
                    oldPanel.CachedSkiaBitmap = null;
                }
                MosaicProject.Clear();
                _lastDetectionResult = null;
                VisibleObjects = Array.Empty<AstrophotographyObject>();
                TotalCount = 0;
                Tier1Count = 0;
                Tier2Count = 0;
                Tier3Count = 0;
                SpeculativeCount = 0;
                AnomalyCount = 0;
            }
            else
            {
                // Skip files already present in the mosaic when appending
                var existingPaths = MosaicProject.Panels
                    .Where(p => !string.IsNullOrEmpty(p.FilePath))
                    .Select(p => Path.GetFullPath(p.FilePath!))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                valid = valid
                    .Where(f => !existingPaths.Contains(Path.GetFullPath(f)))
                    .ToList();

                if (valid.Count == 0)
                {
                    StatusMessage = IsTurkish
                        ? "Seçilen fotoğraflar zaten projede mevcut."
                        : "Selected photos are already loaded in the project.";
                    return;
                }
            }

            int existingCount = MosaicProject.Count;
            var newlyAdded = new List<MosaicPanel>();
            bool isBatchMode = valid.Count > 1 || existingCount > 0;

            // Decode images in parallel on background worker threads (bounded concurrency to avoid OOM on 8-16GB devices)
            int maxDecoders = Math.Clamp(Environment.ProcessorCount / 2, 1, 3);
            var decodedItems = await Task.Run(() =>
            {
                var items = new (string Path, int Width, int Height, BitmapSource? WpfPreview, SKBitmap? KeptSkBmp, IReadOnlyList<AstrophotographyObject>? Stars, string? Error)[valid.Count];
                Parallel.For(0, valid.Count, new ParallelOptions { MaxDegreeOfParallelism = maxDecoders }, idx =>
                {
                    string p = valid[idx];
                    try
                    {
                        var sk = ImagePreprocessor.LoadImage(p);
                        if (sk == null || sk.Width <= 0 || sk.Height <= 0)
                        {
                            sk?.Dispose();
                            items[idx] = (p, 0, 0, null, null, null, "Boş veya geçersiz görsel");
                            return;
                        }

                        var wpfPreview = ConvertToPreviewWpfBitmap(sk, 2048);
                        var stars = PhotometricStarFinder.DetectStarsFast(
                            sk,
                            new TilingConfig(DefaultConfidenceThreshold: 0.20f, MinConfidence: 0.05f));

                        // Keep full SKBitmap only for single-image mode or very small sets (<= 2) to prevent OOM on new devices
                        if (!isBatchMode || valid.Count <= 2)
                        {
                            items[idx] = (p, sk.Width, sk.Height, wpfPreview, sk, stars, null);
                        }
                        else
                        {
                            int w = sk.Width;
                            int h = sk.Height;
                            sk.Dispose();
                            items[idx] = (p, w, h, wpfPreview, null, stars, null);
                        }
                    }
                    catch (Exception ex)
                    {
                        items[idx] = (p, 0, 0, null, null, null, ex.Message);
                    }
                });
                return items;
            });

            var successItems = decodedItems.Where(x => x.WpfPreview != null && x.Width > 0 && x.Height > 0).ToList();
            int failedCount = decodedItems.Length - successItems.Count;

            if (successItems.Count == 0)
            {
                string firstErr = decodedItems.FirstOrDefault(x => !string.IsNullOrEmpty(x.Error)).Error ?? "Bilinmeyen hata";
                StatusMessage = IsTurkish
                    ? $"⚠️ Fotoğraflar içe aktarılamadı ({firstErr})."
                    : $"⚠️ Failed to import photos ({firstErr}).";
                return;
            }

            // Set active preview from first successfully decoded image (no double disk decode!)
            var firstItem = successItems[0];
            _selectedImagePath = firstItem.Path;
            if (firstItem.KeptSkBmp != null)
            {
                if (_originalSkBitmap != null && !ReferenceEquals(_originalSkBitmap, firstItem.KeptSkBmp))
                {
                    _originalSkBitmap.Dispose();
                }
                _originalSkBitmap = firstItem.KeptSkBmp;
                UpdateFormatAccuracyBadge(firstItem.Path);
                ApplyDisplayAdjustments();
            }
            else
            {
                _originalSkBitmap?.Dispose();
                _originalSkBitmap = null;
                ImageWidth = firstItem.Width;
                ImageHeight = firstItem.Height;
                ImageBitmap = firstItem.WpfPreview;
                UpdateFormatAccuracyBadge(firstItem.Path);
            }
            OnPropertyChanged(nameof(SelectedImagePath));
            OnPropertyChanged(nameof(ImageFileName));
            OnPropertyChanged(nameof(HasLoadedImage));
            OnPropertyChanged(nameof(ImageDimensionText));

            for (int i = 0; i < successItems.Count; i++)
            {
                var item = successItems[i];
                string path = item.Path;
                int w = item.Width;
                int h = item.Height;

                if (item.WpfPreview != null)
                {
                    DetectionOverlay.CachePanelBitmap(path, item.WpfPreview);
                }

                string panelName = !string.IsNullOrWhiteSpace(path)
                    ? Path.GetFileNameWithoutExtension(path)
                    : $"Panel {existingCount + i + 1}";

                var initialWcs = new WcsMatrix(
                    new CelestialCoordinates(180.0, 30.0),
                    w / 2.0,
                    h / 2.0,
                    2.5,
                    0.0,
                    w,
                    h);

                var panel = new MosaicPanel(
                    Guid.NewGuid().ToString("N"),
                    panelName,
                    w,
                    h,
                    initialWcs,
                    path)
                {
                    CachedBitmap = item.WpfPreview,
                    CachedSkiaBitmap = item.KeptSkBmp,
                    DetectedStars = item.Stars
                };

                panel.PropertyChanged -= OnMosaicPanelPropertyChanged;
                panel.PropertyChanged += OnMosaicPanelPropertyChanged;
                MosaicProject.AddPanel(panel);
                newlyAdded.Add(panel);
            }

            if (failedCount > 0)
            {
                StatusMessage = IsTurkish
                    ? $"⚠️ {successItems.Count} fotoğraf yüklendi, {failedCount} dosya okunamadığı için atlandı."
                    : $"⚠️ Loaded {successItems.Count} photos; skipped {failedCount} unreadable files.";
            }

        if (newlyAdded.Count > 0)
        {
            foreach (var p in MosaicProject.Panels)
            {
                p.IsSelected = false;
            }
            SelectedMosaicPanel = null;
        }

        RefreshMosaicProperties();

        // Architectural Directive: Concurrently plate solve newly added panels (max 4 parallel workers)
        int solveWorkers = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        using var solveSemaphore = new SemaphoreSlim(solveWorkers);
        int solvedCount = 0;

        var solveTasks = newlyAdded.Select(async panel =>
        {
            await solveSemaphore.WaitAsync();
            try
            {
                if (string.IsNullOrEmpty(panel.FilePath)) return;

                var solvedNeighbors = MosaicProject.Panels
                    .Where(other => other != panel && other.IsSolved && other.Wcs != null)
                    .ToList();
                PlateSolvingResult? bestCatRes = null;

                foreach (var neighbor in solvedNeighbors)
                {
                    var catRes = await _plateSolver.SolveAsync(
                        panel.FilePath,
                        panel.Width,
                        panel.Height,
                        detectedStars: null,
                        cancellationToken: default,
                        hintCoordinates: neighbor.Wcs!.CenterCoordinates);

                    if (catRes.IsSuccess && catRes.Wcs != null)
                    {
                        bestCatRes = catRes;
                        break;
                    }
                }

                if (bestCatRes == null || !bestCatRes.IsSuccess)
                {
                    bestCatRes = await _plateSolver.SolveAsync(
                        panel.FilePath,
                        panel.Width,
                        panel.Height,
                        detectedStars: null,
                        cancellationToken: default,
                        hintCoordinates: null);
                }

                if (bestCatRes != null && bestCatRes.IsSuccess && bestCatRes.Wcs != null)
                {
                    panel.Wcs = bestCatRes.Wcs;
                    panel.TargetName = bestCatRes.TargetName;
                }

                int done = Interlocked.Increment(ref solvedCount);
                ProgressValue = 5.0 + ((double)done / newlyAdded.Count) * 35.0;
                StatusMessage = IsTurkish
                    ? $"🔭 [Aşama 1/3 - Astrometri {done}/{newlyAdded.Count}] '{panel.Name}' çözüldü"
                    : $"🔭 [Stage 1/3 - Astrometry {done}/{newlyAdded.Count}] Solved '{panel.Name}'";
            }
            catch { }
            finally
            {
                solveSemaphore.Release();
            }
        });

        await Task.WhenAll(solveTasks);

        // Pass 2: Multi-pass relative star-pattern alignment & neighbor-hint retry for any remaining unsolved panels
        // (Handles chains where Panel 1 was solved AFTER Panel 0, or Panel 3 overlaps with Panel 2 instead of Panel 0)
        bool progressMade = true;
        int pass = 0;
        while (progressMade && pass < 3)
        {
            progressMade = false;
            pass++;
            var unsolvedPanels = MosaicProject.Panels.Where(p => !p.IsSolved && !string.IsNullOrEmpty(p.FilePath)).ToList();
            if (unsolvedPanels.Count == 0) break;

            var solvedNeighbors = MosaicProject.Panels.Where(p => p.IsSolved && p.Wcs != null).ToList();
            if (solvedNeighbors.Count == 0) break;

            foreach (var panel in unsolvedPanels)
            {
                StatusMessage = IsTurkish
                    ? $"🔗 [Aşama 2/3 - Mozaik Kenetleme] '{panel.Name}' komşu karelerle ortak yıldızlardan hizalanıyor..."
                    : $"🔗 [Stage 2/3 - Mosaic Alignment] Aligning '{panel.Name}' against solved neighbor panels via shared stars...";
                await Task.Yield();
                try
                {
                    // 2A. Retry catalog solver using newly available neighbor hints
                    bool solvedNow = false;
                    foreach (var neighbor in solvedNeighbors)
                    {
                        var catRes = await _plateSolver.SolveAsync(
                            panel.FilePath!,
                            panel.Width,
                            panel.Height,
                            detectedStars: null,
                            cancellationToken: default,
                            hintCoordinates: neighbor.Wcs!.CenterCoordinates);

                        if (catRes.IsSuccess && catRes.Wcs != null)
                        {
                            panel.Wcs = catRes.Wcs;
                            panel.TargetName = catRes.TargetName;
                            solvedNow = true;
                            progressMade = true;
                            solvedNeighbors.Add(panel);
                            break;
                        }
                    }
                    if (solvedNow) continue;

                    // 2B. Try relative star-pattern registration against ALL solved neighbors (pick highest inlier match)
                    IReadOnlyList<AstrophotographyObject>? detectedStars = panel.DetectedStars;
                    if (detectedStars == null || detectedStars.Count < 4)
                    {
                        SkiaSharp.SKBitmap? bmp = (panel.CachedSkiaBitmap as SkiaSharp.SKBitmap) ?? (panel.CachedBitmap as SkiaSharp.SKBitmap);
                        bool shouldDisposeBmp = false;
                        if (bmp == null && !string.IsNullOrEmpty(panel.FilePath) && File.Exists(panel.FilePath))
                        {
                            bmp = ImagePreprocessor.LoadImage(panel.FilePath);
                            shouldDisposeBmp = true;
                        }

                        if (bmp != null)
                        {
                            StatusMessage = IsTurkish
                                ? $"⭐ [Aşama 2/3 - Yıldız Çıkarımı] '{panel.Name}' ({panel.Width}x{panel.Height} px) üzerindeki binlerce yıldız taranıyor..."
                                : $"⭐ [Stage 2/3 - Star Extraction] Extracting thousands of stars in '{panel.Name}' ({panel.Width}x{panel.Height} px)...";
                            await Task.Yield();
                            detectedStars = await Task.Run(() => PhotometricStarFinder.DetectStarsFast(
                                bmp,
                                new TilingConfig(DefaultConfidenceThreshold: 0.20f, MinConfidence: 0.05f)));
                            panel.DetectedStars = detectedStars;
                            if (shouldDisposeBmp)
                            {
                                bmp.Dispose();
                            }
                        }
                    }

                    if (detectedStars != null && detectedStars.Count >= 4)
                    {
                        MosaicStarAligner.AlignmentResult? bestAlign = null;
                        MosaicPanel? bestNeighbor = null;

                        foreach (var neighbor in solvedNeighbors)
                        {
                            var alignRes = MosaicStarAligner.TryAlign(panel, detectedStars, neighbor, neighbor.DetectedStars);
                            if (alignRes.IsSuccess && alignRes.SolvedWcs != null)
                            {
                                if (bestAlign == null ||
                                    alignRes.MatchedStars > bestAlign.MatchedStars ||
                                    (alignRes.MatchedStars == bestAlign.MatchedStars && alignRes.RmsResidualPixels < bestAlign.RmsResidualPixels))
                                {
                                    bestAlign = alignRes;
                                    bestNeighbor = neighbor;
                                }
                            }
                        }

                        if (bestAlign != null && bestAlign.SolvedWcs != null && bestNeighbor != null)
                        {
                            panel.Wcs = bestAlign.SolvedWcs;
                            panel.TargetName = $"{bestNeighbor.TargetName} ({bestAlign.MatchedStars} yıldız kenetli)";
                            progressMade = true;
                            solvedNeighbors.Add(panel);
                        }
                    }
                }
                catch { }
            }
        }

        RefreshMosaicProperties();

        if (MosaicProject.Count == 1)
        {
            CurrentWcs = MosaicProject.Panels[0].Wcs;
            SolvedCoordinatesText = CurrentWcs?.CenterCoordinates.ToString() ?? "-";
        }
        else
        {
            var masterWcs = MosaicProject.ComputeMasterWcs();
            CurrentWcs = masterWcs ?? MosaicProject.Panels[0].Wcs;
            SolvedCoordinatesText = CurrentWcs?.CenterCoordinates.ToString() ?? "-";
        }

        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
        OnPropertyChanged(nameof(IsSingleImageActive));

        // Scan all loaded mosaic panels in parallel at 100% full sensor resolution
        await ExecuteAnalyzeAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = IsTurkish
                ? $"⚠️ Toplu içe aktarma hatası: {ex.Message}"
                : $"⚠️ Batch import error: {ex.Message}";
        }
        finally
        {
            _isBatchImporting = false;
            IsLoading = false;
        }
    }

    private void ExecuteSelectImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = IsTurkish ? "Astrofotoğraf Seç" : "Select Astrophotograph",
            Multiselect = true,
            Filter = IsTurkish
                ? "Desteklenen Görseller (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.fits;*.fit;*.fts;*.xisf)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.fits;*.fit;*.fts;*.xisf|Tüm Dosyalar (*.*)|*.*"
                : "Supported Images (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.fits;*.fit;*.fts;*.xisf)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.fits;*.fit;*.fts;*.xisf|All Files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true && dialog.FileNames.Length > 0)
        {
            _ = LoadImagesAsync(dialog.FileNames, append: false);
        }
    }

    private void ExecuteAddPhotos()
    {
        var dialog = new OpenFileDialog
        {
            Title = IsTurkish ? "Fotoğrafları Projeye / Mozaik Atlasına Ekle" : "Add Photos to Project / Mosaic Atlas",
            Multiselect = true,
            Filter = IsTurkish
                ? "Desteklenen Görseller (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.fits;*.fit;*.fts;*.xisf)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.fits;*.fit;*.fts;*.xisf|Tüm Dosyalar (*.*)|*.*"
                : "Supported Images (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.fits;*.fit;*.fts;*.xisf)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.fits;*.fit;*.fts;*.xisf|All Files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true && dialog.FileNames.Length > 0)
        {
            _ = LoadImagesAsync(dialog.FileNames, append: true);
        }
    }

    public async Task ExecutePlateSolveAllPanelsAsync()
    {
        if (MosaicProject.Count == 0) return;
        IsLoading = true;
        for (int i = 0; i < MosaicProject.Count; i++)
        {
            var p = MosaicProject.Panels[i];
            if (string.IsNullOrEmpty(p.FilePath)) continue;

            StatusMessage = IsTurkish
                ? $"🔭 [{i + 1}/{MosaicProject.Count}] '{p.Name}' plate solving..."
                : $"🔭 [{i + 1}/{MosaicProject.Count}] Plate solving '{p.Name}'...";

            try
            {
                var solvedNeighbors = MosaicProject.Panels.Where(other => other != p && other.IsSolved && other.Wcs != null).ToList();
                PlateSolvingResult? bestCatRes = null;

                foreach (var neighbor in solvedNeighbors)
                {
                    var catRes = await _plateSolver.SolveAsync(
                        p.FilePath,
                        p.Width,
                        p.Height,
                        detectedStars: null,
                        cancellationToken: default,
                        hintCoordinates: neighbor.Wcs!.CenterCoordinates);

                    if (catRes.IsSuccess && catRes.Wcs != null)
                    {
                        bestCatRes = catRes;
                        break;
                    }
                }

                if (bestCatRes == null || !bestCatRes.IsSuccess)
                {
                    bestCatRes = await _plateSolver.SolveAsync(
                        p.FilePath,
                        p.Width,
                        p.Height,
                        detectedStars: null,
                        cancellationToken: default,
                        hintCoordinates: null);
                }

                if (bestCatRes != null && bestCatRes.IsSuccess && bestCatRes.Wcs != null)
                {
                    p.Wcs = bestCatRes.Wcs;
                    p.TargetName = bestCatRes.TargetName;
                }
            }
            catch { }
        }

        // Second pass: relative star-pattern registration against ALL solved neighbors for any remaining unsolved panels
        bool progressMade = true;
        int pass = 0;
        while (progressMade && pass < 3)
        {
            progressMade = false;
            pass++;
            var unsolvedPanels = MosaicProject.Panels.Where(p => !p.IsSolved && !string.IsNullOrEmpty(p.FilePath)).ToList();
            if (unsolvedPanels.Count == 0) break;

            var solvedNeighbors = MosaicProject.Panels.Where(p => p.IsSolved && p.Wcs != null).ToList();
            if (solvedNeighbors.Count == 0) break;

            foreach (var p in unsolvedPanels)
            {
                StatusMessage = IsTurkish
                    ? $"🔗 [Aşama 2/3 - Mozaik Kenetleme] '{p.Name}' komşu karelerle ortak yıldızlardan hizalanıyor..."
                    : $"🔗 [Stage 2/3 - Mosaic Alignment] Aligning '{p.Name}' against solved neighbor panels via shared stars...";
                await Task.Yield();
                try
                {
                    IReadOnlyList<AstrophotographyObject>? detectedStars = p.DetectedStars;
                    if (detectedStars == null || detectedStars.Count < 4)
                    {
                        SkiaSharp.SKBitmap? bmp = (p.CachedSkiaBitmap as SkiaSharp.SKBitmap) ?? (p.CachedBitmap as SkiaSharp.SKBitmap);
                        bool shouldDisposeBmp = false;
                        if (bmp == null && !string.IsNullOrEmpty(p.FilePath) && File.Exists(p.FilePath))
                        {
                            bmp = ImagePreprocessor.LoadImage(p.FilePath);
                            shouldDisposeBmp = true;
                        }

                        if (bmp != null)
                        {
                            StatusMessage = IsTurkish
                                ? $"⭐ [Aşama 2/3 - Yıldız Çıkarımı] '{p.Name}' ({p.Width}x{p.Height} px) üzerindeki binlerce yıldız taranıyor..."
                                : $"⭐ [Stage 2/3 - Star Extraction] Extracting thousands of stars in '{p.Name}' ({p.Width}x{p.Height} px)...";
                            await Task.Yield();
                            detectedStars = await Task.Run(() => PhotometricStarFinder.DetectStarsFast(
                                bmp,
                                new TilingConfig(DefaultConfidenceThreshold: 0.20f, MinConfidence: 0.05f)));
                            p.DetectedStars = detectedStars;
                            if (shouldDisposeBmp)
                            {
                                bmp.Dispose();
                            }
                        }
                    }

                    if (detectedStars != null && detectedStars.Count >= 4)
                    {
                        MosaicStarAligner.AlignmentResult? bestAlign = null;
                        MosaicPanel? bestNeighbor = null;

                        foreach (var neighbor in solvedNeighbors)
                        {
                            var alignRes = MosaicStarAligner.TryAlign(p, detectedStars, neighbor, neighbor.DetectedStars);
                            if (alignRes.IsSuccess && alignRes.SolvedWcs != null)
                            {
                                if (bestAlign == null ||
                                    alignRes.MatchedStars > bestAlign.MatchedStars ||
                                    (alignRes.MatchedStars == bestAlign.MatchedStars && alignRes.RmsResidualPixels < bestAlign.RmsResidualPixels))
                                {
                                    bestAlign = alignRes;
                                    bestNeighbor = neighbor;
                                }
                            }
                        }

                        if (bestAlign != null && bestAlign.SolvedWcs != null && bestNeighbor != null)
                        {
                            p.Wcs = bestAlign.SolvedWcs;
                            p.TargetName = $"{bestNeighbor.TargetName} ({bestAlign.MatchedStars} yıldız kenetli)";
                            progressMade = true;
                            solvedNeighbors.Add(p);
                        }
                    }
                }
                catch { }
            }
        }

        var masterWcs = MosaicProject.ComputeMasterWcs();
        CurrentWcs = masterWcs ?? MosaicProject.Panels[0].Wcs;
        SolvedCoordinatesText = CurrentWcs?.CenterCoordinates.ToString() ?? "-";
        RefreshMosaicProperties();
        await ExecuteAnalyzeAsync();
    }

    public async Task SolveAndAddMosaicBatchAsync(IEnumerable<string> filePaths)
    {
        int count = 0;
        foreach (var path in filePaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                StatusMessage = IsTurkish
                    ? $"Mozaik paneli çözümleniyor: {Path.GetFileName(path)}..."
                    : $"Solving mosaic panel: {Path.GetFileName(path)}...";

                using var skBmp = ImagePreprocessor.LoadImage(path);
                int w = skBmp.Width;
                int h = skBmp.Height;

                var solvedNeighbor = MosaicProject.Panels.FirstOrDefault(other => other.TargetName != null && other.Wcs != null);
                CelestialCoordinates? hint = solvedNeighbor?.Wcs?.CenterCoordinates;

                WcsMatrix? solvedWcs = null;
                string? solvedTarget = null;

                var res = await _plateSolver.SolveAsync(
                    path,
                    w,
                    h,
                    detectedStars: null,
                    cancellationToken: default,
                    hintCoordinates: hint);

                if (!res.IsSuccess && hint != null)
                {
                    res = await _plateSolver.SolveAsync(
                        path,
                        w,
                        h,
                        detectedStars: null,
                        cancellationToken: default,
                        hintCoordinates: null);
                }

                if (res.IsSuccess && res.Wcs != null)
                {
                    solvedWcs = res.Wcs;
                    solvedTarget = res.TargetName;
                }

                if (solvedWcs == null && solvedNeighbor != null)
                {
                    var detectedStars = PhotometricStarFinder.DetectStarsFast(
                        skBmp,
                        new TilingConfig(DefaultConfidenceThreshold: 0.20f, MinConfidence: 0.05f));

                    if (detectedStars.Count >= 4)
                    {
                        var tempPanel = new MosaicPanel(Guid.NewGuid().ToString("N"), Path.GetFileNameWithoutExtension(path), w, h, null, path)
                        {
                            DetectedStars = detectedStars
                        };
                        var alignRes = MosaicStarAligner.TryAlign(tempPanel, detectedStars, solvedNeighbor, solvedNeighbor.DetectedStars);
                        if (alignRes.IsSuccess && alignRes.SolvedWcs != null)
                        {
                            solvedWcs = alignRes.SolvedWcs;
                            solvedTarget = $"{solvedNeighbor.TargetName} ({alignRes.MatchedStars} yıldız kenetli)";
                        }
                    }
                }

                if (solvedWcs != null)
                {
                    AddOrUpdateMosaicPanel(path, w, h, solvedWcs, solvedTarget);
                    count++;
                }
            }
            catch
            {
                // Continue
            }
        }

        StatusMessage = IsTurkish
            ? $"{count} yeni mozaik paneli başarıyla küreye eklendi (Toplam panel: {MosaicProject.Count})."
            : $"{count} mosaic panels added to celestial sphere (Total panels: {MosaicProject.Count}).";

        if (count > 0)
        {
            _ = ExecuteAnalyzeAsync();
        }
    }

    private void ExecutePerformExport()
    {
        if (!HasLoadedImage && _lastDetectionResult == null) return;

        IReadOnlyDictionary<string, float>? perPanelMap = null;
        if (MosaicProject.Count > 0)
        {
            var dict = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var p in MosaicProject.Panels)
            {
                dict[p.Id] = p.ConfidenceThreshold;
            }
            perPanelMap = dict;
        }

        var filtered = _lastDetectionResult != null
            ? (IReadOnlyList<AstrophotographyObject>)_lastDetectionResult.Filter(_confidenceThreshold, _mask, perPanelMap).FilteredObjects
            : VisibleObjects;

        string fmt = ExportFormat.ToUpperInvariant();
        if (fmt == "CSV")
        {
            var sfd = new SaveFileDialog
            {
                Title = IsTurkish ? "CSV Kaydet" : "Save CSV",
                Filter = "CSV (*.csv)|*.csv",
                FileName = $"AstroDetections_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };
            if (sfd.ShowDialog() == true)
            {
                AstroCounter.Core.Export.ResultExporter.ExportToCsv(filtered, sfd.FileName);
                IsExportPanelOpen = false;
                StatusMessage = IsTurkish ? $"CSV kaydedildi: {Path.GetFileName(sfd.FileName)}" : $"CSV saved: {Path.GetFileName(sfd.FileName)}";
            }
            return;
        }

        if (fmt == "JSON")
        {
            var sfd = new SaveFileDialog
            {
                Title = IsTurkish ? "JSON Kaydet" : "Save JSON",
                Filter = "JSON (*.json)|*.json",
                FileName = $"AstroDetections_{DateTime.Now:yyyyMMdd_HHmmss}.json"
            };
            if (sfd.ShowDialog() == true)
            {
                AstroCounter.Core.Export.ResultExporter.ExportToJson(filtered, CurrentWcs, sfd.FileName);
                IsExportPanelOpen = false;
                StatusMessage = IsTurkish ? $"JSON kaydedildi: {Path.GetFileName(sfd.FileName)}" : $"JSON saved: {Path.GetFileName(sfd.FileName)}";
            }
            return;
        }

        // Image export: TIFF, PNG, JPG
        string ext = fmt.ToLowerInvariant();
        string prefix = IsExportPanorama360 ? "AstroPanorama360" : "AstroDetected";
        var imgSfd = new SaveFileDialog
        {
            Title = IsTurkish ? $"{fmt} Olarak Kaydet" : $"Save as {fmt}",
            Filter = $"{fmt} (*.{ext})|*.{ext}|All Files (*.*)|*.*",
            FileName = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}"
        };

        if (imgSfd.ShowDialog() == true)
        {
            try
            {
                var cfg = new AstroCounter.Core.Export.ExportConfig(
                    Format: fmt,
                    IncludeBoundingBoxes: ExportIncludeBoxes,
                    IncludeLabels: ExportIncludeLabels,
                    IncludeSphericalGrid: ExportIncludeGrid,
                    IncludeConstellations: ExportIncludeConstellations,
                    ScaleFactor: ExportScale,
                    ExportProjectionMode: ExportProjectionMode);

                string sourceImg = SelectedImagePath ?? MosaicProject.Panels.FirstOrDefault(p => !string.IsNullOrEmpty(p.FilePath))?.FilePath ?? string.Empty;
                AstroCounter.Core.Export.ResultExporter.ExportAdvancedImage(
                    sourceImg,
                    filtered,
                    imgSfd.FileName,
                    cfg,
                    CurrentWcs,
                    _mask,
                    MosaicProject);

                IsExportPanelOpen = false;
                StatusMessage = IsTurkish
                    ? $"{fmt} ({(IsExportPanorama360 ? "360° Panoramik" : "2D Görsel")}) kaydedildi: {Path.GetFileName(imgSfd.FileName)}"
                    : $"{fmt} ({(IsExportPanorama360 ? "360° Panorama" : "2D Canvas")}) saved: {Path.GetFileName(imgSfd.FileName)}";
            }
            catch (Exception ex)
            {
                StatusMessage = IsTurkish ? $"Dışa aktarma hatası: {ex.Message}" : $"Export error: {ex.Message}";
            }
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

    public bool ShowHipparcosStars
    {
        get => _showHipparcosStars;
        set => SetProperty(ref _showHipparcosStars, value);
    }

    public bool ShowConstellations
    {
        get => _showConstellations;
        set => SetProperty(ref _showConstellations, value);
    }

    public bool ShowDsoMarkers
    {
        get => _showDsoMarkers;
        set => SetProperty(ref _showDsoMarkers, value);
    }

    public double PhotoOpacity
    {
        get => _photoOpacity;
        set
        {
            if (SetProperty(ref _photoOpacity, Math.Clamp(value, 0.05, 1.0)))
            {
                OnPropertyChanged(nameof(PhotoOpacityPercentText));
            }
        }
    }

    public string PhotoOpacityPercentText => $"{(int)Math.Round(_photoOpacity * 100)}%";

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
            : (CurrentWcs == null
                ? (IsTurkish ? "🔭 Cisimler (Önce Gökyüzünü Eşle)" : "🔭 Targets (Solve Sky First)")
                : (IsTurkish ? "Kadrajda Katalog Cismi Yok" : "No Catalog Objects in Frame"));

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
        set
        {
            if (SetProperty(ref _currentWcs, value))
            {
                OnPropertyChanged(nameof(IsWcsAvailable));
                OnPropertyChanged(nameof(CanSyncToStellarium));
                OnPropertyChanged(nameof(TargetComboPlaceholderText));
                OnPropertyChanged(nameof(CanvasWidth));
                OnPropertyChanged(nameof(CanvasHeight));
                OnPropertyChanged(nameof(IsSingleImageActive));
                RefreshAvailableTargetsInPhoto();
            }
        }
    }

    public bool IsWcsAvailable => CurrentWcs != null;
    public bool CanSyncToStellarium => IsStellariumConnected && (CurrentWcs != null || IsSphereModeActive);

    private void RefreshAvailableTargetsInPhoto()
    {
        _selectedCatalogueTarget = null;
        OnPropertyChanged(nameof(SelectedCatalogueTarget));
        TargetMarkerPixel = null;
        TargetMarkerLabel = null;

        int refW = (int)Math.Max(1, CanvasWidth);
        int refH = (int)Math.Max(1, CanvasHeight);

        if (_currentWcs == null || refW <= 1 || refH <= 1)
        {
            AvailableTargetsInPhoto = Array.Empty<CataloguePlateSolver.AstronomicalTarget>();
            return;
        }

        AvailableTargetsInPhoto = CataloguePlateSolver.GetTargetsInField(
            _currentWcs,
            refW,
            refH,
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

    /// <summary>
    /// Called when the user interactively drags or zooms the 3D celestial sphere.
    /// Synchronizes Stellarium 1:1 in real time.
    /// </summary>
    public void OnSphereCameraMoved(double raDeg, double decDeg, double fovDeg)
    {
        _sphereCameraRa = raDeg;
        _sphereCameraDec = decDeg;
        _sphereCameraFov = fovDeg;

        var coord = new CelestialCoordinates(raDeg, decDeg);
        CursorCelestialText = $"RA: {coord.FormatRa()} | Dec: {coord.FormatDec()} (FOV: {fovDeg:F1}°)";

        if (!_isStellariumConnected || !_isStellariumSyncEnabled || _isSyncingFromStellarium) return;

        long now = Environment.TickCount64;
        if (now - _lastSyncTicks < 60) return;
        _lastSyncTicks = now;

        _lastPolledStellariumCoords = coord;
        _lastPolledStellariumFov = fovDeg;

        _ = Task.Run(async () =>
        {
            try
            {
                await _stellariumClient.FocusCoordinatesAsync(coord, Math.Clamp(fovDeg, 0.5, 130.0), 0.0);
            }
            catch
            {
                // Ignore transient network errors
            }
        });
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
        double refHeight = visiblePixelsHeight > 10 ? visiblePixelsHeight : CanvasHeight;

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
        if (_currentWcs == null || CanvasWidth <= 1 || CanvasHeight <= 1)
        {
            return;
        }

        if (!_currentWcs.EquatorialToPixel(target.Coordinates, out double px, out double py))
        {
            return;
        }

        // Highlight and jump directly to (px, py) on the photo / mosaic canvas
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
            CanvasHeight);

        _isSyncingFromStellarium = true;
        RequestCenterOnPixel?.Invoke(px, py);
        RequestSetVisiblePixelsHeight?.Invoke(targetVisiblePixelsH);
        RequestCenterOnPixel?.Invoke(px, py);
        RequestSet3DCamera?.Invoke(target.Coordinates.RaDegrees, target.Coordinates.DecDegrees, Math.Clamp(targetFovDeg * 2.2, 8.0, 75.0));
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
        if (!_isStellariumConnected || !_isStellariumSyncEnabled) return;
        if (_currentWcs == null && !IsSphereModeActive) return;
        if (Environment.TickCount64 - _lastSyncTicks < 450) return;

        try
        {
            var status = await _stellariumClient.GetStatusAsync();
            if (status.IsConnected)
            {
                ApplyStellariumSkyAngle(status.SkyRollAngleDegrees);

                bool fovChanged = false;
                if (_isStellariumZoomSyncEnabled && status.CurrentFovDegrees > 0.01)
                {
                    double fovRelDiff = Math.Abs(status.CurrentFovDegrees - _lastPolledStellariumFov) / Math.Max(0.05, _lastPolledStellariumFov);
                    if (fovRelDiff > 0.015)
                    {
                        _lastPolledStellariumFov = status.CurrentFovDegrees;
                        fovChanged = true;
                        if (_currentWcs != null && _currentWcs.PixelScaleArcsec > 0.01)
                        {
                            double targetVisiblePixelsH = (status.CurrentFovDegrees * 3600.0) / _currentWcs.PixelScaleArcsec;
                            _isSyncingFromStellarium = true;
                            RequestSetVisiblePixelsHeight?.Invoke(targetVisiblePixelsH);
                            _isSyncingFromStellarium = false;
                        }
                    }
                }

                double dRa = Math.Abs(status.CurrentCoordinates.RaDegrees - _lastPolledStellariumCoords.RaDegrees);
                if (dRa > 180.0) dRa = 360.0 - dRa;
                double dDec = Math.Abs(status.CurrentCoordinates.DecDegrees - _lastPolledStellariumCoords.DecDegrees);

                double moveThresholdDeg = Math.Max(0.001, status.CurrentFovDegrees * 0.002);
                if (dRa > moveThresholdDeg || dDec > moveThresholdDeg || fovChanged)
                {
                    _lastPolledStellariumCoords = status.CurrentCoordinates;
                    _isSyncingFromStellarium = true;
                    try
                    {
                        // Always sync the 3D celestial sphere camera when Stellarium moves
                        RequestSet3DCamera?.Invoke(
                            status.CurrentCoordinates.RaDegrees,
                            status.CurrentCoordinates.DecDegrees,
                            status.CurrentFovDegrees > 0.5 ? status.CurrentFovDegrees : _sphereCameraFov);

                        // Also sync the 2D canvas if WCS is available
                        if (_currentWcs != null && _currentWcs.EquatorialToPixel(status.CurrentCoordinates, out double px, out double py))
                        {
                            double cw = Math.Max(1.0, CanvasWidth);
                            double ch = Math.Max(1.0, CanvasHeight);
                            if (px >= -cw * 1.5 && px <= cw * 2.5 &&
                                py >= -ch * 1.5 && py <= ch * 2.5)
                            {
                                RequestCenterOnPixel?.Invoke(px, py);
                            }
                        }
                    }
                    finally
                    {
                        _isSyncingFromStellarium = false;
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
            StellariumStatusText = IsTurkish ? "Canlı Senk: Bağlan" : "Live Sync: Connect";
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
            StellariumStatusText = IsTurkish ? "Canlı Senk: Bağlı" : "Live Sync: Active";
            _stellariumPollTimer.Start();

            // Ensure windowed mode, dark sky, and paused clock via API
            await _stellariumClient.EnsureWindowedModeAsync();

            if (_currentWcs != null || IsSphereModeActive)
            {
                StatusMessage = IsTurkish
                    ? "Canlı gök senkronizasyonuna bağlandı. 1:1 odaklanılıyor..."
                    : "Connected to live sky sync. Synchronizing 1:1...";
                await SyncCurrentCenterToStellariumAsync();
            }
            else if (!string.IsNullOrEmpty(SelectedImagePath))
            {
                StatusMessage = IsTurkish
                    ? "Canlı gök senkronizasyonuna bağlandı. Görsel 1:1 eşitleniyor..."
                    : "Connected to live sky sync. Solving and synchronizing image 1:1...";
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
                    ? "Canlı gök senkronizasyonu aktif. Çift yönlü 1:1 eşitleme devrede."
                    : "Live sky synchronization active. Bidirectional 1:1 synchronization enabled.";
            }
        }
        else
        {
            IsStellariumConnected = false;
            StellariumStatusText = IsTurkish ? "Canlı Senk: Çevrimdışı" : "Live Sync: Offline";
            StatusMessage = IsTurkish
                ? "Harici planetaryum gök sunucusu bulunamadı veya açılamadı! Lütfen sunucu eklentisinin açık olduğundan (Port 8090) emin olun."
                : "External celestial planetarium server was not found or could not be reached (Port 8090).";
        }
    }

    private async Task ExecuteSmartPlateSolveAsync()
    {
        if (MosaicProject.Count > 1)
        {
            await ExecutePlateSolveAllPanelsAsync();
        }
        else
        {
            await ExecutePlateSolveAsync();
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
                VisibleObjects.Count > 0 ? VisibleObjects : null);

            if (result.IsSuccess && result.Wcs != null)
            {
                _isStarPatternSolved = true;
                _solvedCameraAngle = result.Wcs.RotationDegrees;
                _userManualAngleOffset = 0.0;
                CurrentWcs = result.Wcs;
                DiagnosticWarningText = result.WarningMessage;
                StellariumGuideText = result.StellariumGuide;

                if (!string.IsNullOrEmpty(SelectedImagePath) && ImageBitmap != null)
                {
                    DetectionOverlay.CachePanelBitmap(SelectedImagePath, ImageBitmap);
                }

                if (MosaicProject.Count <= 1)
                {
                    AddOrUpdateMosaicPanel(
                        SelectedImagePath,
                        (int)Math.Round(ImageWidth),
                        (int)Math.Round(ImageHeight),
                        result.Wcs,
                        result.TargetName);
                }
                else
                {
                    var existing = MosaicProject.Panels.FirstOrDefault(p =>
                        !string.IsNullOrEmpty(p.FilePath) &&
                        string.Equals(p.FilePath, SelectedImagePath, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        existing.Wcs = result.Wcs;
                        existing.TargetName = result.TargetName;
                        RefreshMosaicProperties();
                    }
                }

                // Immediately orient photo to North-Up (plus Stellarium sky roll if connected)
                ApplyStellariumSkyAngle(_stellariumSkyAngle);
                RequestCenterOnPixel?.Invoke(ImageWidth / 2.0, ImageHeight / 2.0);

                var coords = result.Wcs.CenterCoordinates;
                double refHeight = _lastVisiblePixelsHeight > 10 ? _lastVisiblePixelsHeight : ImageHeight;
                double fov = Math.Clamp((refHeight * result.Wcs.PixelScaleArcsec) / 3600.0, 0.05, 130.0);

                if (_lastDetectionResult != null && result.Wcs != null)
                {
                    var anomalyChecked = AstroCounter.Core.Astrometry.TransientAnomalyDetector.DetectAnomalies(
                        _lastDetectionResult.AllObjects,
                        result.Wcs,
                        (int)ImageWidth,
                        (int)ImageHeight);

                    string? pId = MosaicProject.Panels.FirstOrDefault(p =>
                        !string.IsNullOrEmpty(p.FilePath) &&
                        string.Equals(p.FilePath, SelectedImagePath, StringComparison.OrdinalIgnoreCase))?.Id
                        ?? MosaicProject.Panels.FirstOrDefault()?.Id;

                    var withSky = new List<AstrophotographyObject>(anomalyChecked.Count);
                    foreach (var o in anomalyChecked)
                    {
                        var sky = result.Wcs.PixelToEquatorial(o.CenterX, o.CenterY);
                        withSky.Add(o.WithCelestialCoordinates(sky.RaDegrees, sky.DecDegrees, pId));
                    }

                    _lastDetectionResult = new DetectionResult(
                        withSky,
                        _lastDetectionResult.ImageWidth,
                        _lastDetectionResult.ImageHeight,
                        _lastDetectionResult.TileCount,
                        _lastDetectionResult.ElapsedTime,
                        _lastDetectionResult.ExecutionProvider,
                        _lastDetectionResult.RecommendedConfidence);

                    ApplyLiveFilter();
                }

                SolvedCoordinatesText = $"{coords.FormatRa()}  {coords.FormatDec()}";
                TargetDetailsText = IsTurkish
                    ? $"{result.TargetName} ({result.MatchedStarCount} yıldız eşleşti, Kadrajda {AvailableTargetsInPhoto.Count} gök cismi bulundu, FOV: {result.Wcs!.FovWidthDegrees:F1}°x{result.Wcs!.FovHeightDegrees:F1}°)"
                    : $"{result.TargetName} ({result.MatchedStarCount} stars matched, {AvailableTargetsInPhoto.Count} catalog objects in frame, FOV: {result.Wcs!.FovWidthDegrees:F1}°x{result.Wcs!.FovHeightDegrees:F1}°)";
                
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
        if (!IsStellariumConnected) return;
        if (_currentWcs == null && !IsSphereModeActive) return;
        try
        {
            CelestialCoordinates targetCoords;
            double fov;

            if (IsSphereModeActive)
            {
                targetCoords = _currentWcs != null ? _currentWcs.CenterCoordinates : new CelestialCoordinates(_sphereCameraRa, _sphereCameraDec);
                fov = Math.Clamp(_sphereCameraFov, 2.0, 100.0);
                RequestSet3DCamera?.Invoke(targetCoords.RaDegrees, targetCoords.DecDegrees, fov);
            }
            else
            {
                RequestCenterOnPixel?.Invoke(CanvasWidth / 2.0, CanvasHeight / 2.0);
                double refHeight = _lastVisiblePixelsHeight > 10 ? _lastVisiblePixelsHeight : CanvasHeight;
                fov = Math.Clamp((refHeight * _currentWcs!.PixelScaleArcsec) / 3600.0, 0.05, 130.0);
                targetCoords = _currentWcs.CenterCoordinates;
            }

            _lastPolledStellariumCoords = targetCoords;
            _lastPolledStellariumFov = fov;
            _lastSyncTicks = Environment.TickCount64;

            await _stellariumClient.FocusCoordinatesAsync(targetCoords, fov, 0.0);
            var status = await _stellariumClient.GetStatusAsync();
            if (status.IsConnected)
            {
                ApplyStellariumSkyAngle(status.SkyRollAngleDegrees);
            }
            StatusMessage = IsTurkish
                ? $"Gök kubbe görsel merkezine, büyüklüğüne ({fov:F1}°) ve açısına 1:1 odaklandı."
                : $"Celestial dome synchronized 1:1 to image center, FOV ({fov:F1}°), and roll angle.";
        }
        catch (Exception ex)
        {
            StatusMessage = IsTurkish ? $"Gök kubbe eşitleme hatası: {ex.Message}" : $"Celestial sync error: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _stellariumPollTimer.Stop();
        _stellariumClient.Dispose();
        _originalSkBitmap?.Dispose();
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



