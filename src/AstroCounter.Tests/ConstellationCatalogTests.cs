namespace AstroCounter.Tests;

using System.Linq;
using AstroCounter.Core.Astrometry;
using Xunit;

public class ConstellationCatalogTests
{
    [Fact]
    public void ConstellationCatalog_ContainsMajorConstellations()
    {
        var lines = ConstellationCatalog.Lines;
        Assert.NotNull(lines);
        Assert.True(lines.Count >= 25);

        // Check Cygnus lines exist
        var cygnusLines = lines.Where(l => l.ConstellationAbbr == "Cyg").ToList();
        Assert.True(cygnusLines.Count >= 5);

        // Check Orion lines exist
        var orionLines = lines.Where(l => l.ConstellationAbbr == "Ori").ToList();
        Assert.True(orionLines.Count >= 6);

        // Check coordinates validity
        foreach (var line in lines)
        {
            Assert.InRange(line.Start.RaDegrees, 0.0, 360.0);
            Assert.InRange(line.Start.DecDegrees, -90.0, 90.0);
            Assert.InRange(line.End.RaDegrees, 0.0, 360.0);
            Assert.InRange(line.End.DecDegrees, -90.0, 90.0);
            Assert.False(string.IsNullOrWhiteSpace(line.ConstellationName));
            Assert.False(string.IsNullOrWhiteSpace(line.DisplayName));
        }
    }
}

