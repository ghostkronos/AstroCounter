namespace AstroCounter.Core.Astrometry;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AstroCounter.Core.Models;

public class HybridPlateSolver : IPlateSolver
{
    public string SolverName => "Hibrit Astrometri Motoru (ASTAP & Yıldız Üçgen RANSAC)";

    private readonly AstapPlateSolver _astapSolver = new();
    private readonly StarPatternPlateSolver _starPatternSolver = new();
    private readonly CataloguePlateSolver _catalogueSolver = new();

    /// <summary>Enables the dense full-field catalog lock (TPS) after a successful star-pattern solve.</summary>
    public bool EnableCatalogLock { get; set; } = true;

    public async Task<PlateSolvingResult> SolveAsync(
        string imagePath,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<AstrophotographyObject>? detectedStars = null,
        CancellationToken cancellationToken = default,
        CelestialCoordinates? hintCoordinates = null)
    {
        // 1. Try ASTAP if locally installed
        if (AstapPlateSolver.FindAstapExecutable() != null)
        {
            var astapResult = await _astapSolver.SolveAsync(imagePath, imageWidth, imageHeight, detectedStars, cancellationToken, hintCoordinates);
            if (astapResult.IsSuccess)
            {
                return await ApplyCatalogLockAsync(astapResult, imagePath, cancellationToken);
            }
        }

        // 2. True Star-Position & Triangle-Geometry RANSAC Solver (works regardless of filename)
        var patternResult = await _starPatternSolver.SolveAsync(imagePath, imageWidth, imageHeight, detectedStars, cancellationToken, hintCoordinates);
        if (patternResult.IsSuccess)
        {
            return await ApplyCatalogLockAsync(patternResult, imagePath, cancellationToken);
        }

        // 3. Catalogue & Geometric Fallback
        var catResult = await _catalogueSolver.SolveAsync(imagePath, imageWidth, imageHeight, detectedStars, cancellationToken, hintCoordinates);
        if (catResult.IsSuccess)
        {
            return await ApplyCatalogLockAsync(catResult, imagePath, cancellationToken);
        }
        return catResult;
    }

    /// <summary>
    /// Locks the solution onto hundreds of catalog stars across the whole frame (lens distortion included),
    /// so every star in the photo sits exactly on its catalog position - in 2D and on the 3D celestial dome.
    /// </summary>
    private async Task<PlateSolvingResult> ApplyCatalogLockAsync(PlateSolvingResult result, string imagePath, CancellationToken ct)
    {
        if (!EnableCatalogLock || result.Wcs == null || string.IsNullOrWhiteSpace(imagePath))
        {
            return result;
        }

        var (refined, report) = await Task.Run(() =>
        {
            var w = CatalogLockRefiner.Refine(imagePath, result.Wcs, out var rep, ct);
            return (w, rep);
        }, ct);

        if (report == null || ReferenceEquals(refined, result.Wcs))
        {
            return result;
        }

        bool tr = CataloguePlateSolver.UseTurkishLanguage;
        string lockMsg = tr
            ? $" Katalog kilidi: {report.MatchedStars} yıldız tüm kareye oturtuldu (sapma {report.RmsBeforePixels:F1} px → {report.RmsAfterPixels:F2} px, maks. lens düzeltmesi {report.MaxCorrectionPixels:F0} px)."
            : $" Catalog lock: {report.MatchedStars} stars locked across the frame (residual {report.RmsBeforePixels:F1} px → {report.RmsAfterPixels:F2} px, max lens correction {report.MaxCorrectionPixels:F0} px).";

        return result with
        {
            Wcs = refined,
            MatchedStarCount = System.Math.Max(result.MatchedStarCount, report.MatchedStars),
            Message = result.Message + lockMsg,
        };
    }
}
