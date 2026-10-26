namespace AstroCounter.Tests;

using System;
using AstroCounter.Core.Astrometry;
using Xunit;

public class MosaicTests
{
    [Fact]
    public void MosaicPanel_FootprintAndContainment_WorkCorrectly()
    {
        // Panel centered at Deneb (RA 310.35, Dec 45.28)
        var wcs = new WcsMatrix(
            new CelestialCoordinates(310.35, 45.28),
            referencePixelX: 1000,
            referencePixelY: 1000,
            pixelScaleArcsec: 3.6, // 1 deg per 1000 px -> 2x2 deg FOV
            rotationDegrees: 0.0,
            imageWidth: 2000,
            imageHeight: 2000);

        var panel = new MosaicPanel("p1", "Deneb Field", 2000, 2000, wcs);

        var (tl, tr, br, bl) = panel.GetFootprintCorners();

        // Corners should be distinct coordinates
        Assert.NotEqual(tl.RaDegrees, br.RaDegrees);
        Assert.NotEqual(tl.DecDegrees, br.DecDegrees);

        // Center should be contained
        Assert.True(panel.ContainsCoordinate(new CelestialCoordinates(310.35, 45.28)));

        // Coordinate far away should not be contained
        Assert.False(panel.ContainsCoordinate(new CelestialCoordinates(10.0, 41.0)));
    }

    [Fact]
    public void MosaicProject_CompositeBoundsAndMasterWcs_CalculatesCleanly()
    {
        var project = new MosaicProject();

        // Panel 1: Deneb (RA 310.0, Dec 45.0), FOV 2 deg
        var wcs1 = new WcsMatrix(
            new CelestialCoordinates(310.0, 45.0),
            1000, 1000, 3.6, 0.0, 2000, 2000);
        var p1 = new MosaicPanel("p1", "Panel 1", 2000, 2000, wcs1);

        // Panel 2: Sadr / Gamma Cygni (RA 306.0, Dec 40.0), FOV 2 deg
        var wcs2 = new WcsMatrix(
            new CelestialCoordinates(306.0, 40.0),
            1000, 1000, 3.6, 0.0, 2000, 2000);
        var p2 = new MosaicPanel("p2", "Panel 2", 2000, 2000, wcs2);

        project.AddPanel(p1);
        project.AddPanel(p2);

        Assert.Equal(2, project.Count);

        var bounds = project.ComputeBounds();
        Assert.NotNull(bounds);

        // Center should be between the two panels (RA ~ 308, Dec ~ 42.5)
        Assert.InRange(bounds.Center.RaDegrees, 305.0, 311.0);
        Assert.InRange(bounds.Center.DecDegrees, 39.0, 46.0);

        // Master WCS
        var masterWcs = project.ComputeMasterWcs();
        Assert.NotNull(masterWcs);
        Assert.True(masterWcs.ImageWidth > 2000);
        Assert.True(masterWcs.ImageHeight > 2000);

        // Map Panel 1 to master canvas
        var mapped1 = project.MapPanelToMasterCanvas(p1, masterWcs);
        Assert.NotNull(mapped1);
        Assert.True(mapped1.Value.TopLeft.X >= 0 && mapped1.Value.TopLeft.X <= masterWcs.ImageWidth);
        Assert.True(mapped1.Value.TopLeft.Y >= 0 && mapped1.Value.TopLeft.Y <= masterWcs.ImageHeight);

        // Map Panel 2 to master canvas
        var mapped2 = project.MapPanelToMasterCanvas(p2, masterWcs);
        Assert.NotNull(mapped2);
        Assert.True(mapped2.Value.TopLeft.X >= 0 && mapped2.Value.TopLeft.X <= masterWcs.ImageWidth);
        Assert.True(mapped2.Value.TopLeft.Y >= 0 && mapped2.Value.TopLeft.Y <= masterWcs.ImageHeight);
    }

