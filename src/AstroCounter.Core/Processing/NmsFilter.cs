namespace AstroCounter.Core.Processing;

using AstroCounter.Core.Models;

/// <summary>
/// Performs Non-Maximum Suppression (NMS) on candidate detections to eliminate duplicate detections across overlapping tiles.
/// </summary>
public static class NmsFilter
{
    /// <summary>
    /// Applies Non-Maximum Suppression to a collection of detected objects.
    /// </summary>
    /// <param name="candidates">All candidate objects with global coordinates.</param>
    /// <param name="iouThreshold">IoU threshold above which overlapping boxes are suppressed (default ~0.45).</param>
    /// <param name="byClass">If true, suppress only within the same class; if false, suppress across any overlapping class.</param>
    /// <returns>Suppressed list of unique detected objects.</returns>
    public static List<AstrophotographyObject> ApplyNms(
        IEnumerable<AstrophotographyObject> candidates,
        float iouThreshold = 0.45f,
        bool byClass = false)
    {
        var result = new List<AstrophotographyObject>();

        if (byClass)
        {
            var groups = candidates.GroupBy(c => c.ClassName);
            foreach (var group in groups)
            {
                result.AddRange(SuppressionCore(group, iouThreshold));
            }
        }
        else
        {
            result.AddRange(SuppressionCore(candidates, iouThreshold));
        }

        // Re-index remaining objects sequentially starting at 1
        return result
            .OrderByDescending(o => o.Confidence)
            .Select((obj, idx) => obj with { Id = idx + 1 })
            .ToList();
    }

    private static List<AstrophotographyObject> SuppressionCore(
        IEnumerable<AstrophotographyObject> items,
        float iouThreshold)
    {
        var sorted = items.OrderByDescending(o => o.Confidence).ToList();
        var selected = new List<AstrophotographyObject>();

        while (sorted.Count > 0)
        {
            var current = sorted[0];
            selected.Add(current);
            sorted.RemoveAt(0);

            // Filter out candidates that overlap with the current highest-confidence box
            sorted.RemoveAll(candidate => current.Box.ComputeIoU(candidate.Box) >= iouThreshold);
        }

        return selected;
    }
}

