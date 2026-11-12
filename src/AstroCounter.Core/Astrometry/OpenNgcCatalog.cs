namespace AstroCounter.Core.Astrometry;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

/// <summary>
/// Represents a deep-sky object entry from OpenNGC (Messier, NGC, IC, Caldwell).
/// </summary>
public readonly record struct OpenNgcObject(
    string Name,
    string MessierName,
    string CommonName,
    string ObjectType,
    float RaDeg,
    float DecDeg,
    float VMag,
    float MajorAxisArcmin)
{
    public CelestialCoordinates Coordinates => new(RaDeg, DecDeg);

    public string DisplayName => !string.IsNullOrEmpty(MessierName) && !string.IsNullOrEmpty(CommonName)
        ? $"{CommonName} ({MessierName} / {Name})"
        : (!string.IsNullOrEmpty(CommonName) ? $"{CommonName} ({Name})" : (!string.IsNullOrEmpty(MessierName) ? $"{MessierName} ({Name})" : Name));

    public double TypicalFovDegrees => MajorAxisArcmin > 1.0 ? Math.Max(0.5, MajorAxisArcmin / 60.0 * 2.2) : 1.2;

    public double EstimatedPixelScaleArcsec => MajorAxisArcmin > 30.0 ? 2.0 : 1.2;
}

/// <summary>
/// Comprehensive OpenNGC Deep-Sky Object Catalog (13,963 galaxies, nebulae, star clusters, supernova remnants).
/// Embedded as an optimized compressed binary stream within AstroCounter.Core.
/// Features spatial declination indexing and cone-search querying.
/// </summary>
public static class OpenNgcCatalog
{
    private static readonly Lazy<NgcIndex> _catalog = new(LoadCatalog, isThreadSafe: true);

    /// <summary>Total number of deep sky objects in OpenNGC (13,963).</summary>
    public static int TotalObjects => _catalog.Value.Objects.Length;

    /// <summary>All deep sky objects in the catalogue.</summary>
    public static ReadOnlyMemory<OpenNgcObject> AllObjects => _catalog.Value.Objects;

    /// <summary>
    /// Queries deep sky objects within a circular sky patch centered at (centerRaDeg, centerDecDeg).
    /// </summary>
    public static List<OpenNgcObject> QueryRegion(
        double centerRaDeg,
        double centerDecDeg,
        double radiusDeg,
        double maxMag = 18.0,
        int maxResults = 100)
    {
        return _catalog.Value.QueryRegion(centerRaDeg, centerDecDeg, radiusDeg, maxMag, maxResults);
    }

    /// <summary>
    /// Finds a specific object by its catalog identifier (e.g. "M31", "NGC 7000", "IC 434", "NGC6960").
    /// </summary>
    public static OpenNgcObject? FindByName(string identifier)
    {
        return _catalog.Value.FindByName(identifier);
    }

    private sealed class NgcIndex
    {
        public OpenNgcObject[] Objects { get; }
        private readonly int[][] _declinationStrips;
        private readonly Dictionary<string, int> _nameLookup;
        private const double StripSizeDeg = 2.0;
        private const int StripCount = 90; // 180 / 2 = 90 strips from -90 to +90

        public NgcIndex(OpenNgcObject[] objects)
        {
            Objects = objects;
            _nameLookup = new Dictionary<string, int>(objects.Length * 2, StringComparer.OrdinalIgnoreCase);

            var buckets = new List<int>[StripCount];
            for (int i = 0; i < StripCount; i++)
            {
                buckets[i] = new List<int>(objects.Length / StripCount + 50);
            }

            for (int i = 0; i < objects.Length; i++)
            {
                ref readonly var obj = ref objects[i];
                int sIdx = Math.Clamp((int)Math.Floor((obj.DecDeg + 90.0) / StripSizeDeg), 0, StripCount - 1);
                buckets[sIdx].Add(i);

                // Add name lookup keys
                _nameLookup[obj.Name] = i;
                string cleanName = obj.Name.Replace(" ", "");
                if (cleanName != obj.Name) _nameLookup[cleanName] = i;

                if (!string.IsNullOrEmpty(obj.MessierName))
                {
                    _nameLookup[obj.MessierName] = i;
                    string cleanM = obj.MessierName.Replace(" ", "");
                    if (cleanM != obj.MessierName) _nameLookup[cleanM] = i;
                }

                if (!string.IsNullOrEmpty(obj.CommonName))
                {
                    _nameLookup[obj.CommonName] = i;
                }
            }

            _declinationStrips = new int[StripCount][];
            for (int i = 0; i < StripCount; i++)
            {
                _declinationStrips[i] = buckets[i].ToArray();
            }
        }