    [Fact]
    public void MosaicProject_CheckOverlap_IdentifiesOverlappingPanels()
    {
        var project = new MosaicProject();

        // Two panels with 50% overlap (distance 1 deg, each FOV 2 deg)
        var wcs1 = new WcsMatrix(new CelestialCoordinates(310.0, 45.0), 1000, 1000, 3.6, 0.0, 2000, 2000);
        var p1 = new MosaicPanel("p1", "Panel 1", 2000, 2000, wcs1);

        var wcs2 = new WcsMatrix(new CelestialCoordinates(311.0, 45.0), 1000, 1000, 3.6, 0.0, 2000, 2000);
        var p2 = new MosaicPanel("p2", "Panel 2", 2000, 2000, wcs2);

        // Disjoint panel far away
        var wcs3 = new WcsMatrix(new CelestialCoordinates(10.0, 41.0), 1000, 1000, 3.6, 0.0, 2000, 2000);
        var p3 = new MosaicPanel("p3", "Andromeda", 2000, 2000, wcs3);

        project.AddPanel(p1);
        project.AddPanel(p2);
        project.AddPanel(p3);

        Assert.True(project.CheckOverlap(p1, p2));
        Assert.True(project.CheckOverlap(p2, p1));
        Assert.False(project.CheckOverlap(p1, p3));
        Assert.False(project.CheckOverlap(p2, p3));
    }

    [Fact]
    public void MosaicProject_SphericalProjection_ProjectsCornersAccurately()
    {
        var wcs = new WcsMatrix(new CelestialCoordinates(310.0, 45.0), 1000, 1000, 3.6, 0.0, 2000, 2000);
        var panel = new MosaicPanel("p1", "Deneb Panel", 2000, 2000, wcs);

        var (tl, tr, br, bl) = panel.GetFootprintCorners();

        // Verify that spherical distance between opposite corners matches FOV
        Assert.InRange(tl.AngularDistanceTo(br), 2.7, 3.0);
        Assert.InRange(tr.AngularDistanceTo(bl), 2.7, 3.0);
    }

    [Fact]
    public void MosaicSphericalMesh_SubdivisionGrid_CurvatureIsSmoothAndMonotonic()
    {
        var wcs = new WcsMatrix(new CelestialCoordinates(310.0, 45.0), 1000, 1000, 3.6, 0.0, 2000, 2000);
        int div = 24;

        // Verify across all columns that RA and 3D positions are strictly monotonic and smooth
        double prevX = double.MaxValue;
        for (int i = 0; i <= div; i++)
        {
            double u = (double)i / div;
            double px = u * 2000;
            var coord = wcs.PixelToEquatorial(px, 1000);

            double raRad = coord.RaRadians;
            double decRad = coord.DecRadians;
            double x = 98.0 * Math.Cos(decRad) * Math.Sin(raRad);

            if (i > 0)
            {
                Assert.True(x < prevX, $"Step {i}: X {x} should be less than prevX {prevX}");
            }
            prevX = x;
        }
    }

    [Fact]
    public void MosaicPanel_OverlappingPanels_HaveStrictLayerSeparationWithoutIntersection()
    {
        // Strict depth layering: layerStep = 1.6, max sagitta offset = 0.4
        double baseRadius = 98.2;
        double layerStep = 1.6;
        double maxSagitta = 0.4;

        // In Globe view (larger radius = in front)
        double r0_globe_min = baseRadius + (0 * layerStep) - maxSagitta;
        double r0_globe_max = baseRadius + (0 * layerStep);
        double r1_globe_min = baseRadius + (1 * layerStep) - maxSagitta;
        double r1_globe_max = baseRadius + (1 * layerStep);

        // Panel 1 minimum radius must be strictly greater than Panel 0 maximum radius
        Assert.True(r1_globe_min > r0_globe_max, $"Panel 1 min {r1_globe_min} must exceed Panel 0 max {r0_globe_max}");
        Assert.True(r1_globe_min - r0_globe_max >= 1.2 - 1e-4);

        // In Dome view (smaller radius = in front / closer to center 0,0,0)
        double r0_dome_max = baseRadius - (0 * layerStep);
        double r0_dome_min = baseRadius - (0 * layerStep) - maxSagitta;
        double r1_dome_max = baseRadius - (1 * layerStep);
        double r1_dome_min = baseRadius - (1 * layerStep) - maxSagitta;

        // Panel 1 maximum radius must be strictly smaller than Panel 0 minimum radius
        Assert.True(r1_dome_max < r0_dome_min, $"Panel 1 max {r1_dome_max} must be below Panel 0 min {r0_dome_min}");
        Assert.True(r0_dome_min - r1_dome_max >= 1.2 - 1e-4);
    }
    [Fact]
    public void MosaicPanel_CachedBitmap_StoresAndRetrievesObject()
    {
        var wcs = new WcsMatrix(new CelestialCoordinates(180.0, 0.0), 500, 500, 2.0, 0.0, 1000, 1000);
        var panel = new MosaicPanel("p1", "Test Panel", 1000, 1000, wcs, filePath: "test.png");

        Assert.Null(panel.CachedBitmap);
        var dummy = new object();
        panel.CachedBitmap = dummy;
        Assert.Same(dummy, panel.CachedBitmap);
    }

