namespace AstroCounter.Tests;

using AstroCounter.Core.Models;
using AstroCounter.Core.Processing;
using Xunit;

public class ImageTilerTests
{
    [Fact]
    public void ComputeSlices_SingleTileImage_ReturnsExactlyOneSlice()
    {
        var config = new TilingConfig(TileWidth: 640, TileHeight: 640);
        var slices = ImageTiler.ComputeSlices(500, 500, config);

        Assert.Single(slices);
        Assert.Equal(0, slices[0].X);
        Assert.Equal(0, slices[0].Y);
    }

    [Fact]
    public void ComputeSlices_FullHdImage_GeneratesOverlappingSlices()
    {
        // 1920x1080 with 640x640 and 20% overlap (step = 512)
        var config = new TilingConfig(TileWidth: 640, TileHeight: 640, OverlapRatio: 0.20f);
        var slices = ImageTiler.ComputeSlices(1920, 1080, config);

        Assert.NotEmpty(slices);
        // Slices must cover the boundaries
        Assert.Contains(slices, s => s.X == 0 && s.Y == 0);
        Assert.Contains(slices, s => s.X + s.Width == 1920);
        Assert.Contains(slices, s => s.Y + s.Height == 1080);
    }

    [Fact]
    public void ComputeSlices_InvalidDimensions_ReturnsEmpty()
    {
        var config = new TilingConfig();
        var slices = ImageTiler.ComputeSlices(0, -10, config);

        Assert.Empty(slices);
    }
}

