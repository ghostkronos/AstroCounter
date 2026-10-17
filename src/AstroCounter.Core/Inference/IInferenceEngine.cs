namespace AstroCounter.Core.Inference;

using AstroCounter.Core.Models;

public enum DetectionEngineMode
{
    Photometric,      // Astronomical Photometry & DAOStarFinder (Eğitimsiz gerçek yıldız tespiti)
    DeepLearningYolo  // YOLOv8/11 ONNX (DirectML / CPU)
}

public interface IInferenceEngine : IDisposable
{
    DetectionEngineMode Mode { get; set; }
    string ExecutionProviderName { get; }
    bool IsGpuAccelerated { get; }
    IReadOnlyList<string> ClassNames { get; }

    Task<DetectionResult> ProcessImageAsync(
        string imagePath,
        TilingConfig? config = null,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default);
}
