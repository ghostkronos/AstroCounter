namespace AstroCounter.Core.Export;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AstroCounter.Core.Astrometry;
using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using SkiaSharp;

/// <summary>
/// Handles exporting detection metrics to CSV, JSON, Annotated 2D Images/Mosaics, and 360° Equirectangular Panoramas.
/// </summary>
public record ExportConfig(
    string Format, // "TIFF", "PNG", "JPG", "CSV", "JSON"
    bool IncludeBoundingBoxes = true,
    bool IncludeLabels = true,
    bool IncludeSphericalGrid = false,
    bool IncludeConstellations = false,
    int ScaleFactor = 1, // 1 or 2
    string ExportProjectionMode = "Canvas2D" // "Canvas2D", "Panorama360"
);

public static class ResultExporter
{
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
            DrawMask(canvas, mask, stroke);
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
        var records = objects.Select(o =>
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
            generator = "AstroCounter v1.3.1",
            timestamp = DateTime.UtcNow.ToString("o"),
            totalObjects = objects.Count(),
            hasWcs = wcs != null,
            wcsCenter = wcs != null ? new { ra = wcs.CenterCoordinates.RaDegrees, dec = wcs.CenterCoordinates.DecDegrees, fovW = wcs.FovWidthDegrees, fovH = wcs.FovHeightDegrees } : null,
            detections = records
        }, options);

        File.WriteAllText(filePath, json, Encoding.UTF8);
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
        if (string.Equals(config.ExportProjectionMode, "Panorama360", StringComparison.OrdinalIgnoreCase))
        {
            ExportEquirectangularPanorama(sourceImagePath, objects, destinationImagePath, config, wcs, mosaicProject);
            return;
        }

        if (mosaicProject != null && mosaicProject.Count > 1)
        {
            ExportMosaicCanvas(objects, destinationImagePath, config, wcs, mask, mosaicProject);
            return;
        }

        string? validPath = sourceImagePath;
        if ((string.IsNullOrEmpty(validPath) || !File.Exists(validPath)) && mosaicProject != null && mosaicProject.Count > 0)
        {
            validPath = mosaicProject.Panels.FirstOrDefault(p => !string.IsNullOrEmpty(p.FilePath) && File.Exists(p.FilePath))?.FilePath;
        }

        if (string.IsNullOrEmpty(validPath) || !File.Exists(validPath))
            return;

        using var rawBitmap = ImagePreprocessor.LoadImage(validPath);
        int scaleFactor = Math.Clamp(config.ScaleFactor, 1, 2);
        int targetW = Math.Min(12000, rawBitmap.Width * scaleFactor);
        int targetH = Math.Min(12000, rawBitmap.Height * scaleFactor);
        float scale = (float)targetW / Math.Max(1, rawBitmap.Width);

        using var bitmap = (Math.Abs(scale - 1.0f) > 0.01f)
            ? rawBitmap.Resize(new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul), SKSamplingOptions.Default)
            : (rawBitmap.ColorType == SKColorType.Bgra8888 ? rawBitmap.Copy() : rawBitmap.Copy(SKColorType.Bgra8888));

        using var canvas = new SKCanvas(bitmap);
        DrawOverlaysAndDetectionsOnCanvas(canvas, targetW, targetH, scale, objects, config, wcs, mask);
        SaveBitmapToFile(bitmap, destinationImagePath, config.Format);
    }

    private static void ExportMosaicCanvas(
        IEnumerable<AstrophotographyObject> objects,
        string destinationImagePath,
        ExportConfig config,
        WcsMatrix? masterWcs,
        ScanMask? mask,
        MosaicProject mosaicProject)
    {
        masterWcs ??= mosaicProject.ComputeMasterWcs();
        int baseW = masterWcs != null ? Math.Clamp(masterWcs.ImageWidth, 800, 8000) : 3200;
        int baseH = masterWcs != null ? Math.Clamp(masterWcs.ImageHeight, 600, 8000) : 2400;

        int scaleFactor = Math.Clamp(config.ScaleFactor, 1, 2);
        double maxScale = Math.Min(scaleFactor, 8192.0 / Math.Max(baseW, baseH));
        int targetW = Math.Max(800, (int)Math.Round(baseW * maxScale));
        int targetH = Math.Max(600, (int)Math.Round(baseH * maxScale));
        float scale = (float)targetW / baseW;

        using var bitmap = new SKBitmap(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(5, 8, 15));

        var panels = mosaicProject.Panels;
        for (int pIdx = 0; pIdx < panels.Count; pIdx++)
        {
            var p = panels[pIdx];
            if (!p.IsVisible) continue;

            SKBitmap? panelBmp = p.CachedSkiaBitmap as SKBitmap;
            bool disposeBmp = false;
            if (panelBmp == null && !string.IsNullOrEmpty(p.FilePath) && File.Exists(p.FilePath))
            {
                try
                {
                    panelBmp = ImagePreprocessor.LoadImage(p.FilePath);
                    disposeBmp = true;
                }
                catch { }
            }

            if (panelBmp == null) continue;

            try
            {
                var tr = MosaicProject.GetPanelCanvasTransform(p, masterWcs, pIdx);
                var skMatrix = new SKMatrix
                {
                    ScaleX = (float)(tr.M11 * scale),
                    SkewY = (float)(tr.M12 * scale),
                    SkewX = (float)(tr.M21 * scale),
                    ScaleY = (float)(tr.M22 * scale),
                    TransX = (float)(tr.OffsetX * scale),
                    TransY = (float)(tr.OffsetY * scale),
                    Persp2 = 1f
                };

                canvas.Save();
                canvas.SetMatrix(skMatrix);
                using var imgPaint = new SKPaint
                {
                    Color = SKColors.White.WithAlpha((byte)Math.Clamp((int)(p.Opacity * 255), 10, 255)),
                    IsAntialias = true
                };
                using var img = SKImage.FromBitmap(panelBmp);
                canvas.DrawImage(img, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), imgPaint);
                canvas.Restore();
            }
            finally
            {
                if (disposeBmp) panelBmp.Dispose();
            }
        }

        DrawOverlaysAndDetectionsOnCanvas(canvas, targetW, targetH, scale, objects, config, masterWcs, mask);
        SaveBitmapToFile(bitmap, destinationImagePath, config.Format);
    }

    /// <summary>
    /// Exports a full 360° × 180° Equirectangular Spherical Sky Panorama (suitable for 360° VR / Planetarium viewers).
    /// </summary>
    private static void ExportEquirectangularPanorama(
        string? sourceImagePath,
        IEnumerable<AstrophotographyObject> objects,
        string destinationImagePath,
        ExportConfig config,
        WcsMatrix? wcs,
        MosaicProject? mosaicProject)
    {
        int scaleFactor = Math.Clamp(config.ScaleFactor, 1, 2);
        int panoW = scaleFactor == 2 ? 4096 : 2560;
        int panoH = panoW / 2;

        using var panoBmp = new SKBitmap(panoW, panoH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(panoBmp);
        canvas.Clear(new SKColor(4, 7, 15));

        // 1. Collect solved panels to project onto the 360° equirectangular sphere
        var activePanels = new List<(WcsMatrix Wcs, SKBitmap Bmp, bool ShouldDispose, double Opacity)>();
        if (mosaicProject != null && mosaicProject.Count > 0)
        {
            foreach (var p in mosaicProject.Panels)
            {
                if (!p.IsVisible || p.Wcs == null || !p.IsSolved) continue;
                SKBitmap? sk = p.CachedSkiaBitmap as SKBitmap;
                bool disp = false;
                if (sk == null && !string.IsNullOrEmpty(p.FilePath) && File.Exists(p.FilePath))
                {
                    try { sk = ImagePreprocessor.LoadImage(p.FilePath); disp = true; } catch { }
                }
                if (sk != null)
                {
                    activePanels.Add((p.Wcs, sk, disp, p.Opacity));
                }
            }
        }
        else if (wcs != null && !string.IsNullOrEmpty(sourceImagePath) && File.Exists(sourceImagePath))
        {
            try
            {
                var sk = ImagePreprocessor.LoadImage(sourceImagePath);
                activePanels.Add((wcs, sk, true, 1.0));
            }
            catch { }
        }

        // Determine center RA so the active mosaic/photo sits near the horizontal center of the 360° panorama
        double centerRaDeg = wcs?.CenterCoordinates.RaDegrees ?? 180.0;
        if (activePanels.Count > 0)
        {
            centerRaDeg = activePanels[0].Wcs.CenterCoordinates.RaDegrees;
        }

        double RaToPanoX(double raDeg)
        {
            double relRa = ((centerRaDeg - raDeg + 540.0) % 360.0) - 180.0;
            return (0.5 + (relRa / 360.0)) * panoW;
        }

        double DecToPanoY(double decDeg)
        {
            return ((90.0 - Math.Clamp(decDeg, -90.0, 90.0)) / 180.0) * panoH;
        }

        try
        {
            // Project each photo onto its spherical bounding box in the equirectangular map
            foreach (var (pWcs, pBmp, _, pOpacity) in activePanels)
            {
                int srcW = pBmp.Width;
                int srcH = pBmp.Height;
                var c0 = pWcs.PixelToEquatorial(0, 0);
                var c1 = pWcs.PixelToEquatorial(srcW, 0);
                var c2 = pWcs.PixelToEquatorial(srcW, srcH);
                var c3 = pWcs.PixelToEquatorial(0, srcH);
                var cc = pWcs.CenterCoordinates;

                double spanDeg = Math.Max(pWcs.FovWidthDegrees, pWcs.FovHeightDegrees) * 0.85 + 2.0;
                int minY = Math.Max(0, (int)Math.Floor(DecToPanoY(Math.Min(90.0, cc.DecDegrees + spanDeg))));
                int maxY = Math.Min(panoH - 1, (int)Math.Ceiling(DecToPanoY(Math.Max(-90.0, cc.DecDegrees - spanDeg))));

                double cosDec = Math.Max(0.1, Math.Cos(cc.DecRadians));
                double raSpan = Math.Min(180.0, spanDeg / cosDec);
                int minX = Math.Max(0, (int)Math.Floor(RaToPanoX(cc.RaDegrees + raSpan)));
                int maxX = Math.Min(panoW - 1, (int)Math.Ceiling(RaToPanoX(cc.RaDegrees - raSpan)));
                if (minX > maxX) { minX = 0; maxX = panoW - 1; }

                byte alphaByte = (byte)Math.Clamp((int)(pOpacity * 255), 15, 255);

                for (int py = minY; py <= maxY; py++)
                {
                    double decDeg = 90.0 - ((py / (double)panoH) * 180.0);
                    for (int px = minX; px <= maxX; px++)
                    {
                        double relRa = (((px / (double)panoW) - 0.5) * 360.0);
                        double raDeg = ((centerRaDeg - relRa) % 360.0 + 360.0) % 360.0;

                        if (pWcs.EquatorialToPixel(raDeg, decDeg, out double ix, out double iy) &&
                            ix >= 0 && ix < srcW - 1 && iy >= 0 && iy < srcH - 1)
                        {
                            var col = pBmp.GetPixel((int)ix, (int)iy);
                            if (alphaByte < 255)
                            {
                                var dstCol = panoBmp.GetPixel(px, py);
                                byte r = (byte)(((col.Red * alphaByte) + (dstCol.Red * (255 - alphaByte))) / 255);
                                byte g = (byte)(((col.Green * alphaByte) + (dstCol.Green * (255 - alphaByte))) / 255);
                                byte b = (byte)(((col.Blue * alphaByte) + (dstCol.Blue * (255 - alphaByte))) / 255);
                                panoBmp.SetPixel(px, py, new SKColor(r, g, b, 255));
                            }
                            else
                            {
                                panoBmp.SetPixel(px, py, col);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            foreach (var item in activePanels)
            {
                if (item.ShouldDispose) item.Bmp.Dispose();
            }
        }

        // 2. Draw Hipparcos background stars across the 360° panorama
        using var starPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        var allBrightStars = HipparcosCatalog.QueryRegion(centerRaDeg, 0.0, 180.0, 6.0, maxResults: 3500);
        foreach (var s in allBrightStars)
        {
            float sx = (float)RaToPanoX(s.RaDeg);
            float sy = (float)DecToPanoY(s.DecDeg);
            float r = Math.Max(0.8f, (6.5f - s.Mag) * 0.55f * scaleFactor);
            starPaint.Color = s.Bv < 0.0f ? new SKColor(147, 197, 253, 210)
                            : s.Bv < 0.5f ? new SKColor(248, 250, 252, 210)
                            : new SKColor(253, 224, 71, 210);
            canvas.DrawCircle(sx, sy, r, starPaint);
        }

        // 3. Optional Spherical Grid on 360° Panorama
        if (config.IncludeSphericalGrid)
        {
            using var gridPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.0f * scaleFactor,
                Color = new SKColor(56, 189, 248, 70),
                IsAntialias = true
            };
            for (int ra = 0; ra < 360; ra += 30)
            {
                float gx = (float)RaToPanoX(ra);
                canvas.DrawLine(gx, 0, gx, panoH, gridPaint);
            }
            for (int dec = -60; dec <= 60; dec += 30)
            {
                float gy = (float)DecToPanoY(dec);
                canvas.DrawLine(0, gy, panoW, gy, gridPaint);
            }
        }

        // 4. Optional Constellations on 360° Panorama
        if (config.IncludeConstellations)
        {
            using var constPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.3f * scaleFactor,
                Color = new SKColor(56, 189, 248, 155),
                IsAntialias = true
            };
            foreach (var line in ConstellationCatalog.Lines)
            {
                float x1 = (float)RaToPanoX(line.Start.RaDegrees);
                float y1 = (float)DecToPanoY(line.Start.DecDegrees);
                float x2 = (float)RaToPanoX(line.End.RaDegrees);
                float y2 = (float)DecToPanoY(line.End.DecDegrees);
                if (Math.Abs(x1 - x2) < panoW * 0.45f)
                {
                    canvas.DrawLine(x1, y1, x2, y2, constPaint);
                }
            }
        }

        // 5. Optional Bounding Boxes on 360° Panorama
        if (config.IncludeBoundingBoxes)
        {
            using var boxPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f * scaleFactor,
                IsAntialias = true
            };
            foreach (var obj in objects)
            {
                double? ra = obj.RaDegrees;
                double? dec = obj.DecDegrees;
                if (!ra.HasValue && wcs != null)
                {
                    var eq = wcs.PixelToEquatorial(obj.CenterX, obj.CenterY);
                    ra = eq.RaDegrees;
                    dec = eq.DecDegrees;
                }
                if (!ra.HasValue || !dec.HasValue) continue;

                float sx = (float)RaToPanoX(ra.Value);
                float sy = (float)DecToPanoY(dec.Value);
                float sz = 6.0f * scaleFactor;
                boxPaint.Color = obj.Tier switch
                {
                    ConfidenceTier.Tier1_High => new SKColor(0, 230, 118),
                    ConfidenceTier.Tier2_Medium => new SKColor(255, 214, 0),
                    _ => new SKColor(255, 145, 0)
                };
                canvas.DrawRect(sx - (sz * 0.5f), sy - (sz * 0.5f), sz, sz, boxPaint);
            }
        }

        SaveBitmapToFile(panoBmp, destinationImagePath, config.Format);
    }

    private static void DrawOverlaysAndDetectionsOnCanvas(
        SKCanvas canvas,
        int targetW,
        int targetH,
        float scale,
        IEnumerable<AstrophotographyObject> objects,
        ExportConfig config,
        WcsMatrix? wcs,
        ScanMask? mask)
    {
        int longSide = Math.Max(targetW, targetH);
        float minMarker = Math.Max(10f * scale, longSide / 250f);
        float stroke = Math.Max(2f * scale, longSide / 1200f);

        if (mask != null)
        {
            DrawMask(canvas, mask, stroke);
        }

        if (config.IncludeSphericalGrid && wcs != null)
        {
            var gridLines = wcs.GenerateSphericalGrid();
            using var gridPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.2f * scale,
                Color = new SKColor(56, 189, 248, 120),
                IsAntialias = true
            };
            #pragma warning disable CS0618
            foreach (var line in gridLines)
            {
                if (line.Points.Length < 2) continue;
                using var p = new SKPath();
                p.MoveTo(line.Points[0].X * scale, line.Points[0].Y * scale);
                for (int i = 1; i < line.Points.Length; i++)
                {
                    p.LineTo(line.Points[i].X * scale, line.Points[i].Y * scale);
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
                StrokeWidth = 1.5f * scale,
                Color = new SKColor(56, 189, 248, 180),
                IsAntialias = true
            };
            foreach (var line in ConstellationCatalog.Lines)
            {
                if (!wcs.EquatorialToPixel(line.Start.RaDegrees, line.Start.DecDegrees, out double x1, out double y1)) continue;
                if (!wcs.EquatorialToPixel(line.End.RaDegrees, line.End.DecDegrees, out double x2, out double y2)) continue;
                canvas.DrawLine((float)x1 * scale, (float)y1 * scale, (float)x2 * scale, (float)y2 * scale, constLinePaint);
            }
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

        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 11f * scale);
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var textBgPaint = new SKPaint { Color = new SKColor(15, 23, 42, 220), Style = SKPaintStyle.Fill, IsAntialias = true };

        if (config.IncludeBoundingBoxes)
        {
            foreach (var obj in objects)
            {
                paint.Color = obj.Tier switch
                {
                    ConfidenceTier.Tier1_High => tier1Color,
                    ConfidenceTier.Tier2_Medium => tier2Color,
                    ConfidenceTier.Tier3_Low => tier3Color,
                    _ => tierNoiseColor
                };

                float size = Math.Max(Math.Max(obj.Box.Width, obj.Box.Height) * scale, minMarker);
                float cx = obj.CenterX * scale;
                float cy = obj.CenterY * scale;
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
    }

    private static void SaveBitmapToFile(SKBitmap bitmap, string destinationImagePath, string format)
    {
        string ext = Path.GetExtension(destinationImagePath).ToLowerInvariant();
        string fmt = format.ToUpperInvariant();

        if (ext is ".tif" or ".tiff" || fmt == "TIFF")
        {
            WriteUncompressedTiff(bitmap, destinationImagePath);
            return;
        }

        using var image = SKImage.FromBitmap(bitmap);
        if (ext is ".jpg" or ".jpeg" || fmt == "JPG")
        {
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
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
    /// Writes a true 24-bit RGB uncompressed baseline TIFF file directly so scientific TIFF exports never fail or write PNG headers.
    /// </summary>
    private static void WriteUncompressedTiff(SKBitmap bitmap, string filePath)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int rowBytesRgb = width * 3;
        int pixelDataSize = rowBytesRgb * height;

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        // Little-endian TIFF header
        bw.Write((byte)'I');
        bw.Write((byte)'I');
        bw.Write((ushort)42);
        bw.Write((uint)8); // IFD offset right after 8-byte header

        // 10 IFD entries (8 + 2 + 10*12 + 4 = 134 bytes; BitsPerSample array at 134..140; pixel data starts at 140)
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
        WriteTag(279, 4, 1, (uint)pixelDataSize);         // StripByteCounts
        WriteTag(284, 3, 1, 1);                           // PlanarConfiguration = Chunky

        bw.Write((uint)0); // Next IFD offset = 0

        // BitsPerSample values (3 x ushort = 6 bytes)
        bw.Write((ushort)8);
        bw.Write((ushort)8);
        bw.Write((ushort)8);

        byte[] rowBuffer = new byte[rowBytesRgb];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                int idx = x * 3;
                rowBuffer[idx] = c.Red;
                rowBuffer[idx + 1] = c.Green;
                rowBuffer[idx + 2] = c.Blue;
            }
            bw.Write(rowBuffer);
        }
    }

    private static void DrawMask(SKCanvas canvas, ScanMask mask, float stroke)
    {
        foreach (var region in mask.Regions)
        {
            bool exclude = region.Kind == RegionKind.Exclude;
            var baseColor = exclude ? new SKColor(220, 38, 38) : new SKColor(16, 185, 129);

            #pragma warning disable CS0618
            using var path = new SKPath();
            var pts = region.Points;
            path.MoveTo(pts[0].X, pts[0].Y);
            for (int i = 1; i < pts.Count; i++)
            {
                path.LineTo(pts[i].X, pts[i].Y);
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
