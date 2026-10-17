namespace AstroCounter.Core.Models;

using System.Numerics;

public enum RegionKind
{
    Include, // Yalnızca bu alanlar taranır
    Exclude  // Bu alan taranmaz (bina, ağaç, ufuk, yer ışıkları...)
}

/// <summary>
/// User-drawn polygon (rectangles are stored as 4-point polygons) in image pixel coordinates.
/// </summary>
public sealed class SelectionRegion
{
    private readonly Vector2[] _points;
    private readonly float _minX, _minY, _maxX, _maxY;

    public RegionKind Kind { get; }
    public IReadOnlyList<Vector2> Points => _points;

    public SelectionRegion(RegionKind kind, IEnumerable<Vector2> points)
    {
        Kind = kind;
        _points = points.ToArray();
        if (_points.Length < 3)
            throw new ArgumentException("Bir bölge için en az 3 nokta gerekir.", nameof(points));

        _minX = _points.Min(p => p.X);
        _maxX = _points.Max(p => p.X);
        _minY = _points.Min(p => p.Y);
        _maxY = _points.Max(p => p.Y);
    }

    /// <summary>Ray-casting point-in-polygon test.</summary>
    public bool Contains(float x, float y)
    {
        if (x < _minX || x > _maxX || y < _minY || y > _maxY) return false;

        bool inside = false;
        for (int i = 0, j = _points.Length - 1; i < _points.Length; j = i++)
        {
            var pi = _points[i];
            var pj = _points[j];
            if ((pi.Y > y) != (pj.Y > y) &&
                x < ((pj.X - pi.X) * (y - pi.Y) / (pj.Y - pi.Y)) + pi.X)
            {
                inside = !inside;
            }
        }
        return inside;
    }
}

/// <summary>
/// Set of include/exclude regions that defines which part of the frame is scanned.
/// Rules: any Exclude region always wins; if at least one Include region exists,
/// a point must lie inside one of them; with no regions the whole frame is valid.
/// </summary>
public sealed class ScanMask
{
    private readonly List<SelectionRegion> _regions = new();

    public IReadOnlyList<SelectionRegion> Regions => _regions;
    public bool IsEmpty => _regions.Count == 0;

    public void Add(SelectionRegion region) => _regions.Add(region);

    public bool RemoveLast()
    {
        if (_regions.Count == 0) return false;
        _regions.RemoveAt(_regions.Count - 1);
        return true;
    }

    public void Clear() => _regions.Clear();

    public bool IsAllowed(float x, float y)
    {
        bool hasInclude = false;
        bool insideInclude = false;

        foreach (var region in _regions)
        {
            if (region.Kind == RegionKind.Exclude)
            {
                if (region.Contains(x, y)) return false;
            }
            else
            {
                hasInclude = true;
                if (!insideInclude && region.Contains(x, y)) insideInclude = true;
            }
        }

        return !hasInclude || insideInclude;
    }
}
