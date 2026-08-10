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

            Assert.Equal(2, geometry.PointCount);
            Assert.Equal(1, geometry.FirstIndex);
            Assert.Equal(2, geometry.LastIndex);
            Assert.Collection(
                geometry.Samples,
                sample =>
                {
                    Assert.Equal(1, sample.SourceIndex);
                    Assert.Equal(2.5, sample.Value);
                },
                sample =>
                {
                    Assert.Equal(2, sample.SourceIndex);
                    Assert.Equal(4.75, sample.Value);
                });
            AssertScaleContains(geometry.Scale, 2.5, 4.75, 180);
            AssertInvariantCoordinates(geometry.Points, expectedCount: 2);
            Assert.StartsWith("M 266.7,180.0 L ", geometry.AreaPath, StringComparison.Ordinal);
            Assert.EndsWith(" L 533.3,180.0 Z", geometry.AreaPath, StringComparison.Ordinal);
            Assert.DoesNotContain("266,7", geometry.Points, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Spark_geometry_preserves_a_truthful_single_point_and_zero_sample_state()
    {
        var single = SparkChartGeometry.Build([null, 29, null]);

        Assert.Equal(1, single.PointCount);
        Assert.Equal(1, single.FirstIndex);
        Assert.Equal(1, single.LastIndex);
        Assert.Empty(single.AreaPath);
        var sample = Assert.Single(single.Samples);
        Assert.Equal(1, sample.SourceIndex);
        Assert.Equal(29, sample.Value);
        Assert.Equal(400, sample.X);
        Assert.InRange(sample.Y, 0, 180);
        AssertInvariantCoordinates(single.Points, expectedCount: 1);
        AssertScaleContains(single.Scale, 29, 29, 180);

        var empty = SparkChartGeometry.Build([null, double.NaN, double.PositiveInfinity]);

        Assert.Equal(0, empty.PointCount);
        Assert.Empty(empty.Samples);
        Assert.Empty(empty.Points);
        Assert.Empty(empty.AreaPath);
        Assert.Equal("unavailable", empty.Trend);
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
    public void Distance_axis_preserves_a_truthful_empty_series_when_elevation_is_missing()
    {
        var points = TestSupport.Track(8)
            .Select(point => point with { ElevationMeters = null })
            .ToArray();

        var series = ChartSeriesBuilder.Build(points, point => point.ElevationMeters, ChartAxisKind.Distance);

        Assert.Empty(series.Samples);
        Assert.Null(series.Average);
        Assert.Equal(points[^1].DistanceMeters!.Value, series.AxisMaximum);
    }

    [Fact]
    public void Chart_scale_builds_four_or_five_finite_ticks_for_narrow_constant_and_zero_ranges()
    {
        var narrow = ChartScaleBuilder.Build(29, 31, height: 150);
        AssertScaleContains(narrow, 29, 31, 150);

        var constant = ChartScaleBuilder.Build(15, 15, height: 150);
        AssertScaleContains(constant, 15, 15, 150);
        Assert.True(constant.Minimum < 15);
        Assert.True(constant.Maximum > 15);
        Assert.Equal(15, (constant.Minimum + constant.Maximum) / 2, 8);

        var constantSeries = new ChartSeriesData(
            [new ChartSample(0, 0, 15, true), new ChartSample(1, 1, 15, false)],
            15, 15, 15, 100, 1);
        var constantSegments = ChartSeriesBuilder.ToSvgSegments(constantSeries);
        Assert.Equal("0.0,90.0 800.0,90.0", Assert.Single(constantSegments));

        var zero = ChartScaleBuilder.Build(0, 0, height: 150);
        AssertScaleContains(zero, 0, 0, 150);
        Assert.Contains(zero.Ticks, tick => tick.Value == 0);
        Assert.True(zero.Minimum >= 0);

        var tiny = ChartScaleBuilder.Build(0.0011, 0.0013, height: 150);
        AssertScaleContains(tiny, 0.0011, 0.0013, 150);
        Assert.Equal(tiny.Ticks.Count, tiny.Ticks.Select(tick => tick.Label).Distinct().Count());
    }

    [Fact]
    public void Chart_scale_handles_negative_zero_crossing_and_extreme_values_without_non_finite_geometry()
    {
        var negative = ChartScaleBuilder.Build(-31, -29, height: 180);
        AssertScaleContains(negative, -31, -29, 180);
        Assert.True(negative.Maximum < 0);

        var crossing = ChartScaleBuilder.Build(-5, 8, includeZero: true, height: 180);
        AssertScaleContains(crossing, -5, 8, 180);
        Assert.Contains(crossing.Ticks, tick => tick.Value == 0);

        var extreme = ChartScaleBuilder.Build(-1e150, 1e150, height: 180);
        AssertScaleContains(extreme, -1e150, 1e150, 180);
        Assert.True(double.IsFinite(extreme.Project(-1e150, 180)));
        Assert.True(double.IsFinite(extreme.Project(1e150, 180)));
    }

    [Fact]
    public void Chart_scale_labels_are_unique_and_localized_while_positions_remain_numeric()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("da-DK");
            var scale = ChartScaleBuilder.Build(29, 29.4, height: 150);

            Assert.Contains(scale.Ticks, tick => tick.Label.Contains(','));
            Assert.Equal(scale.Ticks.Count, scale.Ticks.Select(tick => tick.Label).Distinct().Count());
            Assert.All(scale.Ticks, tick => Assert.True(double.IsFinite(tick.Position)));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Explicit_plot_domain_keeps_svg_coordinates_finite_and_invariant()
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var points = Enumerable.Range(0, 5)
            .Select(index => new Core.Domain.TrackPoint(
                start.AddSeconds(index),
                1,
                -30,
                index * 10,
                29 + index * 0.5,
                null, null, null, null, null))
            .ToArray();
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("da-DK");
            var series = ChartSeriesBuilder.Build(
                points,
                point => point.ElevationMeters,
                ChartAxisKind.ElapsedTime);
            var scale = ChartScaleBuilder.Build(series.Minimum!.Value, series.Maximum!.Value);
            var segments = ChartSeriesBuilder.ToSvgSegments(
                series,
                verticalPadding: 0,
                plotMinimum: scale.Minimum,
                plotMaximum: scale.Maximum);

            var segment = Assert.Single(segments);
            Assert.Matches(new Regex(@"\d+\.\d+,\d+\.\d+", RegexOptions.CultureInvariant), segment);
            foreach (var coordinate in segment.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = coordinate.Split(',');
                Assert.Equal(2, parts.Length);
                var x = double.Parse(parts[0], CultureInfo.InvariantCulture);
                var y = double.Parse(parts[1], CultureInfo.InvariantCulture);
                Assert.True(double.IsFinite(x));
                Assert.True(double.IsFinite(y));
                Assert.InRange(x, 0, 800);
                Assert.InRange(y, 0, 180);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Axis_ticks_cover_elapsed_short_and_long_distance_and_five_month_positions()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("da-DK");

            var elapsed = ChartAxisLabels.BuildSeriesTicks(3_661, ChartAxisKind.ElapsedTime);
            Assert.Equal(["0:00", "30:30", "1:01:01"], elapsed.Select(tick => tick.Label).ToArray());
            Assert.Equal([0d, 400d, 800d], elapsed.Select(tick => tick.Position).ToArray());

            var oneSecond = ChartAxisLabels.BuildSeriesTicks(1, ChartAxisKind.ElapsedTime);
            AssertThreeDistinctTicks(oneSecond, expectedMaximum: 1);
            Assert.Equal(["0:00,0", "0:00,5", "0:01,0"], oneSecond.Select(tick => tick.Label).ToArray());

            var zeroElapsed = ChartAxisLabels.BuildSeriesTicks(0, ChartAxisKind.ElapsedTime);
            AssertThreeDistinctTicks(zeroElapsed, expectedMaximum: 1);
            Assert.Equal(0, zeroElapsed[0].Value);
            Assert.Equal(oneSecond.Select(tick => tick.Label), zeroElapsed.Select(tick => tick.Label));

            var shortDistance = ChartAxisLabels.BuildSeriesTicks(462, ChartAxisKind.Distance);
            Assert.Equal(["0 m", "231 m", "462 m"], shortDistance.Select(tick => tick.Label).ToArray());

            var subTwoMetres = ChartAxisLabels.BuildSeriesTicks(1.5, ChartAxisKind.Distance);
            AssertThreeDistinctTicks(subTwoMetres, expectedMaximum: 1.5);
            Assert.Equal(["0,00 m", "0,75 m", "1,50 m"], subTwoMetres.Select(tick => tick.Label).ToArray());

            var zeroDistance = ChartAxisLabels.BuildSeriesTicks(0, ChartAxisKind.Distance);
            AssertThreeDistinctTicks(zeroDistance, expectedMaximum: 1);
            Assert.Equal(0, zeroDistance[0].Value);
            Assert.Equal(["0,0 m", "0,5 m", "1,0 m"], zeroDistance.Select(tick => tick.Label).ToArray());

            var longDistance = ChartAxisLabels.BuildSeriesTicks(5_000, ChartAxisKind.Distance);
            Assert.Equal(["0 m", "2,50 km", "5,00 km"], longDistance.Select(tick => tick.Label).ToArray());

            var months = Enumerable.Range(1, 12).Select(index => $"Month {index}").ToArray();
            var monthTicks = ChartAxisLabels.BuildCategoryTicks(months);
            Assert.Equal(5, monthTicks.Count);
            Assert.Equal("Month 1", monthTicks[0].Label);
            Assert.Equal("Month 12", monthTicks[^1].Label);
            Assert.Equal(5, monthTicks.Select(tick => tick.Label).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(0, monthTicks[0].Position);
            Assert.Equal(800d, monthTicks[^1].Position);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
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

    private static void AssertScaleContains(
        ChartScaleGeometry scale,
        double minimum,
        double maximum,
        double height)
    {
        Assert.InRange(scale.Ticks.Count, 4, 5);
        Assert.True(scale.Minimum <= minimum);
        Assert.True(scale.Maximum >= maximum);
        Assert.True(scale.Maximum > scale.Minimum);
        Assert.Equal(scale.Ticks.Count, scale.Ticks.Select(tick => tick.Label).Distinct().Count());
        Assert.All(scale.Ticks, tick =>
        {
            Assert.True(double.IsFinite(tick.Value));
            Assert.True(double.IsFinite(tick.Position));
            Assert.InRange(tick.Position, 0, height);
        });
        Assert.InRange(scale.Project(minimum, height), 0, height);
        Assert.InRange(scale.Project(maximum, height), 0, height);
    }

    private static void AssertInvariantCoordinates(string points, int expectedCount)
    {
        var coordinates = points.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expectedCount, coordinates.Length);
        foreach (var coordinate in coordinates)
        {
            Assert.Matches(new Regex(@"^-?\d+\.\d+,-?\d+\.\d+$", RegexOptions.CultureInvariant), coordinate);
            var parts = coordinate.Split(',');
            Assert.Equal(2, parts.Length);
            var x = double.Parse(parts[0], CultureInfo.InvariantCulture);
            var y = double.Parse(parts[1], CultureInfo.InvariantCulture);
            Assert.True(double.IsFinite(x));
            Assert.True(double.IsFinite(y));
            Assert.InRange(x, 0, 800);
            Assert.InRange(y, 0, 180);
        }
    }

    private static void AssertThreeDistinctTicks(
        IReadOnlyList<ChartAxisTick> ticks,
        double expectedMaximum)
    {
        Assert.Equal(3, ticks.Count);
        Assert.Equal(3, ticks.Select(tick => tick.Label).Distinct(StringComparer.CurrentCulture).Count());
        Assert.Equal(0, ticks[0].Value);
        Assert.Equal(expectedMaximum / 2, ticks[1].Value, 8);
        Assert.Equal(expectedMaximum, ticks[^1].Value, 8);
        Assert.Equal([0d, 400d, 800d], ticks.Select(tick => tick.Position).ToArray());
    }
}
