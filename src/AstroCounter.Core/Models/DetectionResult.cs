namespace AstroCounter.Core.Models;

/// <summary>
/// Contains the complete output of an astrophotography inference run.
/// </summary>
public class DetectionResult
{
    public IReadOnlyList<AstrophotographyObject> AllObjects { get; }
    public int ImageWidth { get; }
    public int ImageHeight { get; }
    public int TileCount { get; }
    public TimeSpan ElapsedTime { get; }
    public string ExecutionProvider { get; }

    public DetectionResult(
        IEnumerable<AstrophotographyObject> allObjects,
        int imageWidth,
        int imageHeight,
        int tileCount,
        TimeSpan elapsedTime,
        string executionProvider)
    {
        AllObjects = allObjects.OrderByDescending(o => o.Confidence).ToList();
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        TileCount = tileCount;
        ElapsedTime = elapsedTime;
        ExecutionProvider = executionProvider;
    }

    /// <summary>
    /// Filters detected objects by minimum confidence in memory without re-inference.
    /// </summary>
    public FilteredResult Filter(float minConfidence)
    {
        var filtered = AllObjects.Where(o => o.Confidence >= minConfidence).ToList();
        int tier1 = filtered.Count(o => o.Tier == ConfidenceTier.Tier1_High);
        int tier2 = filtered.Count(o => o.Tier == ConfidenceTier.Tier2_Medium);
        int tier3 = filtered.Count(o => o.Tier == ConfidenceTier.Tier3_Low);

        return new FilteredResult(
            FilteredObjects: filtered,
            TotalCount: filtered.Count,
            Tier1Count: tier1,
            Tier2Count: tier2,
            Tier3Count: tier3,
            NoiseCount: AllObjects.Count - filtered.Count,
            MinConfidence: minConfidence
        );
    }
}

public record FilteredResult(
    IReadOnlyList<AstrophotographyObject> FilteredObjects,
    int TotalCount,
    int Tier1Count,
    int Tier2Count,
    int Tier3Count,
    int NoiseCount,
    float MinConfidence
);
