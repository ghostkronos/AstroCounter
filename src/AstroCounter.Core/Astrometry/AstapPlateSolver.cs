namespace AstroCounter.Core.Astrometry;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AstroCounter.Core.Models;

/// <summary>
/// Plate solver wrapper for ASTAP (Astrometric STAck Program), the de facto offline astrometry standard.
/// Uses the local astap.exe CLI and Star Database (D50/H18).
/// </summary>
public class AstapPlateSolver : IPlateSolver
{
    public string SolverName => "ASTAP (Yerel Çevrimdışı Motor)";

    private readonly string? _customAstapExecutable;

    public AstapPlateSolver(string? customAstapExecutable = null)
    {
        _customAstapExecutable = customAstapExecutable;
    }

    public static string? FindAstapExecutable(string? customPath = null)
    {
        if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
            return customPath;

        string[] standardLocations =
        {
            @"C:\Program Files\astap\astap.exe",
            @"C:\Program Files (x86)\astap\astap.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "astap", "astap.exe")
        };

        foreach (var path in standardLocations)
        {
            if (File.Exists(path)) return path;
        }

        return null;
    }

    public async Task<PlateSolvingResult> SolveAsync(
        string imagePath,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<AstrophotographyObject>? detectedStars = null,
        CancellationToken cancellationToken = default,
        CelestialCoordinates? hintCoordinates = null)
    {
        var sw = Stopwatch.StartNew();
        string? exe = FindAstapExecutable(_customAstapExecutable);
        if (exe == null)
        {
            return PlateSolvingResult.Failed(
                "ASTAP bulunamadı. Lütfen https://www.hnsky.org/astap.htm adresinden ASTAP ve H18 yıldız veritabanını kurun.",
                sw.Elapsed);
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "AstroCounter_Astap_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string tempImage = Path.Combine(tempDir, "solve_target" + Path.GetExtension(imagePath));
            File.Copy(imagePath, tempImage, true);

            // ASTAP CLI arguments:
            // If hintCoordinates is provided, search within 15 deg radius of hint, otherwise -z 0 (blind solve)
            string hintArgs = hintCoordinates.HasValue
                ? $"-ra {hintCoordinates.Value.RaDegrees / 15.0:F4} -spd {hintCoordinates.Value.DecDegrees + 90.0:F4} -r 15"
                : "-z 0";

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"-f \"{tempImage}\" {hintArgs}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return PlateSolvingResult.Failed("ASTAP işlemi başlatılamadı.", sw.Elapsed);
            }

            await process.WaitForExitAsync(cancellationToken);

            string wcsFile = Path.ChangeExtension(tempImage, ".wcs");
            string iniFile = Path.ChangeExtension(tempImage, ".ini");

            if (!File.Exists(wcsFile) && !File.Exists(iniFile))
            {
                return PlateSolvingResult.Failed("ASTAP gökyüzü çözümünü bulamadı (yıldız deseni eşleşmedi).", sw.Elapsed);
            }

            // Parse WCS header output
            var wcs = ParseWcsFile(File.Exists(wcsFile) ? wcsFile : iniFile, imageWidth, imageHeight);
            sw.Stop();

            if (wcs != null)
            {
                return PlateSolvingResult.Success(wcs, "Astrometrik Alan", sw.Elapsed, detectedStars?.Count ?? 0);
            }

            return PlateSolvingResult.Failed("WCS koordinat çıktısı okunamadı.", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return PlateSolvingResult.Failed($"ASTAP çalıştırma hatası: {ex.Message}", sw.Elapsed);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
        }
    }

    private static WcsMatrix? ParseWcsFile(string path, int width, int height)
    {
        double crval1 = 0, crval2 = 0;
        double crpix1 = width / 2.0, crpix2 = height / 2.0;
        double cdelt = 0;
        double rot = 0;

        foreach (var line in File.ReadAllLines(path))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("CRVAL1", StringComparison.OrdinalIgnoreCase))
                crval1 = ParseValue(trimmed);
            else if (trimmed.StartsWith("CRVAL2", StringComparison.OrdinalIgnoreCase))
                crval2 = ParseValue(trimmed);
            else if (trimmed.StartsWith("CRPIX1", StringComparison.OrdinalIgnoreCase))
                crpix1 = ParseValue(trimmed);
            else if (trimmed.StartsWith("CRPIX2", StringComparison.OrdinalIgnoreCase))
                crpix2 = ParseValue(trimmed);
            else if (trimmed.StartsWith("CDELT1", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("CDELT2", StringComparison.OrdinalIgnoreCase))
                cdelt = Math.Abs(ParseValue(trimmed));
            else if (trimmed.StartsWith("CROTA2", StringComparison.OrdinalIgnoreCase))
                rot = ParseValue(trimmed);
        }

        if (crval1 == 0 && crval2 == 0) return null;

        double pixelScaleArcsec = cdelt > 0 ? cdelt * 3600.0 : 1.5;
        return new WcsMatrix(new CelestialCoordinates(crval1, crval2), crpix1, crpix2, pixelScaleArcsec, rot, width, height);
    }

    private static double ParseValue(string line)
    {
        int eq = line.IndexOf('=');
        if (eq < 0) return 0;
        int slash = line.IndexOf('/', eq);
        string valStr = (slash > eq ? line[(eq + 1)..slash] : line[(eq + 1)..]).Trim();
        double.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double val);
        return val;
    }
}