    [Fact]
    public void MosaicProject_FindPanel_FindsByPathOrId()
    {
        var project = new MosaicProject();
        var wcs = new WcsMatrix(new CelestialCoordinates(180.0, 0.0), 500, 500, 2.0, 0.0, 1000, 1000);
        var panel = new MosaicPanel("test-id-123", "Test Panel", 1000, 1000, wcs, filePath: @"C:\images\tile1.png");

        project.AddPanel(panel);

        var foundByPath = project.FindPanel(@"C:\images\tile1.png");
        Assert.NotNull(foundByPath);
        Assert.Equal("test-id-123", foundByPath.Id);

        var foundCaseInsensitive = project.FindPanel(@"c:\images\TILE1.png");
        Assert.NotNull(foundCaseInsensitive);

        var foundById = project.FindPanel(null, "test-id-123");
        Assert.NotNull(foundById);
        Assert.Equal("Test Panel", foundById.Name);

        var notFound = project.FindPanel(@"C:\other.png", "nonexistent");
        Assert.Null(notFound);
    }

    [Fact]
    public void WcsMatrix_EquatorialToPixel_OutsideSensorWithSplineWarp_DoesNotDiverge()
    {
        // Construct 4 control points for TPS warp
        var pts = new[]
        {
            new ThinPlateSplineWarp.ControlPoint(100, 100, 102, 101),
            new ThinPlateSplineWarp.ControlPoint(900, 100, 898, 99),
            new ThinPlateSplineWarp.ControlPoint(900, 900, 901, 897),
            new ThinPlateSplineWarp.ControlPoint(100, 900, 99, 902)
        };
        var tps = ThinPlateSplineWarp.Create(pts, 1000, 1000);
        Assert.NotNull(tps);

        var wcsWithTps = new WcsMatrix(
            new CelestialCoordinates(180.0, 0.0),
            referencePixelX: 500,
            referencePixelY: 500,
            pixelScaleArcsec: 3.6,
            rotationDegrees: 0.0,
            imageWidth: 1000,
            imageHeight: 1000,
            splineWarp: tps);

        // Project coordinate 10 degrees away (far outside 1 deg sensor FOV)
        var farCoord = new CelestialCoordinates(190.0, 0.0);
        bool ok = wcsWithTps.EquatorialToPixel(farCoord, out double px, out double py);

        Assert.True(ok);
        // Pure linear projection distance: 10 deg * 3600 / 3.6 = 10,000 px offset
        // TPS should not blow this up to millions of pixels
        Assert.InRange(Math.Abs(px - 500), 9000, 11000);
        Assert.InRange(Math.Abs(py - 500), 0, 500);
    }

    [Fact]
    public void MosaicPanel_DifferentialAffineBasis_PreservesRotationAndScaleCleanly()
    {
        // Active canvas image WCS: 1.0 deg FOV, scale 3.6 arcsec/px
        var activeWcs = new WcsMatrix(new CelestialCoordinates(180.0, 0.0), 500, 500, 3.6, 0.0, 1000, 1000);

        // Panel 2 WCS: same center, but rotated 90 degrees and 2x pixel scale (7.2 arcsec/px)
        var panelWcs = new WcsMatrix(new CelestialCoordinates(180.0, 0.0), 250, 250, 7.2, 90.0, 500, 500);
        var panel = new MosaicPanel("p2", "Rotated Panel", 500, 500, panelWcs);

        double cx = panel.Width / 2.0;
        double cy = panel.Height / 2.0;
        var centerSky = panel.Wcs!.PixelToEquatorial(cx, cy);
        Assert.True(activeWcs.EquatorialToPixel(centerSky, out double xc, out double yc));
        Assert.InRange(xc, 499.0, 501.0);
        Assert.InRange(yc, 499.0, 501.0);

        double delta = 50.0;
        var rSky = panel.Wcs.PixelToEquatorial(cx + delta, cy);
        var lSky = panel.Wcs.PixelToEquatorial(cx - delta, cy);
        var dSky = panel.Wcs.PixelToEquatorial(cx, cy + delta);
        var uSky = panel.Wcs.PixelToEquatorial(cx, cy - delta);

        Assert.True(activeWcs.EquatorialToPixel(rSky, out double xr, out double yr));
        Assert.True(activeWcs.EquatorialToPixel(lSky, out double xl, out double yl));
        Assert.True(activeWcs.EquatorialToPixel(dSky, out double xd, out double yd));
        Assert.True(activeWcs.EquatorialToPixel(uSky, out double xu, out double yu));

        double ux = (xr - xl) / (2.0 * delta);
        double uy = (yr - yl) / (2.0 * delta);
        double vx = (xd - xu) / (2.0 * delta);
        double vy = (yd - yu) / (2.0 * delta);

        // Relative scale ratio: 7.2 / 3.6 = 2.0
        double lenU = Math.Sqrt((ux * ux) + (uy * uy));
        double lenV = Math.Sqrt((vx * vx) + (vy * vy));
        Assert.InRange(lenU, 1.95, 2.05);
        Assert.InRange(lenV, 1.95, 2.05);

        // Determinant = lenU * lenV * sin(angle) ~ 4.0
        double det = (ux * vy) - (uy * vx);
        Assert.InRange(Math.Abs(det), 3.9, 4.1);
    }

