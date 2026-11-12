namespace AstroCounter.Core.Astrometry;

using System;
using System.Buffers;
using System.Collections.Generic;

/// <summary>
/// High-performance 2D spherical spatial hash grid indexing celestial objects into 1x1 degree bins.
/// Supports RA wrap-around (e.g., 359° to 1°), polar cap convergence routing (|Dec| > 80°),
/// and zero-allocation bounded queries.
/// </summary>
public sealed class SpatialHashIndex<T> where T : struct
{
    public const int CellSizeDeg = 1;
    public const double PolarCapThresholdDec = 80.0;

    // Keys are encoded as: (raBin << 9) | decBin
    // raBin in [0..359] (9 bits), decBin in [0..179] (8 bits)
    // Dedicated polar cap bins:
    private const int NorthPolarKey = (360 << 9) | 0;
    private const int SouthPolarKey = (360 << 9) | 1;

    private readonly Dictionary<int, List<T>> _buckets;

    public int TotalBuckets => _buckets.Count;

    public SpatialHashIndex(int initialCapacity = 4096)
    {
        _buckets = new Dictionary<int, List<T>>(initialCapacity);
    }

    /// <summary>
    /// Computes the unique spatial hash bin key for celestial coordinates (RA, Dec).
    /// Polar cap regions (|Dec| > 80°) are routed to dedicated polar convergence buckets.
    /// </summary>
    public static int ComputeKey(double ra, double dec)
    {
        if (dec > PolarCapThresholdDec) return NorthPolarKey;
        if (dec < -PolarCapThresholdDec) return SouthPolarKey;

        int raBin = (int)Math.Floor(((ra % 360.0) + 360.0) % 360.0);
        int decBin = (int)Math.Floor(Math.Clamp(dec, -90.0, 89.999) + 90.0);
        return (raBin << 9) | decBin;
    }

    /// <summary>
    /// Inserts an item at celestial coordinates (ra, dec).
    /// </summary>
    public void Insert(double ra, double dec, in T item)
    {
        int key = ComputeKey(ra, dec);
        if (!_buckets.TryGetValue(key, out var list))
        {
            list = new List<T>(16);
            _buckets[key] = list;
        }
        list.Add(item);
    }

    /// <summary>
    /// Queries all items falling into hash cells that intersect the given celestial bounding box [raMin..raMax, decMin..decMax].
    /// Automatically handles RA wrap-around (when raMin > raMax) and polar caps.
    /// Fills the destination span and returns the number of items retrieved.
    /// </summary>
    public int QueryBoundingBox(double raMin, double raMax, double decMin, double decMax, Span<T> destination)
    {
        int written = 0;
        int maxDest = destination.Length;

        // 1. Polar cap checks
        if (decMax > PolarCapThresholdDec && written < maxDest)
        {
            if (_buckets.TryGetValue(NorthPolarKey, out var northList))
            {
                for (int i = 0; i < northList.Count && written < maxDest; i++)
                {
                    destination[written++] = northList[i];
                }
            }
        }

        if (decMin < -PolarCapThresholdDec && written < maxDest)
        {
            if (_buckets.TryGetValue(SouthPolarKey, out var southList))
            {
                for (int i = 0; i < southList.Count && written < maxDest; i++)
                {
                    destination[written++] = southList[i];
                }
            }
        }

        // 2. Mid-latitude cells
        double effDecMin = Math.Max(-PolarCapThresholdDec, decMin);
        double effDecMax = Math.Min(PolarCapThresholdDec, decMax);

        if (effDecMin <= effDecMax)
        {
            int dStart = (int)Math.Floor(effDecMin + 90.0);
            int dEnd = (int)Math.Floor(effDecMax + 90.0);

            double normRaMin = ((raMin % 360.0) + 360.0) % 360.0;
            double normRaMax = ((raMax % 360.0) + 360.0) % 360.0;

            if (normRaMin <= normRaMax)
            {
                int rStart = (int)Math.Floor(normRaMin);
                int rEnd = (int)Math.Floor(normRaMax);
                written = QueryRangeSpan(rStart, rEnd, dStart, dEnd, destination, written);
            }
            else
            {
                // RA wrap-around across 0h / 360° boundary
                int rStart1 = (int)Math.Floor(normRaMin);
                int rEnd1 = 359;
                written = QueryRangeSpan(rStart1, rEnd1, dStart, dEnd, destination, written);

                int rStart2 = 0;
                int rEnd2 = (int)Math.Floor(normRaMax);
                written = QueryRangeSpan(rStart2, rEnd2, dStart, dEnd, destination, written);
            }
        }

        return written;
    }

