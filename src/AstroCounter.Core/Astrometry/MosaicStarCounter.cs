namespace AstroCounter.Core.Astrometry;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using SkiaSharp;

/// <summary>
/// Detailed statistics and results for a full-resolution multi-panel celestial mosaic photometric count.
/// </summary>
public record MosaicCountingProgress(
    int CompletedPanels,
    int TotalPanels,
    int TotalRawStars,
    int TotalUniqueStars,
    string CurrentPanelName,
    float OverallPercent);

public record PanelCountDetail(
    string PanelId,
    string PanelName,
    int RawStarsCount,
    int UniqueStarsCount);

public class MosaicCountingResult
{
    public IReadOnlyList<AstrophotographyObject> AllObjects { get; }
    public int TotalUniqueStars => AllObjects.Count;
    public int RawDetectionsCount { get; }
    public int OverlapDetectionsCount { get; }
    public IReadOnlyList<PanelCountDetail> PanelDetails { get; }
    public TimeSpan ElapsedTime { get; }
    public WcsMatrix MasterWcs { get; }
    public int Tier1Count { get; }
    public int Tier2Count { get; }
    public int Tier3Count { get; }

    public MosaicCountingResult(
        IReadOnlyList<AstrophotographyObject> allObjects,
        int rawDetectionsCount,
        int overlapDetectionsCount,
        IReadOnlyList<PanelCountDetail> panelDetails,
        TimeSpan elapsedTime,
        WcsMatrix masterWcs)
    {
        AllObjects = allObjects;
        RawDetectionsCount = rawDetectionsCount;
        OverlapDetectionsCount = overlapDetectionsCount;
        PanelDetails = panelDetails;
        ElapsedTime = elapsedTime;
        MasterWcs = masterWcs;

        int t1 = 0, t2 = 0, t3 = 0;
        for (int i = 0; i < allObjects.Count; i++)
        {
            var obj = allObjects[i];
            if (obj.Tier == ConfidenceTier.Tier1_High) t1++;
            else if (obj.Tier == ConfidenceTier.Tier2_Medium) t2++;
            else if (obj.Tier == ConfidenceTier.Tier3_Low) t3++;
        }
        Tier1Count = t1;
        Tier2Count = t2;
        Tier3Count = t3;
    }
}

