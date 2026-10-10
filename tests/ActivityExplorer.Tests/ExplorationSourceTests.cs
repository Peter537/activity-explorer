using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using Dynastream.Fit;

namespace ActivityExplorer.Tests;

public sealed class ExplorationSourceTests
{
    [Theory]
    [InlineData("gpx")]
    [InlineData("tcx")]
    public async Task Original_track_boundaries_are_recovered_without_changing_the_canonical_stream(string format)
    {
        var directory = TestSupport.NewDirectory();
        var content = format == "gpx" ? Gpx(split: true) : Tcx();
        var source = await Source(directory, $"activity.{format}", content);
        var canonical = Assert.Single(await new XmlActivityImporter().ReadAsync(source.StoredPath, SourceKind.Gpx)).Parsed.Points;
        var stream = new ActivityStream { CompressedPayload = TrackCodec.Encode(canonical), PointCount = canonical.Count };
        var originalBytes = stream.CompressedPayload.ToArray();
        var fingerprint = TrackCodec.Fingerprint(stream);

        var result = await Reader().ReadAsync(TrackCodec.Decode(stream.CompressedPayload), [source]);

        Assert.True(result.VerifiedContinuity);
        Assert.Equal(2, Assert.Single(result.BreakBeforeIndices));
        Assert.Null(result.Diagnostic);
        Assert.Equal(originalBytes, stream.CompressedPayload);
        Assert.Equal(fingerprint, TrackCodec.Fingerprint(stream));
    }

    [Fact]
    public async Task Matching_sources_union_breaks_but_unrelated_sources_do_not_hide_verified_evidence()
    {
        var directory = TestSupport.NewDirectory();
        var continuous = await Source(directory, "continuous.gpx", Gpx(split: false));
        var split = await Source(directory, "split.gpx", Gpx(split: true));
        var unrelated = await Source(directory, "unrelated.gpx", Gpx(split: false).Replace("lat=\"1.0001\"", "lat=\"2.0001\"", StringComparison.Ordinal));
        var canonical = Assert.Single(await new XmlActivityImporter().ReadAsync(continuous.StoredPath, SourceKind.Gpx)).Parsed.Points;

        var evidence = await Reader().ReadAsync(canonical, [continuous, unrelated, split, split]);

        Assert.True(evidence.VerifiedContinuity);
        Assert.Equal(2, Assert.Single(evidence.BreakBeforeIndices));
    }

    [Theory]
    [InlineData("gpx")]
    [InlineData("tcx")]
    public async Task Self_closing_source_points_preserve_canonical_ordinals_but_break_continuity(string format)
    {
        var directory = TestSupport.NewDirectory();
        var content = format == "gpx"
            ? Gpx(split: false).Replace("<trkpt lat=\"1.0002\"", "<trkpt lat=\"1.00015\" lon=\"-30\"/><trkpt lat=\"1.0002\"", StringComparison.Ordinal)
            : Tcx().Replace("</Track><Track>", "<Trackpoint/>", StringComparison.Ordinal);
        var source = await Source(directory, $"empty-point.{format}", content);
        var canonical = Assert.Single(await new XmlActivityImporter().ReadAsync(source.StoredPath, SourceKind.Gpx)).Parsed.Points;

        var result = await Reader().ReadAsync(canonical, [source]);

        Assert.Equal(4, canonical.Count);
        Assert.All(canonical, point => Assert.NotNull(point.Timestamp));
        Assert.True(result.VerifiedContinuity);
        Assert.Equal(2, Assert.Single(result.BreakBeforeIndices));
    }

