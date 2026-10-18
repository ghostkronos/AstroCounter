namespace AstroCounter.Core.Stellarium;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AstroCounter.Core.Astrometry;

/// <summary>
/// Communicates with Stellarium's built-in RemoteControl HTTP API (default: http://localhost:8090).
/// Enables bidirectional camera orientation, FOV matching, and synchronized tracking.
/// </summary>
public sealed class StellariumClient : IStellariumClient
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private string _serverUrl;

    public string ServerUrl
    {
        get => _serverUrl;
        set => _serverUrl = value.TrimEnd('/');
    }

    public bool IsConnected { get; private set; }

    public StellariumClient(string serverUrl = "http://localhost:8090", HttpClient? httpClient = null)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(2.0)
            };
            _ownsHttpClient = true;
        }
    }

    public async Task<bool> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{_serverUrl}/api/main/status", cancellationToken);
            IsConnected = response.IsSuccessStatusCode;
            return IsConnected;
        }
        catch
        {
            IsConnected = false;
            return false;
        }
    }

    public async Task<StellariumStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{_serverUrl}/api/main/status?propId=-2", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                IsConnected = false;
                return StellariumStatus.Disconnected(_serverUrl, $"HTTP {(int)response.StatusCode} hatası.");
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            double fov = 45.0;
            if (root.TryGetProperty("view", out var viewEl) && viewEl.TryGetProperty("fov", out var fovEl))
            {
                fov = fovEl.GetDouble();
            }

            double latitudeDeg = 40.0;
            if (root.TryGetProperty("location", out var locEl) && locEl.TryGetProperty("latitude", out var latEl))
            {
                latitudeDeg = latEl.GetDouble();
            }

            bool isEquatorialMount = false;
            if (root.TryGetProperty("propertyChanges", out var propChangesEl) &&
                propChangesEl.TryGetProperty("changes", out var changesEl) &&
                changesEl.TryGetProperty("StelMovementMgr.equatorialMount", out var eqMountEl))
            {
                if (eqMountEl.ValueKind == JsonValueKind.True) isEquatorialMount = true;
                else if (eqMountEl.ValueKind == JsonValueKind.False) isEquatorialMount = false;
            }

            string? selectedObject = null;
            if (root.TryGetProperty("selectioninfo", out var selEl) && selEl.ValueKind == JsonValueKind.String)
            {
                string info = selEl.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    selectedObject = ExtractTitle(info);
                }
            }

            var (coords, skyRollAngleDeg) = await GetViewDetailsAsync(latitudeDeg, isEquatorialMount, cancellationToken);

            IsConnected = true;
            return StellariumStatus.Connected(coords, fov, selectedObject, _serverUrl, skyRollAngleDeg);
        }
        catch (Exception ex)
        {
            IsConnected = false;
            return StellariumStatus.Disconnected(_serverUrl, ex.Message);
        }
    }

    public async Task<CelestialCoordinates> GetViewCoordinatesAsync(CancellationToken cancellationToken = default)
    {
        var (coords, _) = await GetViewDetailsAsync(40.0, false, cancellationToken);
        return coords;
    }

    private async Task<(CelestialCoordinates Coords, double SkyRollAngleDegrees)> GetViewDetailsAsync(
        double latitudeDeg,
        bool isEquatorialMount,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{_serverUrl}/api/main/view", cancellationToken);
            if (!response.IsSuccessStatusCode) return (default, 0.0);

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            CelestialCoordinates coords = default;
            if (TryParseVector3(root, "j2000", out double jx, out double jy, out double jz))
            {
                double raRad = Math.Atan2(jy, jx);
                if (raRad < 0) raRad += 2.0 * Math.PI;
                double decRad = Math.Asin(Math.Clamp(jz, -1.0, 1.0));
                coords = CelestialCoordinates.FromRadians(raRad, decRad);
            }

            double skyRollAngleDeg = 0.0;
            if (!isEquatorialMount && TryParseVector3(root, "altAz", out double azX, out double azY, out double azZ))
            {
                // In Stellarium's altAz frame: +X is South, +Y is East, +Z is Zenith (Up on screen).
                // North Celestial Pole unit vector in altAz frame is (-cos(phi), 0, sin(phi)).
                double latRad = latitudeDeg * (Math.PI / 180.0);
                double cosPhi = Math.Cos(latRad);
                double sinPhi = Math.Sin(latRad);
                double horizSq = (azX * azX) + (azY * azY);

                double pRight = -cosPhi * azY;
                double pUp = (cosPhi * azX * azZ) + (sinPhi * horizSq);

                if (Math.Abs(pRight) > 1e-9 || Math.Abs(pUp) > 1e-9)
                {
                    skyRollAngleDeg = Math.Atan2(pRight, pUp) * (180.0 / Math.PI);
                    if (skyRollAngleDeg < 0) skyRollAngleDeg += 360.0;
                }
            }

            return (coords, skyRollAngleDeg);
        }
        catch
        {
            return (default, 0.0);
        }
    }

    private static bool TryParseVector3(JsonElement root, string propertyName, out double x, out double y, out double z)
    {
        x = 0;
        y = 0;
        z = 0;
        if (!root.TryGetProperty(propertyName, out var el)) return false;

        if (el.ValueKind == JsonValueKind.Array)
        {
            var arr = el.EnumerateArray();
            if (arr.MoveNext()) x = arr.Current.GetDouble();
            if (arr.MoveNext()) y = arr.Current.GetDouble();
            if (arr.MoveNext())
            {
                z = arr.Current.GetDouble();
                return true;
            }
        }
        else if (el.ValueKind == JsonValueKind.String)
        {
            string raw = (el.GetString() ?? "").Trim('[', ']', ' ');
            var parts = raw.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 3 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Commands Stellarium's camera to center onto celestial coordinates (RA, Dec in J2000 3D Cartesian vector).
    /// </summary>
    public async Task<bool> FocusCoordinatesAsync(
        CelestialCoordinates coordinates,
        double? fovDegrees = null,
        double durationSeconds = 0.25,
        CancellationToken cancellationToken = default)
    {
        try
        {
            double raRad = coordinates.RaRadians;
            double decRad = coordinates.DecRadians;

            // Convert spherical equatorial (RA, Dec) to 3D Cartesian unit vector [x, y, z] for J2000
            double cosDec = Math.Cos(decRad);
            double x = cosDec * Math.Cos(raRad);
            double y = cosDec * Math.Sin(raRad);
            double z = Math.Sin(decRad);

            string j2000Str = string.Format(CultureInfo.InvariantCulture, "[{0:F6},{1:F6},{2:F6}]", x, y, z);

            var parameters = new Dictionary<string, string>
            {
                { "j2000", j2000Str }
            };

            using var content = new FormUrlEncodedContent(parameters);
            using var response = await _httpClient.PostAsync($"{_serverUrl}/api/main/view", content, cancellationToken);

            IsConnected = response.IsSuccessStatusCode;

            if (fovDegrees.HasValue && fovDegrees.Value > 0)
            {
                await SetFovAsync(fovDegrees.Value, cancellationToken);
            }

            return IsConnected;
        }
        catch
        {
            IsConnected = false;
            return false;
        }
    }

    public async Task<bool> SetFovAsync(double fovDegrees, CancellationToken cancellationToken = default)
    {
        try
        {
            var parameters = new Dictionary<string, string>
            {
                { "fov", fovDegrees.ToString("F4", CultureInfo.InvariantCulture) }
            };

            using var content = new FormUrlEncodedContent(parameters);
            using var response = await _httpClient.PostAsync($"{_serverUrl}/api/main/fov", content, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Configures Stellarium for side-by-side 1:1 astrophotography comparison:
    /// Exits fullscreen, pauses time drift so stars stay still, and disables ground/daylight obstruction.
    /// </summary>
    public async Task<bool> EnsureWindowedModeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await SetStelPropertyAsync("MainView.flagFullScreen", "false", cancellationToken);
            await SetStelPropertyAsync("LandscapeMgr.atmosphereDisplayed", "false", cancellationToken);
            await SetStelPropertyAsync("LandscapeMgr.landscapeDisplayed", "false", cancellationToken);
            await SetStelPropertyAsync("LandscapeMgr.fogDisplayed", "false", cancellationToken);

            // Pause time rate so the sky does not drift away during side-by-side comparison
            using var timeContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "timerate", "0" }
            });
            _ = await _httpClient.PostAsync($"{_serverUrl}/api/main/time", timeContent, cancellationToken);

            // Clear any sticky object selection that would fight manual panning
            using var focusContent = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());
            _ = await _httpClient.PostAsync($"{_serverUrl}/api/main/focus", focusContent, cancellationToken);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> SetStelPropertyAsync(string id, string value, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "id", id },
                { "value", value }
            });
            using var response = await _httpClient.PostAsync($"{_serverUrl}/api/stelproperty/set", content, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractTitle(string htmlSnippet)
    {
        int h2Start = htmlSnippet.IndexOf("<h2>", StringComparison.OrdinalIgnoreCase);
        if (h2Start >= 0)
        {
            int contentStart = h2Start + 4;
            int h2End = htmlSnippet.IndexOf("</h2>", contentStart, StringComparison.OrdinalIgnoreCase);
            if (h2End > contentStart)
            {
                return htmlSnippet[contentStart..h2End].Trim();
            }
        }

        return htmlSnippet.Length > 40 ? htmlSnippet[..40] + "..." : htmlSnippet;
    }

    /// <summary>
    /// Executes JavaScript / SSC code directly in Stellarium via RemoteControl API.
    /// Tries /api/scripts/direct, then falls back to user script directory execution.
    /// </summary>
    public async Task<bool> RunScriptDirectAsync(string scriptCode, CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. Try direct script execution endpoint: POST /api/scripts/direct
            var parameters = new Dictionary<string, string>
            {
                { "code", scriptCode }
            };

            using var content = new FormUrlEncodedContent(parameters);
            var response = await _httpClient.PostAsync($"{_serverUrl}/api/scripts/direct", content, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            // 2. Fallback: Write script to user's Stellarium scripts folder and run via /api/scripts/run?id=astro_temp.ssc
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string userScriptsDir = System.IO.Path.Combine(appData, "Stellarium", "scripts");
            System.IO.Directory.CreateDirectory(userScriptsDir);
            string scriptPath = System.IO.Path.Combine(userScriptsDir, "astro_temp.ssc");
            await System.IO.File.WriteAllTextAsync(scriptPath, scriptCode, cancellationToken);

            var runParams = new Dictionary<string, string>
            {
                { "id", "astro_temp.ssc" }
            };
            using var runContent = new FormUrlEncodedContent(runParams);
            var runResponse = await _httpClient.PostAsync($"{_serverUrl}/api/scripts/run", runContent, cancellationToken);
            return runResponse.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Projects an astrophotography image directly into Stellarium's 3D celestial sphere,
    /// warped and 1-to-1 aligned with the spherical grid and real stars using core.loadSkyImage.
    /// </summary>
    public async Task<bool> ProjectSkyImageAsync(
        string imagePath,
        WcsMatrix wcs,
        string imageId = "astro_counter_layer",
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Normalize path for Stellarium script (forward slashes)
            string normalizedPath = imagePath.Replace('\\', '/');

            // Calculate exact 4 spherical corners in counter-clockwise order:
            // [0] Top-Left, [1] Bottom-Left, [2] Bottom-Right, [3] Top-Right
            var (tl, bl, br, tr) = wcs.GetCornerCoordinates();

            double centerRa = wcs.CenterCoordinates.RaDegrees;
            double centerDec = wcs.CenterCoordinates.DecDegrees;
            double fov = Math.Max(wcs.FovWidthDegrees, wcs.FovHeightDegrees);

            // Script: remove existing layer, load warped 4-corner sky image, slew to coordinates, set FOV
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"core.removeSkyImage('{imageId}');");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "core.loadSkyImage('{0}', '{1}', {2:F5}, {3:F5}, {4:F5}, {5:F5}, {6:F5}, {7:F5}, {8:F5}, {9:F5}, 0.05, 18, true);",
                imageId, normalizedPath,
                tl.RaDegrees, tl.DecDegrees,
                bl.RaDegrees, bl.DecDegrees,
                br.RaDegrees, br.DecDegrees,
                tr.RaDegrees, tr.DecDegrees));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "core.moveToRaDec({0:F5}, {1:F5});", centerRa, centerDec));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "StelMovementMgr.zoomTo({0:F3}, 1);", fov * 1.5));

            bool success = await RunScriptDirectAsync(sb.ToString(), cancellationToken);
            return success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Removes a previously projected image from Stellarium's sky sphere.
    /// </summary>
    public async Task<bool> RemoveSkyImageAsync(
        string imageId = "astro_counter_layer",
        CancellationToken cancellationToken = default)
    {
        string script = $"core.removeSkyImage('{imageId}');";
        return await RunScriptDirectAsync(script, cancellationToken);
    }

    /// <summary>
    /// Queries Stellarium's celestial object database for coordinates by name (e.g. "M31", "Vega", "Orion").
    /// </summary>
    public async Task<CelestialCoordinates?> SearchObjectCoordinatesAsync(
        string objectName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                $"{_serverUrl}/api/objects/info?name={Uri.EscapeDataString(objectName)}&format=json",
                cancellationToken);

            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check if coordinates exist in JSON (ra/dec in degrees or hour angle)
            double raDeg = 0, decDeg = 0;
            bool found = false;

            if (root.TryGetProperty("raJ2000", out var raJEl) && root.TryGetProperty("decJ2000", out var decJEl))
            {
                raDeg = raJEl.GetDouble();
                decDeg = decJEl.GetDouble();
                found = true;
            }
            else if (root.TryGetProperty("ra", out var raEl) && root.TryGetProperty("dec", out var decEl))
            {
                raDeg = raEl.GetDouble();
                decDeg = decEl.GetDouble();
                found = true;
            }
            else if (root.TryGetProperty("j2000", out var j2000El) && j2000El.ValueKind == JsonValueKind.Array)
            {
                var arr = j2000El.EnumerateArray();
                if (arr.MoveNext())
                {
                    double x = arr.Current.GetDouble();
                    if (arr.MoveNext())
                    {
                        double y = arr.Current.GetDouble();
                        if (arr.MoveNext())
                        {
                            double z = arr.Current.GetDouble();
                            double raRad = Math.Atan2(y, x);
                            if (raRad < 0) raRad += 2.0 * Math.PI;
                            double decRad = Math.Asin(Math.Clamp(z, -1.0, 1.0));
                            return CelestialCoordinates.FromRadians(raRad, decRad);
                        }
                    }
                }
            }

            if (found)
            {
                return new CelestialCoordinates(raDeg, decDeg);
            }
        }
        catch
        {
            // Silently fall back
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

