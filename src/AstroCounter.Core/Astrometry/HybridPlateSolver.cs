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

    public async Task<PlateSolvingResult> SolveAsync(
        string imagePath,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<AstrophotographyObject>? detectedStars = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Try ASTAP if locally installed
        if (AstapPlateSolver.FindAstapExecutable() != null)
        {
            var astapResult = await _astapSolver.SolveAsync(imagePath, imageWidth, imageHeight, detectedStars, cancellationToken);
            if (astapResult.IsSuccess)
            {
                return astapResult;
            }
        }

        // 2. True Star-Position & Triangle-Geometry RANSAC Solver (works regardless of filename)
        var patternResult = await _starPatternSolver.SolveAsync(imagePath, imageWidth, imageHeight, detectedStars, cancellationToken);
        if (patternResult.IsSuccess)
        {
            return patternResult;
        }

        // 3. Catalogue & Geometric Fallback
        return await _catalogueSolver.SolveAsync(imagePath, imageWidth, imageHeight, detectedStars, cancellationToken);
    }
}
