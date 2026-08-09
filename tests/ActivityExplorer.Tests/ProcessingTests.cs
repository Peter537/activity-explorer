using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;

namespace ActivityExplorer.Tests;

public sealed class ProcessingTests
{
    [Fact]
    public void Brotli_stream_round_trip_preserves_samples()
    {
        var input = TestSupport.Track();
        var payload = TrackCodec.Encode(input);
        var output = TrackCodec.Decode(payload);
        Assert.Equal(input.Count, output.Count);
        Assert.Equal(input[8].PowerWatts, output[8].PowerWatts);
        Assert.True(payload.Length < System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(input).Length);
    }

    [Fact]
    public void Wkb_round_trip_preserves_geometry_and_bounds()
    {
        var input = TestSupport.Track().ToArray();
        input[7] = input[7] with { ElevationMeters = null };
        var wkb = GeometryCodec.ToWkb(input);
        var output = GeometryCodec.FromWkb(wkb);
        var bounds = GeometryCodec.Bounds(output);
        Assert.Equal(input.Length, output.Count);
        Assert.Equal(input[6].ElevationMeters, output[6].ElevationMeters);
        Assert.Null(output[7].ElevationMeters);
        Assert.Equal(input[8].ElevationMeters, output[8].ElevationMeters);
        Assert.InRange(bounds.MinLat!.Value, 0.9999, 1.0001);
        Assert.True(GeometryCodec.DistanceMeters(output) > 300);
    }

    [Fact]
    public void Natural_fingerprint_is_stable()
    {
        var parsed = Parsed(TestSupport.Track());
        Assert.Equal(Fingerprint.For(parsed), Fingerprint.For(parsed));
    }

    [Fact]
    public async Task Segment_matcher_accepts_forward_noisy_path()
    {
        var activity = TestSupport.Track(60);
        var segment = activity.Skip(10).Take(25)
            .Select(x => x with { Latitude = x.Latitude + 0.00002, Longitude = x.Longitude - 0.00002 }).ToArray();
        var matches = await new SegmentMatcher().MatchAsync(activity, segment, 30);
        var match = Assert.Single(matches);
        Assert.True(match.CoveragePercent >= 95);
        Assert.True(match.StartIndex < match.EndIndex);
    }

    [Fact]
    public async Task Segment_matcher_rejects_reverse_direction()
    {
        var activity = TestSupport.Track(60);
        var segment = activity.Skip(10).Take(25).Reverse().ToArray();
        Assert.Empty(await new SegmentMatcher().MatchAsync(activity, segment, 30));
    }

    [Fact]
    public async Task Segment_matcher_retains_multiple_passes()
    {
        var pass = TestSupport.Track(40).ToArray();
        var activity = pass.Concat(pass.Select((point, index) => point with { Timestamp = point.Timestamp!.Value.AddMinutes(10) })).ToArray();
        var segment = pass.Skip(5).Take(20).ToArray();
        var matches = await new SegmentMatcher().MatchAsync(activity, segment, 30);
        Assert.True(matches.Count >= 2);
    }

    [Fact]
    public async Task Segment_matcher_rejects_a_nearby_parallel_track()
    {
        var segment = TestSupport.Track(40).ToArray();
        var parallel = segment.Select(point => point with { Longitude = point.Longitude + 0.001 }).ToArray();
        Assert.Empty(await new SegmentMatcher().MatchAsync(parallel, segment, 30));
    }

    [Fact]
    public async Task Segment_matcher_keeps_a_continuous_pass_when_a_later_return_is_closer()
    {
        var start = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var segment = Enumerable.Range(0, 51)
            .Select(index => Point(
                start.AddSeconds(index + 2),
                55 + index * 0.00004 + Math.Sin(index / 5d) * 0.00001,
                12 + index * 0.00008))
            .ToArray();
        var geometry = new[]
            {
                Point(start, segment[0].Latitude!.Value - 0.00015, segment[0].Longitude!.Value - 0.00005),
                Point(start.AddSeconds(1), segment[0].Latitude!.Value - 0.00007, segment[0].Longitude!.Value - 0.00002)
            }
            .Concat(segment)
            .Concat(segment.Reverse())
            .ToArray();
        var activity = geometry.Select((point, index) => point with { Timestamp = start.AddSeconds(index) }).ToArray();

        var match = Assert.Single(await new SegmentMatcher().MatchAsync(activity, segment, 30));

        Assert.Equal(2, match.StartIndex);
        Assert.Equal(52, match.EndIndex);
        Assert.Equal(100, match.CoveragePercent, 8);
        Assert.Equal(0, match.MeanDistanceMeters, 8);
    }

    [Fact]
    public async Task Segment_matcher_preserves_raw_indices_around_missing_coordinates_and_uneven_spacing()
    {
        var start = new DateTimeOffset(2026, 6, 2, 8, 0, 0, TimeSpan.Zero);
        var activity = new[]
        {
            Point(start, 54.9998, 11.9998),
            Point(start.AddSeconds(1), 55, 12),
            Point(start.AddSeconds(2), 55.00003, 12.00004),
            new TrackPoint(start.AddSeconds(3), null, null, null, 12, null, null, null, null, null),
            Point(start.AddSeconds(4), 55.00011, 12.00015),
            Point(start.AddSeconds(5), 55.00024, 12.00032),
            Point(start.AddSeconds(6), 55.0003, 12.0004),
            Point(start.AddSeconds(7), 55.0005, 12.0006)
        };
        var segment = activity.Skip(1).Take(6)
            .Where(point => point.Latitude.HasValue && point.Longitude.HasValue)
            .ToArray();

        var match = Assert.Single(await new SegmentMatcher().MatchAsync(activity, segment, 20));

        Assert.Equal(1, match.StartIndex);
        Assert.Equal(6, match.EndIndex);
        Assert.Equal(100, match.CoveragePercent, 8);
    }