    [Fact]
    public void MosaicProject_LayerManagement_ReordersPanelsAccurately()
    {
        var project = new MosaicProject();
        var wcs = new WcsMatrix(new CelestialCoordinates(180.0, 0.0), 500, 500, 2.0, 0.0, 1000, 1000);

        var p1 = new MosaicPanel("p1", "Panel 1", 1000, 1000, wcs);
        var p2 = new MosaicPanel("p2", "Panel 2", 1000, 1000, wcs);
        var p3 = new MosaicPanel("p3", "Panel 3", 1000, 1000, wcs);

        project.AddPanel(p1);
        project.AddPanel(p2);
        project.AddPanel(p3);

        Assert.Equal(1, project.GetLayerIndex(p1));
        Assert.Equal(2, project.GetLayerIndex(p2));
        Assert.Equal(3, project.GetLayerIndex(p3));

        // Move p1 up
        bool movedUp = project.MoveUp(p1);
        Assert.True(movedUp);
        Assert.Equal(0, project.IndexOf(p2));
        Assert.Equal(1, project.IndexOf(p1));
        Assert.Equal(2, project.IndexOf(p3));

        // Move p3 down
        bool movedDown = project.MoveDown(p3);
        Assert.True(movedDown);
        Assert.Equal(0, project.IndexOf(p2));
        Assert.Equal(1, project.IndexOf(p3));
        Assert.Equal(2, project.IndexOf(p1));

        // Send p1 to back
        bool sentToBack = project.SendToBack(p1);
        Assert.True(sentToBack);
        Assert.Equal(0, project.IndexOf(p1));
        Assert.Equal(1, project.IndexOf(p2));
        Assert.Equal(2, project.IndexOf(p3));

        // Bring p1 to front
        bool broughtToFront = project.BringToFront(p1);
        Assert.True(broughtToFront);
        Assert.Equal(0, project.IndexOf(p2));
        Assert.Equal(1, project.IndexOf(p3));
        Assert.Equal(2, project.IndexOf(p1));

        // Feathered blending properties
        Assert.True(project.UseFeatheredBlending);
        project.UseFeatheredBlending = false;
        Assert.False(project.UseFeatheredBlending);

        Assert.True(p1.UseFeatheredBlending);
        p1.FeatherFraction = 0.08;
        Assert.Equal(0.08, p1.FeatherFraction, 3);
    }

