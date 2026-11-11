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
    private readonly object _sessionLock = new();
    private readonly string _inputName = "images";
    private readonly string _onnxProviderName;
    private readonly List<string> _classNames = new() { "star", "galaxy", "nebula", "satellite_trail" };
    private bool _disposed;

    public DetectionEngineMode Mode { get; set; } = DetectionEngineMode.HybridFusion;

    public string ExecutionProviderName => Mode switch
    {
        DetectionEngineMode.HybridFusion => _session != null
            ? (AstroCounter.Core.Astrometry.CataloguePlateSolver.UseTurkishLanguage
                ? $"Hibrit Füzyon (Fotometri + {_onnxProviderName})"
                : $"Hybrid Fusion (Photometry + {_onnxProviderName})")
            : (AstroCounter.Core.Astrometry.CataloguePlateSolver.UseTurkishLanguage
                ? "Hibrit Füzyon (Fotometri + Morfolojik AI)"
                : "Hybrid Fusion (Photometry + Morphological AI)"),
        DetectionEngineMode.Photometric => AstroCounter.Core.Astrometry.CataloguePlateSolver.UseTurkishLanguage
            ? "Astronomik Fotometri (DAOStarFinder / SNR)"
            : "Astronomical Photometry (DAOStarFinder / SNR)",
        DetectionEngineMode.DeepLearningYolo => _session != null
            ? _onnxProviderName
            : (AstroCounter.Core.Astrometry.CataloguePlateSolver.UseTurkishLanguage ? "ONNX Model Yüklenmedi" : "ONNX Model Not Loaded"),
        _ => AstroCounter.Core.Astrometry.CataloguePlateSolver.UseTurkishLanguage ? "Bilinmeyen" : "Unknown"
    };

    public bool IsGpuAccelerated => (Mode == DetectionEngineMode.DeepLearningYolo || Mode == DetectionEngineMode.HybridFusion) && _isGpuActive;
    private readonly bool _isGpuActive;

    public IReadOnlyList<string> ClassNames => _classNames;

    public OnnxInferenceEngine(string? modelPath = null, int gpuDeviceId = 0, DetectionEngineMode initialMode = DetectionEngineMode.HybridFusion)
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
                sessionOptions.EnableMemoryPattern = false;
                sessionOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
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
            using var rawBitmap = ImagePreprocessor.LoadImage(imagePath);
            int imgW = rawBitmap.Width;
            int imgH = rawBitmap.Height;

            var candidates = new List<AstrophotographyObject>();
            int tileCount = 1;

            bool isCompressed = Path.GetExtension(imagePath).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                Path.GetExtension(imagePath).Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                Path.GetExtension(imagePath).Equals(".webp", StringComparison.OrdinalIgnoreCase);

            if (Mode == DetectionEngineMode.HybridFusion)
            {
                // Unified Fusion: DAOStarFinder Photometry + Deep Learning / Morphology AI
                var photoStars = PhotometricStarFinder.DetectStars(rawBitmap, config, progress, cancellationToken, isCompressedFormat: isCompressed);
                List<AstrophotographyObject>? yoloCandidates = null;

                if (_session != null)
                {
                    using var yoloBitmap = ImagePreprocessor.AutoBalanceBrightness(rawBitmap, out _);
                    var slices = ImageTiler.ComputeSlices(imgW, imgH, config);
                    tileCount = slices.Count;
                    var tileList = new List<AstrophotographyObject>();

                    for (int i = 0; i < slices.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var slice = slices[i];
                        var tileTensor = ImagePreprocessor.ExtractTileTensor(yoloBitmap, slice, config.TileWidth, config.TileHeight);
                        var detectedOnTile = RunTileInference(tileTensor, slice, config.MinConfidence);
                        tileList.AddRange(detectedOnTile);
                    }

                    tileList = NmsFilter.ApplyNms(tileList, config.NmsIouThreshold);
                    yoloCandidates = VerifyAndCalibrateYoloDetections(rawBitmap, tileList, isCompressed);
                }

                candidates = FusionDetectionEngine.FuseDetections(photoStars, yoloCandidates, isCompressedFormat: isCompressed);
            }
            else if (Mode == DetectionEngineMode.DeepLearningYolo && _session != null)
            {
                // Run YOLO deep learning inference across SAHI tiles with normalized contrast
                using var yoloBitmap = ImagePreprocessor.AutoBalanceBrightness(rawBitmap, out _);
                var slices = ImageTiler.ComputeSlices(imgW, imgH, config);
                tileCount = slices.Count;

                for (int i = 0; i < slices.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var slice = slices[i];

                    var tileTensor = ImagePreprocessor.ExtractTileTensor(yoloBitmap, slice, config.TileWidth, config.TileHeight);
                    var detectedOnTile = RunTileInference(tileTensor, slice, config.MinConfidence);
                    candidates.AddRange(detectedOnTile);

                    progress?.Report((float)(i + 1) / slices.Count);
                }

                candidates = NmsFilter.ApplyNms(candidates, config.NmsIouThreshold);
                candidates = VerifyAndCalibrateYoloDetections(rawBitmap, candidates, isCompressed);
            }
            else
            {
                // Built-in high-precision astronomical photometric detection (DAOStarFinder / SNR standard)
                // Operates directly on raw linear astronomical data with internal background estimation
                candidates = PhotometricStarFinder.DetectStars(rawBitmap, config, progress, cancellationToken, isCompressedFormat: isCompressed);
                tileCount = 1;
            }

            stopwatch.Stop();

            float recommendedConfidence = AutoConfidenceCalculator.ComputeOptimalThreshold(candidates, isCompressed);

            return new DetectionResult(
                allObjects: candidates,
                imageWidth: imgW,
                imageHeight: imgH,
                tileCount: tileCount,
                elapsedTime: stopwatch.Elapsed,
                executionProvider: ExecutionProviderName,
                recommendedConfidence: recommendedConfidence
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

        lock (_sessionLock)
        {
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
        }

        return resultsList;
    }

    private static unsafe List<AstrophotographyObject> VerifyAndCalibrateYoloDetections(
        SKBitmap rawBitmap,
        List<AstrophotographyObject> candidates,
        bool isCompressed)
    {
        if (candidates.Count == 0 || rawBitmap.Width <= 0 || rawBitmap.Height <= 0) return candidates;

        int w = rawBitmap.Width;
        int h = rawBitmap.Height;
        byte* basePtr = (byte*)rawBitmap.GetPixels().ToPointer();
        int bpp = rawBitmap.BytesPerPixel;
        int rowBytes = rawBitmap.RowBytes;
        bool isGray = rawBitmap.ColorType == SKColorType.Gray8;
        bool isRgba = rawBitmap.ColorType == SKColorType.Rgba8888;
        int redIdx = isRgba ? 0 : 2;
        int blueIdx = isRgba ? 2 : 0;

        float SampleLum(int x, int y)
        {
            int px = Math.Clamp(x, 0, w - 1);
            int py = Math.Clamp(y, 0, h - 1);
            byte* p = basePtr + ((long)py * rowBytes) + (px * bpp);
            if (isGray) return p[0];
            return (p[redIdx] * 0.299f) + (p[1] * 0.587f) + (p[blueIdx] * 0.114f);
        }

        var result = new List<AstrophotographyObject>(candidates.Count);
        Span<float> ring = stackalloc float[16];
        Span<float> devs = stackalloc float[16];

        foreach (var obj in candidates)
        {
            int cx = Math.Clamp((int)Math.Round(obj.CenterX), 0, w - 1);
            int cy = Math.Clamp((int)Math.Round(obj.CenterY), 0, h - 1);

            float peakLum = 0f;
            float coreSum = 0f;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    float lum = SampleLum(cx + dx, cy + dy);
                    coreSum += lum;
                    if (lum > peakLum) peakLum = lum;
                }
            }
            float coreMean = coreSum / 9f;

            // Sample 16 points on an outer annulus (radius ~6px) for local sky & noise estimation
            int rIdx = 0;
            for (int k = 0; k < 16; k++)
            {
                double ang = k * (Math.PI * 2.0 / 16.0);
                int rx = cx + (int)Math.Round(Math.Cos(ang) * 6.0);
                int ry = cy + (int)Math.Round(Math.Sin(ang) * 6.0);
                ring[rIdx++] = SampleLum(rx, ry);
            }
            ring.Sort();
            float localBg = (ring[7] + ring[8]) * 0.5f;

            for (int k = 0; k < 16; k++)
            {
                devs[k] = Math.Abs(ring[k] - localBg);
            }
            devs.Sort();
            float madStd = ((devs[7] + devs[8]) * 0.5f) * 1.4826f;
            float effectiveStd = Math.Max(isCompressed ? 2.2f : 1.0f, madStd);

            float netPeak = peakLum - localBg;
            float netMean = coreMean - localBg;
            float localSnr = netMean / Math.Max(0.8f, effectiveStd / 2.2f);

            // Reject phantom YOLO detections on compressed images that lack physical photometric excess
            if (isCompressed && (netPeak < 7.0f || localSnr < 2.8f))
            {
                continue;
            }

            float conf = obj.Confidence;
            if (peakLum >= 245f && netPeak >= 30f)
            {
                conf = Math.Max(conf, 0.96f);
            }
            else if (peakLum >= 210f && netPeak >= 22f)
            {
                conf = Math.Max(conf, 0.88f);
            }
            else if (peakLum >= 160f && netPeak >= 16f && conf < 0.80f)
            {
                conf = Math.Max(conf, 0.82f);
            }

            result.Add(obj with
            {
                Confidence = conf,
                Snr = Math.Max(obj.Snr, Math.Max(0.1f, localSnr))
            });
        }

        return result;
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
