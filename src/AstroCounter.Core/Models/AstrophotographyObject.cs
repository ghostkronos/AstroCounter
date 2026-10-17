namespace AstroCounter.Core.Models;

/// <summary>
/// Represents a celestial body or astronomical feature detected in the image.
/// </summary>
public record AstrophotographyObject(
    int Id,
    BoundingBox Box,
    float Confidence,
    string ClassName = "star",
    float TileOffsetX = 0f,
    float TileOffsetY = 0f
)
{
    public ConfidenceTier Tier => ConfidenceTierExtensions.FromConfidence(Confidence);
    public string TierDisplayName => Tier.GetDisplayName();
    public string TierColorHex => Tier.GetColorHex();

    public float CenterX => Box.X + (Box.Width / 2f);
    public float CenterY => Box.Y + (Box.Height / 2f);
}