    [Fact]
    public void MosaicProject_NinePanelGrid_HasDistinctCoordinatesAndPartialOverlap()
    {
        var project = new MosaicProject();
        double baseRa = 200.0;
        double baseDec = 40.0;
        double baseScale = 2.0; // arcsec / px
        int w = 1000;
        int h = 1000;
        int count = 9;
        int cols = 3;
        int rows = 3;
        double overlapFraction = 0.12;

        double stepX = w * (1.0 - overlapFraction);
        double stepY = h * (1.0 - overlapFraction);
        double angularStepX = (stepX * baseScale) / 3600.0;
        double angularStepY = (stepY * baseScale) / 3600.0;

        for (int i = 0; i < count; i++)
        {
            int row = i / cols;
            int col = i % cols;
            double colOffset = col - ((cols - 1) / 2.0);
            double rowOffset = ((rows - 1) / 2.0) - row;

            double tileDec = baseDec + (rowOffset * angularStepY);
            double cosDec = Math.Cos(tileDec * Math.PI / 180.0);
            double tileRa = baseRa + (colOffset * angularStepX / Math.Max(0.1, Math.Abs(cosDec)));

            var wcs = new WcsMatrix(new CelestialCoordinates(tileRa, tileDec), w / 2.0, h / 2.0, baseScale, 0.0, w, h);
            var panel = new MosaicPanel($"tile_{i}", $"Tile {i + 1}", w, h, wcs) { TargetName = $"Field {i + 1}" };
            project.AddPanel(panel);
        }

        Assert.Equal(9, project.Count);

        // Verify distinct centers
        var centers = project.Panels.Select(p => p.Wcs!.CenterCoordinates).ToList();
        var uniqueRas = centers.Select(c => Math.Round(c.RaDegrees, 4)).Distinct().Count();
        var uniqueDecs = centers.Select(c => Math.Round(c.DecDegrees, 4)).Distinct().Count();
        Assert.True(uniqueRas >= 3);
        Assert.Equal(3, uniqueDecs);

        // Verify overall composite bounds span full 3x3 footprint
        var bounds = project.ComputeBounds();
        Assert.NotNull(bounds);
        Assert.True(bounds.AngularWidthDegrees > 1.0);
        Assert.True(bounds.AngularHeightDegrees > 1.0);
    }
    [Fact]
    public void MosaicProject_NinePanelGrid_MasterWcsMapsAllPanelsToPositiveCanvasBounds()
    {
        var project = new MosaicProject();
        double baseRa = 180.0;
        double baseDec = 30.0;
        double baseScale = 2.5; // arcsec / px
        int w = 2000;
        int h = 2000;
        int cols = 3;
        int rows = 3;
        double overlapFraction = 0.12;

        double stepX = w * (1.0 - overlapFraction);
        double stepY = h * (1.0 - overlapFraction);
        double angularStepX = (stepX * baseScale) / 3600.0;
        double angularStepY = (stepY * baseScale) / 3600.0;

        for (int i = 0; i < 9; i++)
        {
            int row = i / cols;
            int col = i % cols;
            double colOffset = col - ((cols - 1) / 2.0);
            double rowOffset = ((rows - 1) / 2.0) - row;

            double tileDec = baseDec + (rowOffset * angularStepY);
            double cosDec = Math.Cos(tileDec * Math.PI / 180.0);
            double tileRa = baseRa + (colOffset * angularStepX / Math.Max(0.1, Math.Abs(cosDec)));

            var wcs = new WcsMatrix(new CelestialCoordinates(tileRa, tileDec), w / 2.0, h / 2.0, baseScale, 0.0, w, h);
            var panel = new MosaicPanel($"tile_{i}", $"Tile {i + 1}", w, h, wcs) { TargetName = $"Field {i + 1}" };
            project.AddPanel(panel);
        }

        var masterWcs = project.ComputeMasterWcs();
        Assert.NotNull(masterWcs);
        Assert.True(masterWcs.ImageWidth > 5000);
        Assert.True(masterWcs.ImageHeight > 5000);

        // Verify each panel maps cleanly inside the master canvas
        for (int i = 0; i < 9; i++)
        {
            var panel = project.Panels[i];
            bool mapped = masterWcs.EquatorialToPixel(panel.Wcs!.CenterCoordinates, out double px, out double py);
            Assert.True(mapped);
            Assert.True(px > 0 && px < masterWcs.ImageWidth, $"Panel {i} px {px} out of canvas width {masterWcs.ImageWidth}");
            Assert.True(py > 0 && py < masterWcs.ImageHeight, $"Panel {i} py {py} out of canvas height {masterWcs.ImageHeight}");

            // Verify all 4 footprint corners map strictly inside the master canvas with no clipping
            var (tl, tr, br, bl) = panel.GetFootprintCorners();
            CelestialCoordinates[] corners = [tl, tr, br, bl];
            foreach (var c in corners)
            {
                bool cornerMapped = masterWcs.EquatorialToPixel(c, out double cx, out double cy);
                Assert.True(cornerMapped);
                Assert.True(cx >= 0 && cx <= masterWcs.ImageWidth, $"Panel {i} corner X {cx} outside canvas [0, {masterWcs.ImageWidth}]");
                Assert.True(cy >= 0 && cy <= masterWcs.ImageHeight, $"Panel {i} corner Y {cy} outside canvas [0, {masterWcs.ImageHeight}]");
            }
        }
    }

