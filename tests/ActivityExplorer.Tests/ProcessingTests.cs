using ActivityExplorer.Core.Domain;
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
        var input = TestSupport.Track();
        var wkb = GeometryCodec.ToWkb(input);
        var output = GeometryCodec.FromWkb(wkb);
        var bounds = GeometryCodec.Bounds(output);
        Assert.Equal(input.Count, output.Count);
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
