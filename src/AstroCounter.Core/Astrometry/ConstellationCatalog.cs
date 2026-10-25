namespace AstroCounter.Core.Astrometry;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

/// <summary>
/// Represents a stick-figure constellation line connecting two celestial coordinates (J2000).
/// </summary>
public readonly record struct ConstellationLine(
    string ConstellationName,
    string ConstellationNameTr,
    string ConstellationAbbr,
    CelestialCoordinates Start,
    CelestialCoordinates End)
{
    public string DisplayName => CataloguePlateSolver.UseTurkishLanguage ? ConstellationNameTr : ConstellationName;
}

/// <summary>
/// Built-in astronomical catalogue of all 88 official IAU constellation stick figures.
/// Loaded directly from embedded compressed binary data (695 segments connecting bright Hipparcos stars).
/// Features full English and Turkish astronomical nomenclature.
/// </summary>
public static class ConstellationCatalog
{
    private static readonly Lazy<List<ConstellationLine>> _lines = new(LoadEmbeddedConstellations, isThreadSafe: true);

    /// <summary>All constellation line segments across northern and southern celestial hemispheres.</summary>
    public static IReadOnlyList<ConstellationLine> Lines => _lines.Value;

    private static List<ConstellationLine> LoadEmbeddedConstellations()
    {
        var list = new List<ConstellationLine>(750);
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("constellations_88.catalog.gz");

        if (stream != null)
        {
            try
            {
                using var gz = new GZipStream(stream, CompressionMode.Decompress);
                using var reader = new BinaryReader(gz);

                Span<byte> magic = stackalloc byte[5];
                if (reader.Read(magic) == 5 && magic[0] == 'C' && magic[1] == 'O' && magic[2] == 'N' && magic[3] == '8' && magic[4] == '8')
                {
                    uint count = reader.ReadUInt32();
                    byte[] nameBuf = new byte[64];

                    for (int i = 0; i < count; i++)
                    {
                        byte lenEn = reader.ReadByte();
                        reader.Read(nameBuf, 0, lenEn);
                        string nameEn = System.Text.Encoding.UTF8.GetString(nameBuf, 0, lenEn);

                        byte lenTr = reader.ReadByte();
                        reader.Read(nameBuf, 0, lenTr);
                        string nameTr = System.Text.Encoding.UTF8.GetString(nameBuf, 0, lenTr);

                        reader.Read(nameBuf, 0, 4);
                        string abbr = System.Text.Encoding.ASCII.GetString(nameBuf, 0, 4).Trim();

                        float r1 = reader.ReadSingle();
                        float d1 = reader.ReadSingle();
                        float r2 = reader.ReadSingle();
                        float d2 = reader.ReadSingle();

                        list.Add(new ConstellationLine(
                            nameEn,
                            nameTr,
                            abbr,
                            new CelestialCoordinates(r1, d1),
                            new CelestialCoordinates(r2, d2)));
                    }

                    return list;
                }
            }
            catch
            {
                // Fallback to static subset
            }
        }

        // Fallback static subset if stream loading fails
        return BuildStaticFallback();
    }

    private static List<ConstellationLine> BuildStaticFallback()
    {
        var list = new List<ConstellationLine>(64);
        void Add(string name, string nameTr, string abbr, double ra1, double dec1, double ra2, double dec2)
        {
            list.Add(new ConstellationLine(name, nameTr, abbr, new CelestialCoordinates(ra1, dec1), new CelestialCoordinates(ra2, dec2)));
        }

        Add("Cygnus", "Kuğu", "Cyg", 310.358, 45.280, 305.557, 40.257);
        Add("Cygnus", "Kuğu", "Cyg", 305.557, 40.257, 292.680, 27.960);
        Add("Cygnus", "Kuğu", "Cyg", 305.557, 40.257, 311.553, 33.970);
        Add("Cygnus", "Kuğu", "Cyg", 305.557, 40.257, 296.244, 45.131);
        Add("Orion", "Avcı", "Ori", 88.793, 7.407, 81.283, 6.350);
        Add("Orion", "Avcı", "Ori", 88.793, 7.407, 85.190, -1.943);
        Add("Orion", "Avcı", "Ori", 81.283, 6.350, 83.002, -0.299);
        Add("Cassiopeia", "Kraliçe", "Cas", 2.294, 59.150, 10.127, 56.537);
        Add("Ursa Major", "Büyük Ayı", "UMa", 165.933, 61.751, 165.460, 56.382);
        Add("Lyra", "Çalgı", "Lyr", 279.235, 38.784, 282.520, 33.363);
        Add("Taurus", "Boğa", "Tau", 68.980, 16.509, 81.573, 28.608);
        return list;
    }
}
