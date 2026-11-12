namespace AstroCounter.Core.Export;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AstroCounter.Core.Astrometry;
using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using SkiaSharp;

/// <summary>
/// Reports progress updates during asynchronous image or panorama export.
/// </summary>
public readonly record struct ExportProgress(int Percent, string StageKey, string DetailText);

/// <summary>
/// Exact stitched bounding box and native 1:1 pixel dimensions of a mosaic canvas or single image.
/// </summary>
public readonly record struct MosaicExportBounds(
    double MinX,
    double MinY,
    int PixelWidth,
    int PixelHeight,
    long TotalPixels,
    int ActivePanelCount);

/// <summary>
/// Candidate image corner selected for placement of the Observatory watermark.
/// </summary>
public enum WatermarkCorner
{
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft
}

/// <summary>
/// Configuration parameters controlling CSV, JSON, 2D Mosaic/Canvas, and 360° Panorama exports.
/// </summary>
public record ExportConfig(
    string Format, // "TIFF", "PNG", "JPG", "CSV", "JSON"
    bool IncludeBoundingBoxes = true,
    bool IncludeLabels = true,
    bool IncludeSphericalGrid = false,
    bool IncludeConstellations = false,
    int ScaleFactor = 1, // 1 or 2
    string ExportProjectionMode = "Canvas2D", // "Canvas2D", "Panorama360"
    string ResolutionMode = "LegacyScale", // "Native", "Scale1x", "Scale2x", "Uhd4K", "Uhd8K", "Custom", "LegacyScale"
    int CustomWidth = 0,
    int CustomHeight = 0,
    int JpegQuality = 95,
    bool IncludeWatermark = true,
    int? TotalDetectedCount = null,
    bool ApplyDisplayAdjustments = true,
    bool FallbackAutoBalance = false,
    double FallbackBrightness = 0.0,
    double FallbackExposure = 1.0,
    double FallbackContrast = 1.0,
    bool IncludeDsoMarkers = false,
    bool UseTurkishLanguage = false,
    bool HarmonizeMosaicExposure = false
);

public static class ResultExporter
{
    private const int MaxExportSidePixels = 14000;
    private const long MaxExportBufferPixels = 120_000_000L; // ~480 MB BGRA8888 surface ceiling
    private const string WatermarkBrand = "AstroCounter v1.4.1";

