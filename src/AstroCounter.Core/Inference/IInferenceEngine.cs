namespace AstroCounter.Core.Inference;

using AstroCounter.Core.Models;

public interface IInferenceEngine : IDisposable
{
    string ExecutionProviderName { get; }
    bool IsGpuAccelerated { get; }
    IReadOnlyList<string> ClassNames { get; }

    Task<DetectionResult> ProcessImageAsync(
        string imagePath,
        TilingConfig? config = null,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default);
}