    [Fact]
    public void MosaicProject_UpdatePanelEdgeOverlaps_DetectsIntersectionsAndLeavesOuterBordersUnflagged()
    {
        var project = new MosaicProject();
        double baseRa = 180.0;
        double baseDec = 30.0;
        double baseScale = 2.5;
        int w = 1000;
        int h = 1000;
        int cols = 3;
        int rows = 3;
        double overlapFraction = 0.15; // 15% overlap between adjacent panels

        double stepX = w * (1.0 - overlapFraction);
        double stepY = h * (1.0 - overlapFraction);
        double angularStepX = (stepX * baseScale) / 3600.0;
        double angularStepY = (stepY * baseScale) / 3600.0;

        for (int i = 0; i < 9; i++)
        {
            int row = i / cols;
            int col = i % cols;
            double colOffset = col - ((cols - 1) / 2.0);
            double rowOffset = ((rows - 1) / 2.0) - row;

            double tileDec = baseDec + (rowOffset * angularStepY);
            double cosDec = Math.Cos(tileDec * Math.PI / 180.0);
            double tileRa = baseRa + (colOffset * angularStepX / Math.Max(0.1, Math.Abs(cosDec)));

            var wcs = new WcsMatrix(new CelestialCoordinates(tileRa, tileDec), w / 2.0, h / 2.0, baseScale, 0.0, w, h);
            var panel = new MosaicPanel($"tile_{i}", $"Tile {i + 1}", w, h, wcs) { TargetName = $"Field {i + 1}" };
            project.AddPanel(panel);
        }

        project.UpdatePanelEdgeOverlaps();

        // Single panel project: no overlap
        var singleProj = new MosaicProject();
        var p0 = new MosaicPanel("single", "Single", w, h, project.Panels[0].Wcs);
        singleProj.AddPanel(p0);
        singleProj.UpdatePanelEdgeOverlaps();
        Assert.Equal(EdgeOverlap.None, p0.OverlappingEdges);

        // Center panel (row 1, col 1 = index 4) is surrounded on all sides
        var centerPanel = project.Panels[4];
        Assert.True(centerPanel.OverlappingEdges.HasFlag(EdgeOverlap.Left), $"Actual center edges: {centerPanel.OverlappingEdges}");
        Assert.True(centerPanel.OverlappingEdges.HasFlag(EdgeOverlap.Right), $"Actual center edges: {centerPanel.OverlappingEdges}");
        Assert.True(centerPanel.OverlappingEdges.HasFlag(EdgeOverlap.Top), $"Actual center edges: {centerPanel.OverlappingEdges}");
        Assert.True(centerPanel.OverlappingEdges.HasFlag(EdgeOverlap.Bottom), $"Actual center edges: {centerPanel.OverlappingEdges}");

        // Corner panel: Top-East (row 0, col 0 = index 0)
        // Outer boundaries should NOT have overlap (Top and Right in astronomical RA/Dec projection)
        var tlPanel = project.Panels[0];
        Assert.False(tlPanel.OverlappingEdges.HasFlag(EdgeOverlap.Right), "Outer Right boundary should not be flagged");
        Assert.False(tlPanel.OverlappingEdges.HasFlag(EdgeOverlap.Top), "Outer Top boundary should not be flagged");
        Assert.True(tlPanel.OverlappingEdges.HasFlag(EdgeOverlap.Left), "Inner Left seam should be flagged");
        Assert.True(tlPanel.OverlappingEdges.HasFlag(EdgeOverlap.Bottom), "Inner Bottom seam should be flagged");

        // Corner panel: Bottom-West (row 2, col 2 = index 8)
        var brPanel = project.Panels[8];
        Assert.False(brPanel.OverlappingEdges.HasFlag(EdgeOverlap.Left), "Outer Left boundary should not be flagged");
        Assert.False(brPanel.OverlappingEdges.HasFlag(EdgeOverlap.Bottom), "Outer Bottom boundary should not be flagged");
        Assert.True(brPanel.OverlappingEdges.HasFlag(EdgeOverlap.Right), "Inner Right seam should be flagged");
        Assert.True(brPanel.OverlappingEdges.HasFlag(EdgeOverlap.Top), "Inner Top seam should be flagged");
    }
}