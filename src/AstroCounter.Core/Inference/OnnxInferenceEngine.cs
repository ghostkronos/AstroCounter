namespace AstroCounter.Core.Inference;

using System.Diagnostics;
using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

/// <summary>
/// ONNX Runtime engine with DirectML hardware acceleration and automatic CPU fallback.
/// Implements YOLOv8/YOLO11 tensor parsing and SAHI tiling inference.
/// </summary>
public class OnnxInferenceEngine : IInferenceEngine
{
    private readonly InferenceSession? _session;
    private readonly string _inputName = "images";
    private readonly List<string> _classNames = new() { "star", "galaxy", "nebula", "satellite_trail" };
    private bool _disposed;

    public string ExecutionProviderName { get; }
    public bool IsGpuAccelerated { get; }
    public IReadOnlyList<string> ClassNames => _classNames;

    public OnnxInferenceEngine(string? modelPath = null, int gpuDeviceId = 0)
    {
        if (!string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath))
        {
            var sessionOptions = new SessionOptions();
            bool gpuActive = false;
            string provider = "CPU Fallback";

            try
            {
                // Attempt DirectML initialization for NVIDIA, AMD, Intel GPU acceleration
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
                ExecutionProviderName = provider;
                IsGpuAccelerated = gpuActive;

                var firstInput = _session.InputMetadata.Keys.FirstOrDefault();
                if (!string.IsNullOrEmpty(firstInput))
                {
                    _inputName = firstInput;
                }
            }
            catch (Exception)
            {
                // In case model failed to load with DirectML, try strict CPU
                var cpuOptions = new SessionOptions();
                cpuOptions.AppendExecutionProvider_CPU();
                _session = new InferenceSession(modelPath, cpuOptions);
                ExecutionProviderName = "CPU Fallback";
                IsGpuAccelerated = false;
            }
        }
        else
        {
            _session = null;
            ExecutionProviderName = "Built-in Photometric Star Finder";
            IsGpuAccelerated = false;
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

            var slices = ImageTiler.ComputeSlices(imgW, imgH, config);
            var candidates = new List<AstrophotographyObject>();

            if (_session != null)
            {
                // Run deep learning inference across SAHI tiles
                for (int i = 0; i < slices.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var slice = slices[i];

                    var tileTensor = ImagePreprocessor.ExtractTileTensor(bitmap, slice, config.TileWidth, config.TileHeight);
                    var detectedOnTile = RunTileInference(tileTensor, slice, config.MinConfidence);
                    candidates.AddRange(detectedOnTile);

                    progress?.Report((float)(i + 1) / slices.Count);
                }
            }
            else
            {
                // Built-in high-precision astronomical photometric detection (DAOStarFinder inspired)
                candidates = RunPhotometricStarFinder(bitmap, config, progress, cancellationToken);
            }

            // Apply global Non-Maximum Suppression across all overlapping tile coordinates
            var suppressed = NmsFilter.ApplyNms(candidates, config.NmsIouThreshold);

            stopwatch.Stop();

            return new DetectionResult(
                allObjects: suppressed,
                imageWidth: imgW,
                imageHeight: imgH,
                tileCount: slices.Count,
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

        // YOLOv8/11 format typically: [1, 4 + classes, 8400]
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
        else if (shape.Length == 3 && shape[1] >= shape[2])
        {
            // Transposed format: [1, 8400, 4 + classes]
            int numAnchors = shape[1];
            int numChannels = shape[2];
            int numClasses = numChannels - 4;

            for (int a = 0; a < numAnchors; a++)
            {
                float cx = outTensor[0, a, 0];
                float cy = outTensor[0, a, 1];
                float w = outTensor[0, a, 2];
                float h = outTensor[0, a, 3];

                float maxScore = 0f;
                int bestClassId = 0;

                for (int c = 0; c < numClasses; c++)
                {
                    float score = outTensor[0, a, 4 + c];
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

    /// <summary>
    /// Classical astronomy photometric detection (SNR-based DAOStarFinder method)
    /// </summary>
    private List<AstrophotographyObject> RunPhotometricStarFinder(
        SKBitmap bitmap,
        TilingConfig config,
        IProgress<float>? progress,
        CancellationToken cancellationToken)
    {
        var candidates = new List<AstrophotographyObject>();
        int width = bitmap.Width;
        int height = bitmap.Height;

        float sum = 0f;
        float sumSq = 0f;
        int count = 0;
        int step = Math.Max(1, Math.Min(width, height) / 200);

        for (int y = 0; y < height; y += step)
        {
            for (int x = 0; x < width; x += step)
            {
                var pixel = bitmap.GetPixel(x, y);
                float lum = (pixel.Red * 0.299f) + (pixel.Green * 0.587f) + (pixel.Blue * 0.114f);
                sum += lum;
                sumSq += lum * lum;
                count++;
            }
        }

        if (count == 0) return candidates;
        float mean = sum / count;
        float variance = Math.Max(1f, (sumSq / count) - (mean * mean));
        float stdDev = (float)Math.Sqrt(variance);

        float threshold = mean + (3.0f * stdDev);
        int id = 1;
        int margin = 3;

        for (int y = margin; y < height - margin; y += 2)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (int x = margin; x < width - margin; x += 2)
            {
                var centerPix = bitmap.GetPixel(x, y);
                float centerLum = (centerPix.Red * 0.299f) + (centerPix.Green * 0.587f) + (centerPix.Blue * 0.114f);

                if (centerLum > threshold)
                {
                    var topPix = bitmap.GetPixel(x, y - 1);
                    var btmPix = bitmap.GetPixel(x, y + 1);
                    var leftPix = bitmap.GetPixel(x - 1, y);
                    var rightPix = bitmap.GetPixel(x + 1, y);

                    float topLum = (topPix.Red * 0.299f) + (topPix.Green * 0.587f) + (topPix.Blue * 0.114f);
                    float btmLum = (btmPix.Red * 0.299f) + (btmPix.Green * 0.587f) + (btmPix.Blue * 0.114f);
                    float leftLum = (leftPix.Red * 0.299f) + (leftPix.Green * 0.587f) + (leftPix.Blue * 0.114f);
                    float rightLum = (rightPix.Red * 0.299f) + (rightPix.Green * 0.587f) + (rightPix.Blue * 0.114f);

                    if (centerLum >= topLum && centerLum >= btmLum && centerLum >= leftLum && centerLum >= rightLum)
                    {
                        float snr = (centerLum - mean) / stdDev;
                        float confidence = Math.Clamp(snr / 15.0f, 0.10f, 1.0f);

                        float radius = Math.Clamp(2.0f + (snr * 0.3f), 4.0f, 24.0f);
                        float boxX = Math.Max(0, x - (radius / 2f));
                        float boxY = Math.Max(0, y - (radius / 2f));

                        candidates.Add(new AstrophotographyObject(
                            Id: id++,
                            Box: new BoundingBox(boxX, boxY, radius, radius),
                            Confidence: confidence,
                            ClassName: confidence > 0.85f ? "star" : (confidence > 0.70f ? "faint_star" : "background_source"),
                            TileOffsetX: 0,
                            TileOffsetY: 0
                        ));
                    }
                }
            }

            if (y % 100 == 0)
            {
                progress?.Report((float)y / height);
            }
        }

        return candidates;
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

