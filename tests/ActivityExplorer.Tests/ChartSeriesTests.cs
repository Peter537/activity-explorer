using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Web.Components.Shared;

namespace ActivityExplorer.Tests;

public sealed class ChartSeriesTests
{
    private static readonly JsonSerializerOptions LegacyJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Spark_geometry_uses_invariant_decimals_and_real_area_endpoints_under_comma_decimal_culture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CommaDecimalCulture();
            var geometry = SparkChartGeometry.Build([null, 2.5, 4.75, null]);

            Assert.Equal("266.7,170.0 533.3,20.0", geometry.Points);
            Assert.Equal("M 266.7,180.0 L 266.7,170.0 L 533.3,20.0 L 533.3,180.0 Z", geometry.AreaPath);
            Assert.Equal(1, geometry.FirstIndex);
            Assert.Equal(2, geometry.LastIndex);
            Assert.DoesNotContain("266,7", geometry.Points, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Downsampling_preserves_extrema_and_invariant_svg_under_comma_decimal_culture()
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var points = Enumerable.Range(0, 1_000)
            .Select(index => new Core.Domain.TrackPoint(start.AddSeconds(index), 1, -30, index * 5, index % 17, 5, 100 + index % 20, null, null, null))
            .ToArray();
        points[321] = points[321] with { ElevationMeters = -12.5 };
        points[654] = points[654] with { ElevationMeters = 123.75 };

        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CommaDecimalCulture();
            var series = ChartSeriesBuilder.Build(points, point => point.ElevationMeters, ChartAxisKind.ElapsedTime, maximumSamples: 40);
            var svg = ChartSeriesBuilder.ToSvgSegments(series);

            Assert.InRange(series.Samples.Count, 20, 40);
            Assert.Equal(-12.5, series.Minimum);
            Assert.Equal(123.75, series.Maximum);
            Assert.Equal(100, series.CoveragePercent);
            Assert.Single(svg);
            Assert.Matches(new Regex(@"\d+\.\d+,\d+\.\d+", RegexOptions.CultureInvariant), svg[0]);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Missing_samples_and_timestamp_gaps_split_lines_and_report_coverage()
    {
        var points = TestSupport.Track(12).ToArray();
        points[5] = points[5] with { HeartRate = null };
        points[9] = points[9] with { Timestamp = points[8].Timestamp!.Value.AddMinutes(2) };

        var series = ChartSeriesBuilder.Build(points, point => point.HeartRate, ChartAxisKind.ElapsedTime, maximumSamples: 100);
        var segments = ChartSeriesBuilder.ToSvgSegments(series);

        Assert.Equal(11, series.Samples.Count);
        Assert.Equal(11d / 12d * 100d, series.CoveragePercent, 6);
        Assert.Equal(3, segments.Count);
    }

    [Fact]
    public void Stream_without_optional_respiration_decodes_as_null()
    {
        var legacy = new[]
        {
            new
            {
                timestamp = "2026-01-01T10:00:00+00:00",
                latitude = 1d,
                longitude = -30d,
                distanceMeters = 0d,
                elevationMeters = 10d,
                speedMetersPerSecond = 4d,
                heartRate = 130d,
                cadence = 85d,
                powerWatts = (double?)null,
                temperatureCelsius = 18d
            }
        };
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            JsonSerializer.Serialize(brotli, legacy, LegacyJsonOptions);

        var point = Assert.Single(TrackCodec.Decode(output.ToArray()));
        Assert.Null(point.RespirationRate);
        Assert.Equal(130, point.HeartRate);
    }

    private static CultureInfo CommaDecimalCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = ",";
        return culture;
    }
}
