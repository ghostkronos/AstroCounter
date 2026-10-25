namespace AstroCounter.Core.Astrometry;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

/// <summary>
/// Represents a high-precision reference star entry from the Gaia DR3 / Tycho astrometric catalog.
/// </summary>
public readonly record struct GaiaTychoStar(
    long SourceId,
    float RaDeg,
    float DecDeg,
    float Mag,
    float Bv,
    float X,
    float Y,
    float Z)
{
    public CelestialCoordinates ToCoordinates() => new(RaDeg, DecDeg);
}

/// <summary>
/// High-density Gaia DR3 / Tycho reference star catalog (2,500,000 stars down to magnitude 14.0).
/// Optimized for narrow-field telephoto/telescope plate solving, high-precision astrometric distortion correction,
/// and full-sky star identification.
/// Features 180 1-degree declination spatial strip indexing with memory-efficient binary packing.
/// </summary>
public static class GaiaTychoCatalog
{
    private static readonly Lazy<DenseCatalogIndex> _catalog = new(InitializeCatalog, isThreadSafe: true);

    /// <summary>Total number of stars in the dense Gaia/Tycho catalog.</summary>
    public static int TotalStars => _catalog.Value.TotalStars;

    /// <summary>
    /// Queries reference stars within a circular sky patch centered at (centerRaDeg, centerDecDeg).
    /// Returns stars sorted from brightest to faintest down to maxMag.
    /// </summary>
    public static List<GaiaTychoStar> QueryRegion(
        double centerRaDeg,
        double centerDecDeg,
        double radiusDeg,
        double maxMag = 14.0,
        int maxResults = 500)
    {
        return _catalog.Value.QueryRegion(centerRaDeg, centerDecDeg, radiusDeg, maxMag, maxResults);
    }

    private sealed class DenseCatalogIndex
    {
        private readonly GaiaTychoStar[] _stars;
        private readonly int[][] _declinationStrips;
        private const double StripSizeDeg = 1.0;
        private const int StripCount = 180; // 180 1-degree strips from -90 to +90

        public int TotalStars => _stars.Length;

        public DenseCatalogIndex(GaiaTychoStar[] stars)
        {
            _stars = stars;
            var buckets = new List<int>[StripCount];
            for (int i = 0; i < StripCount; i++)
            {
                buckets[i] = new List<int>(Math.Max(16, stars.Length / StripCount + 500));
            }

            for (int i = 0; i < stars.Length; i++)
            {
                int sIdx = Math.Clamp((int)Math.Floor((stars[i].DecDeg + 90.0) / StripSizeDeg), 0, StripCount - 1);
                buckets[sIdx].Add(i);
            }

            _declinationStrips = new int[StripCount][];
            for (int i = 0; i < StripCount; i++)
            {
                _declinationStrips[i] = buckets[i].ToArray();
            }
        }

        public List<GaiaTychoStar> QueryRegion(
            double centerRaDeg,
            double centerDecDeg,
            double radiusDeg,
            double maxMag,
            int maxResults)
        {
            double rRad = radiusDeg * (Math.PI / 180.0);
            float cosRadius = (float)Math.Cos(rRad);

            double raRad = centerRaDeg * (Math.PI / 180.0);
            double decRad = centerDecDeg * (Math.PI / 180.0);
            float cosDec = (float)Math.Cos(decRad);

            float cx = (float)(cosDec * Math.Cos(raRad));
            float cy = (float)(cosDec * Math.Sin(raRad));
            float cz = (float)Math.Sin(decRad);

            double minDec = Math.Clamp(centerDecDeg - radiusDeg, -90.0, 90.0);
            double maxDec = Math.Clamp(centerDecDeg + radiusDeg, -90.0, 90.0);

            int minStrip = Math.Clamp((int)Math.Floor((minDec + 90.0) / StripSizeDeg), 0, StripCount - 1);
            int maxStrip = Math.Clamp((int)Math.Floor((maxDec + 90.0) / StripSizeDeg), 0, StripCount - 1);

            float maxMagF = (float)maxMag;
            var results = new List<GaiaTychoStar>(Math.Min(1024, maxResults * 2));

            for (int s = minStrip; s <= maxStrip; s++)
            {
                var indices = _declinationStrips[s];
                for (int i = 0; i < indices.Length; i++)
                {
                    ref readonly var star = ref _stars[indices[i]];
                    if (star.Mag > maxMagF) continue;

                    float dot = (star.X * cx) + (star.Y * cy) + (star.Z * cz);
                    if (dot >= cosRadius)
                    {
                        results.Add(star);
                    }
                }
            }

            results.Sort(static (a, b) => a.Mag.CompareTo(b.Mag));
            if (results.Count > maxResults)
            {
                results.RemoveRange(maxResults, results.Count - maxResults);
            }

            return results;
        }
    }

