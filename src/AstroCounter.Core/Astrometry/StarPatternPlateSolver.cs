namespace AstroCounter.Core.Astrometry;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using SkiaSharp;

/// <summary>
/// True blind geometric astrometric plate solver based on star-triangle invariants and
/// RANSAC 2D similarity-transform verification on gnomonic (TAN) tangent planes.
/// Recognizes constellations and star fields directly from star positions in the image,
/// solving exact (RA, Dec) center, pixel scale (arcsec/px), and camera roll angle (0–360°).
/// </summary>
public class StarPatternPlateSolver : IPlateSolver
{
    public string SolverName => "Yıldız Konum & Üçgen Geometrisi Çözücü (RANSAC TAN)";

    public record CatalogStar(string Name, double RaDeg, double DecDeg, double Mag);

    public record DetectedStar(double X, double Y, double Flux);

    public static readonly CatalogStar[] BrightStarCatalog =
    [
        // === Andromeda / Pegasus / Triangulum / Aries / Pisces ===
        new("Alpheratz (α And)", 2.097, 29.090, 2.06),
        new("Mirach (β And)", 17.433, 35.621, 2.05),
        new("Almach (γ And)", 30.975, 42.330, 2.10),
        new("δ And", 9.832, 30.861, 3.28),
        new("μ And", 14.188, 38.499, 3.87),
        new("ν And", 12.453, 41.079, 4.53),
        new("M31 (Andromeda Core)", 10.685, 41.269, 3.40),
        new("51 And (Nembus)", 24.498, 48.628, 3.57),
        new("π And", 9.220, 33.719, 4.36),
        new("ε And", 9.639, 29.312, 4.37),
        new("ζ And", 11.835, 24.267, 4.08),
        new("η And", 14.302, 23.418, 4.42),
        new("φ And", 17.376, 47.242, 4.25),
        new("λ And", 354.391, 46.458, 3.82),
        new("κ And", 355.102, 44.334, 4.14),
        new("ι And", 354.567, 43.268, 4.29),
        new("ο And", 345.480, 42.326, 3.62),
        new("Scheat (β Peg)", 345.944, 28.083, 2.42),
        new("Markab (α Peg)", 346.190, 15.205, 2.48),
        new("Algenib (γ Peg)", 3.309, 15.184, 2.84),
        new("Enif (ε Peg)", 326.046, 9.875, 2.39),
        new("Matar (η Peg)", 340.751, 30.221, 2.95),
        new("Homam (ζ Peg)", 340.366, 10.831, 3.41),
        new("Sadalbari (μ Peg)", 342.501, 24.602, 3.48),
        new("λ Peg", 341.673, 23.565, 3.95),
        new("τ Peg", 350.169, 23.740, 4.60),
        new("υ Peg", 351.345, 23.404, 4.40),
        new("π Peg", 332.495, 33.179, 4.29),
        new("Mothallah (α Tri)", 28.270, 29.579, 3.42),
        new("β Tri", 32.386, 34.987, 3.00),
        new("γ Tri", 34.329, 33.847, 4.01),
        new("M33 (Triangulum Core)", 23.462, 30.660, 5.70),
        new("Hamal (α Ari)", 31.793, 23.462, 2.00),
        new("Sheratan (β Ari)", 28.660, 20.808, 2.65),
        new("Mesarthim (γ Ari)", 28.383, 19.294, 3.86),
        new("41 Ari (Bharani)", 42.496, 27.261, 3.63),
        new("η Psc (Alpherg)", 22.871, 15.346, 3.62),

        // === Cassiopeia / Perseus / Cepheus / Lacerta ===
        new("Schedar (α Cas)", 10.127, 56.537, 2.23),
        new("Caph (β Cas)", 2.295, 59.150, 2.27),
        new("γ Cas", 14.177, 60.717, 2.47),
        new("Ruchbah (δ Cas)", 21.454, 60.235, 2.68),
        new("Segin (ε Cas)", 28.599, 63.670, 3.37),
        new("Achird (η Cas)", 12.276, 57.815, 3.44),
        new("ζ Cas", 9.243, 53.897, 3.66),
        new("κ Cas", 8.250, 62.932, 4.16),
        new("ι Cas", 37.266, 67.402, 4.52),
        new("NGC 869/884 (Double Cluster)", 35.125, 57.133, 4.30),
        new("Mirfak (α Per)", 51.081, 49.861, 1.79),
        new("Algol (β Per)", 47.042, 40.956, 2.12),
        new("γ Per", 46.199, 53.506, 2.93),
        new("δ Per", 55.731, 47.788, 3.01),
        new("ε Per", 59.463, 40.010, 2.88),
        new("ζ Per", 58.533, 31.884, 2.85),
        new("Menkib (ξ Per)", 59.741, 35.791, 4.04),
        new("Atik (ο Per)", 56.080, 32.288, 3.83),
        new("Miram (η Per)", 42.674, 55.896, 3.76),
        new("θ Per", 41.050, 49.228, 4.12),
        new("ι Per", 47.266, 49.613, 4.05),
        new("κ Per", 47.367, 44.857, 3.80),
        new("ρ Per", 46.294, 38.840, 3.39),
        new("ν Per", 56.299, 42.579, 3.77),
        new("16 Per", 42.646, 38.318, 4.23),
        new("Alderamin (α Cep)", 319.645, 62.586, 2.46),
        new("Alfirk (β Cep)", 322.165, 70.561, 3.23),
        new("Errai (γ Cep)", 354.837, 77.632, 3.21),
        new("ζ Cep", 332.714, 58.201, 3.35),
        new("η Cep", 311.322, 61.839, 3.43),
        new("ι Cep", 342.420, 66.200, 3.52),
        new("δ Cep", 337.293, 58.415, 3.90),
        new("α Lac", 337.823, 50.282, 3.77),

        // === Summer Triangle: Cygnus / Lyra / Aquila / Delphinus / Sagitta / Vulpecula ===
        new("Vega (α Lyr)", 279.235, 38.784, 0.03),
        new("Sheliak (β Lyr)", 282.520, 33.363, 3.52),
        new("Sulafat (γ Lyr)", 284.736, 32.690, 3.24),
        new("ε Lyr", 281.085, 39.646, 3.90),
        new("ζ1 Lyr", 281.193, 37.605, 4.36),
        new("δ2 Lyr", 283.626, 36.899, 4.22),
        new("R Lyr", 283.834, 43.946, 4.08),
        new("κ Lyr", 274.966, 36.065, 4.33),
        new("Deneb (α Cyg)", 310.358, 45.280, 1.25),
        new("Sadr (γ Cyg)", 305.557, 40.257, 2.23),
        new("Gienah (ε Cyg)", 311.553, 33.970, 2.48),
        new("δ Cyg", 296.244, 45.131, 2.87),
        new("Albireo (β Cyg)", 292.680, 27.960, 3.08),
        new("ζ Cyg", 318.234, 30.227, 3.21),
        new("ξ Cyg", 316.233, 43.928, 3.73),
        new("τ Cyg", 318.698, 38.046, 3.72),
        new("ι Cyg", 292.426, 51.729, 3.79),
        new("κ Cyg", 289.276, 53.368, 3.77),
        new("η Cyg", 299.077, 35.083, 3.89),
        new("ν Cyg", 314.293, 41.167, 3.94),
        new("ο1 Cyg", 303.408, 46.741, 3.79),
        new("Altair (α Aql)", 297.696, 8.868, 0.77),
        new("Tarazed (γ Aql)", 296.565, 10.613, 2.72),
        new("Alshain (β Aql)", 298.828, 6.407, 3.71),
        new("ζ Aql (Okab)", 286.353, 13.863, 2.99),
        new("θ Aql", 302.826, -0.821, 3.23),
        new("δ Aql", 291.375, 3.115, 3.36),
        new("λ Aql", 286.562, -4.882, 3.44),
        new("η Aql", 298.118, 1.006, 3.80),
        new("12 Aql", 285.910, -5.739, 4.02),
        new("Sualocin (α Del)", 309.910, 15.912, 3.77),
        new("Rotanev (β Del)", 309.387, 14.595, 3.63),
        new("γ Del", 311.665, 16.124, 3.87),
        new("δ Del", 310.865, 15.075, 4.43),
        new("ε Del (Deneb Dulfim)", 308.303, 11.303, 4.03),
        new("γ Sge", 299.689, 19.492, 3.47),
        new("δ Sge", 296.847, 18.534, 3.82),
        new("α Sge (Sham)", 295.024, 18.014, 4.38),
        new("β Sge", 295.262, 17.476, 4.38),
        new("Anser (α Vul)", 292.176, 24.665, 4.44),

        // === Hercules / Draco / Corona Borealis / Boötes / Ophiuchus ===
        new("Kornephoros (β Her)", 247.555, 21.490, 2.77),
        new("ζ Her", 250.322, 31.603, 2.81),
        new("η Her", 250.724, 38.922, 3.53),
        new("π Her", 258.758, 36.809, 3.16),
        new("ε Her", 255.072, 30.926, 3.92),
        new("Sarin (δ Her)", 258.762, 24.839, 3.14),
        new("μ Her", 266.615, 27.721, 3.42),
        new("θ Her", 269.063, 37.250, 3.86),
        new("ξ Her", 269.396, 29.248, 3.70),
        new("ο Her", 271.886, 28.762, 3.83),
        new("109 Her", 275.925, 21.770, 3.84),
        new("τ Her", 244.935, 46.313, 3.89),
        new("σ Her", 248.526, 42.437, 4.20),
        new("Rasalgethi (α Her)", 258.662, 14.390, 3.08),
        new("Eltanin (γ Dra)", 269.152, 51.489, 2.23),
        new("Rastaban (β Dra)", 262.608, 52.301, 2.79),
        new("ξ Dra (Grumium)", 268.382, 56.873, 3.75),
        new("ν Dra", 263.054, 55.184, 4.13),
        new("Altais (δ Dra)", 288.139, 67.662, 3.07),
        new("η Dra (Athebyne)", 245.998, 61.514, 2.74),
        new("θ Dra", 240.472, 58.565, 4.01),
        new("Edasich (ι Dra)", 231.232, 58.966, 3.29),
        new("Thuban (α Dra)", 211.097, 64.376, 3.65),
        new("Alphecca (α CrB)", 233.672, 26.715, 2.23),
        new("Nusakan (β CrB)", 231.957, 29.106, 3.68),
        new("γ CrB", 235.686, 26.296, 3.84),
        new("Arcturus (α Boo)", 213.915, 19.182, 0.04),
        new("Izar (ε Boo)", 221.247, 27.074, 2.37),
        new("Nekkar (β Boo)", 225.486, 40.391, 3.50),
        new("Seginus (γ Boo)", 218.019, 38.308, 3.03),
        new("δ Boo", 228.876, 33.315, 3.47),
        new("Muphrid (η Boo)", 208.671, 18.398, 2.68),
        new("Rasalhague (α Oph)", 263.734, 12.560, 2.08),
        new("Cebalrai (β Oph)", 265.868, 4.567, 2.77),
        new("κ Oph", 254.417, 9.375, 3.20),
        new("72 Oph", 271.837, 9.564, 3.73),

        // === Taurus / Orion / Auriga / Gemini / Ursa Major / Ursa Minor ===
        new("Aldebaran (α Tau)", 68.980, 16.509, 0.85),
        new("Elnath (β Tau)", 81.573, 28.608, 1.65),
        new("M45 (Pleiades / Alcyone)", 56.871, 24.105, 1.60),
        new("ζ Tau (Tianguan)", 84.411, 21.143, 3.00),
        new("θ2 Tau", 67.166, 15.871, 3.40),
        new("γ Tau", 64.948, 15.628, 3.65),
        new("ε Tau (Ain)", 67.154, 19.180, 3.53),
        new("λ Tau", 60.170, 12.490, 3.47),
        new("Capella (α Aur)", 79.172, 45.998, 0.08),
        new("Menkalinan (β Aur)", 89.882, 44.948, 1.90),
        new("θ Aur (Mahasim)", 89.930, 37.213, 2.62),
        new("ι Aur (Hassaleh)", 74.248, 33.166, 2.69),
        new("ε Aur", 75.492, 43.823, 2.99),
        new("η Aur", 76.629, 41.234, 3.17),
        new("Betelgeuse (α Ori)", 88.793, 7.407, 0.50),
        new("Rigel (β Ori)", 78.634, -8.202, 0.13),
        new("Bellatrix (γ Ori)", 81.283, 6.350, 1.64),
        new("Mintaka (δ Ori)", 83.002, -0.299, 2.23),
        new("Alnilam (ε Ori)", 84.053, -1.202, 1.69),
        new("Alnitak (ζ Ori)", 85.190, -1.943, 1.77),
        new("Saiph (κ Ori)", 86.939, -9.669, 2.06),
        new("M42 (Orion Nebula)", 83.822, -5.391, 3.00),
        new("Hatysa (ι Ori)", 84.730, -5.910, 2.75),
        new("Saif al Jabbar (η Ori)", 83.784, -2.397, 3.35),
        new("σ Ori", 84.687, -2.600, 3.77),
        new("τ Ori", 83.273, -6.837, 3.59),
        new("Meissa (λ Ori)", 88.793, 9.934, 3.39),
        new("Tabit (π3 Ori)", 73.063, 6.961, 3.16),
        new("π4 Ori", 72.801, 5.605, 3.69),
        new("π5 Ori", 73.543, 2.441, 3.70),
        new("Trapezium (θ1 Ori)", 83.819, -5.387, 4.98),
        new("θ2 Ori", 83.857, -5.415, 5.02),
        new("Atlas (27 Tau)", 57.291, 24.053, 3.63),
        new("Electra (17 Tau)", 56.219, 24.114, 3.70),
        new("Maia (20 Tau)", 56.491, 24.368, 3.87),
        new("Merope (23 Tau)", 56.634, 23.948, 4.18),
        new("Taygeta (19 Tau)", 56.326, 24.468, 4.30),
        new("Pleione (28 Tau)", 57.295, 24.135, 5.05),
        new("Celaeno (16 Tau)", 56.104, 24.292, 5.45),
        new("Alcor (80 UMa)", 201.298, 54.988, 3.99),
        new("Tania Australis (μ UMa)", 157.564, 41.499, 3.06),
        new("Tania Borealis (λ UMa)", 154.274, 42.915, 3.45),
        new("Talitha (ι UMa)", 134.869, 48.042, 3.12),
        new("Theta UMa", 143.208, 51.677, 3.17),
        new("Muscida (ο UMa)", 128.966, 60.718, 3.35),
        new("Psi UMa", 166.427, 44.498, 3.00),
        new("Castor (α Gem)", 113.650, 31.888, 1.58),
        new("Pollux (β Gem)", 116.329, 28.026, 1.14),
        new("Alhena (γ Gem)", 99.428, 16.399, 1.93),
        new("Dubhe (α UMa)", 165.932, 61.751, 1.79),
        new("Merak (β UMa)", 165.460, 56.382, 2.37),
        new("Phecda (γ UMa)", 178.458, 53.695, 2.44),
        new("Megrez (δ UMa)", 183.857, 57.033, 3.31),
        new("Alioth (ε UMa)", 193.507, 55.960, 1.77),
        new("Mizar (ζ UMa)", 200.981, 54.925, 2.04),
        new("Alkaid (η UMa)", 206.885, 49.313, 1.86),
        new("Polaris (α UMi)", 37.954, 89.264, 1.98),
        new("Kochab (β UMi)", 222.676, 74.156, 2.08),
        new("Pherkad (γ UMi)", 230.182, 71.834, 3.05),
        new("Yildun (δ UMi)", 260.065, 86.586, 4.35),
        new("Urodelus (ε UMi)", 252.483, 82.037, 4.21),
        new("Akhfa (ζ UMi)", 236.438, 77.794, 4.28),
        new("Anwar (η UMi)", 244.600, 75.755, 4.95),
        new("5 UMi", 213.913, 75.696, 4.25),
        new("4 UMi", 212.871, 77.798, 5.00),
        new("HR 286", 14.869, 86.255, 5.86),
        new("HR 1662", 76.541, 88.026, 5.76),

        // === Leo / Leo Minor / Coma Berenices ===
        new("Regulus (α Leo)", 152.093, 11.967, 1.36),
        new("Algieba (γ Leo)", 154.993, 19.841, 2.01),
        new("Denebola (β Leo)", 177.265, 14.572, 2.14),
        new("Zosma (δ Leo)", 168.527, 20.524, 2.56),
        new("Chertan (θ Leo)", 168.827, 15.429, 3.32),
        new("Adhafera (ζ Leo)", 154.173, 23.417, 3.44),
        new("Al Jabhah (η Leo)", 151.833, 16.763, 3.48),
        new("Subra (ο Leo)", 147.669, 9.890, 3.52),
        new("Rasalas (μ Leo)", 148.190, 26.007, 3.88),
        new("Alterf (λ Leo)", 143.125, 22.968, 4.32),
        new("ρ Leo", 158.223, 9.307, 3.84),
        new("Diadem (α Com)", 197.982, 17.528, 4.32),
        new("β Com", 197.868, 27.878, 4.23),

        // === Canis Major / Canis Minor / Monoceros ===
        new("Sirius (α CMa)", 101.287, -16.716, -1.46),
        new("Adhara (ε CMa)", 104.656, -28.972, 1.50),
        new("Wezen (δ CMa)", 107.098, -26.393, 1.83),
        new("Murzim (β CMa)", 95.675, -17.956, 1.98),
        new("Aludra (η CMa)", 111.024, -29.303, 2.45),
        new("Furud (ζ CMa)", 95.033, -30.063, 3.02),
        new("Muliphein (γ CMa)", 106.037, -15.633, 4.11),
        new("Procyon (α CMi)", 114.825, 5.225, 0.38),
        new("Gomeisa (β CMi)", 111.787, 8.289, 2.89),

        // === Virgo / Corvus ===
        new("Spica (α Vir)", 201.298, -11.161, 0.98),
        new("Porrima (γ Vir)", 190.415, -1.449, 2.74),
        new("Vindemiatrix (ε Vir)", 195.544, 10.959, 2.85),
        new("Heze (ζ Vir)", 203.896, -0.667, 3.38),
        new("Zavijava (β Vir)", 177.674, 1.765, 3.59),
        new("Zaniah (η Vir)", 184.977, -0.667, 3.89),
        new("Gienah Corvi (γ Crv)", 183.951, -17.542, 2.58),
        new("Kraz (β Crv)", 188.960, -23.397, 2.65),
        new("Algorab (δ Crv)", 187.466, -16.515, 2.94),

        // === Sagittarius / Scutum ===
        new("Kaus Australis (ε Sgr)", 276.043, -34.384, 1.79),
        new("Nunki (σ Sgr)", 283.816, -26.297, 2.05),
        new("Ascella (ζ Sgr)", 285.653, -29.880, 2.60),
        new("Kaus Media (δ Sgr)", 275.242, -29.828, 2.72),
        new("Kaus Borealis (λ Sgr)", 276.993, -25.421, 2.82),
        new("Alnasl (γ2 Sgr)", 271.438, -30.424, 2.98),
        new("Arkab Prior (β1 Sgr)", 290.672, -44.460, 3.96),
        new("α Sct", 279.791, -8.243, 3.85),
        new("β Sct", 281.764, -4.747, 4.22),
    ];

