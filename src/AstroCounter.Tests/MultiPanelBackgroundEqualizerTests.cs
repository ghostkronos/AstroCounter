namespace AstroCounter.Tests;

using System;
using System.Collections.Generic;
using AstroCounter.Core.Processing;
using SkiaSharp;
using Xunit;

public class MultiPanelBackgroundEqualizerTests
{
    [Fact]
    public void EstimateBackground_UniformBitmap_ReturnsExactLuminance()
    {
        using var bmp = new SKBitmap(128, 128, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(40, 40, 40));
        }

        var stats = MultiPanelBackgroundEqualizer.EstimateBackground(bmp);

        Assert.InRange(stats.MedianLuminance, 39.0f, 41.0f);
        Assert.True(stats.Sigma < 1.0f);
    }

    [Fact]
    public void ComputeEqualizationOffsets_CalculatesCorrectShifts()
    {
        var panel1 = new MultiPanelBackgroundEqualizer.PanelBackgroundStats(15.0f, 2.0f, 0f);
        var panel2 = new MultiPanelBackgroundEqualizer.PanelBackgroundStats(35.0f, 4.0f, 0f);
        var panel3 = new MultiPanelBackgroundEqualizer.PanelBackgroundStats(20.0f, 1.5f, 0f);

        var list = new List<MultiPanelBackgroundEqualizer.PanelBackgroundStats> { panel1, panel2, panel3 };
        float target = 20.0f;

        var offsets = MultiPanelBackgroundEqualizer.ComputeEqualizationOffsets(list, target);

        Assert.Equal(3, offsets.Length);
        Assert.Equal(5.0f, offsets[0]);   // 20 - 15 = +5
        Assert.Equal(-15.0f, offsets[1]);  // 20 - 35 = -15
        Assert.Equal(0.0f, offsets[2]);   // 20 - 20 = 0
    }

    [Fact]
    public void ApplyBackgroundOffset_AdjustsPixelValuesSafely()
    {
        using var bmp = new SKBitmap(32, 32, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(100, 100, 100));
        }

        using var adjusted = MultiPanelBackgroundEqualizer.ApplyBackgroundOffset(bmp, -20.0f);

        var pixel = adjusted.GetPixel(10, 10);
        Assert.Equal(80, pixel.Red);
        Assert.Equal(80, pixel.Green);
        Assert.Equal(80, pixel.Blue);
    }
}

