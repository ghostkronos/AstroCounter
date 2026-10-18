namespace AstroCounter.Core.Astrometry;

using System;
using System.Globalization;

/// <summary>
/// Represents celestial equatorial coordinates (Right Ascension and Declination) in the J2000 epoch.
/// </summary>
public readonly record struct CelestialCoordinates
{
    /// <summary>Right Ascension in degrees (0.0 to 360.0).</summary>
    public double RaDegrees { get; }

    /// <summary>Declination in degrees (-90.0 to +90.0).</summary>
    public double DecDegrees { get; }

    /// <summary>Right Ascension in hours (0.0 to 24.0).</summary>
    public double RaHours => RaDegrees / 15.0;

    /// <summary>Right Ascension in radians (0.0 to 2*PI).</summary>
    public double RaRadians => RaDegrees * (Math.PI / 180.0);

    /// <summary>Declination in radians (-PI/2 to +PI/2).</summary>
    public double DecRadians => DecDegrees * (Math.PI / 180.0);

    public CelestialCoordinates(double raDegrees, double decDegrees)
    {
        // Normalize RA to [0, 360)
        double normRa = raDegrees % 360.0;
        if (normRa < 0) normRa += 360.0;
        RaDegrees = normRa;

        // Clamp Dec to [-90, +90]
        DecDegrees = Math.Clamp(decDegrees, -90.0, 90.0);
    }

    public static CelestialCoordinates FromHoursAndDegrees(double raHours, double decDegrees)
        => new(raHours * 15.0, decDegrees);

    public static CelestialCoordinates FromRadians(double raRad, double decRad)
        => new(raRad * (180.0 / Math.PI), decRad * (180.0 / Math.PI));

    /// <summary>
    /// Formats Right Ascension in standard astronomical format: HHh MMm SS.ss
    /// </summary>
    public string FormatRa()
    {
        double totalHours = RaHours;
        int hours = (int)totalHours;
        double remMinutes = (totalHours - hours) * 60.0;
        int minutes = (int)remMinutes;
        double seconds = (remMinutes - minutes) * 60.0;

        return string.Format(CultureInfo.InvariantCulture, "{0:D2}h {1:D2}m {2:00.0}s", hours, minutes, seconds);
    }

    /// <summary>
    /// Formats Declination in standard astronomical format: +DD° MM′ SS.s″
    /// </summary>
    public string FormatDec()
    {
        char sign = DecDegrees >= 0 ? '+' : '-';
        double absDec = Math.Abs(DecDegrees);
        int degrees = (int)absDec;
        double remMinutes = (absDec - degrees) * 60.0;
        int minutes = (int)remMinutes;
        double seconds = (remMinutes - minutes) * 60.0;

        return string.Format(CultureInfo.InvariantCulture, "{0}{1:D2}° {2:D2}′ {3:00.0}″", sign, degrees, minutes, seconds);
    }

    /// <summary>
    /// Formats both coordinates: "RA 00h 42m 44.3s · Dec +41° 16′ 09″"
    /// </summary>
    public override string ToString() => $"RA {FormatRa()} · Dec {FormatDec()}";

    /// <summary>
    /// Computes angular separation (great-circle distance) in degrees between two celestial points.
    /// </summary>
    public double AngularDistanceTo(CelestialCoordinates other)
    {
        double dRa = (other.RaDegrees - RaDegrees) * (Math.PI / 180.0);
        double dec1 = DecRadians;
        double dec2 = other.DecRadians;

        double sinDec1 = Math.Sin(dec1);
        double cosDec1 = Math.Cos(dec1);
        double sinDec2 = Math.Sin(dec2);
        double cosDec2 = Math.Cos(dec2);

        double cosDist = (sinDec1 * sinDec2) + (cosDec1 * cosDec2 * Math.Cos(dRa));
        cosDist = Math.Clamp(cosDist, -1.0, 1.0);

        return Math.Acos(cosDist) * (180.0 / Math.PI);
    }
}

