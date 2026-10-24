namespace AstroCounter.Core.Models;

using System.Collections.Generic;
using System.Linq;

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
    public float RecommendedConfidence { get; }

    public DetectionResult(
        IEnumerable<AstrophotographyObject> allObjects,
        int imageWidth,
        int imageHeight,
        int tileCount,
        TimeSpan elapsedTime,
        string executionProvider,
        float recommendedConfidence = 0.25f)
    {
        AllObjects = allObjects.OrderByDescending(o => o.Confidence).ToList();
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        TileCount = tileCount;
        ElapsedTime = elapsedTime;
        ExecutionProvider = executionProvider;
        RecommendedConfidence = recommendedConfidence;
    }

    /// <summary>
    /// Filters detected objects by minimum confidence (and optionally by the user's scan mask
    /// and per-panel confidence thresholds) in memory without re-inference.
    /// </summary>
    public FilteredResult Filter(
        float minConfidence,
        ScanMask? mask = null,
        IReadOnlyDictionary<string, float>? panelConfidenceThresholds = null)
    {
        bool useMask = mask is { IsEmpty: false };
        bool usePanelThresholds = panelConfidenceThresholds != null && panelConfidenceThresholds.Count > 0;
        var filtered = AllObjects
            .Where(o =>
            {
                float threshold = minConfidence;
                if (usePanelThresholds && !string.IsNullOrEmpty(o.PanelId) &&
                    panelConfidenceThresholds!.TryGetValue(o.PanelId, out float pThresh))
                {
                    threshold = pThresh;
                }
                return o.Confidence >= threshold && (!useMask || mask!.IsAllowed(o.CenterX, o.CenterY));
            })
            .ToList();
        int tier1 = filtered.Count(o => o.Tier == ConfidenceTier.Tier1_High);
        int tier2 = filtered.Count(o => o.Tier == ConfidenceTier.Tier2_Medium);
        int tier3 = filtered.Count(o => o.Tier == ConfidenceTier.Tier3_Low);
        int speculativeNoise = filtered.Count(o => o.Tier == ConfidenceTier.FilteredNoise);

        return new FilteredResult(
            FilteredObjects: filtered,
            TotalCount: filtered.Count,
            Tier1Count: tier1,
            Tier2Count: tier2,
            Tier3Count: tier3,
            NoiseCount: speculativeNoise,
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