    [Fact]
    public async Task Missing_corrupt_hash_changed_and_mismatched_originals_produce_visible_point_only_evidence()
    {
        var directory = TestSupport.NewDirectory();
        var valid = await Source(directory, "valid.gpx", Gpx(split: false));
        var canonical = Assert.Single(await new XmlActivityImporter().ReadAsync(valid.StoredPath, SourceKind.Gpx)).Parsed.Points;
        var corrupt = await Source(directory, "corrupt.gpx", "<gpx><trk>");
        var corruptFit = await Source(directory, "corrupt.fit", "This is not a FIT recording.");
        var mismatch = await Source(directory, "mismatch.gpx", Gpx(split: false).Replace("2026-01-01T09:00:10Z", "2026-01-01T09:00:11Z", StringComparison.Ordinal));
        var missing = new SourceFile { StoredPath = Path.Combine(directory, "missing.gpx"), OriginalName = "missing.gpx" };
        var changed = await Source(directory, "changed.gpx", Gpx(split: false));
        await System.IO.File.AppendAllTextAsync(changed.StoredPath, " ");

        foreach (var source in new[] { corrupt, corruptFit, mismatch, missing, changed })
        {
            var evidence = await Reader().ReadAsync(canonical, [source]);
            Assert.False(evidence.VerifiedContinuity);
            Assert.Empty(evidence.BreakBeforeIndices);
            Assert.Contains("only recorded GPS samples", evidence.Diagnostic, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Exact_matching_uses_time_instants_and_retains_missing_position_records()
    {
        var directory = TestSupport.NewDirectory();
        var source = await Source(directory, "positions.tcx", Tcx().Replace(
            "<Position><LatitudeDegrees>1.0001</LatitudeDegrees><LongitudeDegrees>-30</LongitudeDegrees></Position>",
            "", StringComparison.Ordinal));
        var canonical = Assert.Single(await new XmlActivityImporter().ReadAsync(source.StoredPath, SourceKind.Tcx)).Parsed.Points;
        var adjustedOffsets = canonical.Select(point => point with { Timestamp = point.Timestamp?.ToOffset(TimeSpan.FromHours(3)) }).ToArray();

        Assert.True((await Reader().ReadAsync(adjustedOffsets, [source])).VerifiedContinuity);
        Assert.False((await Reader().ReadAsync(adjustedOffsets.Where(point => point.Latitude.HasValue).ToArray(), [source])).VerifiedContinuity);
    }

    [Theory]
    [InlineData(false, 12, 18)]
    [InlineData(true, 12, 18)]
    [InlineData(false, 10, 20)]
    [InlineData(true, 10, 20)]
    public async Task Fit_timer_pauses_break_edges_even_when_event_messages_follow_records(bool eventsLast,
        int stopSeconds, int startSeconds)
    {
        var directory = TestSupport.NewDirectory();
        var path = Path.Combine(directory, "paused.fit");
        var start = new System.DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        using (var output = System.IO.File.Create(path))
        {
            var encoder = new Encode(output, ProtocolVersion.V20);
            var file = new FileIdMesg();
            file.SetType(Dynastream.Fit.File.Activity);
            file.SetTimeCreated(new Dynastream.Fit.DateTime(start));
            encoder.Write(file);
            WriteRecord(encoder, start, 0);
            WriteRecord(encoder, start, 1);
            if (!eventsLast) WritePause(encoder, start, stopSeconds, startSeconds);
            WriteRecord(encoder, start, 2);
            WriteRecord(encoder, start, 3);
            if (eventsLast) WritePause(encoder, start, stopSeconds, startSeconds);
            var session = new SessionMesg();
            session.SetSport(Sport.Cycling);
            session.SetStartTime(new Dynastream.Fit.DateTime(start));
            session.SetTimestamp(new Dynastream.Fit.DateTime(start.AddSeconds(30)));
            encoder.Write(session);
            encoder.Close();
        }
        var canonical = Assert.Single(await new FitActivityImporter().ReadAsync(path, SourceKind.Fit)).Parsed.Points;
        var source = new SourceFile
        {
            OriginalName = "paused.fit",
            StoredPath = path,
            Sha256 = await Fingerprint.Sha256Async(path, default)
        };

        var result = await Reader().ReadAsync(canonical, [source]);

        Assert.True(result.VerifiedContinuity);
        Assert.Equal(2, Assert.Single(result.BreakBeforeIndices));
    }

    [Fact]
    public async Task Reading_originals_honors_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Reader().ReadAsync([], [], cancellation.Token));
    }

    [Fact]
    public async Task Later_fit_clock_reset_does_not_resume_earlier_paused_records()
    {
        var directory = TestSupport.NewDirectory();
        var path = Path.Combine(directory, "reset.fit");
        var start = new System.DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        using (var output = System.IO.File.Create(path))
        {
            var encoder = new Encode(output, ProtocolVersion.V20);
            var file = new FileIdMesg();
            file.SetType(Dynastream.Fit.File.Activity);
            encoder.Write(file);
            WriteRecord(encoder, start, 0);
            WriteRecord(encoder, start, 1);
            WriteTimerEvent(encoder, start, 10, EventType.StopAll);
            WriteRecord(encoder, start, 2);
            WriteRecord(encoder, start, 3);
            WriteTimerEvent(encoder, start, 15, EventType.Start);
            WriteRecord(encoder, start, 4, seconds: 15);
            WriteRecord(encoder, start, 5, seconds: 25);
            var session = new SessionMesg();
            session.SetSport(Sport.Cycling);
            session.SetStartTime(new Dynastream.Fit.DateTime(start));
            encoder.Write(session);
            encoder.Close();
        }
        var canonical = Assert.Single(await new FitActivityImporter().ReadAsync(path, SourceKind.Fit)).Parsed.Points;
        var source = new SourceFile
        {
            OriginalName = "reset.fit",
            StoredPath = path,
            Sha256 = await Fingerprint.Sha256Async(path, default)
        };

        var result = await Reader().ReadAsync(canonical, [source]);

        Assert.True(result.VerifiedContinuity);
        Assert.Collection(result.BreakBeforeIndices.Order(), index => Assert.Equal(2, index), index => Assert.Equal(3, index));
    }

    [Fact]
    public async Task Canonical_async_decode_preserves_samples_and_payload_bytes()
    {
        var points = TestSupport.Track(100);
        var payload = TrackCodec.Encode(points);
        var original = payload.ToArray();

        Assert.Equal(points, await TrackCodec.DecodeAsync(payload));
        Assert.Equal(original, payload);
        Assert.Empty(await TrackCodec.DecodeAsync(Array.Empty<byte>()));
    }

    [Fact]
    public async Task Canonical_async_decode_cancels_after_decompression_has_started()
    {
        var payload = TrackCodec.Encode(TestSupport.Track(100));
        using var source = new PendingCompressedStream(payload);
        using var cancellation = new CancellationTokenSource();
        var decoding = TrackCodec.DecodeAsync(source, cancellation.Token).AsTask();
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decoding);
        Assert.True(source.ReadCount >= 2);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TrackCodec.DecodeAsync(Array.Empty<byte>(), cancellation.Token).AsTask());
    }

    private static void WriteRecord(Encode encoder, System.DateTime start, int index, int? seconds = null)
    {
        var record = new RecordMesg();
        record.SetTimestamp(new Dynastream.Fit.DateTime(start.AddSeconds(seconds ?? index * 10)));
        record.SetPositionLat((int)((1 + index * 0.0001) * 2_147_483_648d / 180));
        record.SetPositionLong((int)(-30 * 2_147_483_648d / 180));
        encoder.Write(record);
    }

    private static void WritePause(Encode encoder, System.DateTime start, int stopSeconds, int startSeconds)
    {
        foreach (var (seconds, type) in new[] { (stopSeconds, EventType.StopAll), (startSeconds, EventType.Start) })
            WriteTimerEvent(encoder, start, seconds, type);
    }

    private static void WriteTimerEvent(Encode encoder, System.DateTime start, int seconds, EventType type)
    {
        var message = new EventMesg();
        message.SetEvent(Event.Timer);
        message.SetEventType(type);
        message.SetTimestamp(new Dynastream.Fit.DateTime(start.AddSeconds(seconds)));
        encoder.Write(message);
    }

    private static async Task<SourceFile> Source(string directory, string name, string content)
    {
        var path = TestSupport.Write(directory, name, content);
        return new SourceFile
        {
            OriginalName = name,
            StoredPath = path,
            Sha256 = await Fingerprint.Sha256Async(path, default)
        };
    }

    private static ExplorationSourceReader Reader() => new(new TestOriginalStore());

    private static string Gpx(bool split) => """
        <gpx><trk><type>cycling</type><trkseg>
        <trkpt lat="1" lon="-30"><time>2026-01-01T09:00:00Z</time></trkpt>
        <trkpt lat="1.0001" lon="-30"><time>2026-01-01T09:00:10Z</time></trkpt>
        BREAK
        <trkpt lat="1.0002" lon="-30"><time>2026-01-01T09:00:20Z</time></trkpt>
        <trkpt lat="1.0003" lon="-30"><time>2026-01-01T09:00:30Z</time></trkpt>
        </trkseg></trk></gpx>
        """.Replace("BREAK", split ? "</trkseg><trkseg>" : "", StringComparison.Ordinal);

    private static string Tcx() => """
        <TrainingCenterDatabase><Activities><Activity Sport="Biking"><Lap><Track>
        <Trackpoint><Time>2026-01-01T09:00:00Z</Time><Position><LatitudeDegrees>1</LatitudeDegrees><LongitudeDegrees>-30</LongitudeDegrees></Position></Trackpoint>
        <Trackpoint><Time>2026-01-01T09:00:10Z</Time><Position><LatitudeDegrees>1.0001</LatitudeDegrees><LongitudeDegrees>-30</LongitudeDegrees></Position></Trackpoint>
        </Track><Track>
        <Trackpoint><Time>2026-01-01T09:00:20Z</Time><Position><LatitudeDegrees>1.0002</LatitudeDegrees><LongitudeDegrees>-30</LongitudeDegrees></Position></Trackpoint>
        <Trackpoint><Time>2026-01-01T09:00:30Z</Time><Position><LatitudeDegrees>1.0003</LatitudeDegrees><LongitudeDegrees>-30</LongitudeDegrees></Position></Trackpoint>
        </Track></Lap></Activity></Activities></TrainingCenterDatabase>
        """;

    private sealed class TestOriginalStore : IOriginalStore
    {
        public string ResolveStoredPath(string storedPath) => storedPath;
        public string ToStoredPath(string fullPath) => throw new NotSupportedException();
        public string GetOriginalTarget(Guid ownerId, string sha256, string extension) => throw new NotSupportedException();
    }

    private sealed class PendingCompressedStream(byte[] payload) : MemoryStream(payload)
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadCount { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            if (ReadCount == 1)
            {
                var count = base.Read(buffer.Span[..Math.Min(4, buffer.Length)]);
                ReadStarted.TrySetResult();
                return count;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
