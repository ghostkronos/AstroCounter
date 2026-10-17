namespace AstroCounter.Core.Export;

using System.Globalization;
using System.Text;
using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using SkiaSharp;

/// <summary>
/// Handles exporting detection metrics to CSV and annotated images using SkiaSharp.
/// </summary>
public static class ResultExporter
{
    /// <summary>
    /// Exports detected astronomical objects to a CSV file.
    /// </summary>
    public static void ExportToCsv(IEnumerable<AstrophotographyObject> objects, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,ClassName,Confidence,Tier,SNR_Sigma,X,Y,Width,Height,CenterX,CenterY");

        foreach (var obj in objects)
        {
            float snr = ConfidenceClassifier.EstimateEquivalentSnrSigma(obj.Confidence);
            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0},{1},{2:F4},{3},{4:F2},{5:F2},{6:F2},{7:F2},{8:F2},{9:F2},{10:F2}",
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
                obj.CenterY
            ));
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// Generates and saves an annotated version of the image with color-coded bounding boxes.
    /// Markers are never drawn smaller than a minimum size so faint stars stay visible,
    /// and the optional scan mask regions are outlined on the image.
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

        var tier1Color = new SKColor(0, 230, 118);  // Emerald / Neon Green
        var tier2Color = new SKColor(255, 214, 0);  // Amber Yellow
        var tier3Color = new SKColor(255, 145, 0);  // Orange
        var tierNoiseColor = new SKColor(239, 68, 68); // Red for speculative / filtered noise

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

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Open(destinationImagePath, FileMode.Create, FileAccess.Write);
        data.SaveTo(stream);
    }

    private static void DrawMask(SKCanvas canvas, ScanMask mask, float stroke)
    {
        foreach (var region in mask.Regions)
        {
            bool exclude = region.Kind == RegionKind.Exclude;
            var baseColor = exclude ? new SKColor(220, 38, 38) : new SKColor(16, 185, 129);

            using var path = new SKPath();
            var pts = region.Points;
            path.MoveTo(pts[0].X, pts[0].Y);
            for (int i = 1; i < pts.Count; i++)
            {
                path.LineTo(pts[i].X, pts[i].Y);
            }
            path.Close();

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
