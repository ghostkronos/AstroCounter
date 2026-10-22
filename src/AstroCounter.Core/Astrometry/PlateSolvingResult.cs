namespace AstroCounter.Core.Astrometry;

using System;

public record PlateSolvingResult(
    bool IsSuccess,
    WcsMatrix? Wcs,
    string TargetName,
    TimeSpan SolvedTime,
    int MatchedStarCount,
    string Message,
    string? WarningMessage = null,
    string? DiagnosticReason = null,
    string? StellariumGuide = null,
    double RadialDistortion = 0.0
)
{
    public bool HasWarning => !string.IsNullOrEmpty(WarningMessage);
    public bool HasDiagnostic => !string.IsNullOrEmpty(DiagnosticReason);

    public static PlateSolvingResult Failed(
        string message,
        TimeSpan elapsed = default,
        string? diagnosticReason = null)
        => new(
            IsSuccess: false,
            Wcs: null,
            TargetName: string.Empty,
            SolvedTime: elapsed,
            MatchedStarCount: 0,
            Message: message,
            DiagnosticReason: diagnosticReason);

    public static PlateSolvingResult Success(
        WcsMatrix wcs,
        string targetName,
        TimeSpan elapsed,
        int matchedStars = 0,
        string message = "Astrometrik çözümleme başarılı.",
        string? warningMessage = null,
        string? stellariumGuide = null,
        double radialDistortion = 0.0)
        => new(
            IsSuccess: true,
            Wcs: wcs,
            TargetName: targetName,
            SolvedTime: elapsed,
            MatchedStarCount: matchedStars,
            Message: message,
            WarningMessage: warningMessage,
            StellariumGuide: stellariumGuide,
            RadialDistortion: radialDistortion);
}
