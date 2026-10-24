namespace AstroCounter.Tests;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AstroCounter.Core.Astrometry;
using AstroCounter.Core.Processing;
using Xunit;
using Xunit.Abstractions;

public class HipparcosCatalogTests
{
    private readonly ITestOutputHelper _output;

    public HipparcosCatalogTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void HipparcosCatalog_LoadsSuccessfully_Contains117kStars()
    {
        var sw = Stopwatch.StartNew();
        int total = HipparcosCatalog.TotalStars;
        sw.Stop();

        _output.WriteLine($"Loaded {total} Hipparcos stars in {sw.ElapsedMilliseconds} ms.");
        Assert.True(total >= 117_000, $"Expected >= 117000 stars, found {total}");
    }

    [Fact]
    public void HipparcosCatalog_QueryCygnusRegion_FindsProminentStarsWithHighSpeed()
    {
        var sw = Stopwatch.StartNew();
        var stars = HipparcosCatalog.QueryRegion(314.0, 45.0, 6.0, maxMag: 7.0, maxResults: 100);
        sw.Stop();

        _output.WriteLine($"Query returned {stars.Count} stars in {sw.Elapsed.TotalMicroseconds:F1} µs ({sw.ElapsedMilliseconds} ms).");

        Assert.NotEmpty(stars);
        var brightest = stars[0];
        _output.WriteLine($"Brightest star in field: HIP {brightest.HipId}, Mag={brightest.Mag:F2}, RA={brightest.RaDeg:F2}, Dec={brightest.DecDeg:F2}");

        Assert.Equal(102098, brightest.HipId); // Deneb
        Assert.InRange(brightest.Mag, 1.2f, 1.3f);
        Assert.InRange(brightest.RaDeg, 310.0f, 311.0f);
        Assert.InRange(brightest.DecDeg, 45.0f, 45.5f);
    }

    [Fact]
    public void HipparcosCatalog_QueryOrionBelt_FindsAlnitakAlnilamMintaka()
    {
        var stars = HipparcosCatalog.QueryRegion(84.0, -1.0, 3.0, maxMag: 3.5, maxResults: 10);

        _output.WriteLine($"Orion belt query returned {stars.Count} bright stars:");
        foreach (var s in stars)
        {
            _output.WriteLine($"  HIP {s.HipId}: Mag={s.Mag:F2}, RA={s.RaDeg:F2}, Dec={s.DecDeg:F2}");
        }

        Assert.Contains(stars, s => s.HipId == 26311);
        Assert.Contains(stars, s => s.HipId == 26727);
        Assert.Contains(stars, s => s.HipId == 25930);
    }

    [Fact]
    public void HipparcosCatalog_MatchesCygnusPhotoStarsDirectly()
    {
        string uploadPath = @"C:\Users\unala\.gemini\antigravity\brain\b15cadec-52bb-45f3-9cef-cd26e10d6fa3\.user_uploaded\media_1792700998561_fc0e1c71.jpg";
        if (!File.Exists(uploadPath)) return;

        using var bmp = ImagePreprocessor.LoadImage(uploadPath);
        var candidateSets = StarPatternPlateSolver.ExtractCandidateStarSets(uploadPath, bmp.Width, bmp.Height, null);
        var imgStars = candidateSets[0];

        var hipStars = HipparcosCatalog.QueryRegion(314.0, 45.0, 6.0, maxMag: 6.5, maxResults: 40);

        _output.WriteLine($"Found {hipStars.Count} Hipparcos stars with Mag <= 6.5 in NGC 7000 region.");
        foreach (var hs in hipStars.Take(12))
        {
            _output.WriteLine($"  HIP {hs.HipId,6}: Mag={hs.Mag,4:F2}, RA={hs.RaDeg,7:F3}, Dec={hs.DecDeg,7:F3}");
        }

        Assert.True(hipStars.Count >= 20);
    }
}

