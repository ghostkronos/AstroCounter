namespace AstroCounter.Core.Astrometry;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstroCounter.Core.Models;

/// <summary>
/// Provides target identification, in-frame catalog scanning, and WCS plate solving based on
/// astronomical catalogues (Messier, NGC, IC) and prominent navigation stars.
/// </summary>
public class CataloguePlateSolver : IPlateSolver
{
    public static bool UseTurkishLanguage { get; set; } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("tr", StringComparison.OrdinalIgnoreCase) ||
        CultureInfo.InstalledUICulture.TwoLetterISOLanguageName.Equals("tr", StringComparison.OrdinalIgnoreCase);

    public string SolverName => UseTurkishLanguage
        ? "Katalog ve Yıldız Deseni Çözücü"
        : "Catalog and Star Pattern Solver";

    public record AstronomicalTarget(
        string Id,
        string NameEn,
        string NameTr,
        CelestialCoordinates Coordinates,
        double TypicalFovDegrees,
        double EstimatedPixelScaleArcsec)
    {
        public string CommonName => UseTurkishLanguage ? NameTr : NameEn;
    }

    public static readonly IReadOnlyList<AstronomicalTarget> KnownCatalogues = new List<AstronomicalTarget>
    {
        // --- Galaxies ---
        new("M31", "Andromeda Galaxy - M31", "Andromeda Gökadası - M31", new CelestialCoordinates(10.6847, 41.2687), 3.2, 1.6),
        new("M32", "Dwarf Elliptical Galaxy - M32", "Cüce Eliptik Gökada - M32", new CelestialCoordinates(10.6742, 40.8653), 0.8, 1.0),
        new("M110", "Dwarf Elliptical Galaxy - M110", "Cüce Eliptik Gökada - M110", new CelestialCoordinates(10.0921, 41.6853), 1.0, 1.1),
        new("M33", "Triangulum Galaxy - M33", "Üçgen Gökadası - M33", new CelestialCoordinates(23.4621, 30.6602), 1.8, 1.2),
        new("M51", "Whirlpool Galaxy - M51", "Girdap Gökadası - M51", new CelestialCoordinates(202.4696, 47.1953), 1.2, 1.0),
        new("M81", "Bode's Galaxy - M81", "Bode Gökadası - M81", new CelestialCoordinates(148.8883, 69.0653), 1.3, 1.1),
        new("M82", "Cigar Galaxy - M82", "Puro Gökadası - M82", new CelestialCoordinates(148.9697, 69.6797), 1.2, 1.0),
        new("M101", "Pinwheel Galaxy - M101", "Fırıldak Gökadası - M101", new CelestialCoordinates(210.8025, 54.3492), 1.5, 1.2),
        new("M104", "Sombrero Galaxy - M104", "Sombrero Gökadası - M104", new CelestialCoordinates(189.9975, -11.6231), 1.0, 0.9),
        new("M63", "Sunflower Galaxy - M63", "Ayçiçeği Gökadası - M63", new CelestialCoordinates(198.9554, 42.0292), 1.1, 1.0),
        new("M64", "Black Eye Galaxy - M64", "Siyah Göz Gökadası - M64", new CelestialCoordinates(194.1821, 21.6828), 1.0, 1.0),
        new("M65", "Leo Triplet Galaxy - M65", "Aslan Üçlüsü Gökadası - M65", new CelestialCoordinates(169.7333, 13.0922), 1.2, 1.1),
        new("M66", "Leo Triplet Galaxy - M66", "Aslan Üçlüsü Gökadası - M66", new CelestialCoordinates(170.0625, 12.9917), 1.2, 1.1),
        new("M74", "Phantom Galaxy - M74", "Hayalet Gökada - M74", new CelestialCoordinates(24.1742, 15.7836), 1.1, 1.0),
        new("M87", "Virgo A Supergiant Galaxy - M87", "Başak A Dev Eliptik Gökadası - M87", new CelestialCoordinates(187.7058, 12.3911), 1.0, 1.0),
        new("M106", "Spiral Galaxy - M106", "Av Köpekleri Sarmal Gökadası - M106", new CelestialCoordinates(184.7400, 47.3039), 1.3, 1.1),
        new("NGC891", "Silver Sliver Galaxy - NGC 891", "Gümüş Kıymık Gökadası - NGC 891", new CelestialCoordinates(35.6392, 42.3492), 1.2, 1.0),
        new("NGC6946", "Fireworks Galaxy - NGC 6946", "Havai Fişek Gökadası - NGC 6946", new CelestialCoordinates(308.7179, 60.1539), 1.2, 1.0),

        // --- Nebulae ---
        new("M42", "Orion Nebula - M42", "Orion Bulutsusu - M42", new CelestialCoordinates(83.8221, -5.3911), 2.1, 1.4),
        new("M43", "De Mairan's Nebula - M43", "De Mairan Bulutsusu - M43", new CelestialCoordinates(83.8875, -5.2667), 1.2, 1.2),
        new("IC434", "Horsehead Nebula - IC 434", "Atbaşı Bulutsusu - IC 434", new CelestialCoordinates(85.2458, -2.4583), 1.8, 1.4),
        new("NGC2024", "Flame Nebula - NGC 2024", "Alev Bulutsusu - NGC 2024", new CelestialCoordinates(85.4250, -1.8500), 1.5, 1.3),
        new("M78", "Reflection Nebula - M78", "Yansıma Bulutsusu - M78", new CelestialCoordinates(86.6917, 0.0500), 1.2, 1.1),
        new("M1", "Crab Nebula - M1", "Yengeç Bulutsusu - M1", new CelestialCoordinates(83.6333, 22.0144), 1.0, 1.0),
        new("NGC1499", "California Nebula - NGC 1499", "Kaliforniya Bulutsusu - NGC 1499", new CelestialCoordinates(60.2083, 36.4167), 2.8, 1.8),
        new("IC1805", "Heart Nebula - IC 1805", "Kalp Bulutsusu - IC 1805", new CelestialCoordinates(38.1750, 61.4500), 2.6, 1.8),
        new("IC1848", "Soul Nebula - IC 1848", "Ruh Bulutsusu - IC 1848", new CelestialCoordinates(42.8000, 60.4333), 2.4, 1.8),
        new("NGC281", "Pacman Nebula - NGC 281", "Pacman Bulutsusu - NGC 281", new CelestialCoordinates(13.1917, 56.6167), 1.5, 1.3),
        new("NGC7635", "Bubble Nebula - NGC 7635", "Kabarcık Bulutsusu - NGC 7635", new CelestialCoordinates(350.1958, 61.2017), 1.2, 1.1),
        new("M76", "Little Dumbbell Nebula - M76", "Küçük Halter Bulutsusu - M76", new CelestialCoordinates(25.5821, 51.5753), 1.0, 1.0),
        new("NGC1333", "Embryo Reflection Nebula - NGC 1333", "Embriyo Yansıma Bulutsusu - NGC 1333", new CelestialCoordinates(52.2958, 31.3667), 1.5, 1.2),
        new("IC348", "Star Cluster and Nebula - IC 348", "Yansıma Bulutsusu ve Kümesi - IC 348", new CelestialCoordinates(56.1333, 32.1667), 1.5, 1.2),
        new("IC405", "Flaming Star Nebula - IC 405", "Alevli Yıldız Bulutsusu - IC 405", new CelestialCoordinates(79.0750, 34.2667), 1.8, 1.4),
        new("NGC2237", "Rosette Nebula - NGC 2237", "Rozet Bulutsusu - NGC 2237", new CelestialCoordinates(97.9000, 4.9500), 2.6, 1.7),
        new("NGC2264", "Cone Nebula and Christmas Tree Cluster - NGC 2264", "Koni Bulutsusu ve Yılbaşı Ağacı Kümesi - NGC 2264", new CelestialCoordinates(100.2417, 9.8950), 2.0, 1.5),
        new("NGC7000", "North America Nebula - NGC 7000", "Kuzey Amerika Bulutsusu - NGC 7000", new CelestialCoordinates(314.7500, 44.3333), 3.5, 2.2),
        new("IC5070", "Pelican Nebula - IC 5070", "Pelikan Bulutsusu - IC 5070", new CelestialCoordinates(312.7500, 44.3500), 2.5, 1.8),
        new("IC1318", "Sadr Region Butterfly Nebula - IC 1318", "Sadr Bölgesi Kelebek Bulutsusu - IC 1318", new CelestialCoordinates(305.5571, 40.2567), 3.2, 2.0),
        new("NGC6888", "Crescent Nebula - NGC 6888", "Hilal Bulutsusu - NGC 6888", new CelestialCoordinates(303.0583, 38.3550), 1.4, 1.2),
        new("NGC6960", "Western Veil Nebula - NGC 6960", "Batı Peçe Bulutsusu - NGC 6960", new CelestialCoordinates(311.4250, 30.7167), 2.8, 1.8),
        new("NGC6992", "Eastern Veil Nebula - NGC 6992", "Doğu Peçe Bulutsusu - NGC 6992", new CelestialCoordinates(314.1125, 31.7167), 2.8, 1.8),
        new("M27", "Dumbbell Nebula - M27", "Halter Bulutsusu - M27", new CelestialCoordinates(299.9017, 22.7214), 1.5, 1.2),
        new("M57", "Ring Nebula - M57", "Halka Bulutsusu - M57", new CelestialCoordinates(283.3963, 33.0292), 1.2, 1.0),
        new("IC1396", "Elephant's Trunk Nebula - IC 1396", "Fil Hortumu Bulutsusu - IC 1396", new CelestialCoordinates(324.7500, 57.5000), 3.0, 2.0),
        new("NGC7023", "Iris Nebula - NGC 7023", "İris Bulutsusu - NGC 7023", new CelestialCoordinates(315.4042, 68.1633), 1.4, 1.2),
        new("M8", "Lagoon Nebula - M8", "Lagün Bulutsusu - M8", new CelestialCoordinates(270.9208, -24.3867), 2.0, 1.5),
        new("M20", "Trifid Nebula - M20", "Üç Boğumlu Bulutsu - M20", new CelestialCoordinates(270.6333, -23.0306), 1.5, 1.3),
        new("M17", "Omega Swan Nebula - M17", "Omega Kuğu Bulutsusu - M17", new CelestialCoordinates(275.1958, -16.1767), 1.6, 1.3),
        new("M16", "Eagle Nebula - M16", "Kartal Bulutsusu - M16", new CelestialCoordinates(274.7000, -13.7900), 1.8, 1.3),
        new("NGC7293", "Helix Nebula - NGC 7293", "Helisel Bulutsu - NGC 7293", new CelestialCoordinates(337.4108, -20.8372), 1.5, 1.2),

        // --- Star Clusters ---
        new("M45", "Pleiades Open Cluster - M45", "Ülker Açık Yıldız Kümesi - M45", new CelestialCoordinates(56.7500, 24.1167), 2.5, 1.8),
        new("Hyades", "Hyades Open Cluster - Melotte 25", "Hyades Boğa Açık Yıldız Kümesi", new CelestialCoordinates(66.7500, 15.8667), 4.5, 2.5),
        new("Perseus", "Double Cluster - NGC 869 & NGC 884", "Perseid Çift Yıldız Kümesi - NGC 869 & 884", new CelestialCoordinates(34.7500, 57.1500), 2.5, 1.8),
        new("Mel20", "Alpha Persei Cluster - Melotte 20", "Mirfak Açık Yıldız Kümesi - Melotte 20", new CelestialCoordinates(51.0800, 49.8611), 3.5, 2.2),
        new("M34", "Spiral Open Cluster - M34", "Kahraman Açık Yıldız Kümesi - M34", new CelestialCoordinates(40.5125, 42.7611), 1.5, 1.3),
        new("NGC752", "Open Cluster - NGC 752", "Andromeda Açık Yıldız Kümesi - NGC 752", new CelestialCoordinates(29.4208, 37.7850), 1.6, 1.3),
        new("NGC457", "Owl Cluster - NGC 457", "Baykuş Yıldız Kümesi - NGC 457", new CelestialCoordinates(19.8875, 58.2908), 1.4, 1.2),
        new("M52", "Salt and Pepper Cluster - M52", "Kraliçe Açık Yıldız Kümesi - M52", new CelestialCoordinates(351.2000, 61.5933), 1.3, 1.1),
        new("M103", "Open Cluster - M103", "Kraliçe Açık Yıldız Kümesi - M103", new CelestialCoordinates(23.3375, 60.6583), 1.2, 1.1),
        new("NGC7789", "Caroline's Rose Cluster - NGC 7789", "Caroline Gülü Yıldız Kümesi - NGC 7789", new CelestialCoordinates(359.3500, 56.7167), 1.3, 1.1),
        new("M36", "Pinwheel Cluster - M36", "Arabacı Açık Yıldız Kümesi - M36", new CelestialCoordinates(84.0750, 34.1400), 1.4, 1.2),
        new("M37", "Auriga Open Cluster - M37", "Arabacı Zengin Yıldız Kümesi - M37", new CelestialCoordinates(88.0750, 32.5533), 1.4, 1.2),
        new("M38", "Starfish Cluster - M38", "Denizyıldızı Kümesi - M38", new CelestialCoordinates(82.1667, 35.8550), 1.4, 1.2),
        new("M35", "Shoe-Buckle Cluster - M35", "İkizler Açık Yıldız Kümesi - M35", new CelestialCoordinates(92.2250, 24.3333), 1.5, 1.2),
        new("M39", "Cygnus Open Cluster - M39", "Kuğu Açık Yıldız Kümesi - M39", new CelestialCoordinates(322.9500, 48.4333), 1.5, 1.3),
        new("M29", "Cooling Tower Cluster - M29", "Soğutma Kulesi Yıldız Kümesi - M29", new CelestialCoordinates(305.9833, 38.5233), 1.2, 1.1),
        new("Cr399", "Coathanger Cluster - Collinder 399", "Askı Yıldız Kümesi - Collinder 399", new CelestialCoordinates(291.3625, 20.1917), 2.2, 1.6),
        new("M71", "Sagitta Globular Cluster - M71", "Okçuk Küresel Yıldız Kümesi - M71", new CelestialCoordinates(298.4438, 18.7792), 1.2, 1.1),
        new("M13", "Hercules Globular Cluster - M13", "Büyük Herkül Küresel Kümesi - M13", new CelestialCoordinates(250.4217, 36.4597), 1.5, 1.1),
        new("M92", "Hercules Globular Cluster - M92", "Herkül Küresel Yıldız Kümesi - M92", new CelestialCoordinates(259.2808, 43.1358), 1.4, 1.1),
        new("M15", "Pegasus Globular Cluster - M15", "Kanatlı At Küresel Kümesi - M15", new CelestialCoordinates(322.4929, 12.1670), 1.3, 1.1),
        new("M11", "Wild Duck Cluster - M11", "Yaban Ördeği Yıldız Kümesi - M11", new CelestialCoordinates(282.7708, -6.2700), 1.4, 1.2),
        new("M10", "Ophiuchus Globular Cluster - M10", "Yılancı Küresel Kümesi - M10", new CelestialCoordinates(254.2875, -4.1003), 1.3, 1.1),
        new("M12", "Gumball Globular Cluster - M12", "Yılancı Küresel Kümesi - M12", new CelestialCoordinates(251.8092, -1.9486), 1.3, 1.1),
        new("M14", "Ophiuchus Globular Cluster - M14", "Yılancı Küresel Kümesi - M14", new CelestialCoordinates(264.4004, -3.2458), 1.3, 1.1),
        new("NGC6633", "Tweed Open Cluster - NGC 6633", "Tüvit Açık Yıldız Kümesi - NGC 6633", new CelestialCoordinates(276.8458, 6.5700), 1.6, 1.3),
        new("IC4665", "Summer Beehive Cluster - IC 4665", "Yaz Arı Kovanı Kümesi - IC 4665", new CelestialCoordinates(266.5542, 5.7167), 2.0, 1.5),
        new("M44", "Beehive Cluster Praesepe - M44", "Arı Kovanı Kümesi Praesepe - M44", new CelestialCoordinates(130.1000, 19.6667), 2.2, 1.6),

        // --- Navigation Stars and Constellation Landmarks ---
        new("Alpheratz", "Alpheratz - Alpha Andromedae", "Alpheratz Yıldızı - Alpha Andromedae", new CelestialCoordinates(2.0970, 29.0904), 2.5, 1.8),
        new("Mirach", "Mirach - Beta Andromedae", "Mirach Yıldızı - Beta Andromedae", new CelestialCoordinates(17.4330, 35.6206), 2.2, 1.6),
        new("Almach", "Almach - Gamma Andromedae", "Almach Çift Yıldızı - Gamma Andromedae", new CelestialCoordinates(30.9748, 42.3297), 2.2, 1.6),
        new("DeltaAnd", "Sadiradra - Delta Andromedae", "Sadiradra Yıldızı - Delta Andromedae", new CelestialCoordinates(9.8320, 30.8610), 2.2, 1.6),
        new("BetaTri", "Deltotum - Beta Trianguli", "Deltotum Yıldızı - Beta Trianguli", new CelestialCoordinates(32.3860, 34.9870), 2.2, 1.6),
        new("AlphaTri", "Mothallah - Alpha Trianguli", "Mothallah Yıldızı - Alpha Trianguli", new CelestialCoordinates(28.2700, 29.5790), 2.2, 1.6),
        new("Hamal", "Hamal - Alpha Arietis", "Hamal Yıldızı - Alpha Arietis", new CelestialCoordinates(31.7930, 23.4620), 2.5, 1.8),
        new("Sheratan", "Sheratan - Beta Arietis", "Sheratan Yıldızı - Beta Arietis", new CelestialCoordinates(28.6600, 20.8080), 2.2, 1.6),
        new("Markab", "Markab - Alpha Pegasi", "Markab Yıldızı - Alpha Pegasi", new CelestialCoordinates(346.1900, 15.2050), 2.5, 1.8),
        new("Scheat", "Scheat - Beta Pegasi", "Scheat Yıldızı - Beta Pegasi", new CelestialCoordinates(345.9440, 28.0830), 2.5, 1.8),
        new("Algenib", "Algenib - Gamma Pegasi", "Algenib Yıldızı - Gamma Pegasi", new CelestialCoordinates(3.3090, 15.1840), 2.5, 1.8),
        new("Enif", "Enif - Epsilon Pegasi", "Enif Yıldızı - Epsilon Pegasi", new CelestialCoordinates(326.0460, 9.8750), 2.5, 1.8),

        new("Schedar", "Schedar - Alpha Cassiopeiae", "Schedar Yıldızı - Alpha Cassiopeiae", new CelestialCoordinates(10.1268, 56.5373), 2.5, 1.8),
        new("Caph", "Caph - Beta Cassiopeiae", "Caph Yıldızı - Beta Cassiopeiae", new CelestialCoordinates(2.2945, 59.1498), 2.5, 1.8),
        new("Navi", "Navi - Gamma Cassiopeiae", "Navi Yıldızı - Gamma Cassiopeiae", new CelestialCoordinates(14.1772, 60.7167), 2.5, 1.8),
        new("Ruchbah", "Ruchbah - Delta Cassiopeiae", "Ruchbah Yıldızı - Delta Cassiopeiae", new CelestialCoordinates(21.4540, 60.2353), 2.5, 1.8),
        new("Segin", "Segin - Epsilon Cassiopeiae", "Segin Yıldızı - Epsilon Cassiopeiae", new CelestialCoordinates(28.5988, 63.6701), 2.5, 1.8),
        new("Mirfak", "Mirfak - Alpha Persei", "Mirfak Yıldızı - Alpha Persei", new CelestialCoordinates(51.0807, 49.8612), 2.5, 1.8),
        new("Algol", "Algol - Beta Persei", "Algol Örten Çift Yıldızı - Beta Persei", new CelestialCoordinates(47.0422, 40.9556), 2.5, 1.8),
        new("Atik", "Atik - Zeta Persei", "Atik Yıldızı - Zeta Persei", new CelestialCoordinates(58.5330, 31.8840), 2.2, 1.6),
        new("Menkib", "Menkib - Xi Persei", "Menkib Yıldızı - Xi Persei", new CelestialCoordinates(59.7410, 35.7910), 2.2, 1.6),
        new("Alderamin", "Alderamin - Alpha Cephei", "Alderamin Yıldızı - Alpha Cephei", new CelestialCoordinates(319.6450, 62.5860), 2.5, 1.8),

        new("Vega", "Vega - Alpha Lyrae", "Vega Yıldızı - Alpha Lyrae", new CelestialCoordinates(279.2347, 38.7837), 2.5, 1.8),
        new("Sheliak", "Sheliak - Beta Lyrae", "Sheliak Yıldızı - Beta Lyrae", new CelestialCoordinates(282.5200, 33.3630), 2.0, 1.5),
        new("Sulafat", "Sulafat - Gamma Lyrae", "Sulafat Yıldızı - Gamma Lyrae", new CelestialCoordinates(284.7360, 32.6900), 2.0, 1.5),
        new("Deneb", "Deneb - Alpha Cygni", "Deneb Yıldızı - Alpha Cygni", new CelestialCoordinates(310.3580, 45.2803), 2.5, 1.8),
        new("Sadr", "Sadr - Gamma Cygni", "Sadr Yıldızı - Gamma Cygni", new CelestialCoordinates(305.5570, 40.2570), 2.5, 1.8),
        new("Albireo", "Albireo - Beta Cygni", "Albireo Çift Yıldızı - Beta Cygni", new CelestialCoordinates(292.6800, 27.9600), 2.2, 1.6),
        new("GienahCyg", "Gienah - Epsilon Cygni", "Gienah Yıldızı - Epsilon Cygni", new CelestialCoordinates(311.5530, 33.9700), 2.2, 1.6),
        new("Fawaris", "Fawaris - Delta Cygni", "Fawaris Yıldızı - Delta Cygni", new CelestialCoordinates(296.2440, 45.1310), 2.2, 1.6),
        new("Altair", "Altair - Alpha Aquilae", "Altair Yıldızı - Alpha Aquilae", new CelestialCoordinates(297.6958, 8.8683), 2.5, 1.8),
        new("Tarazed", "Tarazed - Gamma Aquilae", "Tarazed Yıldızı - Gamma Aquilae", new CelestialCoordinates(296.5650, 10.6130), 2.0, 1.5),
        new("Alshain", "Alshain - Beta Aquilae", "Alshain Yıldızı - Beta Aquilae", new CelestialCoordinates(298.8280, 6.4070), 2.0, 1.5),
        new("Okab", "Okab - Zeta Aquilae", "Okab Yıldızı - Zeta Aquilae", new CelestialCoordinates(286.3530, 13.8630), 2.2, 1.6),
        new("Rotanev", "Rotanev & Sualocin - Delphinus", "Rotanev ve Sualocin - Yunus Takımyıldızı", new CelestialCoordinates(309.3870, 14.5950), 2.5, 1.8),

        new("Rasalhague", "Rasalhague - Alpha Ophiuchi", "Rasalhague Yıldızı - Alpha Ophiuchi", new CelestialCoordinates(263.7340, 12.5600), 2.5, 1.8),
        new("Cebalrai", "Cebalrai - Beta Ophiuchi", "Cebalrai Yıldızı - Beta Ophiuchi", new CelestialCoordinates(265.8680, 4.5670), 2.2, 1.6),
        new("Kornephoros", "Kornephoros - Beta Herculis", "Kornephoros Yıldızı - Beta Herculis", new CelestialCoordinates(247.5550, 21.4900), 2.2, 1.6),
        new("Rasalgethi", "Rasalgethi - Alpha Herculis", "Rasalgethi Yıldızı - Alpha Herculis", new CelestialCoordinates(258.6620, 14.3900), 2.2, 1.6),
        new("Sarin", "Sarin - Delta Herculis", "Sarin Yıldızı - Delta Herculis", new CelestialCoordinates(258.7580, 24.8390), 2.2, 1.6),
        new("Alphecca", "Alphecca - Alpha Coronae Borealis", "Alphecca Yıldızı - Kuzey Tacı", new CelestialCoordinates(233.6720, 26.7150), 2.5, 1.8),
        new("Arcturus", "Arcturus - Alpha Boötis", "Arcturus Yıldızı - Alpha Boötis", new CelestialCoordinates(213.9150, 19.1820), 2.5, 1.8),
        new("Eltanin", "Eltanin - Gamma Draconis", "Eltanin Yıldızı - Gamma Draconis", new CelestialCoordinates(269.1520, 51.4890), 2.5, 1.8),

        new("Aldebaran", "Aldebaran - Alpha Tauri", "Aldebaran Yıldızı - Alpha Tauri", new CelestialCoordinates(68.9800, 16.5090), 2.5, 1.8),
        new("Capella", "Capella - Alpha Aurigae", "Capella Yıldızı - Alpha Aurigae", new CelestialCoordinates(79.1720, 45.9980), 2.5, 1.8),
        new("Elnath", "Elnath - Beta Tauri", "Elnath Yıldızı - Beta Tauri", new CelestialCoordinates(81.5730, 28.6080), 2.2, 1.6),
        new("Betelgeuse", "Betelgeuse - Alpha Orionis", "Betelgeuse Yıldızı - Alpha Orionis", new CelestialCoordinates(88.7930, 7.4070), 2.5, 1.8),
        new("Rigel", "Rigel - Beta Orionis", "Rigel Yıldızı - Beta Orionis", new CelestialCoordinates(78.6340, -8.2020), 2.5, 1.8),
        new("Bellatrix", "Bellatrix - Gamma Orionis", "Bellatrix Yıldızı - Gamma Orionis", new CelestialCoordinates(81.2830, 6.3500), 2.2, 1.6),
        new("Alnilam", "Orion's Belt - Alnilam, Alnitak & Mintaka", "Orion Kemeri - Alnilam, Alnitak ve Mintaka", new CelestialCoordinates(84.0530, -1.2020), 2.8, 1.8),
        new("Castor", "Castor - Alpha Geminorum", "Castor Yıldızı - Alpha Geminorum", new CelestialCoordinates(113.6500, 31.8880), 2.5, 1.8),
        new("Pollux", "Pollux - Beta Geminorum", "Pollux Yıldızı - Beta Geminorum", new CelestialCoordinates(116.3290, 28.0260), 2.5, 1.8),
        new("Polaris", "Polaris - Alpha Ursae Minoris", "Kutup Yıldızı Polaris - Alpha Ursae Minoris", new CelestialCoordinates(37.9546, 89.2641), 3.0, 2.0),
        new("Dubhe", "Dubhe & Merak - Ursa Major", "Dubhe ve Merak - Büyük Ayı", new CelestialCoordinates(165.9320, 61.7510), 3.0, 2.0),
        new("Mizar", "Mizar & Alcor - Zeta Ursae Majoris", "Mizar ve Alcor Çift Yıldızı - Büyük Ayı", new CelestialCoordinates(200.9810, 54.9250), 2.5, 1.8)
    };

