namespace AstroCounter.Tests;

using AstroCounter.Core.Export;
using AstroCounter.Core.Models;
using Xunit;

public class ResultExporterTests
{
    [Fact]
    public void ExportToCsv_WritesValidHeaderAndData()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"astro_test_{Guid.NewGuid()}.csv");
        try
        {
            var objects = new[]
            {
                new AstrophotographyObject(1, new BoundingBox(12.5f, 34.0f, 6.0f, 6.0f), 0.92f, "star")
            };

            ResultExporter.ExportToCsv(objects, tempFile);

            Assert.True(File.Exists(tempFile));
            string[] lines = File.ReadAllLines(tempFile);
            Assert.True(lines.Length >= 2);
            Assert.Contains("Id,ClassName,Confidence,Tier,SNR_Sigma,X,Y,Width,Height,CenterX,CenterY", lines[0]);
            Assert.Contains("star", lines[1]);
            Assert.Contains("Tier 1 (High)", lines[1]);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