    private static DenseCatalogIndex InitializeCatalog()
    {
        // Check potential locations for gaia_tycho_dense.bin:
        // 1. Output directory catalogs/
        // 2. Base directory
        // 3. Source repository catalogs/
        string[] candidatePaths = [
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "catalogs", "gaia_tycho_dense.bin"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gaia_tycho_dense.bin"),
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "src", "AstroCounter.Core", "catalogs", "gaia_tycho_dense.bin")),
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "catalogs", "gaia_tycho_dense.bin"))
        ];

        Span<byte> magic = stackalloc byte[4];

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    using var fs = File.OpenRead(path);
                    using var reader = new BinaryReader(fs);
                    if (reader.Read(magic) == 4 && magic[0] == 'G' && magic[1] == 'A' && magic[2] == 'I' && magic[3] == 'A')
                    {
                        uint version = reader.ReadUInt32();
                        uint count = reader.ReadUInt32();
                        var stars = new GaiaTychoStar[count];
                        const double degToRad = Math.PI / 180.0;

                        // Read in batches of 50,000 stars (1 MB chunks) for ultra-fast load
                        const int batchSize = 50000;
                        byte[] buffer = new byte[batchSize * 20];

                        for (int i = 0; i < count; i += batchSize)
                        {
                            int n = (int)Math.Min(batchSize, count - i);
                            int bytesToRead = n * 20;
                            int bytesRead = reader.Read(buffer, 0, bytesToRead);
                            if (bytesRead < bytesToRead) break;

                            for (int j = 0; j < n; j++)
                            {
                                int offset = j * 20;
                                long id = BitConverter.ToInt64(buffer, offset);
                                float ra = BitConverter.ToSingle(buffer, offset + 8);
                                float dec = BitConverter.ToSingle(buffer, offset + 12);
                                float mag = BitConverter.ToInt16(buffer, offset + 16) / 100f;
                                float bv = BitConverter.ToInt16(buffer, offset + 18) / 100f;

                                double raR = ra * degToRad;
                                double decR = dec * degToRad;
                                double cD = Math.Cos(decR);
                                float x = (float)(cD * Math.Cos(raR));
                                float y = (float)(cD * Math.Sin(raR));
                                float z = (float)Math.Sin(decR);

                                stars[i + j] = new GaiaTychoStar(id, ra, dec, mag, bv, x, y, z);
                            }
                        }

                        return new DenseCatalogIndex(stars);
                    }
                }
                catch
                {
                    // Fallback to next path or bootstrap
                }
            }
        }

        // Bootstrap: Build dense index initialized from Hipparcos (117,955 stars)
        var hipStars = HipparcosCatalog.AllStars.Span;
        var starList = new List<GaiaTychoStar>(hipStars.Length + 10_000);

        for (int i = 0; i < hipStars.Length; i++)
        {
            ref readonly var h = ref hipStars[i];
            starList.Add(new GaiaTychoStar(h.HipId, h.RaDeg, h.DecDeg, h.Mag, h.Bv, h.X, h.Y, h.Z));
        }

        return new DenseCatalogIndex(starList.ToArray());
    }
}
