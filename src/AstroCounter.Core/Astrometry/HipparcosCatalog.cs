namespace AstroCounter.Core.Astrometry;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

/// <summary>
/// Represents a star entry from the ESA Hipparcos astrometry catalogue (J2000 epoch).
/// Includes precomputed 3D celestial sphere unit vectors (X, Y, Z) for ultra-fast spatial querying.
/// </summary>
public readonly record struct HipparcosStar(
    int HipId,
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
/// Provides access to the full ESA Hipparcos catalogue (117,955 stars down to magnitude ~12.0),
/// embedded as an optimized compressed binary stream within AstroCounter.Core.
/// Features 3D vector-accelerated cone searching and declination spatial indexing.
/// </summary>
public static class HipparcosCatalog
{
    private static readonly Lazy<HipparcosIndex> _catalog = new(LoadEmbeddedCatalog, isThreadSafe: true);

    /// <summary>Total number of stars in the loaded Hipparcos catalogue (117,955).</summary>
    public static int TotalStars => _catalog.Value.Stars.Length;

    /// <summary>All stars in the catalogue.</summary>
    public static ReadOnlyMemory<HipparcosStar> AllStars => _catalog.Value.Stars;

    /// <summary>
    /// Queries stars within a circular sky patch (cone search) centered at (centerRaDeg, centerDecDeg).
    /// Results are returned sorted from brightest to faintest.
    /// </summary>
    public static List<HipparcosStar> QueryRegion(
        double centerRaDeg,
        double centerDecDeg,
        double radiusDeg,
        double maxMag = 8.5,
        int maxResults = 120)
    {
        return _catalog.Value.QueryRegion(centerRaDeg, centerDecDeg, radiusDeg, maxMag, maxResults);
    }

    private sealed class HipparcosIndex
    {
        public HipparcosStar[] Stars { get; }
        private readonly int[][] _declinationStrips;
        private const double StripSizeDeg = 5.0;
        private const int StripCount = 36; // 180 / 5 = 36 strips from -90 to +90

        public HipparcosIndex(HipparcosStar[] stars)
        {
            Stars = stars;

            var stripBuckets = new List<int>[StripCount];
            for (int i = 0; i < StripCount; i++)
            {
                stripBuckets[i] = new List<int>(stars.Length / StripCount + 100);
            }

            for (int i = 0; i < stars.Length; i++)
            {
                int stripIdx = Math.Clamp((int)Math.Floor((stars[i].DecDeg + 90.0) / StripSizeDeg), 0, StripCount - 1);
                stripBuckets[stripIdx].Add(i);
            }

            _declinationStrips = new int[StripCount][];
            for (int i = 0; i < StripCount; i++)
            {
                _declinationStrips[i] = stripBuckets[i].ToArray();
            }
        }

        public List<HipparcosStar> QueryRegion(
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
            var results = new List<HipparcosStar>(Math.Min(256, maxResults * 2));

            for (int s = minStrip; s <= maxStrip; s++)
            {
                var indices = _declinationStrips[s];
                for (int i = 0; i < indices.Length; i++)
                {
                    ref readonly var star = ref Stars[indices[i]];
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

    private static HipparcosIndex LoadEmbeddedCatalog()
    {
        var asm = typeof(HipparcosCatalog).Assembly;
        var resName = "hipparcos.catalog.gz";
        var stream = asm.GetManifestResourceStream(resName);
        if (stream == null)
        {
            var names = asm.GetManifestResourceNames();
            var matching = Array.Find(names, n => n.Contains("hipparcos", StringComparison.OrdinalIgnoreCase));
            if (matching != null)
            {
                stream = asm.GetManifestResourceStream(matching);
            }
            if (stream == null)
            {
                throw new InvalidOperationException($"Embedded Hipparcos resource not found. Available resources: [{string.Join(", ", names)}]");
            }
        }

        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var ms = new MemoryStream(118_000 * 16 + 1024);
        gzip.CopyTo(ms);
        ms.Position = 0;

        using var reader = new BinaryReader(ms);
        Span<byte> magic = stackalloc byte[4];
        if (reader.Read(magic) != 4 || magic[0] != 'H' || magic[1] != 'I' || magic[2] != 'P' || magic[3] != '1')
        {
            throw new InvalidDataException("Invalid Hipparcos binary header magic.");
        }

        uint count = reader.ReadUInt32();
        var stars = new HipparcosStar[count];

        const double degToRad = Math.PI / 180.0;
        for (int i = 0; i < count; i++)
        {
            int hip = reader.ReadInt32();
            float ra = reader.ReadSingle();
            float dec = reader.ReadSingle();
            short mag100 = reader.ReadInt16();
            short bv100 = reader.ReadInt16();

            float mag = mag100 / 100.0f;
            float bv = bv100 / 100.0f;

            double raR = ra * degToRad;
            double decR = dec * degToRad;
            double cD = Math.Cos(decR);

            float x = (float)(cD * Math.Cos(raR));
            float y = (float)(cD * Math.Sin(raR));
            float z = (float)Math.Sin(decR);

            stars[i] = new HipparcosStar(hip, ra, dec, mag, bv, x, y, z);
        }

        return new HipparcosIndex(stars);
    }
}
