namespace AstroCounter.Core.Processing;

using AstroCounter.Core.Models;

public readonly record struct TileSlice(int Index, int X, int Y, int Width, int Height);

/// <summary>
/// Handles SAHI (Slicing Aided Hyper Inference) image decomposition into overlapping tiles.
/// </summary>
public static class ImageTiler
{
    public static IReadOnlyList<TileSlice> ComputeSlices(int imageWidth, int imageHeight, TilingConfig config)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
            return Array.Empty<TileSlice>();

        var slices = new List<TileSlice>();
        int tileW = Math.Min(config.TileWidth, imageWidth);
        int tileH = Math.Min(config.TileHeight, imageHeight);

        int stepX = config.StepX;
        int stepY = config.StepY;

        var xCoords = Generate1DSteps(imageWidth, tileW, stepX);
        var yCoords = Generate1DSteps(imageHeight, tileH, stepY);

        int index = 0;
        foreach (int y in yCoords)
        {
            foreach (int x in xCoords)
            {
                slices.Add(new TileSlice(
                    Index: index++,
                    X: x,
                    Y: y,
                    Width: Math.Min(config.TileWidth, imageWidth - x),
                    Height: Math.Min(config.TileHeight, imageHeight - y)
                ));
            }
        }

        return slices;
    }

    private static List<int> Generate1DSteps(int totalSize, int tileSize, int stepSize)
    {
        var steps = new List<int>();
        int current = 0;

        while (current + tileSize <= totalSize)
        {
            steps.Add(current);
            current += stepSize;
        }

        // If the last step didn't reach the boundary, add an overlapping slice pinned to the right/bottom edge
        if (steps.Count == 0 || steps[^1] + tileSize < totalSize)
        {
            int lastStep = Math.Max(0, totalSize - tileSize);
            if (!steps.Contains(lastStep))
            {
                steps.Add(lastStep);
            }
        }

        return steps;
    }
}