/// <summary>
/// High-throughput parallel astronomical photometric star counter for multi-tile mosaic sky atlases.
/// Analyzes each mosaic tile at 100% full sensor resolution (no downsampling) in parallel,
/// extracts sub-pixel stellar centroids, projects each observation into celestial sky coordinates (RA/Dec),
/// and deduplicates overlapping detections using spherical celestial spatial partitioning.
/// Capable of extracting, merging, and counting 100,000+ to 500,000+ stars across the entire sky mosaic.
/// </summary>
public static class MosaicStarCounter
{
    /// <summary>
    /// Analyzes all visible panels in the mosaic project at full resolution in parallel,
    /// merges detections in celestial coordinates, and returns the unified star count.
    /// </summary>
    public static async Task<MosaicCountingResult> CountMosaicStarsAsync(
        MosaicProject mosaicProject,
        TilingConfig? config = null,
        double matchToleranceArcsec = 3.0,
        IProgress<MosaicCountingProgress>? progress = null,
        WcsMatrix? targetWcs = null,
        CancellationToken cancellationToken = default)
    {
        if (mosaicProject == null) throw new ArgumentNullException(nameof(mosaicProject));
        config ??= new TilingConfig(DefaultConfidenceThreshold: 0.25f, MinConfidence: 0.02f);

        var stopwatch = Stopwatch.StartNew();
        var activePanels = mosaicProject.Panels.Where(p => p.IsVisible).ToList();
        if (activePanels.Count == 0)
        {
            var fallbackWcs = new WcsMatrix(new CelestialCoordinates(0, 0), 1000, 1000, 2.0, 0, 2000, 2000);
            return new MosaicCountingResult(
                Array.Empty<AstrophotographyObject>(), 0, 0, Array.Empty<PanelCountDetail>(), TimeSpan.Zero, fallbackWcs);
        }

        // Master unified WCS for the complete mosaic canvas
        var masterWcs = mosaicProject.ComputeMasterWcs() ??
                        activePanels[0].Wcs ??
                        new WcsMatrix(new CelestialCoordinates(180, 30), 1000, 1000, 2.5, 0, 2000, 2000);

        int totalPanels = activePanels.Count;
        int completedCount = 0;
        int rawDetectionsSum = 0;

        // Container for detections from each panel: Panel -> list of (local star, sky coordinate)
        var panelDetectionResults = new ConcurrentDictionary<string, List<(AstrophotographyObject Star, CelestialCoordinates Sky)>>();

        // Parallel processing across panels with controlled concurrency
        int maxConcurrency = Math.Clamp(Environment.ProcessorCount, 1, 8);
        using var semaphore = new SemaphoreSlim(maxConcurrency);

        var tasks = activePanels.Select(async panel =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                SKBitmap? bmp = null;
                bool shouldDispose = false;

                if (panel.CachedSkiaBitmap is SKBitmap skb1)
                {
                    bmp = skb1;
                }
                else if (panel.CachedBitmap is SKBitmap skb2)
                {
                    bmp = skb2;
                }
                else if (!string.IsNullOrEmpty(panel.FilePath) && File.Exists(panel.FilePath))
                {
                    bmp = ImagePreprocessor.LoadImage(panel.FilePath);
                    shouldDispose = true;
                }

                if (bmp == null)
                {
                    // If no disk file or cached SKBitmap, create placeholder if width and height are known
                    bmp = new SKBitmap(Math.Max(100, panel.Width), Math.Max(100, panel.Height));
                    shouldDispose = true;
                }

                try
                {
                    // Run full-resolution photometric DAOStarFinder detection
                    var stars = PhotometricStarFinder.DetectStars(bmp, config, cancellationToken: cancellationToken);

                    // Compute per-photo background optimal auto-confidence threshold, taking lossy JPEG/WebP format into account
                    float optimalConf = AutoConfidenceCalculator.ComputeOptimalThreshold(stars, panel.IsLossyFormat);
                    panel.AutoConfidenceThreshold = optimalConf;
                    panel.DetectedStars = stars;

                    var panelStars = new List<(AstrophotographyObject Star, CelestialCoordinates Sky)>(stars.Count);
                    var panelWcs = panel.Wcs ?? masterWcs;
                    for (int i = 0; i < stars.Count; i++)
                    {
                        var s = stars[i];
                        // Convert star centroid to celestial equatorial coordinates (RA, Dec)
                        var sky = panelWcs.PixelToEquatorial(s.CenterX, s.CenterY);
                        panelStars.Add((s, sky));
                    }

                    panelDetectionResults[panel.Id] = panelStars;
                    Interlocked.Add(ref rawDetectionsSum, stars.Count);

                    int completed = Interlocked.Increment(ref completedCount);
                    progress?.Report(new MosaicCountingProgress(
                        completed,
                        totalPanels,
                        rawDetectionsSum,
                        rawDetectionsSum,
                        panel.Name,
                        (float)completed / totalPanels * 0.85f));
                }
                finally
                {
                    if (shouldDispose)
                    {
                        bmp.Dispose();
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();

        // Step 2: Celestial Coordinate Merging & Deduplication across overlapping panels
        // Uses high-performance spherical angular hash partitioning
        progress?.Report(new MosaicCountingProgress(
            totalPanels, totalPanels, rawDetectionsSum, 0, "Gök Koordinatlarında Birleştiriliyor...", 0.90f));

        var projectionWcs = targetWcs ?? masterWcs;

        var (deduplicatedStars, overlapCount, panelDetails) = MergeAndDeduplicate(
            activePanels, panelDetectionResults, matchToleranceArcsec, projectionWcs);

        stopwatch.Stop();

        progress?.Report(new MosaicCountingProgress(
            totalPanels,
            totalPanels,
            rawDetectionsSum,
            deduplicatedStars.Count,
            "Tamamlandı",
            1.0f));

        return new MosaicCountingResult(
            deduplicatedStars,
            rawDetectionsSum,
            overlapCount,
            panelDetails,
            stopwatch.Elapsed,
            masterWcs);
    }

    /// <summary>
    /// Deduplicates multi-panel star observations within celestial coordinate angular tolerance
    /// and projects unified stars onto the master mosaic canvas coordinate system.
    /// </summary>
    private static (List<AstrophotographyObject> Deduplicated, int OverlapCount, List<PanelCountDetail> Details)
        MergeAndDeduplicate(
            List<MosaicPanel> panels,
            ConcurrentDictionary<string, List<(AstrophotographyObject Star, CelestialCoordinates Sky)>> panelDetections,
            double matchToleranceArcsec,
            WcsMatrix projectionWcs)
    {
        // Spherical hash grid: cell size ~ 30 arcsec (0.00833 deg)
        const double cellSizeDeg = 0.008333; // 30 arcseconds
        var skyGrid = new Dictionary<long, List<CelestialStarRecord>>();

        var panelDetails = new List<PanelCountDetail>(panels.Count);
        int overlapCount = 0;
        int nextId = 1;
        var unifiedList = new List<CelestialStarRecord>(panelDetections.Values.Sum(v => v.Count));

        // Deterministic processing order following panel layer stack
        for (int pIdx = 0; pIdx < panels.Count; pIdx++)
        {
            var panel = panels[pIdx];
            if (!panelDetections.TryGetValue(panel.Id, out var detections) || detections.Count == 0)
            {
                panelDetails.Add(new PanelCountDetail(panel.Id, panel.Name, 0, 0));
                continue;
            }

            int panelUnique = 0;

            for (int i = 0; i < detections.Count; i++)
            {
                var (star, sky) = detections[i];

                double ra = ((sky.RaDegrees % 360.0) + 360.0) % 360.0;
                double dec = Math.Clamp(sky.DecDegrees, -89.9, 89.9);

                int raBin = (int)Math.Floor(ra / cellSizeDeg);
                int decBin = (int)Math.Floor(dec / cellSizeDeg);

                // Account for declination convergence towards celestial poles:
                // delta_RA = delta_theta / cos(Dec)
                double cosDec = Math.Max(0.05, Math.Cos(dec * (Math.PI / 180.0)));
                int maxRaStep = Math.Clamp((int)Math.Ceiling(1.0 / cosDec), 1, 10);
                const int totalRaBins = 43200; // 360.0 / 0.008333333333333333

                // Search neighboring cells for matching star within tolerance (including 0°/360° RA wrap-around)
                CelestialStarRecord? match = null;
                double bestDistArcsec = matchToleranceArcsec;

                for (int dRa = -maxRaStep; dRa <= maxRaStep && match == null; dRa++)
                {
                    int neighborRa = (raBin + dRa) % totalRaBins;
                    if (neighborRa < 0) neighborRa += totalRaBins;

                    for (int dDec = -1; dDec <= 1; dDec++)
                    {
                        long cellKey = (((long)neighborRa) << 32) ^ ((long)(decBin + dDec) & 0xFFFFFFFFL);
                        if (skyGrid.TryGetValue(cellKey, out var cellStars))
                        {
                            for (int k = 0; k < cellStars.Count; k++)
                            {
                                var existing = cellStars[k];
                                double distArcsec = sky.AngularDistanceTo(existing.Sky) * 3600.0;
                                if (distArcsec <= bestDistArcsec)
                                {
                                    bestDistArcsec = distArcsec;
                                    match = existing;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (match != null)
                {
                    // Duplicate observation in overlap region: merge data
                    overlapCount++;
                    match.ObservationCount++;
                    match.MaxConfidence = Math.Max(match.MaxConfidence, star.Confidence);
                    match.MaxSnr = Math.Max(match.MaxSnr, star.Snr);
                }
                else
                {
                    // New unique physical star
                    panelUnique++;
                    var rec = new CelestialStarRecord(
                        nextId++,
                        sky,
                        star.Confidence,
                        star.Snr,
                        star.ClassName,
                        star.Box.Width,
                        panel.Id,
                        star.CenterX,
                        star.CenterY);

                    long myKey = (((long)raBin) << 32) ^ ((long)decBin & 0xFFFFFFFFL);
                    if (!skyGrid.TryGetValue(myKey, out var list))
                    {
                        list = new List<CelestialStarRecord>();
                        skyGrid[myKey] = list;
                    }
                    list.Add(rec);
                    unifiedList.Add(rec);
                }
            }

            panelDetails.Add(new PanelCountDetail(panel.Id, panel.Name, detections.Count, panelUnique));
        }

        // Precompute exact 2D canvas affine transforms for each panel so every star sits 1:1 on its photo pixel in 2D
        var panelTransforms = new Dictionary<string, (double M11, double M12, double M21, double M22, double OffsetX, double OffsetY)>(panels.Count);
        for (int pIdx = 0; pIdx < panels.Count; pIdx++)
        {
            var p = panels[pIdx];
            panelTransforms[p.Id] = MosaicProject.GetPanelCanvasTransform(p, projectionWcs, pIdx);
        }

        // Project deduplicated stars into target/master canvas coordinates
        var resultObjects = new List<AstrophotographyObject>(unifiedList.Count);
        for (int i = 0; i < unifiedList.Count; i++)
        {
            var u = unifiedList[i];
            double px, py;

            if (panelTransforms.TryGetValue(u.FirstPanelId, out var tr))
            {
                // Transform local sensor pixel (LocalX, LocalY) through the exact same 2D affine matrix used to draw the photo bitmap
                px = (u.LocalX * tr.M11) + (u.LocalY * tr.M21) + tr.OffsetX;
                py = (u.LocalX * tr.M12) + (u.LocalY * tr.M22) + tr.OffsetY;
            }
            else if (!projectionWcs.EquatorialToPixel(u.Sky, out px, out py))
            {
                continue;
            }

            float size = Math.Clamp(u.EstimatedSize, 4.0f, 60.0f);
            resultObjects.Add(new AstrophotographyObject(
                Id: u.Id,
                Box: new BoundingBox((float)(px - (size / 2.0)), (float)(py - (size / 2.0)), size, size),
                Confidence: u.MaxConfidence,
                ClassName: u.ClassName,
                TileOffsetX: 0,
                TileOffsetY: 0,
                Snr: u.MaxSnr,
                RaDegrees: u.Sky.RaDegrees,
                DecDegrees: u.Sky.DecDegrees,
                PanelId: u.FirstPanelId
            ));
        }

        return (resultObjects, overlapCount, panelDetails);
    }

    private sealed class CelestialStarRecord
    {
        public int Id { get; }
        public CelestialCoordinates Sky { get; }
        public float MaxConfidence { get; set; }
        public float MaxSnr { get; set; }
        public string ClassName { get; }
        public float EstimatedSize { get; }
        public string FirstPanelId { get; }
        public double LocalX { get; }
        public double LocalY { get; }
        public int ObservationCount { get; set; } = 1;

        public CelestialStarRecord(
            int id,
            CelestialCoordinates sky,
            float confidence,
            float snr,
            string className,
            float estimatedSize,
            string firstPanelId,
            double localX,
            double localY)
        {
            Id = id;
            Sky = sky;
            MaxConfidence = confidence;
            MaxSnr = snr;
            ClassName = className;
            EstimatedSize = estimatedSize;
            FirstPanelId = firstPanelId;
            LocalX = localX;
            LocalY = localY;
        }
    }
}