    private int QueryRangeSpan(int startRa, int endRa, int startDec, int endDec, Span<T> destination, int written)
    {
        int maxDest = destination.Length;
        for (int r = startRa; r <= endRa && written < maxDest; r++)
        {
            int rNorm = ((r % 360) + 360) % 360;
            for (int d = startDec; d <= endDec && written < maxDest; d++)
            {
                int key = (rNorm << 9) | d;
                if (_buckets.TryGetValue(key, out var list))
                {
                    for (int i = 0; i < list.Count && written < maxDest; i++)
                    {
                        destination[written++] = list[i];
                    }
                }
            }
        }
        return written;
    }

    /// <summary>
    /// Queries all items into a destination List for dynamic-sized collection without buffer truncations.
    /// </summary>
    public void QueryBoundingBox(double raMin, double raMax, double decMin, double decMax, List<T> destination)
    {
        void QueryRange(int startRa, int endRa, int startDec, int endDec)
        {
            for (int r = startRa; r <= endRa; r++)
            {
                int rNorm = ((r % 360) + 360) % 360;
                for (int d = startDec; d <= endDec; d++)
                {
                    int key = (rNorm << 9) | d;
                    if (_buckets.TryGetValue(key, out var list))
                    {
                        destination.AddRange(list);
                    }
                }
            }
        }

        // 1. Polar caps
        if (decMax > PolarCapThresholdDec)
        {
            if (_buckets.TryGetValue(NorthPolarKey, out var northList))
            {
                destination.AddRange(northList);
            }
        }

        if (decMin < -PolarCapThresholdDec)
        {
            if (_buckets.TryGetValue(SouthPolarKey, out var southList))
            {
                destination.AddRange(southList);
            }
        }

        // 2. Mid-latitude cells
        double effDecMin = Math.Max(-PolarCapThresholdDec, decMin);
        double effDecMax = Math.Min(PolarCapThresholdDec, decMax);

        if (effDecMin <= effDecMax)
        {
            int dStart = (int)Math.Floor(effDecMin + 90.0);
            int dEnd = (int)Math.Floor(effDecMax + 90.0);

            double normRaMin = ((raMin % 360.0) + 360.0) % 360.0;
            double normRaMax = ((raMax % 360.0) + 360.0) % 360.0;

            if (normRaMin <= normRaMax)
            {
                int rStart = (int)Math.Floor(normRaMin);
                int rEnd = (int)Math.Floor(normRaMax);
                QueryRange(rStart, rEnd, dStart, dEnd);
            }
            else
            {
                int rStart1 = (int)Math.Floor(normRaMin);
                QueryRange(rStart1, 359, dStart, dEnd);
                int rEnd2 = (int)Math.Floor(normRaMax);
                QueryRange(0, rEnd2, dStart, dEnd);
            }
        }
    }

    /// <summary>
    /// Queries candidates falling within a circular sky patch centered at (centerRa, centerDec) with angular radius radiusDeg.
    /// </summary>
    public void QueryCone(double centerRa, double centerDec, double radiusDeg, List<T> destination)
    {
        double decMin = centerDec - radiusDeg;
        double decMax = centerDec + radiusDeg;

        double cosDec = Math.Cos(Math.Clamp(Math.Abs(centerDec), 0.0, 89.0) * (Math.PI / 180.0));
        double deltaRa = cosDec > 1e-4 ? (radiusDeg / cosDec) : 180.0;

        double raMin = centerRa - deltaRa;
        double raMax = centerRa + deltaRa;

        QueryBoundingBox(raMin, raMax, decMin, decMax, destination);
    }
}
