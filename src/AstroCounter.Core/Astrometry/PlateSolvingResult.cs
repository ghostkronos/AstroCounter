namespace AstroCounter.Core.Astrometry;

using System;

public record PlateSolvingResult(
    bool IsSuccess,
    WcsMatrix? Wcs,
    string TargetName,
    TimeSpan SolvedTime,
    int MatchedStarCount,
    string Message
)
{
    public static PlateSolvingResult Failed(string message, TimeSpan elapsed = default)
        => new(false, null, string.Empty, elapsed, 0, message);

    public static PlateSolvingResult Success(
        WcsMatrix wcs,
        string targetName,
        TimeSpan elapsed,
        int matchedStars = 0,
        string message = "Astrometrik çözümleme başarılı.")
        => new(true, wcs, targetName, elapsed, matchedStars, message);
}

