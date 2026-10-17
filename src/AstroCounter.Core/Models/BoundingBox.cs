namespace AstroCounter.Core.Models;

/// <summary>
/// Represents a bounding box in 2D image coordinates (pixels).
/// </summary>
public readonly record struct BoundingBox(float X, float Y, float Width, float Height)
{
    public float Left => X;
    public float Top => Y;
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public float Area => Math.Max(0, Width) * Math.Max(0, Height);

    /// <summary>
    /// Calculates the Intersection over Union (IoU) with another bounding box.
    /// </summary>
    public float ComputeIoU(BoundingBox other)
    {
        float x1 = Math.Max(Left, other.Left);
        float y1 = Math.Max(Top, other.Top);
        float x2 = Math.Min(Right, other.Right);
        float y2 = Math.Min(Bottom, other.Bottom);

        float intersectionWidth = Math.Max(0, x2 - x1);
        float intersectionHeight = Math.Max(0, y2 - y1);
        float intersectionArea = intersectionWidth * intersectionHeight;

        if (intersectionArea <= 0) return 0f;

        float unionArea = Area + other.Area - intersectionArea;
        return unionArea > 0 ? intersectionArea / unionArea : 0f;
    }
}
