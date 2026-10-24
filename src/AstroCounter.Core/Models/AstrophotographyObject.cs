namespace AstroCounter.Core.Models;

/// <summary>
/// Represents a celestial body or astronomical feature detected in the image.
/// Supports both image-relative bounding box pixels and physical celestial coordinates (RA/Dec).
/// </summary>
public record AstrophotographyObject(
    int Id,
    BoundingBox Box,
    float Confidence,
    string ClassName = "star",
    float TileOffsetX = 0f,
    float TileOffsetY = 0f,
    float Snr = 0f,
    bool IsAnomaly = false,
    string? AnomalyType = null,
    double? RaDegrees = null,
    double? DecDegrees = null,
    string? PanelId = null
)
{
    public ConfidenceTier Tier => ConfidenceTierExtensions.FromConfidence(Confidence);
    public string TierDisplayName => Tier.GetDisplayName();
    public string TierColorHex => Tier.GetColorHex();

    public float CenterX => Box.X + (Box.Width / 2f);
    public float CenterY => Box.Y + (Box.Height / 2f);

    public bool HasCelestialCoordinates => RaDegrees.HasValue && DecDegrees.HasValue;

    public AstrophotographyObject WithCelestialCoordinates(double ra, double dec, string? panelId = null)
        => this with { RaDegrees = ra, DecDegrees = dec, PanelId = panelId ?? PanelId };
}
