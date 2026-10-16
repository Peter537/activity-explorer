using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Period_records_follow_owner_local_dates_and_fresh_metadata_timezone_transfer_import_and_deletion()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var east = await setup.SeedOwnerAsync("Overview east");
        var west = await setup.SeedOwnerAsync("Overview west");
        var instant = ReportingInstant("2026-03-29T22:30:00Z");
        var eastActivity = OverviewActivity(east, SportKind.Cycling, instant, 10, 200); eastActivity.Stream = null;
        var westActivity = OverviewActivity(west, SportKind.Cycling, instant, 12, 200); westActivity.Stream = null;
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.Owners.SingleAsync(owner => owner.Id == west)).TimeZoneId = "America/Los_Angeles";
            db.Activities.AddRange(eastActivity, westActivity);
            await db.SaveChangesAsync();
        }
        var statistics = new StatisticsService(setup.Factory);
        var query = new RecordOverviewQuery(Sport: SportKind.Cycling, Period: new(ReportingPreset.Custom,
            new(2026, 3, 30), new(2026, 3, 30)), AsOfUtc: instant);
        var eastDate = await statistics.GetOverviewAsync(query);
        Assert.All(eastDate.Records, record => Assert.Equal(east, record.OwnerId));
        Assert.NotEmpty(eastDate.Records);
        var today = await statistics.GetOverviewAsync(query with { Period = new(ReportingPreset.Today) });
        Assert.Equal(new[] { east, west }.Order(), today.Records.Where(record => record.Kind == RecordKind.Distance).Select(record => record.OwnerId).Order());
        Assert.Equal(new DateOnly(2026, 3, 30), today.Periods.Single(period => period.OwnerId == east).From);
        Assert.Equal(new DateOnly(2026, 3, 29), today.Periods.Single(period => period.OwnerId == west).From);
        await using (var db = await setup.Factory.CreateDbContextAsync())
            await db.Activities.Where(activity => activity.Id == eastActivity.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(activity => activity.Title, "Edited period result").SetProperty(activity => activity.DistanceMeters, 9000));
        var edited = (await statistics.GetOverviewAsync(query)).Records.Single(record => record.Kind == RecordKind.Distance);
        Assert.Equal("Edited period result", edited.ActivityTitle);
        Assert.Equal(9000, edited.Value);
        await using (var db = await setup.Factory.CreateDbContextAsync())
            await db.Owners.Where(owner => owner.Id == east).ExecuteUpdateAsync(setters => setters.SetProperty(owner => owner.TimeZoneId, "UTC"));
        Assert.Empty((await statistics.GetOverviewAsync(query)).Records);
        await using (var db = await setup.Factory.CreateDbContextAsync())
            await db.Activities.Where(activity => activity.Id == eastActivity.Id).ExecuteUpdateAsync(setters => setters.SetProperty(activity => activity.OwnerId, west));
        var transferred = (await statistics.GetOverviewAsync(query with { Period = new(ReportingPreset.Today) })).Records
            .Single(record => record.Kind == RecordKind.Distance);
        Assert.Equal(west, transferred.OwnerId);
        Assert.Equal(eastActivity.Id, transferred.ActivityId);
        await using (var db = await setup.Factory.CreateDbContextAsync())
            await db.Activities.Where(activity => activity.Id == eastActivity.Id).ExecuteDeleteAsync();
        Assert.Equal(westActivity.Id, (await statistics.GetOverviewAsync(query with { Period = new(ReportingPreset.Today) })).Records
            .Single(record => record.Kind == RecordKind.Distance).ActivityId);
        var imported = OverviewActivity(east, SportKind.Cycling, instant.AddDays(1), 20, 200); imported.Stream = null;
        await using (var db = await setup.Factory.CreateDbContextAsync()) { db.Activities.Add(imported); await db.SaveChangesAsync(); }
        Assert.Equal(imported.Id, (await statistics.GetOverviewAsync(query)).Records.Single(record => record.Kind == RecordKind.Distance).ActivityId);
    }

    [Fact]
    public async Task Period_overview_keeps_summary_records_without_streams_and_uses_existing_GPS_rowing_and_gap_qualification()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Overview qualification");
        var start = ReportingInstant("2026-04-01T12:00:00Z");
        var summaryOnly = OverviewActivity(owner, SportKind.Cycling, start, 10, 200); summaryOnly.Stream = null;
        var indoorRun = OverviewActivity(owner, SportKind.Running, start, 3, 200, 300, gps: false); indoorRun.IsIndoor = true;
        var indoorRow = OverviewActivity(owner, SportKind.Rowing, start, 3, 200, gps: false); indoorRow.IsIndoor = true;
        var gaps = OverviewActivity(owner, SportKind.Walking, start, 3, 200, 40);
        var gapPoints = OverviewPoints(start, 3, 200, 40).Select((point, index) => point with
        { Timestamp = index < 20 ? point.Timestamp : point.Timestamp!.Value.AddMinutes(2) }).ToArray();
        gaps.Stream!.CompressedPayload = TrackCodec.Encode(gapPoints);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.AddRange(summaryOnly, indoorRun, indoorRow, gaps);
            await db.SaveChangesAsync();
        }
        var statistics = new StatisticsService(setup.Factory);
        var query = new RecordOverviewQuery(owner, Period: new(ReportingPreset.ThisMonth), AsOfUtc: start);
        var cycling = (await statistics.GetOverviewAsync(query with { Sport = SportKind.Cycling })).Records;
        Assert.Equal(new[] { RecordKind.Distance, RecordKind.Duration, RecordKind.Elevation, RecordKind.AverageSpeed }, cycling.Select(record => record.Kind));
        var running = (await statistics.GetOverviewAsync(query with { Sport = SportKind.Running, Scope = RecordScope.Indoor })).Records;
        Assert.DoesNotContain(running, record => record.Kind == RecordKind.DistanceEffort);
        Assert.Contains(running, record => record.Kind == RecordKind.TimedDistanceEffort);
        Assert.Contains(running, record => record.Kind == RecordKind.PowerCurve);
        var rowing = (await statistics.GetOverviewAsync(query with { Sport = SportKind.Rowing, Scope = RecordScope.Indoor })).Records;
        Assert.DoesNotContain(rowing, record => record.Kind == RecordKind.Elevation);
        Assert.Contains(rowing, record => record.Kind == RecordKind.DistanceEffort && record.Key == "100 m");
        Assert.Contains(rowing, record => record.Kind == RecordKind.TimedDistanceEffort && record.Key == "1 min");
        var walking = (await statistics.GetOverviewAsync(query with { Sport = SportKind.Walking })).Records;
        Assert.DoesNotContain(walking, record => record.Kind is RecordKind.DistanceEffort or RecordKind.TimedDistanceEffort);
        Assert.Contains(walking, record => record.Kind == RecordKind.PowerCurve);
        var damaged = OverviewActivity(owner, SportKind.Cycling, start, 10, 200);
        damaged.Stream!.CompressedPayload = [0, 1, 2];
        await using (var db = await setup.Factory.CreateDbContextAsync()) { db.Activities.Add(damaged); await db.SaveChangesAsync(); }
        await Assert.ThrowsAnyAsync<Exception>(() => statistics.GetOverviewAsync(query with { Sport = SportKind.Cycling }));
        Assert.Empty((await statistics.GetOverviewAsync(query with { Period = new(ReportingPreset.Custom, new(2030, 1, 1), new(2030, 1, 2)) })).Records);
        await using (var db = await setup.Factory.CreateDbContextAsync())
            await db.ActivityStreams.Where(stream => stream.ActivityId == damaged.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(stream => stream.CompressedPayload, TrackCodec.Encode(OverviewPoints(start, 10, 200, 600))));
        Assert.Contains((await statistics.GetOverviewAsync(query with { Sport = SportKind.Cycling })).Records, record => record.Kind == RecordKind.DistanceEffort);
        await Assert.ThrowsAsync<ArgumentException>(() => statistics.GetOverviewAsync(query with { Sport = (SportKind)999 }));
        await Assert.ThrowsAsync<ArgumentException>(() => statistics.GetOverviewAsync(query with { Scope = (RecordScope)999 }));
        await Assert.ThrowsAsync<ArgumentException>(() => statistics.GetOverviewAsync(query with { Period = new(ReportingPreset.Custom, new(2026, 2, 1), new(2026, 1, 1)) }));
    }

    [Fact]
    public async Task Period_stream_decoder_observes_cancellation_during_an_actual_compressed_read()
    {
        using var cancellation = new CancellationTokenSource();
        await using var stream = new CancelDuringOverviewRead(TrackCodec.Encode(TestSupport.Track(100)), cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await TrackCodec.DecodeAsync(stream, cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, stream.Reads);
    }

    private sealed class CancelDuringOverviewRead(byte[] payload, CancellationTokenSource cancellation) : MemoryStream(payload)
    {
        public int Reads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = base.Read(buffer.Span[..Math.Min(buffer.Length, 32)]);
            Reads++;
            cancellation.Cancel();
            return ValueTask.FromResult(count);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