        public OpenNgcObject? FindByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string key = name.Trim();
            if (_nameLookup.TryGetValue(key, out int idx))
            {
                return Objects[idx];
            }
            string noSpace = key.Replace(" ", "");
            if (_nameLookup.TryGetValue(noSpace, out idx))
            {
                return Objects[idx];
            }
            return null;
        }

        public List<OpenNgcObject> QueryRegion(
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
            var results = new List<OpenNgcObject>(Math.Min(256, maxResults * 2));

            const double degToRad = Math.PI / 180.0;

            for (int s = minStrip; s <= maxStrip; s++)
            {
                var indices = _declinationStrips[s];
                for (int i = 0; i < indices.Length; i++)
                {
                    ref readonly var obj = ref Objects[indices[i]];
                    if (obj.VMag < 90.0f && obj.VMag > maxMagF) continue;

                    double oRaRad = obj.RaDeg * degToRad;
                    double oDecRad = obj.DecDeg * degToRad;
                    float oCosDec = (float)Math.Cos(oDecRad);
                    float ox = (float)(oCosDec * Math.Cos(oRaRad));
                    float oy = (float)(oCosDec * Math.Sin(oRaRad));
                    float oz = (float)Math.Sin(oDecRad);

                    float dot = (ox * cx) + (oy * cy) + (oz * cz);
                    if (dot >= cosRadius)
                    {
                        results.Add(obj);
                    }
                }
            }

            // Prioritize Messier and prominent named targets, then by visual magnitude
            results.Sort((a, b) =>
            {
                int aPri = !string.IsNullOrEmpty(a.MessierName) ? 0 : (!string.IsNullOrEmpty(a.CommonName) ? 1 : 2);
                int bPri = !string.IsNullOrEmpty(b.MessierName) ? 0 : (!string.IsNullOrEmpty(b.CommonName) ? 1 : 2);
                if (aPri != bPri) return aPri.CompareTo(bPri);
                return a.VMag.CompareTo(b.VMag);
            });

            if (results.Count > maxResults)
            {
                results.RemoveRange(maxResults, results.Count - maxResults);
            }

            return results;
        }
    }

    private static NgcIndex LoadCatalog()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("open_ngc.catalog.gz");

        if (stream != null)
        {
            try
            {
                using var gz = new GZipStream(stream, CompressionMode.Decompress);
                using var reader = new BinaryReader(gz);

                Span<byte> magic = stackalloc byte[4];
                if (reader.Read(magic) == 4 && magic[0] == 'O' && magic[1] == 'N' && magic[2] == 'G' && magic[3] == 'C')
                {
                    uint count = reader.ReadUInt32();
                    var list = new OpenNgcObject[count];

                    for (int i = 0; i < count; i++)
                    {
                        byte lenN = reader.ReadByte();
                        byte[] bufN = reader.ReadBytes(lenN);
                        string name = System.Text.Encoding.UTF8.GetString(bufN);

                        byte lenM = reader.ReadByte();
                        byte[] bufM = reader.ReadBytes(lenM);
                        string mName = System.Text.Encoding.UTF8.GetString(bufM);

                        byte lenC = reader.ReadByte();
                        byte[] bufC = reader.ReadBytes(lenC);
                        string cName = System.Text.Encoding.UTF8.GetString(bufC);

                        byte lenT = reader.ReadByte();
                        byte[] bufT = reader.ReadBytes(lenT);
                        string oType = System.Text.Encoding.UTF8.GetString(bufT);

                        float ra = reader.ReadSingle();
                        float dec = reader.ReadSingle();
                        float vmag = reader.ReadSingle();
                        float majAx = reader.ReadSingle();

                        list[i] = new OpenNgcObject(name, mName, cName, oType, ra, dec, vmag, majAx);
                    }

                    return new NgcIndex(list);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OpenNgcCatalog] Error loading embedded catalog: {ex}");
                throw;
            }
        }
        else
        {
            throw new InvalidOperationException($"Manifest resource stream 'open_ngc.catalog.gz' was null. Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
        }

        return new NgcIndex(BuildFallbackObjects());
    }

    private static OpenNgcObject[] BuildFallbackObjects()
    {
        return [
            new("NGC 224", "M31", "Andromeda Galaxy", "G", 10.6847f, 41.2687f, 3.44f, 189.7f),
            new("NGC 1976", "M42", "Orion Nebula", "HII", 83.8221f, -5.3911f, 4.0f, 65.0f),
            new("NGC 598", "M33", "Triangulum Galaxy", "G", 23.4621f, 30.6602f, 5.72f, 70.8f),
            new("NGC 5194", "M51", "Whirlpool Galaxy", "G", 202.4696f, 47.1953f, 8.4f, 11.2f),
            new("NGC 7000", "", "North America Nebula", "HII", 314.7500f, 44.3333f, 4.0f, 120.0f),
            new("IC 434", "", "Horsehead Nebula", "Neb", 85.2458f, -2.4583f, 7.3f, 60.0f),
        ];
    }
}
