namespace AstroCounter.Core.Inference;

using System.Diagnostics;
using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

/// <summary>
/// Hybrid astronomical detection engine supporting:
/// 1. Real Astronomical Photometry (DAOStarFinder / SEP) - Default!
/// 2. Deep Learning YOLOv8/11 ONNX (DirectML GPU or CPU Fallback)
/// </summary>
public class OnnxInferenceEngine : IInferenceEngine
{
    private readonly InferenceSession? _session;
    private readonly string _inputName = "images";
    private readonly string _onnxProviderName;
    private readonly List<string> _classNames = new() { "star", "galaxy", "nebula", "satellite_trail" };
    private bool _disposed;

    public DetectionEngineMode Mode { get; set; } = DetectionEngineMode.Photometric;

    public string ExecutionProviderName => Mode switch
    {
        DetectionEngineMode.Photometric => "Astronomik Fotometri (DAOStarFinder / SNR)",
        DetectionEngineMode.DeepLearningYolo => _session != null ? _onnxProviderName : "ONNX Model Yüklenmedi",
        _ => "Bilinmeyen"
    };

    public bool IsGpuAccelerated => Mode == DetectionEngineMode.DeepLearningYolo && _isGpuActive;
    private readonly bool _isGpuActive;

    public IReadOnlyList<string> ClassNames => _classNames;

    public OnnxInferenceEngine(string? modelPath = null, int gpuDeviceId = 0, DetectionEngineMode initialMode = DetectionEngineMode.Photometric)
    {
        Mode = initialMode;
        _onnxProviderName = "CPU Fallback";
        _isGpuActive = false;

        if (!string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath))
        {
            var sessionOptions = new SessionOptions();
            bool gpuActive = false;
            string provider = "CPU Fallback";

            try
            {
                sessionOptions.AppendExecutionProvider_DML(gpuDeviceId);
                gpuActive = true;
                provider = $"DirectML (GPU Device {gpuDeviceId})";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DirectML initialization failed, falling back to CPU: {ex.Message}");
                sessionOptions.AppendExecutionProvider_CPU();
                gpuActive = false;
                provider = "CPU Fallback";
            }

            try
            {
                _session = new InferenceSession(modelPath, sessionOptions);
                _onnxProviderName = provider;
                _isGpuActive = gpuActive;

                var firstInput = _session.InputMetadata.Keys.FirstOrDefault();
                if (!string.IsNullOrEmpty(firstInput))
                {
                    _inputName = firstInput;
                }
            }
            catch (Exception)
            {
                var cpuOptions = new SessionOptions();
                cpuOptions.AppendExecutionProvider_CPU();
                _session = new InferenceSession(modelPath, cpuOptions);
                _onnxProviderName = "CPU Fallback";
                _isGpuActive = false;
            }
        }
        else
        {
            _session = null;
            _onnxProviderName = "ONNX Model Bulunamadı";
            _isGpuActive = false;
        }
    }

    public async Task<DetectionResult> ProcessImageAsync(
        string imagePath,
        TilingConfig? config = null,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default)
    {
        config ??= new TilingConfig();
        var stopwatch = Stopwatch.StartNew();

        return await Task.Run(() =>
        {
            using var bitmap = ImagePreprocessor.LoadImage(imagePath);
            int imgW = bitmap.Width;
            int imgH = bitmap.Height;

            var candidates = new List<AstrophotographyObject>();
            int tileCount = 1;

            if (Mode == DetectionEngineMode.DeepLearningYolo && _session != null)
            {
                // Run YOLO deep learning inference across SAHI tiles
                var slices = ImageTiler.ComputeSlices(imgW, imgH, config);
                tileCount = slices.Count;

                for (int i = 0; i < slices.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var slice = slices[i];

                    var tileTensor = ImagePreprocessor.ExtractTileTensor(bitmap, slice, config.TileWidth, config.TileHeight);
                    var detectedOnTile = RunTileInference(tileTensor, slice, config.MinConfidence);
                    candidates.AddRange(detectedOnTile);

                    progress?.Report((float)(i + 1) / slices.Count);
                }

                candidates = NmsFilter.ApplyNms(candidates, config.NmsIouThreshold);
            }
            else
            {
                // Built-in high-precision astronomical photometric detection (DAOStarFinder / SNR standard)
                candidates = PhotometricStarFinder.DetectStars(bitmap, config, progress, cancellationToken);
                tileCount = 1;
            }

            stopwatch.Stop();

            return new DetectionResult(
                allObjects: candidates,
                imageWidth: imgW,
                imageHeight: imgH,
                tileCount: tileCount,
                elapsedTime: stopwatch.Elapsed,
                executionProvider: ExecutionProviderName
            );
        }, cancellationToken);
    }

    private List<AstrophotographyObject> RunTileInference(float[] tensorData, TileSlice slice, float minConfidenceThreshold)
    {
        var resultsList = new List<AstrophotographyObject>();
        if (_session == null) return resultsList;

        var dimensions = new[] { 1, 3, 640, 640 };
        var inputTensor = new DenseTensor<float>(tensorData, dimensions);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName, inputTensor)
        };

        using var outputSession = _session.Run(inputs);
        var firstOutput = outputSession.FirstOrDefault();
        if (firstOutput == null) return resultsList;

        var outTensor = firstOutput.AsTensor<float>();
        var shape = outTensor.Dimensions;

        if (shape.Length == 3 && shape[1] < shape[2])
        {
            int numChannels = shape[1];
            int numAnchors = shape[2];
            int numClasses = numChannels - 4;

            for (int a = 0; a < numAnchors; a++)
            {
                float cx = outTensor[0, 0, a];
                float cy = outTensor[0, 1, a];
                float w = outTensor[0, 2, a];
                float h = outTensor[0, 3, a];

                float maxScore = 0f;
                int bestClassId = 0;

                for (int c = 0; c < numClasses; c++)
                {
                    float score = outTensor[0, 4 + c, a];
                    if (score > maxScore)
                    {
                        maxScore = score;
                        bestClassId = c;
                    }
                }

                if (maxScore >= minConfidenceThreshold && w > 1f && h > 1f)
                {
                    float localX = cx - (w / 2f);
                    float localY = cy - (h / 2f);

                    if (localX < 0 || localY < 0 || localX > slice.Width || localY > slice.Height)
                        continue;

                    float globalX = localX + slice.X;
                    float globalY = localY + slice.Y;

                    string className = bestClassId < _classNames.Count ? _classNames[bestClassId] : "star";

                    resultsList.Add(new AstrophotographyObject(
                        Id: 0,
                        Box: new BoundingBox(globalX, globalY, w, h),
                        Confidence: Math.Clamp(maxScore, 0f, 1f),
                        ClassName: className,
                        TileOffsetX: slice.X,
                        TileOffsetY: slice.Y
                    ));
                }
            }
        }

        return resultsList;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _session?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
