namespace AstroCounter.Core.Astrometry;

using System;
using System.Collections.Generic;

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
/// Built-in astronomical catalogue of major constellation stick figures across northern and southern hemispheres.
/// Enables in-app interactive planetarium rendering without requiring external software like Stellarium.
/// </summary>
public static class ConstellationCatalog
{
    public static readonly IReadOnlyList<ConstellationLine> Lines = BuildConstellationLines();

    private static List<ConstellationLine> BuildConstellationLines()
    {
        var list = new List<ConstellationLine>(200);

        void Add(string name, string nameTr, string abbr, double ra1, double dec1, double ra2, double dec2)
        {
            list.Add(new ConstellationLine(
                name,
                nameTr,
                abbr,
                new CelestialCoordinates(ra1, dec1),
                new CelestialCoordinates(ra2, dec2)));
        }

        // --- CYGNUS (The Swan / Northern Cross / Kuğu) ---
        // Deneb (310.36, 45.28) -> Sadr (305.56, 40.26)
        Add("Cygnus", "Kuğu", "Cyg", 310.358, 45.280, 305.557, 40.257);
        // Sadr -> Albireo (292.68, 27.96)
        Add("Cygnus", "Kuğu", "Cyg", 305.557, 40.257, 292.680, 27.960);
        // Sadr -> Gienah / Epsilon Cyg (311.55, 33.97)
        Add("Cygnus", "Kuğu", "Cyg", 305.557, 40.257, 311.553, 33.970);
        // Sadr -> Delta Cyg (296.24, 45.13)
        Add("Cygnus", "Kuğu", "Cyg", 305.557, 40.257, 296.244, 45.131);
        // Deneb -> Xi Cyg (316.23, 43.93)
        Add("Cygnus", "Kuğu", "Cyg", 310.358, 45.280, 316.233, 43.928);
        // Delta Cyg -> Iota Cyg (292.43, 51.73)
        Add("Cygnus", "Kuğu", "Cyg", 296.244, 45.131, 292.426, 51.729);

        // --- ORION (The Hunter / Avcı) ---
        // Betelgeuse (88.79, 7.41) -> Bellatrix (81.28, 6.35)
        Add("Orion", "Avcı", "Ori", 88.793, 7.407, 81.283, 6.350);
        // Betelgeuse -> Alnitak (85.19, -1.94)
        Add("Orion", "Avcı", "Ori", 88.793, 7.407, 85.190, -1.943);
        // Bellatrix -> Mintaka (83.00, -0.30)
        Add("Orion", "Avcı", "Ori", 81.283, 6.350, 83.002, -0.299);
        // Belt: Alnitak -> Alnilam (84.05, -1.20) -> Mintaka
        Add("Orion", "Avcı", "Ori", 85.190, -1.943, 84.053, -1.202);
        Add("Orion", "Avcı", "Ori", 84.053, -1.202, 83.002, -0.299);
        // Mintaka -> Rigel (78.63, -8.20)
        Add("Orion", "Avcı", "Ori", 83.002, -0.299, 78.634, -8.202);
        // Alnitak -> Saiph (86.94, -9.67)
        Add("Orion", "Avcı", "Ori", 85.190, -1.943, 86.939, -9.669);
        // Rigel -> Saiph
        Add("Orion", "Avcı", "Ori", 78.634, -8.202, 86.939, -9.669);

        // --- CASSIOPEIA (The Queen / Kraliçe - 'W' Asterism) ---
        // Caph / Beta Cas (2.29, 59.15) -> Schedar / Alpha Cas (10.13, 56.54)
        Add("Cassiopeia", "Kraliçe", "Cas", 2.294, 59.150, 10.127, 56.537);
        // Schedar -> Gamma Cas / Navi (14.18, 60.72)
        Add("Cassiopeia", "Kraliçe", "Cas", 10.127, 56.537, 14.177, 60.717);
        // Gamma Cas -> Ruchbah / Delta Cas (21.45, 60.23)
        Add("Cassiopeia", "Kraliçe", "Cas", 14.177, 60.717, 21.454, 60.235);
        // Ruchbah -> Segin / Epsilon Cas (26.24, 63.67)
        Add("Cassiopeia", "Kraliçe", "Cas", 21.454, 60.235, 26.242, 63.670);

        // --- URSA MAJOR (Big Dipper / Büyük Ayı) ---
        // Dubhe (165.93, 61.75) -> Merak (165.46, 56.38)
        Add("Ursa Major", "Büyük Ayı", "UMa", 165.933, 61.751, 165.460, 56.382);
        // Merak -> Phecda (178.46, 53.69)
        Add("Ursa Major", "Büyük Ayı", "UMa", 165.460, 56.382, 178.458, 53.695);
        // Phecda -> Megrez (183.86, 57.03)
        Add("Ursa Major", "Büyük Ayı", "UMa", 178.458, 53.695, 183.857, 57.033);
        // Megrez -> Dubhe
        Add("Ursa Major", "Büyük Ayı", "UMa", 183.857, 57.033, 165.933, 61.751);
        // Handle: Megrez -> Alioth (193.51, 55.96)
        Add("Ursa Major", "Büyük Ayı", "UMa", 183.857, 57.033, 193.507, 55.960);
        // Alioth -> Mizar (200.98, 54.92)
        Add("Ursa Major", "Büyük Ayı", "UMa", 193.507, 55.960, 200.981, 54.925);
        // Mizar -> Alkaid (206.89, 49.31)
        Add("Ursa Major", "Büyük Ayı", "UMa", 200.981, 54.925, 206.885, 49.313);

        // --- LYRA (The Harp / Çalgı) ---
        // Vega (279.23, 38.78) -> Sheliak / Beta Lyr (282.52, 33.36)
        Add("Lyra", "Çalgı", "Lyr", 279.235, 38.784, 282.520, 33.363);
        // Vega -> Sulafat / Gamma Lyr (284.75, 32.69)
        Add("Lyra", "Çalgı", "Lyr", 279.235, 38.784, 284.746, 32.690);
        // Sheliak -> Sulafat
        Add("Lyra", "Çalgı", "Lyr", 282.520, 33.363, 284.746, 32.690);
        // Sulafat -> Delta2 Lyr (283.69, 36.90)
        Add("Lyra", "Çalgı", "Lyr", 284.746, 32.690, 283.693, 36.900);
        // Delta2 Lyr -> Vega
        Add("Lyra", "Çalgı", "Lyr", 283.693, 36.900, 279.235, 38.784);

        // --- TAURUS (The Bull / Boğa) ---
        // Aldebaran (68.98, 16.51) -> Elnath / Beta Tau (81.57, 28.61)
        Add("Taurus", "Boğa", "Tau", 68.980, 16.509, 81.573, 28.608);
        // Aldebaran -> Tianguan / Zeta Tau (84.41, 21.14)
        Add("Taurus", "Boğa", "Tau", 68.980, 16.509, 84.411, 21.143);
        // Hyades 'V': Aldebaran -> Ain / Epsilon Tau (67.15, 19.18)
        Add("Taurus", "Boğa", "Tau", 68.980, 16.509, 67.154, 19.180);
        // Hyades 'V': Aldebaran -> Gamma Tau (64.95, 15.63)
        Add("Taurus", "Boğa", "Tau", 68.980, 16.509, 64.948, 15.628);

        // --- ANDROMEDA ---
        // Alpheratz / Alpha And (2.10, 29.09) -> Mirach / Beta And (17.43, 35.62)
        Add("Andromeda", "Andromeda", "And", 2.097, 29.090, 17.433, 35.621);
        // Mirach -> Almach / Gamma And (30.97, 42.33)
        Add("Andromeda", "Andromeda", "And", 17.433, 35.621, 30.975, 42.333);
        // Mirach -> Mu And (16.99, 38.50) (pointing towards M31 galaxy!)
        Add("Andromeda", "Andromeda", "And", 17.433, 35.621, 16.990, 38.500);

        // --- PEGASUS (Great Square / Kanatlı At) ---
        // Alpheratz (2.10, 29.09) -> Algenib (3.65, 15.18)
        Add("Pegasus", "Kanatlı At", "Peg", 2.097, 29.090, 3.650, 15.180);
        // Algenib -> Markab (346.18, 15.21)
        Add("Pegasus", "Kanatlı At", "Peg", 3.650, 15.180, 346.180, 15.210);
        // Markab -> Scheat (346.18, 28.08)
        Add("Pegasus", "Kanatlı At", "Peg", 346.180, 15.210, 346.180, 28.080);
        // Scheat -> Alpheratz
        Add("Pegasus", "Kanatlı At", "Peg", 346.180, 28.080, 2.097, 29.090);

        // --- AQUILA (The Eagle / Kartal) ---
        // Altair (297.70, 8.87) -> Tarazed (296.56, 10.61)
        Add("Aquila", "Kartal", "Aql", 297.696, 8.868, 296.565, 10.613);
        // Altair -> Alshain (298.83, 6.41)
        Add("Aquila", "Kartal", "Aql", 297.696, 8.868, 298.828, 6.407);
        // Altair -> Delta Aql (291.38, 3.12)
        Add("Aquila", "Kartal", "Aql", 297.696, 8.868, 291.375, 3.115);

        // --- CANIS MAJOR (Greater Dog / Büyük Köpek) ---
        // Sirius (101.29, -16.72) -> Murzim (97.55, -17.96)
        Add("Canis Major", "Büyük Köpek", "CMa", 101.287, -16.716, 97.550, -17.960);
        // Sirius -> Wezen (107.10, -26.39)
        Add("Canis Major", "Büyük Köpek", "CMa", 101.287, -16.716, 107.100, -26.390);
        // Wezen -> Adhara (104.66, -28.97)
        Add("Canis Major", "Büyük Köpek", "CMa", 107.100, -26.390, 104.660, -28.970);
        // Wezen -> Aludra (111.02, -29.30)
        Add("Canis Major", "Büyük Köpek", "CMa", 107.100, -26.390, 111.020, -29.300);

        // --- LEO (The Lion / Aslan) ---
        // Regulus (152.09, 11.97) -> Algieba (156.97, 19.84)
        Add("Leo", "Aslan", "Leo", 152.093, 11.967, 156.970, 19.840);
        // Algieba -> Zosma (168.53, 20.52)
        Add("Leo", "Aslan", "Leo", 156.970, 19.840, 168.530, 20.520);
        // Zosma -> Denebola (177.26, 14.57)
        Add("Leo", "Aslan", "Leo", 168.530, 20.520, 177.260, 14.570);
        // Regulus -> Denebola
        Add("Leo", "Aslan", "Leo", 152.093, 11.967, 177.260, 14.570);

        return list;
    }
}

