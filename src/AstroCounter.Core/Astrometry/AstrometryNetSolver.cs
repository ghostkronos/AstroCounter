namespace AstroCounter.Core.Astrometry;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AstroCounter.Core.Models;

/// <summary>
/// Plate solver using nova.astrometry.net blind astrometry API.
/// Identifies any astrophotography image from star centroids anywhere on the celestial sphere.
/// </summary>
public sealed class AstrometryNetSolver : IPlateSolver, IDisposable
{
    public string SolverName => "Astrometry.net Kör Yıldız Çözücü";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private string? _apiKey;

    public string? ApiKey
    {
        get => _apiKey;
        set => _apiKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public AstrometryNetSolver(string? apiKey = null, HttpClient? httpClient = null)
    {
        _apiKey = apiKey;
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(60.0)
            };
            _ownsHttpClient = true;
        }
    }

    public async Task<PlateSolvingResult> SolveAsync(
        string imagePath,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<AstrophotographyObject>? detectedStars = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        if (!File.Exists(imagePath))
        {
            return PlateSolvingResult.Failed($"Görsel dosyası bulunamadı: {imagePath}", sw.Elapsed);
        }

        try
        {
            // 1. Authenticate if an API key is provided
            string? sessionKey = null;
            if (!string.IsNullOrEmpty(_apiKey))
            {
                sessionKey = await LoginAsync(_apiKey, cancellationToken);
            }

            // 2. Upload image to Astrometry.net
            using var fileStream = File.OpenRead(imagePath);
            using var content = new MultipartFormDataContent();

            var reqJsonObj = new
            {
                allow_commercial_use = "d",
                allow_modifications = "d",
                publicly_visible = "y",
                session = sessionKey
            };
            string reqJsonStr = JsonSerializer.Serialize(reqJsonObj);
            content.Add(new StringContent(reqJsonStr), "request-json");
            content.Add(new StreamContent(fileStream), "file", Path.GetFileName(imagePath));

            using var uploadResponse = await _httpClient.PostAsync(
                "https://nova.astrometry.net/api/upload",
                content,
                cancellationToken);

            if (!uploadResponse.IsSuccessStatusCode)
            {
                return PlateSolvingResult.Failed($"Astrometry.net yükleme hatası: HTTP {(int)uploadResponse.StatusCode}", sw.Elapsed);
            }

            string uploadJson = await uploadResponse.Content.ReadAsStringAsync(cancellationToken);
            using var uploadDoc = JsonDocument.Parse(uploadJson);
            if (!uploadDoc.RootElement.TryGetProperty("subid", out var subIdEl))
            {
                string msg = uploadDoc.RootElement.TryGetProperty("errormessage", out var errEl)
                    ? errEl.GetString() ?? "Bilinmeyen hata"
                    : "Submission ID alınamadı.";
                return PlateSolvingResult.Failed($"Astrometry.net: {msg}", sw.Elapsed);
            }

            long subId = subIdEl.GetInt64();

            // 3. Poll submission status until job is created (up to 45 seconds)
            long? jobId = null;
            var pollDeadline = DateTime.UtcNow.AddSeconds(45.0);

            while (DateTime.UtcNow < pollDeadline && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(2000, cancellationToken);

                using var subResponse = await _httpClient.GetAsync(
                    $"https://nova.astrometry.net/api/submissions/{subId}",
                    cancellationToken);

                if (!subResponse.IsSuccessStatusCode) continue;

                string subJson = await subResponse.Content.ReadAsStringAsync(cancellationToken);
                using var subDoc = JsonDocument.Parse(subJson);
                var root = subDoc.RootElement;

                if (root.TryGetProperty("jobs", out var jobsEl) &&
                    jobsEl.ValueKind == JsonValueKind.Array &&
                    jobsEl.GetArrayLength() > 0)
                {
                    jobId = jobsEl[0].GetInt64();
                    break;
                }
            }

            if (!jobId.HasValue)
            {
                return PlateSolvingResult.Failed("Astrometry.net çözüm kuyruğu zaman aşımına uğradı veya sıra bekleniyor.", sw.Elapsed);
            }

            // 4. Poll job calibration (up to 45 seconds)
            var jobDeadline = DateTime.UtcNow.AddSeconds(45.0);
            while (DateTime.UtcNow < jobDeadline && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(2500, cancellationToken);

                using var calResponse = await _httpClient.GetAsync(
                    $"https://nova.astrometry.net/api/jobs/{jobId.Value}/calibration/",
                    cancellationToken);

                if (!calResponse.IsSuccessStatusCode) continue;

                string calJson = await calResponse.Content.ReadAsStringAsync(cancellationToken);
                using var calDoc = JsonDocument.Parse(calJson);
                var root = calDoc.RootElement;

                if (root.TryGetProperty("ra", out var raEl) &&
                    root.TryGetProperty("dec", out var decEl) &&
                    root.TryGetProperty("pixscale", out var scaleEl))
                {
                    double ra = raEl.GetDouble();
                    double dec = decEl.GetDouble();
                    double pixscale = scaleEl.GetDouble();
                    double orientation = root.TryGetProperty("orientation", out var oriEl) ? oriEl.GetDouble() : 0.0;

                    var wcs = new WcsMatrix(
                        new CelestialCoordinates(ra, dec),
                        imageWidth / 2.0,
                        imageHeight / 2.0,
                        pixscale,
                        orientation,
                        imageWidth,
                        imageHeight);

                    return PlateSolvingResult.Success(
                        wcs,
                        $"Astrometry.net (RA: {ra:F2}°, Dec: {dec:F2}°)",
                        sw.Elapsed,
                        detectedStars?.Count ?? 50,
                        "Astrometry.net ile gökyüzü konumu başarıyla tespit edildi.");
                }
            }

            return PlateSolvingResult.Failed("Astrometry.net yıldız çözümleme işlemi tamamlanamadı.", sw.Elapsed);
        }
        catch (Exception ex)
        {
            return PlateSolvingResult.Failed($"Astrometry.net istisna hatası: {ex.Message}", sw.Elapsed);
        }
    }

    private async Task<string?> LoginAsync(string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            var reqObj = new { apikey = apiKey };
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("request-json", JsonSerializer.Serialize(reqObj))
            });

            using var resp = await _httpClient.PostAsync("https://nova.astrometry.net/api/login", content, cancellationToken);
            if (!resp.IsSuccessStatusCode) return null;

            string json = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("session", out var sessionEl))
            {
                return sessionEl.GetString();
            }
        }
        catch
        {
            // Silently fall back to anonymous upload
        }

        return null;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
