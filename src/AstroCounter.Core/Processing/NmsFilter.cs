namespace AstroCounter.Core.Processing;

using AstroCounter.Core.Models;

/// <summary>
/// Performs Non-Maximum Suppression (NMS) on candidate detections to eliminate duplicate detections across overlapping tiles.
/// </summary>
public static class NmsFilter
{
    private const float CellSize = 64f;

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

    /// <summary>
    /// Greedy NMS accelerated with a spatial hash: a candidate is only compared against
    /// already selected boxes whose centres are close enough to possibly overlap (O(n) instead of O(n^2)).
    /// </summary>
    private static List<AstrophotographyObject> SuppressionCore(
        IEnumerable<AstrophotographyObject> items,
        float iouThreshold)
    {
        var sorted = items.OrderByDescending(o => o.Confidence).ToList();
        var selected = new List<AstrophotographyObject>(sorted.Count);
        var grid = new Dictionary<long, List<AstrophotographyObject>>();
        float maxSelectedExtent = 0f;

        foreach (var candidate in sorted)
        {
            float candidateExtent = Math.Max(candidate.Box.Width, candidate.Box.Height);
            // Two boxes can only overlap if their centres are closer than half the sum of their extents.
            float reach = ((maxSelectedExtent + candidateExtent) * 0.5f) + 1f;

            if (IsSuppressed(candidate, reach, grid, iouThreshold))
                continue;

            selected.Add(candidate);
            long key = CellKey(CellIndex(candidate.CenterX), CellIndex(candidate.CenterY));
            if (!grid.TryGetValue(key, out var bucket))
            {
                bucket = new List<AstrophotographyObject>();
                grid[key] = bucket;
            }
            bucket.Add(candidate);
            maxSelectedExtent = Math.Max(maxSelectedExtent, candidateExtent);
        }

        return selected;
    }

    private static bool IsSuppressed(
        AstrophotographyObject candidate,
        float reach,
        Dictionary<long, List<AstrophotographyObject>> grid,
        float iouThreshold)
    {
        int x0 = CellIndex(candidate.CenterX - reach);
        int x1 = CellIndex(candidate.CenterX + reach);
        int y0 = CellIndex(candidate.CenterY - reach);
        int y1 = CellIndex(candidate.CenterY + reach);

        for (int cy = y0; cy <= y1; cy++)
        {
            for (int cx = x0; cx <= x1; cx++)
            {
                if (!grid.TryGetValue(CellKey(cx, cy), out var bucket)) continue;

                foreach (var other in bucket)
                {
                    if (other.Box.ComputeIoU(candidate.Box) >= iouThreshold)
                        return true;
                }
            }
        }

        return false;
    }

    private static int CellIndex(float coordinate) => (int)MathF.Floor(coordinate / CellSize);

    private static long CellKey(int cx, int cy) => ((long)cx << 32) | (uint)cy;
}