    [Fact]
    public async Task Segment_matcher_supports_overlapping_endpoint_zones_for_a_short_segment()
    {
        var start = new DateTimeOffset(2026, 6, 3, 8, 0, 0, TimeSpan.Zero);
        var activity = Enumerable.Range(0, 5)
            .Select(index => Point(start.AddSeconds(index), 55, 12 + index * 0.00004))
            .ToArray();
        var segment = activity.Skip(1).Take(3).ToArray();

        var match = Assert.Single(await new SegmentMatcher().MatchAsync(activity, segment, 30));

        Assert.Equal(1, match.StartIndex);
        Assert.Equal(3, match.EndIndex);
        Assert.Equal(100, match.CoveragePercent, 8);
    }

    [Fact]
    public async Task Segment_matcher_interpolates_across_the_antimeridian()
    {
        var start = new DateTimeOffset(2026, 6, 4, 8, 0, 0, TimeSpan.Zero);
        var activity = new[]
        {
            Point(start, 1, 179.97),
            Point(start.AddSeconds(1), 1, 179.985),
            Point(start.AddSeconds(2), 1, -179.995),
            Point(start.AddSeconds(3), 1, -179.98),
            Point(start.AddSeconds(4), 1, -179.965)
        };

        var match = Assert.Single(await new SegmentMatcher().MatchAsync(activity, activity, 30));

        Assert.Equal(0, match.StartIndex);
        Assert.Equal(4, match.EndIndex);
        Assert.Equal(100, match.CoveragePercent, 8);
    }

    [Fact]
    public async Task Segment_matcher_breaks_equal_endpoint_scores_by_stream_order()
    {
        var start = new DateTimeOffset(2026, 6, 5, 8, 0, 0, TimeSpan.Zero);
        var segment = Enumerable.Range(0, 6)
            .Select(index => Point(start.AddSeconds(index + 1), 55 + index * 0.0001, 12 + index * 0.0001))
            .ToArray();
        var activity = new[] { segment[0] }
            .Concat(segment)
            .Concat([segment[^1]])
            .Select((point, index) => point with { Timestamp = start.AddSeconds(index) })
            .ToArray();

        var match = Assert.Single(await new SegmentMatcher().MatchAsync(activity, segment, 20));

        Assert.Equal(0, match.StartIndex);
        Assert.Equal(6, match.EndIndex);
    }

    [Fact]
    public async Task Segment_matcher_caps_alignment_for_an_antipodal_path_without_overflow()
    {
        var start = new DateTimeOffset(2026, 6, 6, 8, 0, 0, TimeSpan.Zero);
        var path = new[]
        {
            Point(start, 0, 0),
            Point(start.AddHours(1), 0, 180)
        };

        Assert.Equal(
            SegmentMatcher.MaximumAlignmentSamples,
            SegmentMatcher.AlignmentSampleCount(TrackPathAnalysis.HaversineMeters(0, 0, 0, 180)));
        var match = Assert.Single(await new SegmentMatcher().MatchAsync(path, path, 30));
        Assert.Equal(0, match.StartIndex);
        Assert.Equal(1, match.EndIndex);
        Assert.Equal(100, match.CoveragePercent, 8);
    }

    [Fact]
    public async Task Segment_matcher_rejects_absent_endpoints_before_measuring_an_extreme_path()
    {
        var start = new DateTimeOffset(2026, 6, 7, 8, 0, 0, TimeSpan.Zero);
        var segment = Enumerable.Range(0, 1_100)
            .Select(index => Point(start.AddSeconds(index), 0, index % 2 == 0 ? 0 : 180))
            .ToArray();
        var remoteActivity = new[]
        {
            Point(start, 80, 90),
            Point(start.AddSeconds(1), 80.001, 90.001)
        };

        Assert.Empty(await new SegmentMatcher().MatchAsync(remoteActivity, segment, 30));
    }

    [Fact]
    public async Task Segment_matcher_observes_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new SegmentMatcher().MatchAsync(TestSupport.Track(60), TestSupport.Track(20), 30, cancellation.Token));
    }

    private static TrackPoint Point(DateTimeOffset timestamp, double latitude, double longitude) =>
        new(timestamp, latitude, longitude, null, 10, 4, 130, 85, 200, 15);

    private static ParsedActivity Parsed(IReadOnlyList<TrackPoint> points) => new()
    {
        Sport = SportKind.Cycling,
        Title = "Test",
        StartTimeUtc = points[0].Timestamp!.Value,
        DistanceMeters = GeometryCodec.DistanceMeters(points),
        MovingTimeSeconds = 60,
        ElapsedTimeSeconds = 60,
        Points = points
    };
}