    public record struct CandidateRegion(string RegionName, double RaDeg, double DecDeg, double MaxRadiusDeg = 36.0);

    public static readonly CandidateRegion[] CandidateSkyRegions =
    [
        // Targeted Deep Fields / Zoomed Telephoto (narrow radius 8° - 14°)
        new("M31 Andromeda Deep Field", 10.68, 41.27, 9.0),
        new("M42 Orion Nebula Deep Field", 83.82, -5.39, 9.0),
        new("M45 Pleiades Deep Field", 56.87, 24.11, 8.0),
        new("North Celestial Pole - Polaris Vicinity", 0.0, 89.0, 14.0),
        new("Double Cluster - Perseus Deep Field", 34.75, 57.15, 9.0),
        new("Cygnus Sadr / Veil Deep Field", 308.0, 38.0, 12.0),

        // Broad Constellation Regions (radius 32° - 38°)
        new("Andromeda - Triangulum - Cassiopeia", 14.0, 38.0, 36.0),
        new("Cassiopeia - Perseus - Andromeda", 28.0, 54.0, 36.0),
        new("Perseus - Cassiopeia - Auriga", 48.0, 48.0, 36.0),
        new("Perseus - Taurus - Pleiades", 56.0, 32.0, 36.0),
        new("Pegasus - Andromeda - Pisces", 350.0, 26.0, 36.0),
        new("Summer Triangle: Cygnus - Lyra - Aquila", 298.0, 30.0, 36.0),
        new("Cygnus - Lyra - Vulpecula", 295.0, 38.0, 36.0),
        new("Hercules - Lyra - Draco - Ophiuchus", 265.0, 32.0, 36.0),
        new("Aquila - Delphinus - Sagitta", 298.0, 12.0, 36.0),
        new("Cygnus - Cepheus - Lacerta", 320.0, 50.0, 36.0),
        new("Hercules - Corona Borealis - Boötes", 242.0, 30.0, 36.0),
        new("Orion - Taurus", 82.0, 4.0, 36.0),
        new("Auriga - Gemini - Taurus", 88.0, 32.0, 36.0),
        new("Ursa Major - Draco - Ursa Minor", 190.0, 58.0, 36.0),
        new("Leo - Leo Minor - Coma Berenices", 165.0, 18.0, 34.0),
        new("Virgo - Corvus - Spica", 195.0, -5.0, 34.0),
        new("Canis Major - Monoceros - Sirius", 102.0, -16.0, 32.0),
        new("Sagittarius - Scutum - Milky Way Core", 276.0, -25.0, 32.0),
        new("Boötes - Corona Borealis - Canes Venatici", 210.0, 35.0, 34.0),
    ];

