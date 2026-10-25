namespace AstroCounter.Core.Astrometry;

using System;
using System.Collections.Generic;
using System.Linq;
using AstroCounter.Core.Models;

/// <summary>
/// Represents a single astrophotography photo tile within a celestial sky mosaic.
/// Encapsulates its image dimensions, WCS celestial astrometry solution, footprint, and rendering properties.
/// </summary>
public class MosaicPanel : System.ComponentModel.INotifyPropertyChanged
{
    private double _opacity = 1.0;
    private bool _isVisible = true;
    private double _brightness = 0.0;
    private double _exposure = 1.0;
    private double _contrast = 1.0;
    private bool _isAutoBrightnessEnabled = false;
    private bool _useFeatheredBlending = true;
    private double _featherFraction = 0.05;
    private float _confidenceThreshold = 0.25f;
    private float _autoConfidenceThreshold = 0.25f;
    private bool _isAutoConfidenceEnabled = true;
    private bool _isSelected = false;

    public string Id { get; }
    public string Name { get; set; }
    public string? FilePath { get; set; }
    public int Width { get; }
    public int Height { get; }
    public WcsMatrix? Wcs { get; set; }
    public string? TargetName { get; set; }

    public bool IsLossyFormat
    {
        get
        {
            string ext = System.IO.Path.GetExtension(FilePath ?? string.Empty).ToLowerInvariant();
            return ext is ".jpg" or ".jpeg" or ".webp";
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
                OnPropertyChanged(nameof(CardBorderBrush));
                OnPropertyChanged(nameof(CardBackground));
                OnPropertyChanged(nameof(CardBorderThickness));
            }
        }
    }

    public string CardBorderBrush => _isSelected ? "#38BDF8" : "#23324B";
    public string CardBackground => _isSelected ? "#162A45" : "#131C2E";
    public double CardBorderThickness => _isSelected ? 1.8 : 1.0;

    public float ConfidenceThreshold
    {
        get => _confidenceThreshold;
        set
        {
            float clamped = (float)Math.Round(Math.Clamp(value, 0.01f, 0.99f), 2);
            if (Math.Abs(_confidenceThreshold - clamped) > 1e-4f)
            {
                _confidenceThreshold = clamped;
                OnPropertyChanged(nameof(ConfidenceThreshold));
                OnPropertyChanged(nameof(ConfidencePercentText));
            }
        }
    }

    public float AutoConfidenceThreshold
    {
        get => _autoConfidenceThreshold;
        set
        {
            float clamped = (float)Math.Round(Math.Clamp(value, 0.01f, 0.99f), 2);
            if (Math.Abs(_autoConfidenceThreshold - clamped) > 1e-4f)
            {
                _autoConfidenceThreshold = clamped;
                OnPropertyChanged(nameof(AutoConfidenceThreshold));
                if (_isAutoConfidenceEnabled)
                {
                    ConfidenceThreshold = clamped;
                }
            }
        }
    }

    public bool IsAutoConfidenceEnabled
    {
        get => _isAutoConfidenceEnabled;
        set
        {
            if (_isAutoConfidenceEnabled != value)
            {
                _isAutoConfidenceEnabled = value;
                OnPropertyChanged(nameof(IsAutoConfidenceEnabled));
                if (value)
                {
                    ConfidenceThreshold = _autoConfidenceThreshold;
                }
            }
        }
    }

    public string ConfidencePercentText => CataloguePlateSolver.UseTurkishLanguage
        ? $"%{(int)Math.Round(_confidenceThreshold * 100)}"
        : $"{(int)Math.Round(_confidenceThreshold * 100)}%";

    public double Exposure
    {
        get => _exposure;
        set
        {
            if (Math.Abs(_exposure - value) > 1e-4)
            {
                _exposure = value;
                OnPropertyChanged(nameof(Exposure));
            }
        }
    }

    public bool IsAutoBrightnessEnabled
    {
        get => _isAutoBrightnessEnabled;
        set
        {
            if (_isAutoBrightnessEnabled != value)
            {
                _isAutoBrightnessEnabled = value;
                OnPropertyChanged(nameof(IsAutoBrightnessEnabled));
            }
        }
    }

    /// <summary>
    /// Soft feathered alpha edge blending to eliminate hard seam boundaries between overlapping mosaic tiles.
    /// </summary>
    public bool UseFeatheredBlending
    {
        get => _useFeatheredBlending;
        set
        {
            if (_useFeatheredBlending != value)
            {
                _useFeatheredBlending = value;
                OnPropertyChanged(nameof(UseFeatheredBlending));
            }
        }
    }

    /// <summary>
    /// Feathering fraction along image borders (0.01 - 0.25, default 0.05).
    /// </summary>
    public double FeatherFraction
    {
        get => _featherFraction;
        set
        {
            double clamped = Math.Clamp(value, 0.01, 0.25);
            if (Math.Abs(_featherFraction - clamped) > 1e-4)
            {
                _featherFraction = clamped;
                OnPropertyChanged(nameof(FeatherFraction));
            }
        }
    }

    /// <summary>
    /// Cached in-memory image bitmap representation (e.g., WPF BitmapSource or byte buffer)
    /// allowing zero-IO rendering across 2D canvas and 3D celestial sphere.
    /// </summary>
    public object? CachedBitmap { get; set; }

    /// <summary>
    /// Cached SkiaSharp SKBitmap representation for fast CPU photometry and plate solving.
    /// </summary>
    public object? CachedSkiaBitmap { get; set; }

    public double Opacity
    {
        get => _opacity;
        set
        {
            if (Math.Abs(_opacity - value) > 1e-4)
            {
                _opacity = value;
                OnPropertyChanged(nameof(Opacity));
            }
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible != value)
            {
                _isVisible = value;
                OnPropertyChanged(nameof(IsVisible));
            }
        }
    }

    public double Brightness
    {
        get => _brightness;
        set
        {
            if (Math.Abs(_brightness - value) > 1e-4)
            {
                _brightness = value;
                OnPropertyChanged(nameof(Brightness));
            }
        }
    }

    public double Contrast
    {
        get => _contrast;
        set
        {
            if (Math.Abs(_contrast - value) > 1e-4)
            {
                _contrast = value;
                OnPropertyChanged(nameof(Contrast));
            }
        }
    }

    public bool IsSolved => Wcs != null && (TargetName != null || Math.Abs(Wcs.CenterCoordinates.RaDegrees - 180.0) > 1e-3 || Math.Abs(Wcs.CenterCoordinates.DecDegrees - 30.0) > 1e-3);
    public string StatusText => IsSolved
        ? (TargetName != null ? $"✅ {TargetName}" : (CataloguePlateSolver.UseTurkishLanguage ? "✅ Çözümlendi" : "✅ Solved"))
        : (CataloguePlateSolver.UseTurkishLanguage ? "⚠️ Hizalanamadı / Hizalanıyor" : "⚠️ Unsolved / Aligning");
    public string CenterCoordinatesText => Wcs != null ? $"{Wcs.CenterCoordinates.FormatRa()}  {Wcs.CenterCoordinates.FormatDec()}" : "-";
    public string ResolutionText => $"{Width} × {Height} px";
    public string L_OpacityLabel => CataloguePlateSolver.UseTurkishLanguage ? "Opaklık:" : "Opacity:";

    public string FormatBadgeText
    {
        get
        {
            bool tr = CataloguePlateSolver.UseTurkishLanguage;
            string ext = System.IO.Path.GetExtension(FilePath ?? string.Empty).ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" or ".webp" => tr ? "⚠️ JPEG Kayıplı" : "⚠️ Lossy JPEG",
                ".png" => tr ? "PNG Kayıpsız" : "Lossless PNG",
                ".tif" or ".tiff" => tr ? "TIFF Kayıpsız" : "Lossless TIFF",
                ".fits" or ".fit" or ".fts" => tr ? "FITS Bilimsel" : "Scientific FITS",
                ".cr2" or ".cr3" or ".nef" or ".arw" or ".dng" => tr ? "RAW Kayıpsız" : "Lossless RAW",
                _ => tr ? "Format Bilinmiyor" : "Unknown Format"
            };
        }
    }

    public string FormatBadgeTooltip
    {
        get
        {
            bool tr = CataloguePlateSolver.UseTurkishLanguage;
            string ext = System.IO.Path.GetExtension(FilePath ?? string.Empty).ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" or ".webp" => tr
                    ? "JPEG sıkıştırma artefaktları içerir; otomatik güven eşiği gürültüyü önlemek için daha dikkatli (yüksek) seçilir."
                    : "Contains lossy JPEG compression artifacts; auto-confidence uses a stricter threshold to suppress block noise.",
                ".png" => tr ? "Kayıpsız 8/16-bit RGB format." : "Lossless 8/16-bit RGB format.",
                ".tif" or ".tiff" => tr
                    ? "Kayıpsız yüksek dinamik aralıklı astrofotografi formatı. Doğru fotometri için idealdir."
                    : "Lossless high-dynamic-range astrophotography format. Ideal for accurate photometry.",
                ".fits" or ".fit" or ".fts" => tr
                    ? "16/32-bit lineer bilimsel astronomik FITS veri formatı."
                    : "16/32-bit linear scientific astronomical FITS data format.",
                ".cr2" or ".cr3" or ".nef" or ".arw" or ".dng" => tr
                    ? "Kamera ham sensör verisi (RAW)."
                    : "Camera raw sensor data (RAW).",
                _ => tr ? "Görsel formatı." : "Image format."
            };
        }
    }

    public string FormatBadgeBackground
    {
        get
        {
            string ext = System.IO.Path.GetExtension(FilePath ?? string.Empty).ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" => "#2D1B04",
                ".tif" or ".tiff" => "#064E3B",
                ".fits" or ".fit" or ".fts" => "#3B0764",
                ".cr2" or ".cr3" or ".nef" or ".arw" or ".dng" => "#1E1B4B",
                _ => "#1E293B"
            };
        }
    }

    public string FormatBadgeForeground
    {
        get
        {
            string ext = System.IO.Path.GetExtension(FilePath ?? string.Empty).ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" => "#FBBF24",
                ".tif" or ".tiff" => "#34D399",
                ".fits" or ".fit" or ".fts" => "#C084FC",
                ".cr2" or ".cr3" or ".nef" or ".arw" or ".dng" => "#818CF8",
                _ => "#94A3B8"
            };
        }
    }

    public string FormatBadgeBorder
    {
        get
        {
            string ext = System.IO.Path.GetExtension(FilePath ?? string.Empty).ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" => "#D97706",
                ".tif" or ".tiff" => "#059669",
                ".fits" or ".fit" or ".fts" => "#9333EA",
                ".cr2" or ".cr3" or ".nef" or ".arw" or ".dng" => "#6366F1",
                _ => "#475569"
            };
        }
    }

    /// <summary>
    /// Cached detected star centroids from photometric analysis, used for fast mosaic registration.
    /// </summary>
    public IReadOnlyList<AstrophotographyObject>? DetectedStars { get; set; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }

    public void NotifyLocalizationChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(FormatBadgeText));
        OnPropertyChanged(nameof(FormatBadgeTooltip));
        OnPropertyChanged(nameof(ConfidencePercentText));
        OnPropertyChanged(nameof(L_OpacityLabel));
    }

    public MosaicPanel(
        string id,
        string name,
        int width,
        int height,
        WcsMatrix? wcs = null,
        string? filePath = null,
        string? targetName = null)
    {
        Id = id ?? Guid.NewGuid().ToString("N");
        Name = name;
        Width = width;
        Height = height;
        Wcs = wcs;
        FilePath = filePath;
        TargetName = targetName;

        bool isLossy = IsLossyFormat;
        _autoConfidenceThreshold = isLossy ? 0.34f : 0.24f;
        _confidenceThreshold = _autoConfidenceThreshold;
    }

    /// <summary>
    /// Gets the 4 corners in celestial coordinates (RA, Dec) in order:
    /// Top-Left, Top-Right, Bottom-Right, Bottom-Left.
    /// </summary>
    public (CelestialCoordinates TopLeft, CelestialCoordinates TopRight, CelestialCoordinates BottomRight, CelestialCoordinates BottomLeft) GetFootprintCorners()
    {
        if (Wcs == null)
        {
            var fallback = new CelestialCoordinates(180, 30);
            return (fallback, fallback, fallback, fallback);
        }
        var tl = Wcs.PixelToEquatorial(0, 0);
        var tr = Wcs.PixelToEquatorial(Width, 0);
        var br = Wcs.PixelToEquatorial(Width, Height);
        var bl = Wcs.PixelToEquatorial(0, Height);
        return (tl, tr, br, bl);
    }

    /// <summary>
    /// Determines whether a celestial coordinate lies inside this panel's sensor field of view.
    /// </summary>
    public bool ContainsCoordinate(CelestialCoordinates coord)
    {
        if (Wcs == null || !Wcs.EquatorialToPixel(coord, out double x, out double y))
        {
            return false;
        }

        return x >= 0 && x <= Width && y >= 0 && y <= Height;
    }
}