    /// <summary>
    /// Exports detected astronomical objects to a CSV file.
    /// </summary>
    public static void ExportToCsv(IEnumerable<AstrophotographyObject> objects, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,ClassName,Confidence,Tier,SNR_Sigma,X,Y,Width,Height,CenterX,CenterY,RaDegrees,DecDegrees,PanelId");

        foreach (var obj in objects)
        {
            float snr = ConfidenceClassifier.EstimateEquivalentSnrSigma(obj.Confidence);
            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0},{1},{2:F4},{3},{4:F2},{5:F2},{6:F2},{7:F2},{8:F2},{9:F2},{10:F2},{11},{12},{13}",
                obj.Id,
                obj.ClassName,
                obj.Confidence,
                obj.Tier.GetShortName(),
                snr,
                obj.Box.X,
                obj.Box.Y,
                obj.Box.Width,
                obj.Box.Height,
                obj.CenterX,
                obj.CenterY,
                obj.RaDegrees.HasValue ? obj.RaDegrees.Value.ToString("F6", CultureInfo.InvariantCulture) : "",
                obj.DecDegrees.HasValue ? obj.DecDegrees.Value.ToString("F6", CultureInfo.InvariantCulture) : "",
                obj.PanelId ?? ""
            ));
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// Generates and saves an annotated version of the image with color-coded bounding boxes.
    /// </summary>
    public static void ExportAnnotatedImage(
        string sourceImagePath,
        IEnumerable<AstrophotographyObject> objects,
        string destinationImagePath,
        ScanMask? mask = null)
    {
        using var bitmap = ImagePreprocessor.LoadImage(sourceImagePath);
        using var canvas = new SKCanvas(bitmap);

        int longSide = Math.Max(bitmap.Width, bitmap.Height);
        float minMarker = Math.Max(10f, longSide / 250f);
        float stroke = Math.Max(2f, longSide / 1200f);

        var tier1Color = new SKColor(0, 230, 118);
        var tier2Color = new SKColor(255, 214, 0);
        var tier3Color = new SKColor(255, 145, 0);
        var tierNoiseColor = new SKColor(239, 68, 68);

        if (mask != null)
        {
            DrawMask(canvas, mask, stroke, 1f, 1f, 0f, 0f);
        }

        using var haloPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = stroke + 2f,
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, 170)
        };

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = stroke,
            IsAntialias = true
        };

        foreach (var obj in objects)
        {
            paint.Color = obj.Tier switch
            {
                ConfidenceTier.Tier1_High => tier1Color,
                ConfidenceTier.Tier2_Medium => tier2Color,
                ConfidenceTier.Tier3_Low => tier3Color,
                _ => tierNoiseColor
            };

            float size = Math.Max(Math.Max(obj.Box.Width, obj.Box.Height), minMarker);
            float x = obj.CenterX - (size / 2f);
            float y = obj.CenterY - (size / 2f);

            canvas.DrawRect(x, y, size, size, haloPaint);
            canvas.DrawRect(x, y, size, size, paint);
        }

        SaveBitmapToFile(bitmap, destinationImagePath, "PNG");
    }

    /// <summary>
    /// Exports detected objects and optional celestial astrometry to JSON.
    /// </summary>
    public static void ExportToJson(
        IEnumerable<AstrophotographyObject> objects,
        WcsMatrix? wcs,
        string filePath)
    {
        var objList = objects as IReadOnlyCollection<AstrophotographyObject> ?? objects.ToList();
        var records = objList.Select(o =>
        {
            double? ra = o.RaDegrees;
            double? dec = o.DecDegrees;
            if (!ra.HasValue && wcs != null)
            {
                var eq = wcs.PixelToEquatorial(o.CenterX, o.CenterY);
                ra = eq.RaDegrees;
                dec = eq.DecDegrees;
            }

            return new
            {
                id = o.Id,
                className = o.ClassName,
                confidence = Math.Round(o.Confidence, 4),
                tier = o.Tier.ToString(),
                snrSigma = Math.Round(ConfidenceClassifier.EstimateEquivalentSnrSigma(o.Confidence), 2),
                pixelX = Math.Round(o.CenterX, 2),
                pixelY = Math.Round(o.CenterY, 2),
                raDegrees = ra.HasValue ? Math.Round(ra.Value, 6) : (double?)null,
                decDegrees = dec.HasValue ? Math.Round(dec.Value, 6) : (double?)null,
                panelId = o.PanelId,
                box = new { x = o.Box.X, y = o.Box.Y, width = o.Box.Width, height = o.Box.Height }
            };
        });

        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        string json = System.Text.Json.JsonSerializer.Serialize(new
        {
            generator = WatermarkBrand,
            timestamp = DateTime.UtcNow.ToString("o"),
            totalObjects = objList.Count,
            hasWcs = wcs != null,
            wcsCenter = wcs != null ? new { ra = wcs.CenterCoordinates.RaDegrees, dec = wcs.CenterCoordinates.DecDegrees, fovW = wcs.FovWidthDegrees, fovH = wcs.FovHeightDegrees } : null,
            detections = records
        }, options);

        File.WriteAllText(filePath, json, Encoding.UTF8);
    }

    /// <summary>
    /// Computes the exact unified bounding rectangle and native 1:1 stitched pixel area of all visible mosaic panels
    /// (or the active single image dimensions when no mosaic project is present).
    /// </summary>
    public static MosaicExportBounds ComputeMosaicExportBounds(
        MosaicProject? mosaicProject,
        WcsMatrix? masterWcs = null,
        int singleImageWidth = 0,
        int singleImageHeight = 0)
    {
        if (mosaicProject != null && mosaicProject.Count > 0)
        {
            WcsMatrix? effectiveMaster = masterWcs ?? mosaicProject.ComputeMasterWcs();
            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;
            int activeCount = 0;

            var panels = mosaicProject.Panels;
            Span<(double lx, double ly)> corners = stackalloc (double, double)[4];
            for (int i = 0; i < panels.Count; i++)
            {
                var p = panels[i];
                if (!p.IsVisible || p.Width <= 0 || p.Height <= 0)
                {
                    continue;
                }

                var tr = MosaicProject.GetPanelCanvasTransform(p, effectiveMaster, i);
                corners[0] = (0.0, 0.0);
                corners[1] = (p.Width, 0.0);
                corners[2] = (p.Width, p.Height);
                corners[3] = (0.0, p.Height);

                foreach (var (lx, ly) in corners)
                {
                    double cx = (lx * tr.M11) + (ly * tr.M21) + tr.OffsetX;
                    double cy = (lx * tr.M12) + (ly * tr.M22) + tr.OffsetY;
                    if (!double.IsFinite(cx) || !double.IsFinite(cy)) continue;
                    if (cx < minX) minX = cx;
                    if (cx > maxX) maxX = cx;
                    if (cy < minY) minY = cy;
                    if (cy > maxY) maxY = cy;
                }

                activeCount++;
            }

            if (activeCount > 0 && double.IsFinite(minX) && double.IsFinite(maxX) && double.IsFinite(minY) && double.IsFinite(maxY))
            {
                int w = Math.Max(1, (int)Math.Ceiling(maxX - minX));
                int h = Math.Max(1, (int)Math.Ceiling(maxY - minY));
                return new MosaicExportBounds(minX, minY, w, h, (long)w * h, activeCount);
            }
        }

        if (singleImageWidth > 0 && singleImageHeight > 0)
        {
            return new MosaicExportBounds(
                0.0,
                0.0,
                singleImageWidth,
                singleImageHeight,
                (long)singleImageWidth * singleImageHeight,
                1);
        }

        return new MosaicExportBounds(0.0, 0.0, 1920, 1080, 1920L * 1080L, 0);
    }

    /// <summary>
    /// Computes safe, memory-bounded output pixel dimensions for the selected projection and resolution mode.
    /// </summary>
    public static (int Width, int Height) ComputeTargetExportDimensions(
        ExportConfig config,
        MosaicExportBounds bounds,
        MosaicProject? mosaicProject = null,
        WcsMatrix? wcs = null)
    {
        bool isPano = string.Equals(config.ExportProjectionMode, "Panorama360", StringComparison.OrdinalIgnoreCase);
        string mode = config.ResolutionMode ?? "LegacyScale";

        if (isPano)
        {
            if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase) && (config.CustomWidth > 0 || config.CustomHeight > 0))
            {
                int cw = config.CustomWidth > 0 ? config.CustomWidth : Math.Max(640, config.CustomHeight * 2);
                int ch = config.CustomHeight > 0 ? config.CustomHeight : Math.Max(320, cw / 2);
                return FitWithinMemoryLimits(Math.Max(320, cw), Math.Max(160, ch));
            }

            int panoW = mode switch
            {
                "Native" => ComputePanoramaNativeWidth(mosaicProject, wcs),
                "Uhd8K" => 7680,
                "Uhd4K" => 3840,
                "Scale2x" => 4096,
                "Scale1x" => 2560,
                _ => Math.Clamp(config.ScaleFactor, 1, 2) == 2 ? 4096 : 2560
            };
            int panoH = Math.Max(160, panoW / 2);
            return FitWithinMemoryLimits(panoW, panoH);
        }

        int srcW = Math.Max(1, bounds.PixelWidth);
        int srcH = Math.Max(1, bounds.PixelHeight);
        double aspect = srcW / (double)srcH;

        int targetW;
        int targetH;

        switch (mode)
        {
            case "Native":
                targetW = srcW;
                targetH = srcH;
                break;

            case "Custom":
                if (config.CustomWidth > 0 && config.CustomHeight > 0)
                {
                    targetW = config.CustomWidth;
                    targetH = config.CustomHeight;
                }
                else if (config.CustomWidth > 0)
                {
                    targetW = config.CustomWidth;
                    targetH = Math.Max(1, (int)Math.Round(targetW / aspect));
                }
                else if (config.CustomHeight > 0)
                {
                    targetH = config.CustomHeight;
                    targetW = Math.Max(1, (int)Math.Round(targetH * aspect));
                }
                else
                {
                    targetW = srcW;
                    targetH = srcH;
                }
                break;

            case "Uhd8K":
                (targetW, targetH) = ScaleLongSide(srcW, srcH, 7680);
                break;

            case "Uhd4K":
                (targetW, targetH) = ScaleLongSide(srcW, srcH, 3840);
                break;

            case "Scale2x":
                if (mosaicProject != null && mosaicProject.Count > 1)
                {
                    int targetLong = Math.Max(Math.Max(srcW, srcH), 4096);
                    (targetW, targetH) = ScaleLongSide(srcW, srcH, Math.Min(8192, targetLong));
                }
                else
                {
                    targetW = srcW * 2;
                    targetH = srcH * 2;
                }
                break;

            case "Scale1x":
                if (mosaicProject != null && mosaicProject.Count > 1)
                {
                    int targetLong = Math.Min(Math.Max(srcW, srcH), 2560);
                    (targetW, targetH) = ScaleLongSide(srcW, srcH, targetLong);
                }
                else
                {
                    targetW = srcW;
                    targetH = srcH;
                }
                break;

            default: // "LegacyScale"
                int sf = Math.Clamp(config.ScaleFactor, 1, 2);
                if (mosaicProject != null && mosaicProject.Count > 1)
                {
                    double maxScale = Math.Min(sf, 8192.0 / Math.Max(srcW, srcH));
                    targetW = Math.Max(800, (int)Math.Round(srcW * maxScale));
                    targetH = Math.Max(600, (int)Math.Round(srcH * maxScale));
                }
                else
                {
                    targetW = Math.Min(12000, srcW * sf);
                    targetH = Math.Min(12000, srcH * sf);
                }
                break;
        }

        return FitWithinMemoryLimits(Math.Max(64, targetW), Math.Max(64, targetH));
    }

    private static (int W, int H) ScaleLongSide(int srcW, int srcH, int targetLong)
    {
        if (srcW >= srcH)
        {
            int w = Math.Max(64, targetLong);
            int h = Math.Max(64, (int)Math.Round(w * (srcH / (double)srcW)));
            return (w, h);
        }
        else
        {
            int h = Math.Max(64, targetLong);
            int w = Math.Max(64, (int)Math.Round(h * (srcW / (double)srcH)));
            return (w, h);
        }
    }

    private static (int W, int H) FitWithinMemoryLimits(int w, int h)
    {
        double scale = 1.0;
        int maxSide = Math.Max(w, h);
        if (maxSide > MaxExportSidePixels)
        {
            scale = Math.Min(scale, MaxExportSidePixels / (double)maxSide);
        }

        long totalPx = (long)w * h;
        if (totalPx > MaxExportBufferPixels)
        {
            scale = Math.Min(scale, Math.Sqrt(MaxExportBufferPixels / (double)totalPx));
        }

        if (scale < 0.9999)
        {
            w = Math.Max(64, (int)Math.Floor(w * scale));
            h = Math.Max(64, (int)Math.Floor(h * scale));
        }

        return (w, h);
    }

    private static int ComputePanoramaNativeWidth(MosaicProject? mosaicProject, WcsMatrix? wcs)
    {
        double minArcsecPerPx = double.PositiveInfinity;
        if (mosaicProject != null && mosaicProject.Count > 0)
        {
            foreach (var p in mosaicProject.Panels)
            {
                if (p.IsVisible && p.IsSolved && p.Wcs != null && p.Wcs.PixelScaleArcsec > 0.05)
                {
                    if (p.Wcs.PixelScaleArcsec < minArcsecPerPx)
                    {
                        minArcsecPerPx = p.Wcs.PixelScaleArcsec;
                    }
                }
            }
        }

        if (!double.IsFinite(minArcsecPerPx) && wcs != null && wcs.PixelScaleArcsec > 0.05)
        {
            minArcsecPerPx = wcs.PixelScaleArcsec;
        }

        if (!double.IsFinite(minArcsecPerPx))
        {
            return 4096;
        }

        double fullSphereWidth = (360.0 * 3600.0) / Math.Max(10.0, minArcsecPerPx);
        int clamped = (int)Math.Clamp(Math.Round(fullSphereWidth), 4096, 12000);
        if ((clamped & 1) != 0) clamped--;
        return clamped;
    }

    /// <summary>
    /// Exports a single image, a full 2D multi-panel mosaic canvas, or a 360° Equirectangular Celestial Panorama.
    /// </summary>
    public static void ExportAdvancedImage(
        string? sourceImagePath,
        IEnumerable<AstrophotographyObject> objects,
        string destinationImagePath,
        ExportConfig config,
        WcsMatrix? wcs = null,
        ScanMask? mask = null,
        MosaicProject? mosaicProject = null)
    {
        ExportAdvancedImage(sourceImagePath, objects, destinationImagePath, config, wcs, mask, mosaicProject, progress: null, cancellationToken: CancellationToken.None);
    }

    /// <summary>
    /// Exports a single image, a full 2D multi-panel mosaic canvas, or a 360° Equirectangular Celestial Panorama
    /// with live progress reporting and cooperative cancellation support.
    /// </summary>
    public static void ExportAdvancedImage(
        string? sourceImagePath,
        IEnumerable<AstrophotographyObject> objects,
        string destinationImagePath,
        ExportConfig config,
        WcsMatrix? wcs,
        ScanMask? mask,
        MosaicProject? mosaicProject,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var objList = objects as IReadOnlyList<AstrophotographyObject> ?? objects.ToList();

        if (string.Equals(config.ExportProjectionMode, "Panorama360", StringComparison.OrdinalIgnoreCase))
        {
            ExportEquirectangularPanorama(sourceImagePath, objList, destinationImagePath, config, wcs, mosaicProject, progress, cancellationToken);
            return;
        }

        if (mosaicProject != null && mosaicProject.Count > 1)
        {
            ExportMosaicCanvas(objList, destinationImagePath, config, wcs, mask, mosaicProject, progress, cancellationToken);
            return;
        }

        string? validPath = sourceImagePath;
        MosaicPanel? singlePanel = null;
        if (mosaicProject != null && mosaicProject.Count > 0)
        {
            singlePanel = mosaicProject.Panels.FirstOrDefault(p => p.IsVisible && !string.IsNullOrEmpty(p.FilePath) && File.Exists(p.FilePath));
            if ((string.IsNullOrEmpty(validPath) || !File.Exists(validPath)) && singlePanel != null)
            {
                validPath = singlePanel.FilePath;
            }
        }

        if (string.IsNullOrEmpty(validPath) || !File.Exists(validPath))
        {
            return;
        }

        ReportProgress(progress, 12, "load", config.UseTurkishLanguage ? "Görsel hazırlanıyor..." : "Preparing image...");

        using var rawBitmap = ImagePreprocessor.LoadImage(validPath);
        cancellationToken.ThrowIfCancellationRequested();

        using var tonedBitmap = PrepareToneAdjustedBitmap(singlePanel, rawBitmap, config);
        var bounds = ComputeMosaicExportBounds(null, null, tonedBitmap.Width, tonedBitmap.Height);
        var (targetW, targetH) = ComputeTargetExportDimensions(config, bounds, mosaicProject, wcs);
        float scaleX = targetW / (float)Math.Max(1, tonedBitmap.Width);
        float scaleY = targetH / (float)Math.Max(1, tonedBitmap.Height);
        float uniformScale = (scaleX + scaleY) * 0.5f;

        ReportProgress(progress, 48, "render", $"{targetW} × {targetH} px");

        using var bitmap = (targetW != tonedBitmap.Width || targetH != tonedBitmap.Height)
            ? tonedBitmap.Resize(new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            : tonedBitmap.Copy();

        cancellationToken.ThrowIfCancellationRequested();
        ReportProgress(progress, 75, "overlays", config.UseTurkishLanguage ? "Katmanlar ve tespitler işleniyor..." : "Rendering layers & detections...");

        using var canvas = new SKCanvas(bitmap);
        var markerPoints = new List<(float X, float Y)>(objList.Count);
        DrawOverlaysAndDetectionsOnCanvas(canvas, targetW, targetH, scaleX, scaleY, 0f, 0f, objList, config, wcs, mask, markerPoints);

        if (config.IncludeWatermark)
        {
            ReportProgress(progress, 88, "watermark", config.UseTurkishLanguage ? "Boş köşeye filigran yerleştiriliyor..." : "Placing watermark in empty corner...");
            int count = config.TotalDetectedCount ?? objList.Count;
            DrawSmartEmptyCornerWatermark(bitmap, canvas, markerPoints, count, config.UseTurkishLanguage, uniformScale);
        }

        cancellationToken.ThrowIfCancellationRequested();
        ReportProgress(progress, 95, "encode", config.UseTurkishLanguage ? $"{config.Format} kodlanıyor..." : $"Encoding {config.Format}...");
        SaveBitmapToFile(bitmap, destinationImagePath, config.Format, config.JpegQuality);
        ReportProgress(progress, 100, "done", config.UseTurkishLanguage ? "Tamamlandı!" : "Complete!");
    }

    private static void ExportMosaicCanvas(
        IReadOnlyList<AstrophotographyObject> objects,
        string destinationImagePath,
        ExportConfig config,
        WcsMatrix? masterWcs,
        ScanMask? mask,
        MosaicProject mosaicProject,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        masterWcs ??= mosaicProject.ComputeMasterWcs();
        mosaicProject.UpdatePanelEdgeOverlaps(masterWcs);

        var bounds = ComputeMosaicExportBounds(mosaicProject, masterWcs);
        var (targetW, targetH) = ComputeTargetExportDimensions(config, bounds, mosaicProject, masterWcs);
        float scaleX = targetW / (float)Math.Max(1, bounds.PixelWidth);
        float scaleY = targetH / (float)Math.Max(1, bounds.PixelHeight);
        float uniformScale = (scaleX + scaleY) * 0.5f;

        ReportProgress(progress, 8, "load", config.UseTurkishLanguage ? "Mozaik panelleri hazırlanıyor..." : "Loading mosaic panels...");
        var preparedPanels = LoadPreparedMosaicPanels(mosaicProject, config, allowUnsolved: true, cancellationToken);
        try
        {
            if (config.HarmonizeMosaicExposure && preparedPanels.Count >= 2)
            {
                HarmonizePanelSkyPedestals(preparedPanels);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, 28, "render", $"{targetW} × {targetH} px ({preparedPanels.Count} panel)");

            using var bitmap = new SKBitmap(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);
            RenderFeatheredMosaic2D(bitmap, preparedPanels, bounds, scaleX, scaleY, mosaicProject.UseFeatheredBlending, masterWcs, progress, cancellationToken);

            ReportProgress(progress, 82, "overlays", config.UseTurkishLanguage ? "Katmanlar ve tespitler işleniyor..." : "Drawing overlays & detections...");
            using var canvas = new SKCanvas(bitmap);
            var markerPoints = new List<(float X, float Y)>(objects.Count);
            DrawOverlaysAndDetectionsOnCanvas(
                canvas,
                targetW,
                targetH,
                scaleX,
                scaleY,
                (float)-bounds.MinX,
                (float)-bounds.MinY,
                objects,
                config,
                masterWcs,
                mask,
                markerPoints);

            if (config.IncludeWatermark)
            {
                ReportProgress(progress, 90, "watermark", config.UseTurkishLanguage ? "Boş köşeye filigran yerleştiriliyor..." : "Placing watermark in empty corner...");
                int count = config.TotalDetectedCount ?? objects.Count;
                DrawSmartEmptyCornerWatermark(bitmap, canvas, markerPoints, count, config.UseTurkishLanguage, uniformScale);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, 96, "encode", config.UseTurkishLanguage ? $"{config.Format} dosyası yazılıyor..." : $"Writing {config.Format} file...");
            SaveBitmapToFile(bitmap, destinationImagePath, config.Format, config.JpegQuality);
            ReportProgress(progress, 100, "done", config.UseTurkishLanguage ? "Tamamlandı!" : "Complete!");
        }
        finally
        {
            foreach (var p in preparedPanels)
            {
                p.Dispose();
            }
        }
    }

    private static unsafe void RenderFeatheredMosaic2D(
        SKBitmap targetBitmap,
        List<PreparedPanel> panels,
        MosaicExportBounds bounds,
        float scaleX,
        float scaleY,
        bool useFeatheredBlending,
        WcsMatrix? masterWcs,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        int outW = targetBitmap.Width;
        int outH = targetBitmap.Height;
        int outStride = targetBitmap.RowBytes;
        byte* outBasePtr = (byte*)targetBitmap.GetPixels();

        // Fill base deep-space canvas (#05080F)
        const byte bgR = 5, bgG = 8, bgB = 15;

        // Precompute inverse affine mappings + screen bounds per panel
        var renderInfos = new List<(
            PreparedPanel Panel,
            double InvM11, double InvM12, double InvM21, double InvM22,
            double Tx, double Ty,
            int MinX, int MaxX, int MinY, int MaxY,
            nint Ptr, int SrcW, int SrcH, int RowBytes)>(panels.Count);

        Span<(double lx, double ly)> corners = stackalloc (double, double)[4];
        for (int i = 0; i < panels.Count; i++)
        {
            var pp = panels[i];
            var tr = MosaicProject.GetPanelCanvasTransform(pp.Panel!, masterWcs, pp.OriginalIndex);

            double a = tr.M11 * scaleX;
            double b = tr.M21 * scaleX;
            double c = tr.M12 * scaleY;
            double d = tr.M22 * scaleY;
            double tx = (tr.OffsetX - bounds.MinX) * scaleX;
            double ty = (tr.OffsetY - bounds.MinY) * scaleY;
            double det = (a * d) - (b * c);
            if (Math.Abs(det) < 1e-10 || !double.IsFinite(det)) continue;

            double invDet = 1.0 / det;
            double invM11 = d * invDet;
            double invM12 = -b * invDet;
            double invM21 = -c * invDet;
            double invM22 = a * invDet;

            int srcW = pp.BgraBmp.Width;
            int srcH = pp.BgraBmp.Height;

            double pMinX = double.PositiveInfinity, pMinY = double.PositiveInfinity;
            double pMaxX = double.NegativeInfinity, pMaxY = double.NegativeInfinity;
            corners[0] = (0, 0);
            corners[1] = (srcW, 0);
            corners[2] = (srcW, srcH);
            corners[3] = (0, srcH);
            foreach (var (lx, ly) in corners)
            {
                double sx = (a * lx) + (b * ly) + tx;
                double sy = (c * lx) + (d * ly) + ty;
                if (sx < pMinX) pMinX = sx;
                if (sx > pMaxX) pMaxX = sx;
                if (sy < pMinY) pMinY = sy;
                if (sy > pMaxY) pMaxY = sy;
            }

            int ix0 = Math.Clamp((int)Math.Floor(pMinX) - 1, 0, outW - 1);
            int ix1 = Math.Clamp((int)Math.Ceiling(pMaxX) + 1, 0, outW - 1);
            int iy0 = Math.Clamp((int)Math.Floor(pMinY) - 1, 0, outH - 1);
            int iy1 = Math.Clamp((int)Math.Ceiling(pMaxY) + 1, 0, outH - 1);
            if (ix0 > ix1 || iy0 > iy1) continue;

            renderInfos.Add((pp, invM11, invM12, invM21, invM22, tx, ty, ix0, ix1, iy0, iy1,
                pp.BgraBmp.GetPixels(), srcW, srcH, pp.BgraBmp.RowBytes));
        }

        int completedRows = 0;
        Parallel.For(0, outH, new ParallelOptions { CancellationToken = cancellationToken }, py =>
        {
            byte* dstRow = outBasePtr + (py * outStride);
            for (int px = 0; px < outW; px++)
            {
                float sumR = 0f, sumG = 0f, sumB = 0f, sumW = 0f;
                float maxAlpha = 0f;
                bool hit = false;

                for (int k = 0; k < renderInfos.Count; k++)
                {
                    var ri = renderInfos[k];
                    if (py < ri.MinY || py > ri.MaxY || px < ri.MinX || px > ri.MaxX)
                    {
                        continue;
                    }

                    double ux = px - ri.Tx;
                    double uy = py - ri.Ty;
                    double ix = (ri.InvM11 * ux) + (ri.InvM12 * uy);
                    double iy = (ri.InvM21 * ux) + (ri.InvM22 * uy);

                    if (ix < 0.0 || ix > ri.SrcW - 1.0 || iy < 0.0 || iy > ri.SrcH - 1.0)
                    {
                        continue;
                    }

                    hit = true;
                    SampleBilinearBgra((byte*)ri.Ptr, ri.SrcW, ri.SrcH, ri.RowBytes, ix, iy, out float bVal, out float gVal, out float rVal, out float aVal);

                    float edgeW = (useFeatheredBlending && ri.Panel.Feather != null)
                        ? ri.Panel.Feather.EvaluateWeight((float)ix, (float)iy)
                        : 1.0f;

                    float panelOpacity = (float)Math.Clamp(ri.Panel.Opacity, 0.02, 1.0);
                    float srcAlpha = aVal * (1f / 255f);
                    float w = Math.Max(0.0005f, edgeW) * panelOpacity * srcAlpha;

                    sumR += rVal * w;
                    sumG += gVal * w;
                    sumB += bVal * w;
                    sumW += w;

                    float effectiveAlpha = panelOpacity * srcAlpha;
                    if (effectiveAlpha > maxAlpha) maxAlpha = effectiveAlpha;
                }

                int o = px * 4;
                if (!hit || sumW <= 1e-6f)
                {
                    dstRow[o + 0] = bgB;
                    dstRow[o + 1] = bgG;
                    dstRow[o + 2] = bgR;
                    dstRow[o + 3] = 255;
                }
                else
                {
                    float invW = 1.0f / sumW;
                    float cr = sumR * invW;
                    float cg = sumG * invW;
                    float cb = sumB * invW;

                    if (maxAlpha < 0.999f)
                    {
                        float invA = 1.0f - maxAlpha;
                        cr = (cr * maxAlpha) + (bgR * invA);
                        cg = (cg * maxAlpha) + (bgG * invA);
                        cb = (cb * maxAlpha) + (bgB * invA);
                    }

                    dstRow[o + 0] = (byte)Math.Clamp((int)MathF.Round(cb), 0, 255);
                    dstRow[o + 1] = (byte)Math.Clamp((int)MathF.Round(cg), 0, 255);
                    dstRow[o + 2] = (byte)Math.Clamp((int)MathF.Round(cr), 0, 255);
                    dstRow[o + 3] = 255;
                }
            }

            int done = Interlocked.Increment(ref completedRows);
            if ((done & 63) == 0)
            {
                int pct = 28 + (int)Math.Round((done / (double)outH) * 50.0);
                ReportProgress(progress, pct, "render", $"{outW} × {outH} px");
            }
        });
    }

    /// <summary>
    /// Exports a full 360° × 180° Equirectangular Spherical Sky Panorama (suitable for 360° VR / Planetarium viewers).
    /// </summary>
    private static void ExportEquirectangularPanorama(
        string? sourceImagePath,
        IReadOnlyList<AstrophotographyObject> objects,
        string destinationImagePath,
        ExportConfig config,
        WcsMatrix? wcs,
        MosaicProject? mosaicProject,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var bounds = ComputeMosaicExportBounds(mosaicProject, wcs);
        var (panoW, panoH) = ComputeTargetExportDimensions(config, bounds, mosaicProject, wcs);
        float overlayScale = panoW / 2560f;

        ReportProgress(progress, 8, "load", config.UseTurkishLanguage ? "360° küresel paneller yükleniyor..." : "Preparing 360° spherical panels...");

        List<PreparedPanel> activePanels;
        if (mosaicProject != null && mosaicProject.Count > 0)
        {
            mosaicProject.UpdatePanelEdgeOverlaps(wcs ?? mosaicProject.ComputeMasterWcs());
            activePanels = LoadPreparedMosaicPanels(mosaicProject, config, allowUnsolved: false, cancellationToken);
        }
        else
        {
            activePanels = new List<PreparedPanel>();
        }

        if (activePanels.Count == 0 && wcs != null && !string.IsNullOrEmpty(sourceImagePath) && File.Exists(sourceImagePath))
        {
            using var raw = ImagePreprocessor.LoadImage(sourceImagePath);
            var toned = PrepareToneAdjustedBitmap(null, raw, config);
            activePanels.Add(new PreparedPanel(null, wcs, toned, 1.0, null, 0));
        }

        try
        {
            if (config.HarmonizeMosaicExposure && activePanels.Count >= 2)
            {
                HarmonizePanelSkyPedestals(activePanels);
            }

            double centerRaDeg = ComputeCircularMeanRa(activePanels, wcs);

            double RaToPanoX(double raDeg)
            {
                double relRa = ((centerRaDeg - raDeg + 540.0) % 360.0) - 180.0;
                return (0.5 + (relRa / 360.0)) * panoW;
            }

            double DecToPanoY(double decDeg)
            {
                return ((90.0 - Math.Clamp(decDeg, -90.0, 90.0)) / 180.0) * panoH;
            }

            ReportProgress(progress, 22, "render", $"{panoW} × {panoH} px 360°");

            using var panoBmp = new SKBitmap(panoW, panoH, SKColorType.Bgra8888, SKAlphaType.Premul);
            bool useFeather = mosaicProject?.UseFeatheredBlending ?? true;
            var fullProjection = new EquirectangularProjection(centerRaDeg, 360.0, -90.0, 90.0, panoW, panoH);

            RenderEquirectangularPanelsParallel(
                panoBmp,
                activePanels,
                fullProjection,
                useFeather,
                transparentBackground: false,
                progress,
                cancellationToken);

            ReportProgress(progress, 78, "overlays", config.UseTurkishLanguage ? "Katalog yıldızları ve katmanlar çiziliyor..." : "Drawing sky catalog & overlays...");
            using var canvas = new SKCanvas(panoBmp);
            var markerPoints = new List<(float X, float Y)>(objects.Count);

            // 1. Hipparcos bright sky stars across 360° panorama
            using (var starPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true })
            {
                var allBrightStars = HipparcosCatalog.QueryRegion(centerRaDeg, 0.0, 180.0, 6.2, maxResults: 3500);
                foreach (var s in allBrightStars)
                {
                    float sx = (float)RaToPanoX(s.RaDeg);
                    float sy = (float)DecToPanoY(s.DecDeg);
                    float r = Math.Max(0.8f, (6.5f - s.Mag) * 0.55f * overlayScale);
                    starPaint.Color = s.Bv < 0.0f ? new SKColor(147, 197, 253, 210)
                                    : s.Bv < 0.5f ? new SKColor(248, 250, 252, 210)
                                    : new SKColor(253, 224, 71, 210);
                    canvas.DrawCircle(sx, sy, r, starPaint);
                }
            }

            // 2. Spherical RA/Dec Grid + Degree/Hour Ticks
            if (config.IncludeSphericalGrid)
            {
                DrawPanoramaSphericalGrid(canvas, panoW, panoH, overlayScale, RaToPanoX, DecToPanoY);
            }

            // 3. Constellations with clean 360° horizontal wrap splitting
            if (config.IncludeConstellations)
            {
                DrawPanoramaConstellations(canvas, panoW, overlayScale, RaToPanoX, DecToPanoY);
            }

            // 4. Optional Deep-Sky Objects (OpenNGC) near covered mosaic regions
            if (config.IncludeDsoMarkers)
            {
                DrawPanoramaDsoMarkers(canvas, panoW, panoH, overlayScale, activePanels, RaToPanoX, DecToPanoY, config.UseTurkishLanguage);
            }

            // 5. Detection boxes + confidence labels
            DrawPanoramaDetections(canvas, panoW, overlayScale, objects, config, wcs, activePanels, RaToPanoX, DecToPanoY, markerPoints);

            // 6. Always-on Smart Empty-Corner Observatory Watermark
            if (config.IncludeWatermark)
            {
                ReportProgress(progress, 90, "watermark", config.UseTurkishLanguage ? "Boş köşeye filigran yerleştiriliyor..." : "Placing watermark in empty corner...");
                int totalCount = config.TotalDetectedCount ?? objects.Count;
                DrawSmartEmptyCornerWatermark(panoBmp, canvas, markerPoints, totalCount, config.UseTurkishLanguage, overlayScale, isPanorama: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, 96, "encode", config.UseTurkishLanguage ? $"{config.Format} kaydediliyor..." : $"Encoding {config.Format}...");
            SaveBitmapToFile(panoBmp, destinationImagePath, config.Format, config.JpegQuality);
            ReportProgress(progress, 100, "done", config.UseTurkishLanguage ? "Tamamlandı!" : "Complete!");
        }
        finally
        {
            foreach (var item in activePanels)
            {
                item.Dispose();
            }
        }
    }

    /// <summary>
    /// Renders solved mosaic panels onto a cropped or full equirectangular cylindrical sky projection.
    /// Used by both the live 2D equirectangular viewer (with <paramref name="transparentBackground"/> = true)
    /// and 360° panorama export pipelines. Caller retains ownership of input bitmaps.
    /// </summary>
    public static SKBitmap RenderEquirectangularCompositeBitmap(
        EquirectangularProjection projection,
        IReadOnlyList<(MosaicPanel Panel, SKBitmap Bitmap)> panels,
        bool useFeatheredBlending,
        bool harmonizeSkyTone = false,
        bool transparentBackground = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(panels);

        var output = new SKBitmap(projection.CanvasWidth, projection.CanvasHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        var ownedCopies = new List<SKBitmap>();
        var prepared = new List<PreparedPanel>(panels.Count);

        try
        {
            var workingBmps = new List<SKBitmap>(panels.Count);
            var validPanels = new List<MosaicPanel>(panels.Count);
            for (int i = 0; i < panels.Count; i++)
            {
                var (p, rawBmp) = panels[i];
                if (!p.IsVisible || !p.IsSolved || p.Wcs == null || rawBmp == null || rawBmp.Width <= 0 || rawBmp.Height <= 0)
                {
                    continue;
                }

                SKBitmap bmpToUse;
                if (harmonizeSkyTone || rawBmp.ColorType != SKColorType.Bgra8888)
                {
                    bmpToUse = rawBmp.ColorType == SKColorType.Bgra8888
                        ? rawBmp.Copy()
                        : rawBmp.Copy(SKColorType.Bgra8888);
                    ownedCopies.Add(bmpToUse);
                }
                else
                {
                    bmpToUse = rawBmp;
                }

                workingBmps.Add(bmpToUse);
                validPanels.Add(p);
            }

            if (harmonizeSkyTone && workingBmps.Count >= 2)
            {
                // Keep ownedCopies synchronized in case MosaicSkyHarmonizer replaces any bitmap in-place
                var snapshots = workingBmps.ToArray();
                MosaicSkyHarmonizer.HarmonizeBitmaps(snapshots);
                for (int i = 0; i < snapshots.Length; i++)
                {
                    if (!ReferenceEquals(snapshots[i], workingBmps[i]))
                    {
                        ownedCopies.Remove(workingBmps[i]);
                        ownedCopies.Add(snapshots[i]);
                        workingBmps[i] = snapshots[i];
                    }
                }
            }

            for (int i = 0; i < validPanels.Count; i++)
            {
                var p = validPanels[i];
                var bmp = workingBmps[i];
                var feather = new FeatherContext(p, bmp.Width, bmp.Height);
                prepared.Add(new PreparedPanel(p, p.Wcs, bmp, p.Opacity, feather, i));
            }

            if (prepared.Count > 0)
            {
                RenderEquirectangularPanelsParallel(
                    output,
                    prepared,
                    projection,
                    useFeatheredBlending,
                    transparentBackground,
                    progress: null,
                    cancellationToken);
            }
            else if (!transparentBackground)
            {
                output.Erase(new SKColor(4, 7, 15, 255));
            }
            else
            {
                output.Erase(SKColors.Transparent);
            }

            return output;
        }
        finally
        {
            foreach (var owned in ownedCopies)
            {
                owned.Dispose();
            }
        }
    }

    private static unsafe void RenderEquirectangularPanelsParallel(
        SKBitmap panoBmp,
        List<PreparedPanel> activePanels,
        EquirectangularProjection projection,
        bool useFeather,
        bool transparentBackground,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        int panoW = panoBmp.Width;
        int panoH = panoBmp.Height;
        int dstStride = panoBmp.RowBytes;
        byte* dstBase = (byte*)panoBmp.GetPixels();

        // Build tight 3D spherical cone & Dec bounds for every active panel so off-panel pixels reject in O(1)
        var cones = new List<PanoramaPanelCone>(activePanels.Count);
        foreach (var pp in activePanels)
        {
            if (pp.Wcs == null) continue;
            cones.Add(BuildPanoramaCone(pp));
        }

        double centerRaRad = projection.CenterRaDeg * (Math.PI / 180.0);
        double decMaxDeg = projection.DecMaxDeg;
        double decSpanDeg = projection.DecSpanDeg;
        double raSpanDeg = projection.RaSpanDeg;
        int completedRows = 0;

        Parallel.For(0, panoH, new ParallelOptions { CancellationToken = cancellationToken }, py =>
        {
            double decDeg = decMaxDeg - (((py + 0.5) / panoH) * decSpanDeg);
            double decRad = decDeg * (Math.PI / 180.0);
            double sinD = Math.Sin(decRad);
            double cosD = Math.Cos(decRad);

            // Subtle deep night-sky vertical gradient background (used when opaque background requested)
            byte bgR = (byte)(4 + (int)(Math.Abs(cosD) * 4));
            byte bgG = (byte)(7 + (int)(Math.Abs(cosD) * 5));
            byte bgB = (byte)(15 + (int)(Math.Abs(cosD) * 10));

            byte* rowPtr = dstBase + (py * dstStride);

            // Fast check which panels can possibly touch this declination row
            Span<int> rowPanels = cones.Count <= 32 ? stackalloc int[cones.Count] : new int[cones.Count];
            int rowPanelCount = 0;
            for (int c = 0; c < cones.Count; c++)
            {
                if (decDeg >= cones[c].DecMin && decDeg <= cones[c].DecMax)
                {
                    rowPanels[rowPanelCount++] = c;
                }
            }

            if (rowPanelCount == 0)
            {
                if (transparentBackground)
                {
                    new Span<byte>(rowPtr, panoW * 4).Clear();
                }
                else
                {
                    for (int px = 0; px < panoW; px++)
                    {
                        int o = px * 4;
                        rowPtr[o + 0] = bgB;
                        rowPtr[o + 1] = bgG;
                        rowPtr[o + 2] = bgR;
                        rowPtr[o + 3] = 255;
                    }
                }
            }
            else
            {
                for (int px = 0; px < panoW; px++)
                {
                    double relRaDeg = ((((px + 0.5) / panoW) - 0.5) * raSpanDeg);
                    double raRad = centerRaRad - (relRaDeg * (Math.PI / 180.0));
                    double cosRa = Math.Cos(raRad);
                    double sinRa = Math.Sin(raRad);
                    double sx = cosD * cosRa;
                    double sy = cosD * sinRa;
                    double sz = sinD;

                    float sumR = 0f, sumG = 0f, sumB = 0f, sumW = 0f;
                    float maxAlpha = 0f;
                    bool hit = false;

                    double raDeg = ((raRad * (180.0 / Math.PI)) % 360.0 + 360.0) % 360.0;

                    for (int ri = 0; ri < rowPanelCount; ri++)
                    {
                        ref readonly var cone = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(cones)[rowPanels[ri]];
                        double dot = (sx * cone.CenterVecX) + (sy * cone.CenterVecY) + (sz * cone.CenterVecZ);
                        if (dot < cone.CosMaxAng)
                        {
                            continue;
                        }

                        if (!cone.Wcs.EquatorialToPixel(raDeg, decDeg, out double ix, out double iy))
                        {
                            continue;
                        }

                        if (ix < 0.0 || ix > cone.WcsW - 1.0 || iy < 0.0 || iy > cone.WcsH - 1.0)
                        {
                            continue;
                        }

                        // Belt-and-suspenders roundtrip guard for non-linear distortion/TPS
                        if (cone.HasNonlinearWarp)
                        {
                            var rt = cone.Wcs.PixelToEquatorial(ix, iy);
                            double rDot = (sx * Math.Cos(rt.DecRadians) * Math.Cos(rt.RaRadians))
                                        + (sy * Math.Cos(rt.DecRadians) * Math.Sin(rt.RaRadians))
                                        + (sz * Math.Sin(rt.DecRadians));
                            if (rDot < cone.RoundtripCosMin)
                            {
                                continue;
                            }
                        }

                        double bx = Math.Clamp(ix * cone.WcsToBmpX, 0.0, cone.SrcW - 1.0);
                        double by = Math.Clamp(iy * cone.WcsToBmpY, 0.0, cone.SrcH - 1.0);

                        hit = true;
                        SampleBilinearBgra(cone.Ptr, cone.SrcW, cone.SrcH, cone.RowBytes, bx, by, out float bVal, out float gVal, out float rVal, out float aVal);

                        float edgeW = (useFeather && cone.Feather != null)
                            ? cone.Feather.EvaluateWeight((float)bx, (float)by)
                            : 1.0f;

                        float panelOpacity = (float)Math.Clamp(cone.Opacity, 0.02, 1.0);
                        float srcAlpha = aVal * (1f / 255f);
                        float w = Math.Max(0.0005f, edgeW) * panelOpacity * srcAlpha;

                        sumR += rVal * w;
                        sumG += gVal * w;
                        sumB += bVal * w;
                        sumW += w;

                        float effA = panelOpacity * srcAlpha;
                        if (effA > maxAlpha) maxAlpha = effA;
                    }

                    int o = px * 4;
                    if (!hit || sumW <= 1e-6f)
                    {
                        if (transparentBackground)
                        {
                            rowPtr[o + 0] = 0;
                            rowPtr[o + 1] = 0;
                            rowPtr[o + 2] = 0;
                            rowPtr[o + 3] = 0;
                        }
                        else
                        {
                            rowPtr[o + 0] = bgB;
                            rowPtr[o + 1] = bgG;
                            rowPtr[o + 2] = bgR;
                            rowPtr[o + 3] = 255;
                        }
                    }
                    else
                    {
                        float invW = 1.0f / sumW;
                        float cr = sumR * invW;
                        float cg = sumG * invW;
                        float cb = sumB * invW;

                        if (transparentBackground)
                        {
                            float alpha = Math.Clamp(maxAlpha, 0f, 1f);
                            rowPtr[o + 0] = (byte)Math.Clamp((int)MathF.Round(cb * alpha), 0, 255);
                            rowPtr[o + 1] = (byte)Math.Clamp((int)MathF.Round(cg * alpha), 0, 255);
                            rowPtr[o + 2] = (byte)Math.Clamp((int)MathF.Round(cr * alpha), 0, 255);
                            rowPtr[o + 3] = (byte)Math.Clamp((int)MathF.Round(255f * alpha), 0, 255);
                        }
                        else
                        {
                            if (maxAlpha < 0.999f)
                            {
                                float invA = 1.0f - maxAlpha;
                                cr = (cr * maxAlpha) + (bgR * invA);
                                cg = (cg * maxAlpha) + (bgG * invA);
                                cb = (cb * maxAlpha) + (bgB * invA);
                            }

                            rowPtr[o + 0] = (byte)Math.Clamp((int)MathF.Round(cb), 0, 255);
                            rowPtr[o + 1] = (byte)Math.Clamp((int)MathF.Round(cg), 0, 255);
                            rowPtr[o + 2] = (byte)Math.Clamp((int)MathF.Round(cr), 0, 255);
                            rowPtr[o + 3] = 255;
                        }
                    }
                }
            }

            if (progress != null)
            {
                int done = Interlocked.Increment(ref completedRows);
                if ((done & 31) == 0)
                {
                    int pct = 22 + (int)Math.Round((done / (double)panoH) * 54.0);
                    ReportProgress(progress, pct, "render", $"{panoW} × {panoH} px 360°");
                }
            }
        });
    }

    private readonly unsafe struct PanoramaPanelCone
    {
        public WcsMatrix Wcs { get; init; }
        public byte* Ptr { get; init; }
        public int SrcW { get; init; }
        public int SrcH { get; init; }
        public int WcsW { get; init; }
        public int WcsH { get; init; }
        public double WcsToBmpX { get; init; }
        public double WcsToBmpY { get; init; }
        public int RowBytes { get; init; }
        public double Opacity { get; init; }
        public FeatherContext? Feather { get; init; }
        public double CenterVecX { get; init; }
        public double CenterVecY { get; init; }
        public double CenterVecZ { get; init; }
        public double CosMaxAng { get; init; }
        public double DecMin { get; init; }
        public double DecMax { get; init; }
        public bool HasNonlinearWarp { get; init; }
        public double RoundtripCosMin { get; init; }
    }

    private static unsafe PanoramaPanelCone BuildPanoramaCone(PreparedPanel pp)
    {
        var wcs = pp.Wcs!;
        int srcW = pp.BgraBmp.Width;
        int srcH = pp.BgraBmp.Height;
        int wcsW = Math.Max(2, wcs.ImageWidth > 0 ? wcs.ImageWidth : srcW);
        int wcsH = Math.Max(2, wcs.ImageHeight > 0 ? wcs.ImageHeight : srcH);

        double cCosD = Math.Cos(wcs.CenterCoordinates.DecRadians);
        double cx = cCosD * Math.Cos(wcs.CenterCoordinates.RaRadians);
        double cy = cCosD * Math.Sin(wcs.CenterCoordinates.RaRadians);
        double cz = Math.Sin(wcs.CenterCoordinates.DecRadians);

        double minDot = 1.0;
        double minDec = 90.0;
        double maxDec = -90.0;

        for (int gy = 0; gy <= 4; gy++)
        {
            double py = (gy / 4.0) * (wcsH - 1);
            for (int gx = 0; gx <= 4; gx++)
            {
                double px = (gx / 4.0) * (wcsW - 1);
                var eq = wcs.PixelToEquatorial(px, py);
                if (eq.DecDegrees < minDec) minDec = eq.DecDegrees;
                if (eq.DecDegrees > maxDec) maxDec = eq.DecDegrees;

                double cd = Math.Cos(eq.DecRadians);
                double vx = cd * Math.Cos(eq.RaRadians);
                double vy = cd * Math.Sin(eq.RaRadians);
                double vz = Math.Sin(eq.DecRadians);
                double dot = (cx * vx) + (cy * vy) + (cz * vz);
                if (dot < minDot) minDot = dot;
            }
        }

        double maxAng = Math.Acos(Math.Clamp(minDot, -1.0, 1.0));
        double paddedAng = Math.Min(Math.PI * 0.49, (maxAng * 1.16) + 0.035);
        double cosMaxAng = Math.Cos(paddedAng);
        double decMarginDeg = Math.Max(2.5, (maxAng * (180.0 / Math.PI)) * 0.20);

        bool hasNonlinear = Math.Abs(wcs.RadialDistortionK1) > 1e-7
                         || Math.Abs(wcs.RadialDistortionK2) > 1e-7
                         || wcs.SplineWarp != null;

        double tolDeg = Math.Clamp((Math.Max(wcsW, wcsH) * 0.025 * wcs.PixelScaleArcsec / 3600.0) + 0.40, 0.40, 6.0);
        double roundtripCosMin = Math.Cos(tolDeg * (Math.PI / 180.0));

        return new PanoramaPanelCone
        {
            Wcs = wcs,
            Ptr = (byte*)pp.BgraBmp.GetPixels(),
            SrcW = srcW,
            SrcH = srcH,
            WcsW = wcsW,
            WcsH = wcsH,
            WcsToBmpX = srcW == wcsW ? 1.0 : (srcW - 1.0) / Math.Max(1.0, wcsW - 1.0),
            WcsToBmpY = srcH == wcsH ? 1.0 : (srcH - 1.0) / Math.Max(1.0, wcsH - 1.0),
            RowBytes = pp.BgraBmp.RowBytes,
            Opacity = pp.Opacity,
            Feather = pp.Feather,
            CenterVecX = cx,
            CenterVecY = cy,
            CenterVecZ = cz,
            CosMaxAng = cosMaxAng,
            DecMin = Math.Max(-90.0, minDec - decMarginDeg),
            DecMax = Math.Min(90.0, maxDec + decMarginDeg),
            HasNonlinearWarp = hasNonlinear,
            RoundtripCosMin = roundtripCosMin
        };
    }

    private static double ComputeCircularMeanRa(List<PreparedPanel> panels, WcsMatrix? fallbackWcs)
    {
        if (panels.Count == 0)
        {
            return fallbackWcs?.CenterCoordinates.RaDegrees ?? 180.0;
        }

        double sumSin = 0.0, sumCos = 0.0;
        int counted = 0;
        foreach (var pp in panels)
        {
            if (pp.Wcs == null) continue;
            sumSin += Math.Sin(pp.Wcs.CenterCoordinates.RaRadians);
            sumCos += Math.Cos(pp.Wcs.CenterCoordinates.RaRadians);
            counted++;
        }

        if (counted == 0 || (Math.Abs(sumSin) < 1e-6 && Math.Abs(sumCos) < 1e-6))
        {
            return panels[0].Wcs?.CenterCoordinates.RaDegrees ?? fallbackWcs?.CenterCoordinates.RaDegrees ?? 180.0;
        }

        double meanRad = Math.Atan2(sumSin, sumCos);
        return ((meanRad * (180.0 / Math.PI)) % 360.0 + 360.0) % 360.0;
    }

    private static void DrawPanoramaSphericalGrid(
        SKCanvas canvas,
        int panoW,
        int panoH,
        float scale,
        Func<double, double> raToX,
        Func<double, double> decToY)
    {
        using var gridPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.0f, 1.0f * scale),
            Color = new SKColor(56, 189, 248, 70),
            IsAntialias = true
        };
        using var labelFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal), Math.Clamp(11f * scale, 10f, 32f));
        using var labelPaint = new SKPaint { Color = new SKColor(148, 163, 184, 175), IsAntialias = true };

        for (int ra = 0; ra < 360; ra += 30)
        {
            float gx = (float)raToX(ra);
            canvas.DrawLine(gx, 0, gx, panoH, gridPaint);
            canvas.DrawText($"{ra / 15}h", gx + (4f * scale), Math.Clamp(18f * scale, 14f, 48f), SKTextAlign.Left, labelFont, labelPaint);
        }

        for (int dec = -60; dec <= 60; dec += 30)
        {
            float gy = (float)decToY(dec);
            canvas.DrawLine(0, gy, panoW, gy, gridPaint);
            string decLabel = dec >= 0 ? $"+{dec}°" : $"{dec}°";
            canvas.DrawText(decLabel, 8f * scale, gy - (4f * scale), SKTextAlign.Left, labelFont, labelPaint);
        }
    }

    private static void DrawPanoramaConstellations(
        SKCanvas canvas,
        int panoW,
        float scale,
        Func<double, double> raToX,
        Func<double, double> decToY)
    {
        using var constPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.1f, 1.3f * scale),
            Color = new SKColor(56, 189, 248, 155),
            IsAntialias = true
        };

        foreach (var line in ConstellationCatalog.Lines)
        {
            float x1 = (float)raToX(line.Start.RaDegrees);
            float y1 = (float)decToY(line.Start.DecDegrees);
            float x2 = (float)raToX(line.End.RaDegrees);
            float y2 = (float)decToY(line.End.DecDegrees);
            float dx = x2 - x1;

            if (Math.Abs(dx) <= panoW * 0.5f)
            {
                canvas.DrawLine(x1, y1, x2, y2, constPaint);
            }
            else if (dx > panoW * 0.5f)
            {
                canvas.DrawLine(x1, y1, x2 - panoW, y2, constPaint);
                canvas.DrawLine(x1 + panoW, y1, x2, y2, constPaint);
            }
            else
            {
                canvas.DrawLine(x1, y1, x2 + panoW, y2, constPaint);
                canvas.DrawLine(x1 - panoW, y1, x2, y2, constPaint);
            }
        }
    }

    private static void DrawPanoramaDsoMarkers(
        SKCanvas canvas,
        int panoW,
        int panoH,
        float scale,
        List<PreparedPanel> panels,
        Func<double, double> raToX,
        Func<double, double> decToY,
        bool useTurkish)
    {
        if (panels.Count == 0) return;

        using var dsoPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.2f, 1.4f * scale),
            Color = new SKColor(192, 132, 252, 200),
            PathEffect = SKPathEffect.CreateDash(new[] { 4f * scale, 3f * scale }, 0f),
            IsAntialias = true
        };
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), Math.Clamp(11f * scale, 10f, 34f));
        using var textPaint = new SKPaint { Color = new SKColor(216, 180, 254, 235), IsAntialias = true };

        var allCandidates = new List<OpenNgcObject>();
        foreach (var pp in panels)
        {
            if (pp.Wcs == null) continue;
            double radiusDeg = Math.Max(3.0, Math.Sqrt((pp.Wcs.FovWidthDegrees * pp.Wcs.FovWidthDegrees) + (pp.Wcs.FovHeightDegrees * pp.Wcs.FovHeightDegrees)) * 0.65);
            allCandidates.AddRange(OpenNgcCatalog.QueryRegion(pp.Wcs.CenterCoordinates.RaDegrees, pp.Wcs.CenterCoordinates.DecDegrees, radiusDeg, maxMag: 9.2, maxResults: 24));
        }

        var sortedDsos = CollectSortedUniqueDsos(allCandidates);
        var placedBoxes = new List<SKRect>(32);
        const int maxPanoramaDsoLabels = 14;
        int drawn = 0;

        foreach (var dso in sortedDsos)
        {
            if (drawn >= maxPanoramaDsoLabels) break;

            float sx = (float)raToX(dso.RaDeg);
            float sy = (float)decToY(dso.DecDeg);
            if (sx < 0 || sx > panoW || sy < 0 || sy > panoH) continue;

            float radius = Math.Clamp((float)(dso.MajorAxisArcmin / 60.0 / 360.0 * panoW * 0.5), 7f * scale, 36f * scale);
            if (TryPlaceDsoMarker(canvas, dso, sx, sy, radius, scale, panoW, panoH, font, dsoPaint, textPaint, placedBoxes))
            {
                drawn++;
            }
        }
    }

    private static void DrawPanoramaDetections(
        SKCanvas canvas,
        int panoW,
        float scale,
        IReadOnlyList<AstrophotographyObject> objects,
        ExportConfig config,
        WcsMatrix? fallbackWcs,
        List<PreparedPanel> panels,
        Func<double, double> raToX,
        Func<double, double> decToY,
        List<(float X, float Y)> markerPoints)
    {
        var panelWcsById = new Dictionary<string, WcsMatrix>(StringComparer.Ordinal);
        foreach (var pp in panels)
        {
            if (pp.Panel != null && pp.Wcs != null)
            {
                panelWcsById[pp.Panel.Id] = pp.Wcs;
            }
        }

        using var boxPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.2f, 1.5f * scale),
            IsAntialias = true
        };
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), Math.Clamp(10f * scale, 9f, 28f));
        using var textPaint = new SKPaint { IsAntialias = true };
        using var textBgPaint = new SKPaint { Color = new SKColor(15, 23, 42, 210), Style = SKPaintStyle.Fill, IsAntialias = true };

        foreach (var obj in objects)
        {
            double? ra = obj.RaDegrees;
            double? dec = obj.DecDegrees;
            if (!ra.HasValue)
            {
                WcsMatrix? resolveWcs = (!string.IsNullOrEmpty(obj.PanelId) && panelWcsById.TryGetValue(obj.PanelId, out var pw))
                    ? pw
                    : fallbackWcs;
                if (resolveWcs != null)
                {
                    var eq = resolveWcs.PixelToEquatorial(obj.CenterX, obj.CenterY);
                    ra = eq.RaDegrees;
                    dec = eq.DecDegrees;
                }
            }

            if (!ra.HasValue || !dec.HasValue) continue;

            float sx = (float)raToX(ra.Value);
            float sy = (float)decToY(dec.Value);
            markerPoints.Add((sx, sy));

            if (!config.IncludeBoundingBoxes) continue;

            float sz = Math.Max(4f, 6.0f * scale);
            boxPaint.Color = obj.Tier switch
            {
                ConfidenceTier.Tier1_High => new SKColor(0, 230, 118),
                ConfidenceTier.Tier2_Medium => new SKColor(255, 214, 0),
                _ => new SKColor(255, 145, 0)
            };
            canvas.DrawRect(sx - (sz * 0.5f), sy - (sz * 0.5f), sz, sz, boxPaint);

            if (config.IncludeLabels && sz >= 6f)
            {
                string label = $"{(int)Math.Round(obj.Confidence * 100)}%";
                float tw = font.MeasureText(label);
                float lx = sx + (sz * 0.5f) + (2.5f * scale);
                canvas.DrawRoundRect(lx - (1.5f * scale), sy - (5.5f * scale), tw + (3f * scale), 11f * scale, 2f * scale, 2f * scale, textBgPaint);
                textPaint.Color = boxPaint.Color;
                canvas.DrawText(label, lx, sy + (3.5f * scale), SKTextAlign.Left, font, textPaint);
            }
        }
    }

    private static void DrawOverlaysAndDetectionsOnCanvas(
        SKCanvas canvas,
        int targetW,
        int targetH,
        float scaleX,
        float scaleY,
        float originShiftX,
        float originShiftY,
        IReadOnlyList<AstrophotographyObject> objects,
        ExportConfig config,
        WcsMatrix? wcs,
        ScanMask? mask,
        List<(float X, float Y)> markerPoints)
    {
        float scale = (scaleX + scaleY) * 0.5f;
        int longSide = Math.Max(targetW, targetH);
        float minMarker = Math.Max(10f * scale, longSide / 250f);
        float stroke = Math.Max(2f * scale, longSide / 1200f);

        if (mask != null)
        {
            DrawMask(canvas, mask, stroke, scaleX, scaleY, originShiftX, originShiftY);
        }

        if (config.IncludeSphericalGrid && wcs != null)
        {
            var gridLines = wcs.GenerateSphericalGrid();
            using var gridPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1f, 1.2f * scale),
                Color = new SKColor(56, 189, 248, 120),
                IsAntialias = true
            };
            #pragma warning disable CS0618
            foreach (var line in gridLines)
            {
                if (line.Points.Length < 2) continue;
                using var p = new SKPath();
                p.MoveTo((float)((line.Points[0].X + originShiftX) * scaleX), (float)((line.Points[0].Y + originShiftY) * scaleY));
                for (int i = 1; i < line.Points.Length; i++)
                {
                    p.LineTo((float)((line.Points[i].X + originShiftX) * scaleX), (float)((line.Points[i].Y + originShiftY) * scaleY));
                }
                canvas.DrawPath(p, gridPaint);
            }
            #pragma warning restore CS0618
        }

        if (config.IncludeConstellations && wcs != null)
        {
            using var constLinePaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1.2f, 1.5f * scale),
                Color = new SKColor(56, 189, 248, 180),
                IsAntialias = true
            };
            foreach (var line in ConstellationCatalog.Lines)
            {
                if (!wcs.EquatorialToPixel(line.Start.RaDegrees, line.Start.DecDegrees, out double x1, out double y1)) continue;
                if (!wcs.EquatorialToPixel(line.End.RaDegrees, line.End.DecDegrees, out double x2, out double y2)) continue;
                float sx1 = (float)((x1 + originShiftX) * scaleX);
                float sy1 = (float)((y1 + originShiftY) * scaleY);
                float sx2 = (float)((x2 + originShiftX) * scaleX);
                float sy2 = (float)((y2 + originShiftY) * scaleY);
                if (Math.Abs(sx2 - sx1) < targetW * 2.5f && Math.Abs(sy2 - sy1) < targetH * 2.5f)
                {
                    canvas.DrawLine(sx1, sy1, sx2, sy2, constLinePaint);
                }
            }
        }

        if (config.IncludeDsoMarkers && wcs != null)
        {
            DrawCanvasDsoMarkers(canvas, targetW, targetH, scaleX, scaleY, originShiftX, originShiftY, wcs);
        }

        var tier1Color = new SKColor(0, 230, 118);
        var tier2Color = new SKColor(255, 214, 0);
        var tier3Color = new SKColor(255, 145, 0);
        var tierNoiseColor = new SKColor(239, 68, 68);

        using var haloPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = stroke + (2f * scale),
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, 180)
        };

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = stroke,
            IsAntialias = true
        };

        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), Math.Clamp(11f * scale, 10f, 36f));
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var textBgPaint = new SKPaint { Color = new SKColor(15, 23, 42, 220), Style = SKPaintStyle.Fill, IsAntialias = true };

        foreach (var obj in objects)
        {
            float cx = (obj.CenterX + originShiftX) * scaleX;
            float cy = (obj.CenterY + originShiftY) * scaleY;
            markerPoints.Add((cx, cy));

            if (!config.IncludeBoundingBoxes) continue;

            paint.Color = obj.Tier switch
            {
                ConfidenceTier.Tier1_High => tier1Color,
                ConfidenceTier.Tier2_Medium => tier2Color,
                ConfidenceTier.Tier3_Low => tier3Color,
                _ => tierNoiseColor
            };

            float size = Math.Max(Math.Max(obj.Box.Width, obj.Box.Height) * scale, minMarker);
            float x = cx - (size / 2f);
            float y = cy - (size / 2f);

            canvas.DrawRect(x, y, size, size, haloPaint);
            canvas.DrawRect(x, y, size, size, paint);

            if (config.IncludeLabels)
            {
                string label = $"{(int)Math.Round(obj.Confidence * 100)}%";
                float textWidth = font.MeasureText(label);
                float lx = cx + (size / 2f) + (3f * scale);
                float ly = cy;
                canvas.DrawRoundRect(lx - (2f * scale), ly - (7f * scale), textWidth + (4f * scale), 14f * scale, 3f * scale, 3f * scale, textBgPaint);
                textPaint.Color = paint.Color;
                canvas.DrawText(label, lx, ly + (4f * scale), SKTextAlign.Left, font, textPaint);
            }
        }
    }

    private static void DrawCanvasDsoMarkers(
        SKCanvas canvas,
        int targetW,
        int targetH,
        float scaleX,
        float scaleY,
        float originShiftX,
        float originShiftY,
        WcsMatrix wcs)
    {
        float scale = (scaleX + scaleY) * 0.5f;
        double fovDiagDeg = Math.Sqrt((wcs.FovWidthDegrees * wcs.FovWidthDegrees) + (wcs.FovHeightDegrees * wcs.FovHeightDegrees));
        var rawDsos = OpenNgcCatalog.QueryRegion(
            wcs.CenterCoordinates.RaDegrees,
            wcs.CenterCoordinates.DecDegrees,
            Math.Clamp(fovDiagDeg * 0.85, 2.0, 90.0),
            maxMag: 9.8,
            maxResults: 32);

        using var dsoPaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.3f, 1.5f * scale),
            Color = new SKColor(192, 132, 252, 200),
            PathEffect = SKPathEffect.CreateDash(new[] { 5f * scale, 4f * scale }, 0f),
            IsAntialias = true
        };
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), Math.Clamp(11.5f * scale, 10f, 36f));
        using var textPaint = new SKPaint { Color = new SKColor(233, 213, 255, 240), IsAntialias = true };

        var sortedDsos = CollectSortedUniqueDsos(rawDsos);
        var placedBoxes = new List<SKRect>(32);
        const int maxCanvasDsoLabels = 16;
        int drawn = 0;

        foreach (var dso in sortedDsos)
        {
            if (drawn >= maxCanvasDsoLabels) break;
            if (!wcs.EquatorialToPixel(dso.RaDeg, dso.DecDeg, out double px, out double py)) continue;
            float sx = (float)((px + originShiftX) * scaleX);
            float sy = (float)((py + originShiftY) * scaleY);
            if (sx < 0 || sx > targetW || sy < 0 || sy > targetH) continue;

            float radius = Math.Clamp((float)(dso.MajorAxisArcmin * 60.0 * 0.5 / Math.Max(0.1, wcs.PixelScaleArcsec) * scale), 10f * scale, 80f * scale);
            if (TryPlaceDsoMarker(canvas, dso, sx, sy, radius, scale, targetW, targetH, font, dsoPaint, textPaint, placedBoxes))
            {
                drawn++;
            }
        }
    }

    private static List<OpenNgcObject> CollectSortedUniqueDsos(IEnumerable<OpenNgcObject> candidates)
    {
        var byKey = new Dictionary<string, OpenNgcObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var dso in candidates)
        {
            string key = !string.IsNullOrEmpty(dso.MessierName) ? dso.MessierName : dso.Name;
            if (string.IsNullOrWhiteSpace(key) || byKey.ContainsKey(key)) continue;
            byKey[key] = dso;
        }

        var sorted = new List<OpenNgcObject>(byKey.Values);
        sorted.Sort((a, b) =>
        {
            int cmp = string.IsNullOrEmpty(a.MessierName).CompareTo(string.IsNullOrEmpty(b.MessierName));
            if (cmp != 0) return cmp;
            cmp = string.IsNullOrEmpty(a.CommonName).CompareTo(string.IsNullOrEmpty(b.CommonName));
            if (cmp != 0) return cmp;
            return a.VMag.CompareTo(b.VMag);
        });
        return sorted;
    }

    private static bool TryPlaceDsoMarker(
        SKCanvas canvas,
        OpenNgcObject dso,
        float sx,
        float sy,
        float radius,
        float scale,
        float canvasW,
        float canvasH,
        SKFont font,
        SKPaint dsoPaint,
        SKPaint textPaint,
        List<SKRect> placedBoxes)
    {
        string key = !string.IsNullOrEmpty(dso.MessierName) ? dso.MessierName : dso.Name;
        string label = !string.IsNullOrEmpty(dso.CommonName) ? $"{key} • {dso.CommonName}" : key;

        float fontSize = font.Size;
        float textW = font.MeasureText(label);
        float gap = 5f * scale;
        float pad = Math.Max(6f, 6f * scale);
        float margin = Math.Max(8f, 10f * scale);

        var circleRect = new SKRect(sx - radius, sy - radius, sx + radius, sy + radius);
        for (int i = 0; i < placedBoxes.Count; i++)
        {
            var expanded = placedBoxes[i];
            expanded.Inflate(pad * 0.5f, pad * 0.5f);
            if (expanded.IntersectsWith(circleRect)) return false;
        }

        for (int slot = 0; slot < 4; slot++)
        {
            float lx, baselineY, boxLeft;
            SKTextAlign align;
            switch (slot)
            {
                case 0: // Right
                    lx = sx + radius + gap;
                    baselineY = sy + (fontSize * 0.35f);
                    align = SKTextAlign.Left;
                    boxLeft = lx;
                    break;
                case 1: // Left
                    lx = sx - radius - gap;
                    baselineY = sy + (fontSize * 0.35f);
                    align = SKTextAlign.Right;
                    boxLeft = lx - textW;
                    break;
                case 2: // Below
                    lx = sx - (textW * 0.5f);
                    baselineY = sy + radius + gap + fontSize;
                    align = SKTextAlign.Left;
                    boxLeft = lx;
                    break;
                default: // Above
                    lx = sx - (textW * 0.5f);
                    baselineY = sy - radius - gap;
                    align = SKTextAlign.Left;
                    boxLeft = lx;
                    break;
            }

            float boxTop = baselineY - fontSize;
            float boxRight = boxLeft + textW;
            float boxBottom = baselineY + (3f * scale);
            if (boxLeft < margin || boxRight > canvasW - margin || boxTop < margin || boxBottom > canvasH - margin)
                continue;

            var candidateRect = new SKRect(boxLeft, boxTop, boxRight, boxBottom);
            candidateRect.Inflate(pad, pad);

            bool collides = false;
            for (int i = 0; i < placedBoxes.Count; i++)
            {
                if (placedBoxes[i].IntersectsWith(candidateRect))
                {
                    collides = true;
                    break;
                }
            }
            if (collides) continue;

            canvas.DrawCircle(sx, sy, radius, dsoPaint);
            canvas.DrawText(label, lx, baselineY, align, font, textPaint);

            var reservedCircle = circleRect;
            reservedCircle.Inflate(pad * 0.5f, pad * 0.5f);
            placedBoxes.Add(reservedCircle);
            placedBoxes.Add(candidateRect);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Selects the emptiest, darkest, star-free corner out of all four corners so the AstroCounter watermark
    /// never obscures detected stars or bright celestial targets.
    /// </summary>
    public static unsafe WatermarkCorner ChooseSmartWatermarkCorner(
        SKBitmap bitmap,
        IReadOnlyList<(float X, float Y)> markerPoints,
        int badgeWidth,
        int badgeHeight,
        int margin)
    {
        int w = bitmap.Width;
        int h = bitmap.Height;
        int bw = Math.Clamp(badgeWidth, 40, Math.Max(40, w - (margin * 2)));
        int bh = Math.Clamp(badgeHeight, 24, Math.Max(24, h - (margin * 2)));
        int m = Math.Clamp(margin, 4, Math.Max(4, Math.Min(w, h) / 10));

        var candidates = new (WatermarkCorner Corner, int X0, int Y0, double TieBreaker)[]
        {
            (WatermarkCorner.BottomRight, Math.Max(0, w - bw - m), Math.Max(0, h - bh - m), 0.0),
            (WatermarkCorner.BottomLeft,  m, Math.Max(0, h - bh - m), 2.0),
            (WatermarkCorner.TopRight,    Math.Max(0, w - bw - m), m, 5.0),
            (WatermarkCorner.TopLeft,     m, m, 8.0)
        };

        using var bgraConvert = bitmap.ColorType == SKColorType.Bgra8888 ? null : bitmap.Copy(SKColorType.Bgra8888);
        SKBitmap work = bgraConvert ?? bitmap;
        byte* basePtr = (byte*)work.GetPixels();
        int rowBytes = work.RowBytes;

        WatermarkCorner bestCorner = WatermarkCorner.BottomRight;
        double bestScore = double.PositiveInfinity;

        const float pad = 12f;

        foreach (var cand in candidates)
        {
            double score = cand.TieBreaker;

            // 1. Massive avoidance penalty for detected stars / boxes inside or touching the badge footprint
            float rx0 = cand.X0 - pad;
            float ry0 = cand.Y0 - pad;
            float rx1 = cand.X0 + bw + pad;
            float ry1 = cand.Y0 + bh + pad;
            int starsInside = 0;

            for (int i = 0; i < markerPoints.Count; i++)
            {
                var (mx, my) = markerPoints[i];
                if (mx >= rx0 && mx <= rx1 && my >= ry0 && my <= ry1)
                {
                    starsInside++;
                }
            }
            score += starsInside * 6000.0;

            // 2. Sample pixel luminance & filled-panel occupancy across an 18x10 grid inside the candidate rect
            const int cols = 18;
            const int rows = 10;
            for (int gy = 0; gy < rows; gy++)
            {
                int sy = Math.Clamp(cand.Y0 + ((gy * bh) / Math.Max(1, rows - 1)), 0, h - 1);
                byte* rowPtr = basePtr + (sy * rowBytes);
                for (int gx = 0; gx < cols; gx++)
                {
                    int sx = Math.Clamp(cand.X0 + ((gx * bw) / Math.Max(1, cols - 1)), 0, w - 1);
                    int o = sx * 4;
                    byte b = rowPtr[o + 0];
                    byte g = rowPtr[o + 1];
                    byte r = rowPtr[o + 2];

                    double lum = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
                    byte maxCh = Math.Max(r, Math.Max(g, b));

                    // Non-empty canvas region vs deep-space empty background (#04070F / #05080F has lum ~ 7-12)
                    if (maxCh > 24 || lum > 16.0)
                    {
                        score += 120.0 + lum;
                    }

                    // Extra quadratic penalty for bright stars / bright nebulosity peaks inside the corner
                    if (maxCh > 65)
                    {
                        double exc = maxCh - 50.0;
                        score += exc * exc * 0.5;
                    }
                }
            }

            if (score < bestScore)
            {
                bestScore = score;
                bestCorner = cand.Corner;
            }
        }

        return bestCorner;
    }

    private static void DrawSmartEmptyCornerWatermark(
        SKBitmap bitmap,
        SKCanvas canvas,
        IReadOnlyList<(float X, float Y)> markerPoints,
        int detectedCount,
        bool useTurkish,
        float baseScale,
        bool isPanorama = false)
    {
        int w = bitmap.Width;
        int h = bitmap.Height;

        float uiScale = Math.Clamp(Math.Min(w, h) / 1400f, 0.72f, 3.8f);
        if (Math.Min(w, h) < 480)
        {
            uiScale = Math.Max(0.45f, Math.Min(w, h) / 680f);
        }

        string title = WatermarkBrand;
        string countText = useTurkish
            ? $"{detectedCount:N0} Gök Cismi Tespit Edildi"
            : $"{detectedCount:N0} Celestial Objects Detected";
        string metaText = isPanorama
            ? $"360° • {w}×{h}"
            : $"{w}×{h} px";

        using var titleFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 15f * uiScale);
        using var countFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 12.5f * uiScale);
        using var metaFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal), 10.5f * uiScale);

        float inlineStarGap = 15f * uiScale;
        float textW = Math.Max(
            titleFont.MeasureText(title) + metaFont.MeasureText(metaText) + (28f * uiScale),
            countFont.MeasureText(countText) + inlineStarGap);
        float iconAreaW = 42f * uiScale;
        float padX = 16f * uiScale;
        float badgeW = Math.Min(w - (16f * uiScale), iconAreaW + textW + (padX * 2f));
        float badgeH = 58f * uiScale;
        int margin = Math.Max(10, (int)Math.Round(22f * uiScale));

        var corner = ChooseSmartWatermarkCorner(bitmap, markerPoints, (int)Math.Ceiling(badgeW), (int)Math.Ceiling(badgeH), margin);

        float bx = corner is WatermarkCorner.BottomRight or WatermarkCorner.TopRight
            ? Math.Max(margin, w - badgeW - margin)
            : margin;
        float by = corner is WatermarkCorner.BottomRight or WatermarkCorner.BottomLeft
            ? Math.Max(margin, h - badgeH - margin)
            : margin;

        var rect = new SKRect(bx, by, bx + badgeW, by + badgeH);
        float radius = 12f * uiScale;

        using var shadowPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 120),
            Style = SKPaintStyle.Fill,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6f * uiScale)
        };
        canvas.DrawRoundRect(new SKRect(bx, by + (3f * uiScale), bx + badgeW, by + badgeH + (3f * uiScale)), radius, radius, shadowPaint);

        using var bgPaint = new SKPaint
        {
            Color = new SKColor(10, 17, 34, 212),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        canvas.DrawRoundRect(rect, radius, radius, bgPaint);

        using var borderPaint = new SKPaint
        {
            Color = new SKColor(56, 189, 248, 130),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, 1.3f * uiScale),
            IsAntialias = true
        };
        canvas.DrawRoundRect(rect, radius, radius, borderPaint);

        // Vector Observatory star emblem inside left circle
        float icx = bx + padX + (14f * uiScale);
        float icy = by + (badgeH * 0.5f);
        using var ringPaint = new SKPaint
        {
            Color = new SKColor(56, 189, 248, 170),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.1f, 1.4f * uiScale),
            IsAntialias = true
        };
        canvas.DrawCircle(icx, icy, 13f * uiScale, ringPaint);

        using var starFill = new SKPaint
        {
            Color = new SKColor(56, 189, 248, 245),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        float rOuter = 9.5f * uiScale;
        float rInner = 2.6f * uiScale;
        #pragma warning disable CS0618
        using (var starPath = new SKPath())
        {
            starPath.MoveTo(icx, icy - rOuter);
            starPath.LineTo(icx + rInner, icy - rInner);
            starPath.LineTo(icx + rOuter, icy);
            starPath.LineTo(icx + rInner, icy + rInner);
            starPath.LineTo(icx, icy + rOuter);
            starPath.LineTo(icx - rInner, icy + rInner);
            starPath.LineTo(icx - rOuter, icy);
            starPath.LineTo(icx - rInner, icy - rInner);
            starPath.Close();
            canvas.DrawPath(starPath, starFill);
        }
        #pragma warning restore CS0618

        float textLeft = bx + padX + iconAreaW;
        using var titlePaint = new SKPaint { Color = new SKColor(248, 250, 252, 245), IsAntialias = true };
        using var countPaint = new SKPaint { Color = new SKColor(52, 211, 153, 245), IsAntialias = true };
        using var metaPaint = new SKPaint { Color = new SKColor(148, 163, 184, 220), IsAntialias = true };

        canvas.DrawText(title, textLeft, by + (23.5f * uiScale), SKTextAlign.Left, titleFont, titlePaint);
        canvas.DrawText(metaText, bx + badgeW - padX, by + (22.5f * uiScale), SKTextAlign.Right, metaFont, metaPaint);

        // Draw crisp 5-point vector star glyph instead of Unicode '★' (avoids missing-glyph box across Skia fonts)
        float sx5 = textLeft + (5.2f * uiScale);
        float sy5 = by + (39.5f * uiScale);
        float outerR5 = 5.0f * uiScale;
        float innerR5 = 2.05f * uiScale;
        #pragma warning disable CS0618
        using (var badgeStar = new SKPath())
        {
            for (int k = 0; k < 10; k++)
            {
                double ang = -Math.PI * 0.5 + (k * Math.PI / 5.0);
                float r = (k & 1) == 0 ? outerR5 : innerR5;
                float px = sx5 + (float)(r * Math.Cos(ang));
                float py = sy5 + (float)(r * Math.Sin(ang));
                if (k == 0) badgeStar.MoveTo(px, py);
                else badgeStar.LineTo(px, py);
            }
            badgeStar.Close();
            canvas.DrawPath(badgeStar, countPaint);
        }
        #pragma warning restore CS0618

        canvas.DrawText(countText, textLeft + inlineStarGap, by + (44f * uiScale), SKTextAlign.Left, countFont, countPaint);
    }

    private static List<PreparedPanel> LoadPreparedMosaicPanels(
        MosaicProject mosaicProject,
        ExportConfig config,
        bool allowUnsolved,
        CancellationToken cancellationToken)
    {
        var result = new List<PreparedPanel>(mosaicProject.Count);
        var panels = mosaicProject.Panels;
        for (int i = 0; i < panels.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = panels[i];
            if (!p.IsVisible) continue;
            if (!allowUnsolved && (p.Wcs == null || !p.IsSolved)) continue;

            SKBitmap? src = p.CachedSkiaBitmap as SKBitmap;
            bool loadedFromDisk = false;
            if (src == null && !string.IsNullOrEmpty(p.FilePath) && File.Exists(p.FilePath))
            {
                try
                {
                    src = ImagePreprocessor.LoadImage(p.FilePath);
                    loadedFromDisk = true;
                }
                catch
                {
                    src = null;
                }
            }

            if (src == null || src.Width <= 0 || src.Height <= 0) continue;

            try
            {
                SKBitmap toned = PrepareToneAdjustedBitmap(p, src, config);
                var feather = new FeatherContext(p, toned.Width, toned.Height);
                result.Add(new PreparedPanel(p, p.Wcs, toned, p.Opacity, feather, i));
            }
            finally
            {
                if (loadedFromDisk) src.Dispose();
            }
        }

        return result;
    }

    private static SKBitmap PrepareToneAdjustedBitmap(MosaicPanel? panel, SKBitmap rawBitmap, ExportConfig config)
    {
        SKBitmap? current = rawBitmap.ColorType == SKColorType.Bgra8888
            ? rawBitmap.Copy()
            : rawBitmap.Copy(SKColorType.Bgra8888);

        if (!config.ApplyDisplayAdjustments)
        {
            return current;
        }

        bool autoBalance = panel?.IsAutoBrightnessEnabled ?? config.FallbackAutoBalance;
        double brightness = panel?.Brightness ?? config.FallbackBrightness;
        double exposure = panel?.Exposure ?? config.FallbackExposure;
        double contrast = panel?.Contrast ?? config.FallbackContrast;

        if (autoBalance)
        {
            var balanced = ImagePreprocessor.AutoBalanceBrightness(current, out _);
            current.Dispose();
            current = balanced;
        }

        if (Math.Abs(brightness) >= 0.5 || Math.Abs(exposure - 1.0) >= 0.01 || Math.Abs(contrast - 1.0) >= 0.01)
        {
            var adjusted = ImagePreprocessor.ApplyDisplayToneAdjustment(current, brightness, exposure, contrast);
            current.Dispose();
            current = adjusted;
        }

        if (current.ColorType != SKColorType.Bgra8888)
        {
            var bgra = current.Copy(SKColorType.Bgra8888);
            current.Dispose();
            current = bgra;
        }

        return current;
    }

    /// <summary>
    /// Equalizes harsh sky-background exposure mismatches (such as bright twilight or light-polluted frames)
    /// across multi-panel mosaics using per-panel dark-sky percentile pedestals while keeping star peaks unclipped.
    /// </summary>
    private static void HarmonizePanelSkyPedestals(List<PreparedPanel> panels)
    {
        if (panels.Count < 2) return;

        var bmps = new SKBitmap[panels.Count];
        for (int p = 0; p < panels.Count; p++)
        {
            bmps[p] = panels[p].BgraBmp;
        }

        MosaicSkyHarmonizer.HarmonizeBitmaps(bmps);

        for (int p = 0; p < panels.Count; p++)
        {
            panels[p].AdoptReplacedBgraBitmap(bmps[p]);
        }
    }

    private static unsafe void SampleBilinearBgra(
        byte* basePtr,
        int width,
        int height,
        int rowBytes,
        double fx,
        double fy,
        out float b,
        out float g,
        out float r,
        out float a)
    {
        int x0 = Math.Clamp((int)Math.Floor(fx), 0, width - 1);
        int y0 = Math.Clamp((int)Math.Floor(fy), 0, height - 1);
        int x1 = Math.Min(width - 1, x0 + 1);
        int y1 = Math.Min(height - 1, y0 + 1);

        float tx = (float)(fx - x0);
        float ty = (float)(fy - y0);
        float itx = 1.0f - tx;
        float ity = 1.0f - ty;

        float w00 = itx * ity;
        float w10 = tx * ity;
        float w01 = itx * ty;
        float w11 = tx * ty;

        byte* row0 = basePtr + (y0 * rowBytes);
        byte* row1 = basePtr + (y1 * rowBytes);

        int i00 = x0 * 4;
        int i10 = x1 * 4;

        b = (row0[i00 + 0] * w00) + (row0[i10 + 0] * w10) + (row1[i00 + 0] * w01) + (row1[i10 + 0] * w11);
        g = (row0[i00 + 1] * w00) + (row0[i10 + 1] * w10) + (row1[i00 + 1] * w01) + (row1[i10 + 1] * w11);
        r = (row0[i00 + 2] * w00) + (row0[i10 + 2] * w10) + (row1[i00 + 2] * w01) + (row1[i10 + 2] * w11);
        a = (row0[i00 + 3] * w00) + (row0[i10 + 3] * w10) + (row1[i00 + 3] * w01) + (row1[i10 + 3] * w11);
    }

    private sealed class PreparedPanel : IDisposable
    {
        public MosaicPanel? Panel { get; }
        public WcsMatrix? Wcs { get; }
        public SKBitmap BgraBmp { get; private set; }
        public double Opacity { get; }
        public FeatherContext? Feather { get; }
        public int OriginalIndex { get; }

        public PreparedPanel(MosaicPanel? panel, WcsMatrix? wcs, SKBitmap bgraBmp, double opacity, FeatherContext? feather, int originalIndex)
        {
            Panel = panel;
            Wcs = wcs;
            BgraBmp = bgraBmp;
            Opacity = opacity;
            Feather = feather;
            OriginalIndex = originalIndex;
        }

        public void AdoptReplacedBgraBitmap(SKBitmap newBitmap)
        {
            if (ReferenceEquals(BgraBmp, newBitmap)) return;
            BgraBmp = newBitmap;
        }

        public void Dispose() => BgraBmp.Dispose();
    }

    private sealed class FeatherContext
    {
        private readonly bool _hasOverlap;
        private readonly bool _useMask;
        private readonly int _width;
        private readonly int _height;
        private readonly int _marginX;
        private readonly int _marginY;
        private readonly float _invWidth;
        private readonly float _invHeight;
        private readonly float[]? _weights;
        private readonly float[]? _depths;
        private readonly EdgeOverlap _edges;

        public FeatherContext(MosaicPanel panel, int bmpWidth, int bmpHeight)
        {
            _width = Math.Max(1, bmpWidth);
            _height = Math.Max(1, bmpHeight);
            _invWidth = 1.0f / Math.Max(1, _width - 1);
            _invHeight = 1.0f / Math.Max(1, _height - 1);

            double fraction = Math.Clamp(panel.FeatherFraction, 0.02, 0.35);
            _marginX = Math.Max(10, (int)(_width * fraction));
            _marginY = Math.Max(10, (int)(_height * fraction));
            _edges = panel.OverlappingEdges;

            var mask = panel.PerimeterOverlapMask;
            if (mask != null && mask.Length >= MosaicPanel.PerimeterSampleCount)
            {
                bool any = false;
                for (int i = 0; i < MosaicPanel.PerimeterSampleCount; i++)
                {
                    if (mask[i]) { any = true; break; }
                }

                if (any)
                {
                    _hasOverlap = true;
                    _useMask = true;
                    _weights = new float[MosaicPanel.PerimeterSampleCount];
                    _depths = new float[MosaicPanel.PerimeterSampleCount];
                    var rawDepths = panel.PerimeterOverlapDepth;
                    bool hasDepth = rawDepths != null && rawDepths.Length >= MosaicPanel.PerimeterSampleCount;

                    for (int k = 0; k < MosaicPanel.PerimeterSampleCount; k++)
                    {
                        int kM2 = (k + 254) % 256;
                        int kM1 = (k + 255) % 256;
                        int kP1 = (k + 1) % 256;
                        int kP2 = (k + 2) % 256;

                        _weights[k] = (mask[kM2] ? 0.10f : 0f)
                                    + (mask[kM1] ? 0.20f : 0f)
                                    + (mask[k]   ? 0.40f : 0f)
                                    + (mask[kP1] ? 0.20f : 0f)
                                    + (mask[kP2] ? 0.10f : 0f);

                        if (hasDepth)
                        {
                            float dMax = 0f;
                            int[] nbs = { kM1, k, kP1 };
                            foreach (int nk in nbs)
                            {
                                if (mask[nk] && rawDepths![nk] > 0f)
                                {
                                    float clamped = Math.Clamp(rawDepths[nk] * 0.90f, 0.03f, (float)fraction);
                                    if (dMax == 0f || clamped < dMax) dMax = clamped;
                                }
                            }
                            _depths[k] = dMax > 0f ? dMax : (float)fraction;
                        }
                        else
                        {
                            _depths[k] = (float)fraction;
                        }
                    }
                    return;
                }
            }

            _hasOverlap = _edges != EdgeOverlap.None;
            _useMask = false;
        }

        public float EvaluateWeight(float x, float y)
        {
            if (!_hasOverlap) return 1.0f;

            float distTop = y;
            float distBottom = (_height - 1) - y;
            float distLeft = x;
            float distRight = (_width - 1) - x;

            if (distTop >= _marginY && distBottom >= _marginY && distLeft >= _marginX && distRight >= _marginX)
            {
                return 1.0f;
            }

            if (_useMask && _weights != null && _depths != null)
            {
                float fTop = distTop < _marginY ? SampleEdgeFactor(distTop, 0, x * _invWidth, _height) : 1.0f;
                float fRight = distRight < _marginX ? SampleEdgeFactor(distRight, 64, y * _invHeight, _width) : 1.0f;
                float fBottom = distBottom < _marginY ? SampleEdgeFactor(distBottom, 128, ((_width - 1) - x) * _invWidth, _height) : 1.0f;
                float fLeft = distLeft < _marginX ? SampleEdgeFactor(distLeft, 192, ((_height - 1) - y) * _invHeight, _width) : 1.0f;
                return fTop * fRight * fBottom * fLeft;
            }

            float ay = 1.0f;
            if (_edges.HasFlag(EdgeOverlap.Top) && distTop < _marginY)
                ay = (float)(0.5 - (0.5 * Math.Cos(Math.PI * Math.Clamp(distTop / _marginY, 0f, 1f))));
            else if (_edges.HasFlag(EdgeOverlap.Bottom) && distBottom < _marginY)
                ay = (float)(0.5 - (0.5 * Math.Cos(Math.PI * Math.Clamp(distBottom / _marginY, 0f, 1f))));

            float ax = 1.0f;
            if (_edges.HasFlag(EdgeOverlap.Left) && distLeft < _marginX)
                ax = (float)(0.5 - (0.5 * Math.Cos(Math.PI * Math.Clamp(distLeft / _marginX, 0f, 1f))));
            else if (_edges.HasFlag(EdgeOverlap.Right) && distRight < _marginX)
                ax = (float)(0.5 - (0.5 * Math.Cos(Math.PI * Math.Clamp(distRight / _marginX, 0f, 1f))));

            return ax * ay;
        }

        private float SampleEdgeFactor(float distPx, int edgeStartIdx, float edgeCoordNorm, int maxDimPx)
        {
            float pos = Math.Clamp(edgeCoordNorm * 63.0f, 0f, 63.0f);
            int k0 = (int)pos;
            int k1 = Math.Min(63, k0 + 1);
            float t = pos - k0;

            float w = (_weights![edgeStartIdx + k0] * (1f - t)) + (_weights[edgeStartIdx + k1] * t);
            if (w <= 0.001f) return 1.0f;

            float d = (_depths![edgeStartIdx + k0] * (1f - t)) + (_depths[edgeStartIdx + k1] * t);
            float effMargin = Math.Max(8.0f, maxDimPx * d);
            if (distPx >= effMargin) return 1.0f;

            float norm = Math.Clamp(distPx / effMargin, 0f, 1f);
            float alphaFeather = (float)(0.5 - (0.5 * Math.Cos(Math.PI * norm)));
            return 1.0f - (w * (1.0f - alphaFeather));
        }
    }

    private static void ReportProgress(IProgress<ExportProgress>? progress, int percent, string stageKey, string detail)
    {
        progress?.Report(new ExportProgress(Math.Clamp(percent, 0, 100), stageKey, detail));
    }

    private static void SaveBitmapToFile(SKBitmap bitmap, string destinationImagePath, string format, int jpegQuality = 95)
    {
        string ext = Path.GetExtension(destinationImagePath).ToLowerInvariant();
        string fmt = format.ToUpperInvariant();
        int quality = Math.Clamp(jpegQuality, 50, 100);

        if (ext is ".tif" or ".tiff" || fmt == "TIFF")
        {
            WriteUncompressedTiff(bitmap, destinationImagePath);
            return;
        }

        using var image = SKImage.FromBitmap(bitmap);
        if (ext is ".jpg" or ".jpeg" || fmt == "JPG")
        {
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
            using var stream = File.Open(destinationImagePath, FileMode.Create, FileAccess.Write);
            data.SaveTo(stream);
        }
        else if (ext is ".webp")
        {
            using var data = image.Encode(SKEncodedImageFormat.Webp, 100);
            using var stream = File.Open(destinationImagePath, FileMode.Create, FileAccess.Write);
            data.SaveTo(stream);
        }
        else
        {
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Open(destinationImagePath, FileMode.Create, FileAccess.Write);
            data.SaveTo(stream);
        }
    }

    /// <summary>
    /// Writes a true 24-bit RGB uncompressed baseline TIFF file directly using buffered raw scanline memory.
    /// </summary>
    private static unsafe void WriteUncompressedTiff(SKBitmap bitmap, string filePath)
    {
        using var bgraConvert = bitmap.ColorType == SKColorType.Bgra8888 ? null : bitmap.Copy(SKColorType.Bgra8888);
        SKBitmap src = bgraConvert ?? bitmap;

        int width = src.Width;
        int height = src.Height;
        int rowBytesRgb = width * 3;
        long pixelDataSizeLong = (long)rowBytesRgb * height;
        uint pixelDataSize = pixelDataSizeLong > uint.MaxValue ? uint.MaxValue : (uint)pixelDataSizeLong;

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536);
        using var bw = new BinaryWriter(fs);

        bw.Write((byte)'I');
        bw.Write((byte)'I');
        bw.Write((ushort)42);
        bw.Write((uint)8);

        const ushort entryCount = 10;
        const uint bitsPerSampleOffset = 8 + 2 + (entryCount * 12) + 4;
        const uint pixelDataOffset = bitsPerSampleOffset + 6;

        bw.Write(entryCount);

        void WriteTag(ushort tag, ushort type, uint count, uint valueOrOffset)
        {
            bw.Write(tag);
            bw.Write(type);
            bw.Write(count);
            bw.Write(valueOrOffset);
        }

        WriteTag(256, 4, 1, (uint)width);                 // ImageWidth
        WriteTag(257, 4, 1, (uint)height);                // ImageLength
        WriteTag(258, 3, 3, bitsPerSampleOffset);         // BitsPerSample (8, 8, 8)
        WriteTag(259, 3, 1, 1);                           // Compression = None
        WriteTag(262, 3, 1, 2);                           // PhotometricInterpretation = RGB
        WriteTag(273, 4, 1, pixelDataOffset);             // StripOffsets
        WriteTag(277, 3, 1, 3);                           // SamplesPerPixel = 3
        WriteTag(278, 4, 1, (uint)height);                // RowsPerStrip = height
        WriteTag(279, 4, 1, pixelDataSize);               // StripByteCounts
        WriteTag(284, 3, 1, 1);                           // PlanarConfiguration = Chunky

        bw.Write((uint)0);
        bw.Write((ushort)8);
        bw.Write((ushort)8);
        bw.Write((ushort)8);

        byte* srcBase = (byte*)src.GetPixels();
        int srcStride = src.RowBytes;
        byte[] rowBuffer = new byte[rowBytesRgb];

        for (int y = 0; y < height; y++)
        {
            byte* rowPtr = srcBase + (y * srcStride);
            for (int x = 0; x < width; x++)
            {
                int so = x * 4;
                int doff = x * 3;
                rowBuffer[doff + 0] = rowPtr[so + 2]; // R
                rowBuffer[doff + 1] = rowPtr[so + 1]; // G
                rowBuffer[doff + 2] = rowPtr[so + 0]; // B
            }
            bw.Write(rowBuffer);
        }
    }

    private static void DrawMask(SKCanvas canvas, ScanMask mask, float stroke, float scaleX, float scaleY, float shiftX, float shiftY)
    {
        foreach (var region in mask.Regions)
        {
            bool exclude = region.Kind == RegionKind.Exclude;
            var baseColor = exclude ? new SKColor(220, 38, 38) : new SKColor(16, 185, 129);

            #pragma warning disable CS0618
            using var path = new SKPath();
            var pts = region.Points;
            if (pts.Count == 0) continue;
            path.MoveTo((pts[0].X + shiftX) * scaleX, (pts[0].Y + shiftY) * scaleY);
            for (int i = 1; i < pts.Count; i++)
            {
                path.LineTo((pts[i].X + shiftX) * scaleX, (pts[i].Y + shiftY) * scaleY);
            }
            path.Close();
            #pragma warning restore CS0618

            using var fill = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                Color = baseColor.WithAlpha(exclude ? (byte)90 : (byte)30)
            };
            using var outline = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = stroke,
                IsAntialias = true,
                Color = baseColor
            };

            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, outline);
        }
    }
}