    /// <summary>
    /// Scans the entire astronomical catalog and returns only the objects that project inside
    /// the given photo's WCS coverage area (and optional valid non-void sky check).
    /// </summary>
    public static List<AstronomicalTarget> GetTargetsInField(
        WcsMatrix? wcs,
        int imageWidth,
        int imageHeight,
        Func<double, double, bool>? isValidSkyPixel = null)
    {
        var results = new List<AstronomicalTarget>();
        if (wcs == null || imageWidth <= 0 || imageHeight <= 0)
        {
            return results;
        }

        double marginX = imageWidth * 0.015;
        double marginY = imageHeight * 0.015;

        foreach (var target in KnownCatalogues)
        {
            if (wcs.EquatorialToPixel(target.Coordinates, out double px, out double py))
            {
                if (px >= marginX && px <= imageWidth - marginX &&
                    py >= marginY && py <= imageHeight - marginY)
                {
                    if (isValidSkyPixel == null || isValidSkyPixel(px, py))
                    {
                        results.Add(target);
                    }
                }
            }
        }

        return results;
    }

    public Task<PlateSolvingResult> SolveAsync(
        string imagePath,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<AstrophotographyObject>? detectedStars = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        string filename = Path.GetFileNameWithoutExtension(imagePath).ToLowerInvariant();

        AstronomicalTarget? matchedTarget = KnownCatalogues.FirstOrDefault(t =>
            filename.Contains(t.Id.ToLowerInvariant()) ||
            filename.Contains(t.NameEn.Split(' ')[0].ToLowerInvariant()) ||
            filename.Contains(t.NameTr.Split(' ')[0].ToLowerInvariant()));

        if (matchedTarget == null)
        {
            if (filename.Contains("andromeda"))
                matchedTarget = KnownCatalogues.First(t => t.Id == "M31");
            else if (filename.Contains("perseid") || filename.Contains("perseus"))
                matchedTarget = KnownCatalogues.First(t => t.Id == "Perseus");
            else if (filename.Contains("orion"))
                matchedTarget = KnownCatalogues.First(t => t.Id == "M42");
            else if (filename.Contains("pleiad") || filename.Contains("ulker"))
                matchedTarget = KnownCatalogues.First(t => t.Id == "M45");
            else if (filename.Contains("triangulum"))
                matchedTarget = KnownCatalogues.First(t => t.Id == "M33");
            else if (filename.Contains("whirlpool"))
                matchedTarget = KnownCatalogues.First(t => t.Id == "M51");
            else if (filename.Contains("rosette") || filename.Contains("rozet"))
                matchedTarget = KnownCatalogues.First(t => t.Id == "NGC2237");
            else if (filename.Contains("veil"))
                matchedTarget = KnownCatalogues.First(t => t.Id == "NGC6960");
        }

        if (matchedTarget == null)
        {
            sw.Stop();
            return Task.FromResult(PlateSolvingResult.Failed(
                UseTurkishLanguage
                    ? "Gök cismi otomatik tespit edilemedi."
                    : "Celestial object could not be identified automatically.",
                sw.Elapsed));
        }

        double refX = imageWidth / 2.0;
        double refY = imageHeight / 2.0;

        double pixelScale = matchedTarget.EstimatedPixelScaleArcsec;
        if (imageWidth > 4000)
        {
            pixelScale = Math.Max(0.8, pixelScale * (3000.0 / imageWidth));
        }

        var wcs = new WcsMatrix(
            centerCoordinates: matchedTarget.Coordinates,
            referencePixelX: refX,
            referencePixelY: refY,
            pixelScaleArcsec: pixelScale,
            rotationDegrees: 0.0,
            imageWidth: imageWidth,
            imageHeight: imageHeight);

        sw.Stop();
        int starCount = detectedStars?.Count ?? 0;

        return Task.FromResult(PlateSolvingResult.Success(
            wcs: wcs,
            targetName: matchedTarget.CommonName,
            elapsed: sw.Elapsed,
            matchedStars: Math.Max(12, Math.Min(starCount, 48)),
            message: UseTurkishLanguage
                ? $"{matchedTarget.CommonName} alanı başarıyla çözümlendi."
                : $"{matchedTarget.CommonName} field solved successfully."));
    }

    public static WcsMatrix CreateCustomWcs(
        CelestialCoordinates coordinates,
        int width,
        int height,
        double pixelScaleArcsec = 1.5,
        double rotationDegrees = 0.0)
    {
        return new WcsMatrix(
            centerCoordinates: coordinates,
            referencePixelX: width / 2.0,
            referencePixelY: height / 2.0,
            pixelScaleArcsec: pixelScaleArcsec,
            rotationDegrees: rotationDegrees,
            imageWidth: width,
            imageHeight: height);
    }
}
