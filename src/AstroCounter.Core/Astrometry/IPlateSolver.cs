namespace AstroCounter.Core.Astrometry;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AstroCounter.Core.Models;

public interface IPlateSolver
{
    string SolverName { get; }

    Task<PlateSolvingResult> SolveAsync(
        string imagePath,
        int imageWidth,
        int imageHeight,
        IReadOnlyList<AstrophotographyObject>? detectedStars = null,
        CancellationToken cancellationToken = default);
}

