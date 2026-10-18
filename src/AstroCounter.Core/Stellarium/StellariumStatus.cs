namespace AstroCounter.Core.Stellarium;

using AstroCounter.Core.Astrometry;

public record StellariumStatus(
    bool IsConnected,
    CelestialCoordinates CurrentCoordinates,
    double CurrentFovDegrees,
    string? SelectedObjectName,
    string ServerUrl,
    string Message,
    double SkyRollAngleDegrees = 0.0
)
{
    public static StellariumStatus Disconnected(string serverUrl, string message = "Stellarium'a bağlanılamadı.")
        => new(false, default, 0, null, serverUrl, message, 0.0);

    public static StellariumStatus Connected(
        CelestialCoordinates coordinates,
        double fov,
        string? selectedObject,
        string serverUrl,
        double skyRollAngleDegrees = 0.0)
        => new(true, coordinates, fov, selectedObject, serverUrl, "Stellarium bağlantısı aktif.", skyRollAngleDegrees);
}