/// <summary>
/// Manages a multi-panel celestial mosaic project, supporting automatic celestial bounding,
/// master unified WCS canvas generation, overlap detection, layer ordering, and multi-tile sky projection.
/// </summary>
public class MosaicProject
{
    private readonly List<MosaicPanel> _panels = new();
    private bool _useFeatheredBlending = true;

    public IReadOnlyList<MosaicPanel> Panels => _panels;
    public int Count => _panels.Count;

    /// <summary>
    /// Global toggle for feathered alpha edge blending across all overlapping mosaic panels.
    /// </summary>
    public bool UseFeatheredBlending
    {
        get => _useFeatheredBlending;
        set => _useFeatheredBlending = value;
    }

    public void AddPanel(MosaicPanel panel)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        if (!_panels.Contains(panel))
        {
            _panels.Add(panel);
        }
    }

    public void AddOrUpdatePanel(MosaicPanel panel)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        _panels.RemoveAll(p => (!string.IsNullOrEmpty(panel.FilePath) && string.Equals(p.FilePath, panel.FilePath, StringComparison.OrdinalIgnoreCase)) ||
                               p.Id == panel.Id);
        _panels.Add(panel);
    }

    public bool RemovePanel(MosaicPanel panel) => _panels.Remove(panel);

    public void Clear() => _panels.Clear();

    public int IndexOf(MosaicPanel panel) => _panels.IndexOf(panel);

    public int GetLayerIndex(MosaicPanel panel)
    {
        int idx = _panels.IndexOf(panel);
        return idx >= 0 ? idx + 1 : 0;
    }

    /// <summary>
    /// Moves the specified mosaic panel one position up towards the front (top layer).
    /// </summary>
    public bool MoveUp(MosaicPanel panel)
    {
        if (panel == null) return false;
        int idx = _panels.IndexOf(panel);
        if (idx < 0 || idx >= _panels.Count - 1) return false;

        _panels.RemoveAt(idx);
        _panels.Insert(idx + 1, panel);
        return true;
    }

    /// <summary>
    /// Moves the specified mosaic panel one position down towards the back (bottom layer).
    /// </summary>
    public bool MoveDown(MosaicPanel panel)
    {
        if (panel == null) return false;
        int idx = _panels.IndexOf(panel);
        if (idx <= 0) return false;

        _panels.RemoveAt(idx);
        _panels.Insert(idx - 1, panel);
        return true;
    }

    /// <summary>
    /// Brings the specified mosaic panel to the very front (drawn last on top).
    /// </summary>
    public bool BringToFront(MosaicPanel panel)
    {
        if (panel == null) return false;
        int idx = _panels.IndexOf(panel);
        if (idx < 0 || idx == _panels.Count - 1) return false;

        _panels.RemoveAt(idx);
        _panels.Add(panel);
        return true;
    }

    /// <summary>
    /// Sends the specified mosaic panel to the very back (drawn first underneath).
    /// </summary>
    public bool SendToBack(MosaicPanel panel)
    {
        if (panel == null) return false;
        int idx = _panels.IndexOf(panel);
        if (idx <= 0) return false;

        _panels.RemoveAt(idx);
        _panels.Insert(0, panel);
        return true;
    }

    /// <summary>
    /// Moves a panel from an old index to a new target index.
    /// </summary>
    public bool Move(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || oldIndex >= _panels.Count ||
            newIndex < 0 || newIndex >= _panels.Count ||
            oldIndex == newIndex)
        {
            return false;
        }

        var panel = _panels[oldIndex];
        _panels.RemoveAt(oldIndex);
        _panels.Insert(newIndex, panel);
        return true;
    }

    public MosaicPanel? FindPanel(string? filePath, string? id = null)
    {
        return _panels.FirstOrDefault(p =>
            (!string.IsNullOrEmpty(filePath) && string.Equals(p.FilePath, filePath, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(id) && p.Id == id));
    }

    /// <summary>
    /// Celestial bounding box computed from all active panels.
    /// </summary>
    public record CelestialBounds(
        CelestialCoordinates Center,
        double AngularWidthDegrees,
        double AngularHeightDegrees,
        double MinRaDegrees,
        double MaxRaDegrees,
        double MinDecDegrees,
        double MaxDecDegrees);

    /// <summary>
    /// Computes the spherical celestial bounds encompassing all active mosaic panels.
    /// Uses 3D Cartesian vector averaging to avoid RA 0°/360° discontinuity.
    /// </summary>
    public CelestialBounds? ComputeBounds()
    {
        var activePanels = _panels.Where(p => p.IsVisible).ToList();
        if (activePanels.Count == 0) return null;

        // Prioritize solved panels with specific celestial targets over default (180, 30) fallback panels
        var solvedPanels = activePanels.Where(p => p.Wcs != null &&
            !(p.Wcs.CenterCoordinates.RaDegrees == 180.0 && p.Wcs.CenterCoordinates.DecDegrees == 30.0 && p.TargetName == null)).ToList();
        var panelsToUse = solvedPanels.Count > 0 ? solvedPanels : activePanels;

        var allCorners = new List<CelestialCoordinates>(panelsToUse.Count * 4);
        foreach (var p in panelsToUse)
        {
            var (tl, tr, br, bl) = p.GetFootprintCorners();
            allCorners.Add(tl);
            allCorners.Add(tr);
            allCorners.Add(br);
            allCorners.Add(bl);
        }

        // 3D vector centroid
        double sumX = 0, sumY = 0, sumZ = 0;
        double minDec = 90.0, maxDec = -90.0;

        foreach (var c in allCorners)
        {
            double raRad = c.RaRadians;
            double decRad = c.DecRadians;
            double cosDec = Math.Cos(decRad);

            sumX += cosDec * Math.Cos(raRad);
            sumY += cosDec * Math.Sin(raRad);
            sumZ += Math.Sin(decRad);

            if (c.DecDegrees < minDec) minDec = c.DecDegrees;
            if (c.DecDegrees > maxDec) maxDec = c.DecDegrees;
        }

        int n = allCorners.Count;
        sumX /= n; sumY /= n; sumZ /= n;
        double norm = Math.Sqrt((sumX * sumX) + (sumY * sumY) + (sumZ * sumZ));
        if (norm < 1e-9) norm = 1.0;

        double centerRaRad = Math.Atan2(sumY, sumX);
        double centerDecRad = Math.Asin(Math.Clamp(sumZ / norm, -1.0, 1.0));
        var center = CelestialCoordinates.FromRadians(centerRaRad, centerDecRad);

        // Calculate maximum angular distance from centroid to any corner
        double maxRadiusDeg = 0;
        foreach (var c in allCorners)
        {
            double distDeg = center.AngularDistanceTo(c);
            if (distDeg > maxRadiusDeg) maxRadiusDeg = distDeg;
        }

        double widthDeg = maxRadiusDeg * 2.0;
        double heightDeg = Math.Max(maxDec - minDec, maxRadiusDeg * 2.0);

        double minRa = (center.RaDegrees - maxRadiusDeg + 360.0) % 360.0;
        double maxRa = (center.RaDegrees + maxRadiusDeg) % 360.0;

        return new CelestialBounds(
            center,
            widthDeg,
            heightDeg,
            minRa,
            maxRa,
            minDec,
            maxDec);
    }

    /// <summary>
    /// Computes a unified master WCS matrix that spans all active panels on a single continuous canvas.
    /// Uses median angular resolution and automatically adapts pixel scale so ultra-wide or multi-target
    /// mosaics never overflow the 2D canvas or blow up wide-field panels relative to narrow-field panels.
    /// </summary>
    public WcsMatrix? ComputeMasterWcs(double? targetPixelScaleArcsec = null, int marginPx = 80)
    {
        var bounds = ComputeBounds();
        if (bounds == null) return null;

        var activePanels = _panels.Where(p => p.IsVisible).ToList();
        if (activePanels.Count == 0) return null;

        double scale;
        if (targetPixelScaleArcsec.HasValue)
        {
            scale = targetPixelScaleArcsec.Value;
        }
        else
        {
            var solvedScales = activePanels
                .Where(p => p.Wcs != null && p.IsSolved)
                .Select(p => p.Wcs!.PixelScaleArcsec)
                .OrderBy(s => s)
                .ToList();

            if (solvedScales.Count == 0)
            {
                solvedScales = activePanels
                    .Where(p => p.Wcs != null)
                    .Select(p => p.Wcs!.PixelScaleArcsec)
                    .OrderBy(s => s)
                    .ToList();
            }

            if (solvedScales.Count == 0)
            {
                scale = 2.5;
            }
            else
            {
                // Use median scale so one narrow-field (1.5"/px) image doesn't force a 60"/px wide-field
                // image to be blown up 40x across the canvas.
                scale = solvedScales[solvedScales.Count / 2];
            }
        }
        scale = Math.Max(0.01, scale);

        // Project all active panel corners onto an unconstrained tangent projection at bounds.Center
        // to discover the exact bounding box (minX, maxX, minY, maxY) needed to house all panels.
        var refProjection = new WcsMatrix(bounds.Center, 0.0, 0.0, scale, 0.0, 1000, 1000);

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        bool hasProjected = false;

        foreach (var p in activePanels)
        {
            if (p.Wcs != null)
            {
                var (tl, tr, br, bl) = p.GetFootprintCorners();
                CelestialCoordinates[] corners = [tl, tr, br, bl, p.Wcs.CenterCoordinates];
                foreach (var c in corners)
                {
                    if (refProjection.EquatorialToPixel(c, out double cx, out double cy))
                    {
                        minX = Math.Min(minX, cx);
                        maxX = Math.Max(maxX, cx);
                        minY = Math.Min(minY, cy);
                        maxY = Math.Max(maxY, cy);
                        hasProjected = true;
                    }
                }
            }
        }

        double spanX = hasProjected ? (maxX - minX) : (bounds.AngularWidthDegrees * 3600.0 / scale);
        double spanY = hasProjected ? (maxY - minY) : (bounds.AngularHeightDegrees * 3600.0 / scale);

        // Add generous margin around all panels so mosaic tiles never touch or cross the canvas boundary
        int effectiveMargin = Math.Max(marginPx, Math.Max(400, (int)(Math.Max(spanX, spanY) * 0.12)));

        const int maxCanvasDim = 16000;
        double minRequiredScaleW = (spanX * scale) / Math.Max(100, maxCanvasDim - (effectiveMargin * 2));
        double minRequiredScaleH = (spanY * scale) / Math.Max(100, maxCanvasDim - (effectiveMargin * 2));
        double minRequiredScale = Math.Max(minRequiredScaleW, minRequiredScaleH);

        if (!targetPixelScaleArcsec.HasValue && scale < minRequiredScale)
        {
            scale = minRequiredScale;
            refProjection = new WcsMatrix(bounds.Center, 0.0, 0.0, scale, 0.0, 1000, 1000);
            minX = double.MaxValue; maxX = double.MinValue;
            minY = double.MaxValue; maxY = double.MinValue;
            foreach (var p in activePanels)
            {
                if (p.Wcs != null)
                {
                    var (tl, tr, br, bl) = p.GetFootprintCorners();
                    CelestialCoordinates[] corners = [tl, tr, br, bl, p.Wcs.CenterCoordinates];
                    foreach (var c in corners)
                    {
                        if (refProjection.EquatorialToPixel(c, out double cx, out double cy))
                        {
                            minX = Math.Min(minX, cx);
                            maxX = Math.Max(maxX, cx);
                            minY = Math.Min(minY, cy);
                            maxY = Math.Max(maxY, cy);
                        }
                    }
                }
            }
            spanX = maxX - minX;
            spanY = maxY - minY;
            effectiveMargin = Math.Max(marginPx, Math.Max(400, (int)(Math.Max(spanX, spanY) * 0.12)));
        }

        int canvasW = (int)Math.Ceiling(spanX + (effectiveMargin * 2));
        int canvasH = (int)Math.Ceiling(spanY + (effectiveMargin * 2));

        // Reserve extra room on the right for fallback cards if any panels are genuinely unsolved
        int unsolvedCount = activePanels.Count(p => !p.IsSolved);
        if (unsolvedCount > 0)
        {
            canvasW += unsolvedCount <= 2 ? 800 : 1600;
        }

        canvasW = Math.Clamp(canvasW, 1200, 24000);
        canvasH = Math.Clamp(canvasH, 1200, 24000);

        double refPx = hasProjected ? (-minX + effectiveMargin) : (canvasW * 0.5);
        double refPy = hasProjected ? (-minY + effectiveMargin) : (canvasH * 0.5);

        return new WcsMatrix(
            bounds.Center,
            refPx,
            refPy,
            scale,
            rotationDegrees: 0.0, // North-Up for master mosaic
            canvasW,
            canvasH);
    }

    /// <summary>
    /// Computes the exact 2D affine matrix (M11, M12, M21, M22, OffsetX, OffsetY) that maps a panel's
    /// local sensor pixels [0..Width, 0..Height] onto the unified 2D Master WCS canvas.
    /// Uses spherical local tangent basis at the panel center with bounded visual scaling so panels
    /// from different lenses/telescopes never blow up one photo into a giant background containing another.
    /// </summary>
    public static (double M11, double M12, double M21, double M22, double OffsetX, double OffsetY)
        GetPanelCanvasTransform(MosaicPanel panel, WcsMatrix? masterWcs, int panelIndex = 0)
    {
        if (panel == null)
        {
            return (1.0, 0.0, 0.0, 1.0, 0.0, 0.0);
        }

        if (masterWcs != null && panel.Wcs != null && panel.IsSolved && panel.Width > 0 && panel.Height > 0)
        {
            double cx = panel.Width / 2.0;
            double cy = panel.Height / 2.0;

            // Use rigid linear+radial gnomonic basis vectors (without local TPS bumps at the center)
            var rigidPanelWcs = panel.Wcs.SplineWarp != null
                ? new WcsMatrix(
                    panel.Wcs.CenterCoordinates,
                    panel.Wcs.ReferencePixelX,
                    panel.Wcs.ReferencePixelY,
                    panel.Wcs.PixelScaleArcsec,
                    panel.Wcs.RotationDegrees,
                    panel.Wcs.ImageWidth,
                    panel.Wcs.ImageHeight,
                    panel.Wcs.RadialDistortionK1,
                    panel.Wcs.RadialDistortionK2,
                    splineWarp: null,
                    isFlippedHorizontally: panel.Wcs.IsFlippedHorizontally)
                : panel.Wcs;

            var centerSky = rigidPanelWcs.PixelToEquatorial(cx, cy);

            double delta = Math.Max(10.0, Math.Min(panel.Width, panel.Height) * 0.20);
            var rightSky = rigidPanelWcs.PixelToEquatorial(cx + delta, cy);
            var leftSky = rigidPanelWcs.PixelToEquatorial(cx - delta, cy);
            var downSky = rigidPanelWcs.PixelToEquatorial(cx, cy + delta);
            var upSky = rigidPanelWcs.PixelToEquatorial(cx, cy - delta);

            if (masterWcs.EquatorialToPixel(centerSky, out double xCenter, out double yCenter) &&
                masterWcs.EquatorialToPixel(rightSky, out double xr, out double yr) &&
                masterWcs.EquatorialToPixel(leftSky, out double xl, out double yl) &&
                masterWcs.EquatorialToPixel(downSky, out double xd, out double yd) &&
                masterWcs.EquatorialToPixel(upSky, out double xu, out double yu))
            {
                double ux = (xr - xl) / (2.0 * delta);
                double uy = (yr - yl) / (2.0 * delta);
                double vx = (xd - xu) / (2.0 * delta);
                double vy = (yd - yu) / (2.0 * delta);

                // Normalize local scale against gnomonic sec^2(c) blow-up at wide angles from master center.
                // Also clamp extreme scale ratios (e.g. mixing a 1.5"/px deep-sky image with a 65"/px wide-field
                // smartphone image) so one photo is never zoomed 40x to swallow the other photo on the 2D canvas!
                double rawScaleRatio = panel.Wcs.PixelScaleArcsec / Math.Max(0.01, masterWcs.PixelScaleArcsec);
                double maxAllowedWidth = Math.Max(panel.Width * 1.6, masterWcs.ImageWidth * 0.65);
                double minAllowedWidth = Math.Min(panel.Width * 0.35, masterWcs.ImageWidth * 0.18);
                double maxRatio = maxAllowedWidth / Math.Max(1.0, panel.Width);
                double minRatio = minAllowedWidth / Math.Max(1.0, panel.Width);
                double expectedScale = Math.Clamp(rawScaleRatio, Math.Min(minRatio, 0.35), Math.Max(maxRatio, 1.6));

                double lenU = Math.Sqrt((ux * ux) + (uy * uy));
                double lenV = Math.Sqrt((vx * vx) + (vy * vy));

                if (lenU > 1e-6 && lenV > 1e-6)
                {
                    ux = (ux / lenU) * expectedScale;
                    uy = (uy / lenU) * expectedScale;
                    vx = (vx / lenV) * expectedScale;
                    vy = (vy / lenV) * expectedScale;

                    double det = (ux * vy) - (uy * vx);
                    if (!double.IsNaN(det) && !double.IsInfinity(det) && Math.Abs(det) >= 1e-8 && Math.Abs(det) <= 1e8)
                    {
                        // Clamp center within the 2D canvas margin rather than rejecting solved panels to the fallback row
                        double marginX = Math.Max(panel.Width * expectedScale * 0.5, 200.0);
                        double marginY = Math.Max(panel.Height * expectedScale * 0.5, 200.0);
                        xCenter = Math.Clamp(xCenter, -marginX, masterWcs.ImageWidth + marginX);
                        yCenter = Math.Clamp(yCenter, -marginY, masterWcs.ImageHeight + marginY);

                        double offsetX = xCenter - (cx * ux) - (cy * vx);
                        double offsetY = yCenter - (cx * uy) - (cy * vy);
                        return (ux, uy, vx, vy, offsetX, offsetY);
                    }
                }
            }
        }

        // Fallback for genuinely unsolved panels: scale to a neat compact card next to the master canvas
        // so unsolved panels never stretch the canvas 30,000 pixels to the right.
        double canvasW = masterWcs != null ? masterWcs.ImageWidth : Math.Max(1000.0, panel.Width);
        double canvasH = masterWcs != null ? masterWcs.ImageHeight : Math.Max(800.0, panel.Height);
        double targetCardWidth = Math.Clamp(canvasW * 0.22, 320.0, 900.0);
        double cardScale = targetCardWidth / Math.Max(1.0, panel.Width);
        double cardH = panel.Height * cardScale;
        double gap = 36.0;

        int col = panelIndex % 2;
        int row = panelIndex / 2;
        double fallbackOx = canvasW + gap + (col * (targetCardWidth + gap));
        double fallbackOy = Math.Max(gap, (canvasH * 0.15) + (row * (cardH + gap)));

        return (cardScale, 0.0, 0.0, cardScale, fallbackOx, fallbackOy);
    }

    /// <summary>
    /// Maps a panel's 4 corners onto the master mosaic canvas pixel coordinates.
    /// </summary>
    public (System.Numerics.Vector2 TopLeft, System.Numerics.Vector2 TopRight, System.Numerics.Vector2 BottomRight, System.Numerics.Vector2 BottomLeft)?
        MapPanelToMasterCanvas(MosaicPanel panel, WcsMatrix masterWcs)
    {
        if (panel == null || masterWcs == null) return null;

        var (tl, tr, br, bl) = panel.GetFootprintCorners();

        if (!masterWcs.EquatorialToPixel(tl, out double x1, out double y1)) return null;
        if (!masterWcs.EquatorialToPixel(tr, out double x2, out double y2)) return null;
        if (!masterWcs.EquatorialToPixel(br, out double x3, out double y3)) return null;
        if (!masterWcs.EquatorialToPixel(bl, out double x4, out double y4)) return null;

        return (
            new System.Numerics.Vector2((float)x1, (float)y1),
            new System.Numerics.Vector2((float)x2, (float)y2),
            new System.Numerics.Vector2((float)x3, (float)y3),
            new System.Numerics.Vector2((float)x4, (float)y4));
    }

    /// <summary>
    /// Checks whether two panels overlap on the celestial sphere by testing corner and center containment.
    /// </summary>
    public bool CheckOverlap(MosaicPanel a, MosaicPanel b)
    {
        if (a == null || b == null || ReferenceEquals(a, b) || a.Wcs == null || b.Wcs == null) return false;

        // Check center of B in A
        if (a.ContainsCoordinate(b.Wcs.CenterCoordinates)) return true;
        // Check center of A in B
        if (b.ContainsCoordinate(a.Wcs.CenterCoordinates)) return true;

        // Check corners of B in A
        var (bTl, bTr, bBr, bBl) = b.GetFootprintCorners();
        if (a.ContainsCoordinate(bTl) || a.ContainsCoordinate(bTr) ||
            a.ContainsCoordinate(bBr) || a.ContainsCoordinate(bBl))
        {
            return true;
        }

        // Check corners of A in B
        var (aTl, aTr, aBr, aBl) = a.GetFootprintCorners();
        if (b.ContainsCoordinate(aTl) || b.ContainsCoordinate(aTr) ||
            b.ContainsCoordinate(aBr) || b.ContainsCoordinate(aBl))
        {
            return true;
        }

        return false;
    }
}
