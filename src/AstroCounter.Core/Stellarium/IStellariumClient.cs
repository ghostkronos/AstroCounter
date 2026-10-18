namespace AstroCounter.Core.Stellarium;

using System;
using System.Threading;
using System.Threading.Tasks;
using AstroCounter.Core.Astrometry;

public interface IStellariumClient : IDisposable
{
    string ServerUrl { get; set; }
    bool IsConnected { get; }

    Task<bool> CheckConnectionAsync(CancellationToken cancellationToken = default);

    Task<StellariumStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<bool> FocusCoordinatesAsync(
        CelestialCoordinates coordinates,
        double? fovDegrees = null,
        double durationSeconds = 0.25,
        CancellationToken cancellationToken = default);

    Task<bool> SetFovAsync(double fovDegrees, CancellationToken cancellationToken = default);

    Task<bool> EnsureWindowedModeAsync(CancellationToken cancellationToken = default);

    Task<bool> RunScriptDirectAsync(string scriptCode, CancellationToken cancellationToken = default);

    Task<bool> ProjectSkyImageAsync(
        string imagePath,
        WcsMatrix wcs,
        string imageId = "astro_counter_layer",
        CancellationToken cancellationToken = default);

    Task<bool> RemoveSkyImageAsync(
        string imageId = "astro_counter_layer",
        CancellationToken cancellationToken = default);

    Task<CelestialCoordinates?> SearchObjectCoordinatesAsync(
        string objectName,
        CancellationToken cancellationToken = default);
}

