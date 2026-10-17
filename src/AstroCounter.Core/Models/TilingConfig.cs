namespace AstroCounter.Core.Models;

/// <summary>
/// Configuration parameters for SAHI (Slicing Aided Hyper Inference) and detection.
/// </summary>
public record TilingConfig(
    int TileWidth = 640,
    int TileHeight = 640,
    float OverlapRatio = 0.20f,
    float NmsIouThreshold = 0.45f,
    float DefaultConfidenceThreshold = 0.25f,
    float MinConfidence = 0.02f
)
{
    public int StepX => Math.Max(1, (int)(TileWidth * (1.0f - OverlapRatio)));
    public int StepY => Math.Max(1, (int)(TileHeight * (1.0f - OverlapRatio)));
}

