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
    /// </summary>
    public static void ExportAnnotatedImage(
        string sourceImagePath,
        IEnumerable<AstrophotographyObject> objects,
        string destinationImagePath)
    {
        using var bitmap = SKBitmap.Decode(sourceImagePath);
        if (bitmap == null)
            throw new InvalidOperationException($"Görsel açılamadı: {sourceImagePath}");

        using var canvas = new SKCanvas(bitmap);

        var tier1Color = new SKColor(0, 230, 118);  // Emerald / Neon Green
        var tier2Color = new SKColor(255, 214, 0);  // Amber Yellow
        var tier3Color = new SKColor(255, 145, 0);  // Orange

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true
        };

        foreach (var obj in objects)
        {
            paint.Color = obj.Tier switch
            {
                ConfidenceTier.Tier1_High => tier1Color,
                ConfidenceTier.Tier2_Medium => tier2Color,
                _ => tier3Color
            };

            canvas.DrawRect(obj.Box.X, obj.Box.Y, obj.Box.Width, obj.Box.Height, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Open(destinationImagePath, FileMode.Create, FileAccess.Write);
        data.SaveTo(stream);
    }
}