    private readonly record struct StarTriangle(
        int I0,
        int I1,
        int I2,
        double RatioAB,
        double RatioBC,
        int Chirality,
        double Perimeter);

    public Task<PlateSolvingResult> SolveAsync(
        string imagePath,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<AstrophotographyObject>? detectedStars = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool tr = CataloguePlateSolver.UseTurkishLanguage;

            if (imageWidth <= 0 || imageHeight <= 0)
            {
                return PlateSolvingResult.Failed(tr ? "Geçersiz görüntü boyutları." : "Invalid image dimensions.", sw.Elapsed);
            }

            // 1. Extract clean, local-background-subtracted bright star centroids directly from the image
            var candidateStarSets = ExtractCandidateStarSets(imagePath, imageWidth, imageHeight, detectedStars);
            if (candidateStarSets.Count == 0)
            {
                return PlateSolvingResult.Failed(
                    tr ? "Yıldız üçgen eşlemesi için yeterli yıldız bulunamadı." : "Insufficient stars detected for triangle pattern matching.",
                    sw.Elapsed);
            }

            WcsMatrix? bestWcs = null;
            int bestInliers = 0;
            string bestRegion = string.Empty;

            foreach (var stars in candidateStarSets)
            {
                if (stars.Count < 6) continue;
                var wcs = MatchStarPatternRansac(stars, imageWidth, imageHeight, out int inliers, out string region);
                if (wcs != null && inliers > bestInliers)
                {
                    bestWcs = wcs;
                    bestInliers = inliers;
                    bestRegion = region;
                }
            }

            sw.Stop();

            if (bestWcs == null || bestInliers < 6)
            {
                return PlateSolvingResult.Failed(
                    tr ? "Yıldız konumlarından eşleşen gökyüzü üçgen deseni bulunamadı." : "No matching sky triangle pattern found from star positions.",
                    sw.Elapsed);
            }

            return PlateSolvingResult.Success(
                wcs: bestWcs,
                targetName: bestRegion,
                elapsed: sw.Elapsed,
                matchedStars: bestInliers,
                message: tr
                    ? $"{bestRegion} alanı yıldız üçgen geometrisiyle çözümlendi ({bestInliers} yıldız eşleşti)."
                    : $"{bestRegion} field solved via star triangle geometry ({bestInliers} stars matched).");
        }, cancellationToken);
    }

    public static List<List<DetectedStar>> ExtractCandidateStarSets(
        string imagePath,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<AstrophotographyObject>? detectedStars = null,
        int maxStars = 38)
    {
        var sets = new List<List<DetectedStar>>();

        if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            try
            {
                using var bmp = ImagePreprocessor.LoadImage(imagePath);
                using var balanced = ImagePreprocessor.AutoBalanceBrightness(bmp, out var info);

                if (info.WasAdjusted)
                {
                    var balancedStars = ExtractBrightStarCentroids(balanced, maxStars);
                    if (balancedStars.Count >= 6)
                    {
                        sets.Add(balancedStars);
                    }
                }

                var rawStars = ExtractBrightStarCentroids(bmp, maxStars);
                if (rawStars.Count >= 6)
                {
                    sets.Add(rawStars);
                }
            }
            catch
            {
                // Fallback to detectedStars list if bitmap loading fails
            }
        }

        if (detectedStars != null && detectedStars.Count >= 6)
        {
            sets.Add(detectedStars
                .OrderByDescending(s => s.Confidence * Math.Sqrt(Math.Max(1f, s.Box.Area)))
                .Take(maxStars)
                .Select(s => new DetectedStar(s.CenterX, s.CenterY, s.Confidence * Math.Max(1f, s.Box.Area)))
                .ToList());
        }

        return sets;
    }

    public static List<DetectedStar> ExtractImageStars(
        string imagePath,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<AstrophotographyObject>? detectedStars = null,
        int maxStars = 38)
    {
        var sets = ExtractCandidateStarSets(imagePath, imageWidth, imageHeight, detectedStars, maxStars);
        return sets.Count > 0 ? sets[0] : [];
    }

    /// <summary>
    /// Fast integral-image local-background-subtracted centroid detector that isolates the true bright
    /// constellation stars while ignoring nebulae, light pollution gradients, and panorama black borders.
    /// Works on both pitch-black skies (ringMean == 0) and bright skies.
    /// </summary>
    public static List<DetectedStar> ExtractBrightStarCentroids(SKBitmap bmp, int maxStars = 38)
    {
        int origW = bmp.Width;
        int origH = bmp.Height;
        double scale = Math.Min(1.0, 1000.0 / Math.Max(origW, origH));
        int w = Math.Max(64, (int)Math.Round(origW * scale));
        int h = Math.Max(64, (int)Math.Round(origH * scale));

        using var small = bmp.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888), SKSamplingOptions.Default);
        float[] lum = new float[w * h];
        var pixels = small.Pixels;

        for (int i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            lum[i] = (0.299f * c.Red) + (0.587f * c.Green) + (0.114f * c.Blue);
        }

        double[] integral = new double[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            double rowSum = 0;
            for (int x = 0; x < w; x++)
            {
                rowSum += lum[(y * w) + x];
                integral[((y + 1) * (w + 1)) + (x + 1)] = integral[(y * (w + 1)) + (x + 1)] + rowSum;
            }
        }

        double BoxSum(int x0, int y0, int x1, int y1)
        {
            x0 = Math.Clamp(x0, 0, w);
            y0 = Math.Clamp(y0, 0, h);
            x1 = Math.Clamp(x1, 0, w);
            y1 = Math.Clamp(y1, 0, h);
            return integral[(y1 * (w + 1)) + x1]
                 - integral[(y0 * (w + 1)) + x1]
                 - integral[(y1 * (w + 1)) + x0]
                 + integral[(y0 * (w + 1)) + x0];
        }

        float[] signal = new float[w * h];
        int bgRad = 12;
        int innerRad = 2;
        int marginX = Math.Max(16, (int)(w * 0.04));
        int marginY = Math.Max(16, (int)(h * 0.04));
        int outerArea = ((2 * bgRad) + 1) * ((2 * bgRad) + 1);
        int innerArea = ((2 * innerRad) + 1) * ((2 * innerRad) + 1);

        double sumDiffSq = 0;
        int countValid = 0;

        for (int y = marginY; y < h - marginY; y++)
        {
            for (int x = marginX; x < w - marginX; x++)
            {
                double innerSum = BoxSum(x - innerRad, y - innerRad, x + innerRad + 1, y + innerRad + 1);
                double innerMean = innerSum / innerArea;
                if (innerMean < 0.6) continue;

                double outerSum = BoxSum(x - bgRad, y - bgRad, x + bgRad + 1, y + bgRad + 1);
                double ringMean = (outerSum - innerSum) / (outerArea - innerArea);

                // Reject panorama black-border step edges where one side of the ring is black void (0)
                // and the other side is bright sky (> 4), while allowing true stars on dark sky (where all 4 sides are dark).
                double leftPatch = BoxSum(x - bgRad, y - 2, x - 4, y + 3) / 40.0;
                double rightPatch = BoxSum(x + 5, y - 2, x + bgRad + 1, y + 3) / 40.0;
                double topPatch = BoxSum(x - 2, y - bgRad, x + 3, y - 4) / 40.0;
                double botPatch = BoxSum(x - 2, y + 5, x + 3, y + bgRad + 1) / 40.0;

                double minSideBg = Math.Min(Math.Min(leftPatch, rightPatch), Math.Min(topPatch, botPatch));
                double maxSideBg = Math.Max(Math.Max(leftPatch, rightPatch), Math.Max(topPatch, botPatch));
                if (minSideBg < 0.15 && maxSideBg > 4.0 && innerMean < maxSideBg * 1.35)
                {
                    continue; // Panorama boundary step edge, not a star
                }

                double localBg = Math.Max(ringMean, maxSideBg * 0.85);
                double net = innerMean - localBg;
                if (net > 0)
                {
                    signal[(y * w) + x] = (float)net;
                    sumDiffSq += net * net;
                    countValid++;
                }
            }
        }

        double rms = countValid > 0 ? Math.Sqrt(sumDiffSq / countValid) : 1.0;
        double threshold = Math.Max(0.8, rms * 2.1);

        var candidates = new List<DetectedStar>();
        // Keep NMS radius small enough (~11px at 750px width) so close pairs like Beta/Gamma Lyr or Altair/Tarazed stay separate
        int nmsRad = Math.Max(7, Math.Min(w, h) / 65);

        for (int y = marginY; y < h - marginY; y++)
        {
            for (int x = marginX; x < w - marginX; x++)
            {
                float val = signal[(y * w) + x];
                if (val < threshold) continue;

                bool isMax = true;
                for (int dy = -2; dy <= 2 && isMax; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        if (signal[((y + dy) * w) + (x + dx)] > val)
                        {
                            isMax = false;
                            break;
                        }
                    }
                }
                if (!isMax) continue;

                double sumW = 0, sumX = 0, sumY = 0;
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        double wgt = Math.Max(0, signal[((y + dy) * w) + (x + dx)]);
                        sumW += wgt;
                        sumX += (x + dx) * wgt;
                        sumY += (y + dy) * wgt;
                    }
                }

                double cx = (sumW > 0 ? sumX / sumW : x) / scale;
                double cy = (sumW > 0 ? sumY / sumW : y) / scale;
                candidates.Add(new DetectedStar(cx, cy, sumW));
            }
        }

        candidates.Sort((a, b) => b.Flux.CompareTo(a.Flux));
        var accepted = new List<DetectedStar>();
        double minDistOrig = nmsRad / scale;

        foreach (var c in candidates)
        {
            bool tooClose = false;
            foreach (var a in accepted)
            {
                double dx = c.X - a.X;
                double dy = c.Y - a.Y;
                if ((dx * dx) + (dy * dy) < minDistOrig * minDistOrig)
                {
                    tooClose = true;
                    break;
                }
            }
            if (!tooClose)
            {
                accepted.Add(c);
                if (accepted.Count >= maxStars) break;
            }
        }

        return accepted;
    }

    /// <summary>
    /// Matches detected image star positions against the J2000 bright star catalog across sky regions
    /// using scale/rotation-invariant triangle ratios and 2D similarity-transform RANSAC verification.
    /// Rejects accidental false-positive alignments using tight geometric residual and bright-star concordance checks.
    /// </summary>
    public static WcsMatrix? MatchStarPatternRansac(
        IReadOnlyList<DetectedStar> imageStars,
        int imageWidth,
        int imageHeight,
        out int bestInlierCount,
        out string bestRegionName)
    {
        bestInlierCount = 0;
        bestRegionName = string.Empty;

        int nImgAll = Math.Min(35, imageStars.Count);
        int nImgTri = Math.Min(18, nImgAll);
        if (nImgTri < 6) return null;

        double imgDiag = Math.Sqrt(((double)imageWidth * imageWidth) + ((double)imageHeight * imageHeight));
        double minSidePx = imgDiag * 0.05;
        double inlierTolPx = imgDiag * 0.0125; // 1.25% of image diagonal

        // Build image star triangles from top brightest stars
        var imgTriangles = BuildTriangles(
            nImgTri,
            i => (imageStars[i].X, imageStars[i].Y),
            minSidePx,
            imgDiag * 0.92);

        double bestScore = -1.0;
        WcsMatrix? bestWcs = null;

        foreach (var region in CandidateSkyRegions)
        {
            var regionCenter = new CelestialCoordinates(region.RaDeg, region.DecDeg);
            var refTangentWcs = new WcsMatrix(regionCenter, 0.0, 0.0, 3600.0, 0.0, 1000, 1000);

            var regionalCat = new List<(CatalogStar Star, double U, double V, double Weight)>();
            foreach (var cs in BrightStarCatalog)
            {
                var sc = new CelestialCoordinates(cs.RaDeg, cs.DecDeg);
                double angDist = regionCenter.AngularDistanceTo(sc);
                if (angDist <= region.MaxRadiusDeg && refTangentWcs.EquatorialToPixel(sc, out double u, out double v))
                {
                    double wgt = Math.Max(0.6, 5.2 - cs.Mag);
                    regionalCat.Add((cs, u, v, wgt));
                }
            }

            if (regionalCat.Count < 6) continue;

            // Sort by brightness (lowest magnitude first) and cap uniformly across regions to prevent density bias
            regionalCat.Sort((a, b) => a.Star.Mag.CompareTo(b.Star.Mag));
            int nCatAll = Math.Min(32, regionalCat.Count);
            int nCatTri = Math.Min(20, nCatAll);

            double minCatSep = 0.35;
            var catTriangles = BuildTriangles(
                nCatTri,
                i => (regionalCat[i].U, regionalCat[i].V),
                minCatSep,
                52.0); // max 52 degrees separation

            const double ratioTol = 0.018;
            double minAllowedFov = 0.4;

            foreach (var it in imgTriangles)
            {
                foreach (var ct in catTriangles)
                {
                    if (it.Chirality != ct.Chirality) continue;
                    if (Math.Abs(it.RatioAB - ct.RatioAB) > ratioTol) continue;
                    if (Math.Abs(it.RatioBC - ct.RatioBC) > ratioTol) continue;

                    double degPerPx = ct.Perimeter / it.Perimeter;
                    double impliedFovDiag = degPerPx * imgDiag;
                    if (impliedFovDiag < minAllowedFov || impliedFovDiag > 95.0) continue;

                    Span<(double X, double Y, double U, double V, double W)> triPairs =
                    [
                        (imageStars[it.I0].X, imageStars[it.I0].Y, regionalCat[ct.I0].U, regionalCat[ct.I0].V, 1.0),
                        (imageStars[it.I1].X, imageStars[it.I1].Y, regionalCat[ct.I1].U, regionalCat[ct.I1].V, 1.0),
                        (imageStars[it.I2].X, imageStars[it.I2].Y, regionalCat[ct.I2].U, regionalCat[ct.I2].V, 1.0),
                    ];

                    if (!FitSimilarityTransform(triPairs, out double a, out double b, out double tu, out double tv))
                        continue;

                    double s = Math.Sqrt((a * a) + (b * b));
                    if (s < 1e-6) continue;

                    double tolDeg = inlierTolPx * s;
                    var matchedPairs = CollectUniqueInlierPairs(
                        imageStars, nImgAll, regionalCat, nCatAll, a, b, tu, tv, tolDeg);

                    int reqPairs = region.MaxRadiusDeg <= 15.0 ? 6 : 7;
                    if (matchedPairs.Count >= reqPairs)
                    {
                        double rawScore = matchedPairs.Sum(p => p.W);
                        if (rawScore > bestScore * 0.85 &&
                            FitSimilarityTransform(matchedPairs.ToArray(), out double ra, out double rb, out double rtu, out double rtv))
                        {
                            var candidateWcs = BuildCenteredWcsFromMatchedStars(
                                refTangentWcs,
                                matchedPairs,
                                regionalCat,
                                imageStars,
                                ra, rb, rtu, rtv,
                                imageWidth,
                                imageHeight,
                                out int refinedInliers,
                                out double refinedScore);

                            int minInliersReq = reqPairs;
                            if (candidateWcs != null && refinedInliers >= minInliersReq && refinedScore > bestScore)
                            {
                                bestScore = refinedScore;
                                bestInlierCount = refinedInliers;
                                bestRegionName = region.RegionName;
                                bestWcs = candidateWcs;
                            }
                        }
                    }
                }
            }
        }

        return bestWcs;
    }

    private static List<StarTriangle> BuildTriangles(
        int count,
        Func<int, (double X, double Y)> getPt,
        double minSide,
        double maxSide)
    {
        var list = new List<StarTriangle>();
        for (int i = 0; i < count - 2; i++)
        {
            var p0 = getPt(i);
            for (int j = i + 1; j < count - 1; j++)
            {
                var p1 = getPt(j);
                double d01 = Hypot(p1.X - p0.X, p1.Y - p0.Y);
                if (d01 < minSide || d01 > maxSide) continue;

                for (int k = j + 1; k < count; k++)
                {
                    var p2 = getPt(k);
                    double d12 = Hypot(p2.X - p1.X, p2.Y - p1.Y);
                    double d20 = Hypot(p0.X - p2.X, p0.Y - p2.Y);
                    if (d12 < minSide || d12 > maxSide || d20 < minSide || d20 > maxSide) continue;

                    // Order vertices so opposite sides are sorted: sideA <= sideB <= sideC
                    // Vertex opposite to sideA is v0, opposite to sideB is v1, opposite to sideC is v2
                    int v0 = i, v1 = j, v2 = k;
                    double sA = d12, sB = d20, sC = d01;

                    if (sA > sB) { (sA, sB) = (sB, sA); (v0, v1) = (v1, v0); }
                    if (sB > sC) { (sB, sC) = (sC, sB); (v1, v2) = (v2, v1); }
                    if (sA > sB) { (sA, sB) = (sB, sA); (v0, v1) = (v1, v0); }

                    if (sC < 1e-6) continue;
                    // Reject nearly collinear degenerate triangles
                    if ((sA + sB) < sC * 1.03) continue;

                    var pv0 = getPt(v0);
                    var pv1 = getPt(v1);
                    var pv2 = getPt(v2);
                    double cross = ((pv1.X - pv0.X) * (pv2.Y - pv0.Y)) - ((pv1.Y - pv0.Y) * (pv2.X - pv0.X));
                    int chirality = cross >= 0 ? 1 : -1;

                    list.Add(new StarTriangle(
                        v0, v1, v2,
                        sA / sC,
                        sB / sC,
                        chirality,
                        sA + sB + sC));
                }
            }
        }
        return list;
    }

    private static List<(double X, double Y, double U, double V, double W)> CollectUniqueInlierPairs(
        IReadOnlyList<DetectedStar> imageStars,
        int nImg,
        List<(CatalogStar Star, double U, double V, double Weight)> regionalCat,
        int nCat,
        double a,
        double b,
        double tu,
        double tv,
        double tolDeg)
    {
        double tolSq = tolDeg * tolDeg;
        var candidates = new List<(int ImgIdx, int CatIdx, double DistSq, double Weight)>();

        for (int i = 0; i < nImg; i++)
        {
            double ix = imageStars[i].X;
            double iy = imageStars[i].Y;
            double predU = (a * ix) - (b * iy) + tu;
            double predV = (b * ix) + (a * iy) + tv;

            for (int j = 0; j < nCat; j++)
            {
                double du = predU - regionalCat[j].U;
                double dv = predV - regionalCat[j].V;
                double d2 = (du * du) + (dv * dv);
                if (d2 <= tolSq)
                {
                    double w = regionalCat[j].Weight * (1.0 + ((nImg - i) / (double)nImg));
                    candidates.Add((i, j, d2, w));
                }
            }
        }

        candidates.Sort((x, y) => x.DistSq.CompareTo(y.DistSq));
        var usedImg = new HashSet<int>();
        var usedCat = new HashSet<int>();
        var result = new List<(double X, double Y, double U, double V, double W)>();

        foreach (var c in candidates)
        {
            if (usedImg.Add(c.ImgIdx) && usedCat.Add(c.CatIdx))
            {
                result.Add((
                    imageStars[c.ImgIdx].X,
                    imageStars[c.ImgIdx].Y,
                    regionalCat[c.CatIdx].U,
                    regionalCat[c.CatIdx].V,
                    c.Weight));
            }
        }

        return result;
    }

    /// <summary>
    /// Converts a regional tangent-plane similarity solution into an exact WcsMatrix centered at the
    /// image's true pixel center (imageWidth/2, imageHeight/2) and refines rotation & scale directly
    /// on the image-centered tangent plane. Also validates bright-star concordance to block random false positives.
    /// </summary>
    private static WcsMatrix? BuildCenteredWcsFromMatchedStars(
        WcsMatrix refTangentWcs,
        List<(double X, double Y, double U, double V, double W)> initialPairs,
        List<(CatalogStar Star, double U, double V, double Weight)> regionalCat,
        IReadOnlyList<DetectedStar> imageStars,
        double a,
        double b,
        double tu,
        double tv,
        int imageWidth,
        int imageHeight,
        out int finalInliers,
        out double finalScore)
    {
        finalInliers = 0;
        finalScore = 0;

        double cx = imageWidth / 2.0;
        double cy = imageHeight / 2.0;
        double imgDiag = Hypot(imageWidth, imageHeight);

        // 1. Find tangent coordinates of the image center on the regional reference plane
        double uCenter = (a * cx) - (b * cy) + tu;
        double vCenter = (b * cx) + (a * cy) + tv;
        CelestialCoordinates centerCoord = refTangentWcs.PixelToEquatorial(uCenter, vCenter);

        // 2. Re-project catalog stars onto a tangent plane centered directly at centerCoord!
        // Performing a second iteration ensures zero tangent-plane re-centering drift.
        for (int iter = 0; iter < 2; iter++)
        {
            var centeredTan = new WcsMatrix(centerCoord, 0.0, 0.0, 3600.0, 0.0, 1000, 1000);
            var centeredCat = new List<(CatalogStar Star, double U, double V, double Weight)>(regionalCat.Count);

            foreach (var item in regionalCat)
            {
                var sc = new CelestialCoordinates(item.Star.RaDeg, item.Star.DecDeg);
                if (centeredTan.EquatorialToPixel(sc, out double cu, out double cv))
                {
                    centeredCat.Add((item.Star, cu, cv, item.Weight));
                }
            }

            if (centeredCat.Count < 6) return null;

            // Map the initial matched pairs to the new centered tangent plane
            var remapped = new List<(double X, double Y, double U, double V, double W)>();
            foreach (var p in initialPairs)
            {
                int idx = regionalCat.FindIndex(rc => Math.Abs(rc.U - p.U) < 1e-6 && Math.Abs(rc.V - p.V) < 1e-6);
                if (idx >= 0)
                {
                    var sc = new CelestialCoordinates(regionalCat[idx].Star.RaDeg, regionalCat[idx].Star.DecDeg);
                    if (centeredTan.EquatorialToPixel(sc, out double cu, out double cv))
                    {
                        remapped.Add((p.X, p.Y, cu, cv, p.W));
                    }
                }
            }

            if (!FitSimilarityTransform(remapped.ToArray(), out a, out b, out tu, out tv))
                return null;

            double scaleDegPerPx = Math.Sqrt((a * a) + (b * b));
            double tolDeg = scaleDegPerPx * imgDiag * 0.013; // Tight 1.3% diagonal tolerance

            var refinedPairs = CollectUniqueInlierPairs(
                imageStars,
                Math.Min(32, imageStars.Count),
                centeredCat,
                Math.Min(32, centeredCat.Count),
                a, b, tu, tv,
                tolDeg);

            if (refinedPairs.Count >= 6 &&
                FitSimilarityTransform(refinedPairs.ToArray(), out a, out b, out tu, out tv))
            {
                initialPairs = refinedPairs;
                regionalCat = centeredCat;
                uCenter = (a * cx) - (b * cy) + tu;
                vCenter = (b * cx) + (a * cy) + tv;
                centerCoord = centeredTan.PixelToEquatorial(uCenter, vCenter);
            }
            else
            {
                return null;
            }
        }

        double finalDegPerPx = Math.Sqrt((a * a) + (b * b));
        if (finalDegPerPx < 1e-6) return null;

        // Compute normalized RMS astrometric residual error across matched pairs
        double sumErrSq = 0;
        foreach (var p in initialPairs)
        {
            double predU = (a * p.X) - (b * p.Y) + tu;
            double predV = (b * p.X) + (a * p.Y) + tv;
            double du = predU - p.U;
            double dv = predV - p.V;
            sumErrSq += (du * du) + (dv * dv);
        }
        double rmsDeg = Math.Sqrt(sumErrSq / initialPairs.Count);
        double rmsNormDiag = rmsDeg / (finalDegPerPx * imgDiag);
        if (rmsNormDiag > 0.015)
        {
            return null; // Reject high-residual accidental alignments
        }

        double pixelScaleArcsec = finalDegPerPx * 3600.0;
        double rotationDeg = Math.Atan2(b, a) * (180.0 / Math.PI);
        rotationDeg = ((rotationDeg % 360.0) + 360.0) % 360.0;

        var candidateWcs = new WcsMatrix(
            centerCoord,
            cx,
            cy,
            pixelScaleArcsec,
            rotationDeg,
            imageWidth,
            imageHeight);

        // Bright-star concordance verification:
        // Ensure prominent catalog stars that project inside the frame are not systematically missing.
        int brightInFrame = 0;
        int brightMatched = 0;
        double marginX = imageWidth * 0.06;
        double marginY = imageHeight * 0.06;
        int topCheckCount = Math.Min(16, regionalCat.Count);

        for (int i = 0; i < topCheckCount; i++)
        {
            var catItem = regionalCat[i];
            var sc = new CelestialCoordinates(catItem.Star.RaDeg, catItem.Star.DecDeg);
            if (candidateWcs.EquatorialToPixel(sc, out double px, out double py))
            {
                if (px >= marginX && px <= imageWidth - marginX &&
                    py >= marginY && py <= imageHeight - marginY)
                {
                    brightInFrame++;
                    bool isMatched = initialPairs.Any(p =>
                        Math.Abs(p.U - catItem.U) < 1e-4 && Math.Abs(p.V - catItem.V) < 1e-4);
                    if (isMatched)
                    {
                        brightMatched++;
                    }
                }
            }
        }

        // Concordance verification: ensure prominent catalog stars are matched when abundant
        if (brightInFrame >= 6 && brightMatched < Math.Max(2, (int)Math.Ceiling(brightInFrame * 0.30)))
        {
            return null;
        }

        finalInliers = initialPairs.Count;
        finalScore = initialPairs.Sum(p => p.W) * (1.0 - (rmsNormDiag * 30.0));

        return candidateWcs;
    }

    /// <summary>
    /// Weighted linear least-squares similarity transform solver:
    /// u = a * x - b * y + tu
    /// v = b * x + a * y + tv
    /// </summary>
    private static bool FitSimilarityTransform(
        ReadOnlySpan<(double X, double Y, double U, double V, double W)> pairs,
        out double a,
        out double b,
        out double tu,
        out double tv)
    {
        a = 0; b = 0; tu = 0; tv = 0;
        if (pairs.Length < 2) return false;

        double sumW = 0, mx = 0, my = 0, mu = 0, mv = 0;
        foreach (var p in pairs)
        {
            sumW += p.W;
            mx += p.W * p.X;
            my += p.W * p.Y;
            mu += p.W * p.U;
            mv += p.W * p.V;
        }
        if (sumW <= 1e-9) return false;

        mx /= sumW;
        my /= sumW;
        mu /= sumW;
        mv /= sumW;

        double denom = 0, numA = 0, numB = 0;
        foreach (var p in pairs)
        {
            double dx = p.X - mx;
            double dy = p.Y - my;
            double du = p.U - mu;
            double dv = p.V - mv;

            denom += p.W * ((dx * dx) + (dy * dy));
            numA += p.W * ((dx * du) + (dy * dv));
            numB += p.W * ((dx * dv) - (dy * du));
        }

        if (denom <= 1e-9) return false;

        a = numA / denom;
        b = numB / denom;
        tu = mu - ((a * mx) - (b * my));
        tv = mv - ((b * mx) + (a * my));
        return true;
    }

    private static double Hypot(double dx, double dy) => Math.Sqrt((dx * dx) + (dy * dy));
}
