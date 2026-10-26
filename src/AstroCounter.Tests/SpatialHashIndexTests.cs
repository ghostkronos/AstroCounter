namespace AstroCounter.Tests;

using System;
using System.Collections.Generic;
using AstroCounter.Core.Astrometry;
using Xunit;

public class SpatialHashIndexTests
{
    private readonly record struct StarEntry(int Id, float Ra, float Dec);

    [Fact]
    public void SpatialHashIndex_BasicInsertAndQuery_ReturnsExpectedItems()
    {
        var index = new SpatialHashIndex<StarEntry>();

        var s1 = new StarEntry(1, 150.2f, 25.4f);
        var s2 = new StarEntry(2, 150.8f, 25.8f);
        var s3 = new StarEntry(3, 180.0f, 40.0f);

        index.Insert(s1.Ra, s1.Dec, s1);
        index.Insert(s2.Ra, s2.Dec, s2);
        index.Insert(s3.Ra, s3.Dec, s3);

        var results = new List<StarEntry>();
        index.QueryBoundingBox(149.0, 151.0, 24.0, 26.0, results);

        Assert.Equal(2, results.Count);
        Assert.Contains(s1, results);
        Assert.Contains(s2, results);
        Assert.DoesNotContain(s3, results);
    }

    [Fact]
    public void SpatialHashIndex_RaWrapAround_FindsStarsAcrossZeroBoundary()
    {
        var index = new SpatialHashIndex<StarEntry>();

        var sNear360 = new StarEntry(1, 359.6f, 10.0f);
        var sNearZero = new StarEntry(2, 0.4f, 10.2f);
        var sAway = new StarEntry(3, 180.0f, 10.0f);

        index.Insert(sNear360.Ra, sNear360.Dec, sNear360);
        index.Insert(sNearZero.Ra, sNearZero.Dec, sNearZero);
        index.Insert(sAway.Ra, sAway.Dec, sAway);

        // Query across 0h / 360° meridian: 358° to 2°
        var results = new List<StarEntry>();
        index.QueryBoundingBox(358.0, 2.0, 9.0, 11.0, results);

        Assert.Equal(2, results.Count);
        Assert.Contains(sNear360, results);
        Assert.Contains(sNearZero, results);
        Assert.DoesNotContain(sAway, results);
    }

    [Fact]
    public void SpatialHashIndex_PolarCapConvergence_RoutesThroughPolarBucket()
    {
        var index = new SpatialHashIndex<StarEntry>();

        var northPolarStar1 = new StarEntry(1, 45.0f, 88.5f);
        var northPolarStar2 = new StarEntry(2, 270.0f, 89.2f);
        var midLatitudeStar = new StarEntry(3, 45.0f, 40.0f);

        index.Insert(northPolarStar1.Ra, northPolarStar1.Dec, northPolarStar1);
        index.Insert(northPolarStar2.Ra, northPolarStar2.Dec, northPolarStar2);
        index.Insert(midLatitudeStar.Ra, midLatitudeStar.Dec, midLatitudeStar);

        // Query north pole region: Dec > 85°
        var results = new List<StarEntry>();
        index.QueryBoundingBox(0.0, 360.0, 85.0, 90.0, results);

        Assert.Equal(2, results.Count);
        Assert.Contains(northPolarStar1, results);
        Assert.Contains(northPolarStar2, results);
        Assert.DoesNotContain(midLatitudeStar, results);
    }

    [Fact]
    public void SpatialHashIndex_ZeroAllocationSpanQuery_FillsSpanCorrectly()
    {
        var index = new SpatialHashIndex<StarEntry>();

        for (int i = 0; i < 50; i++)
        {
            index.Insert(100.0 + (i * 0.01), 30.0 + (i * 0.01), new StarEntry(i, (float)(100.0 + (i * 0.01)), (float)(30.0 + (i * 0.01))));
        }

        Span<StarEntry> buffer = stackalloc StarEntry[100];
        int count = index.QueryBoundingBox(99.0, 101.0, 29.0, 31.0, buffer);

        Assert.Equal(50, count);
        Assert.Equal(0, buffer[0].Id);
        Assert.Equal(49, buffer[49].Id);
    }
}

