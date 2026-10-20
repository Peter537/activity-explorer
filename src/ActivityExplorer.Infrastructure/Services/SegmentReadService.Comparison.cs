using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed partial class SegmentReadService
{
    public async Task<SegmentReadComparison?> CompareAsync(Guid ownerId, Guid segmentId, Guid baselineId, Guid comparisonId, CancellationToken cancellationToken = default)
    {
        if (baselineId == comparisonId) throw new ArgumentException("Choose two different efforts.");
        SegmentSummary summary;
        byte[] geometry;
        SegmentEffortSummary[] efforts;
        Dictionary<Guid, byte[]> payloads;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var snapshot = await ReadSnapshot.BeginAsync(db, cancellationToken);
            var source = db.Segments.AsNoTracking().Where(segment => segment.OwnerId == ownerId && segment.Id == segmentId);
            var loaded = await Summaries(source, db).SingleOrDefaultAsync(cancellationToken);
            if (loaded is null) return null;
            summary = loaded;
            geometry = await source.Select(segment => segment.GeometryWkb).SingleAsync(cancellationToken);
            efforts = await Efforts(db, ownerId, segmentId).Where(effort => effort.Id == baselineId || effort.Id == comparisonId)
                .Select(EffortProjection).ToArrayAsync(cancellationToken);
            if (efforts.Length != 2) return null;
            var ids = efforts.Select(effort => effort.ActivityId).Distinct().ToArray();
            payloads = await db.ActivityStreams.AsNoTracking().Where(stream => stream.OwnerId == ownerId && ids.Contains(stream.ActivityId))
                .ToDictionaryAsync(stream => stream.ActivityId, stream => stream.CompressedPayload, cancellationToken);
        }
        var baseline = efforts.Single(effort => effort.Id == baselineId);
        var comparison = efforts.Single(effort => effort.Id == comparisonId);
        var sources = new Dictionary<Guid, IReadOnlyList<TrackPoint>>();
        foreach (var pair in payloads) sources.Add(pair.Key, await TrackCodec.DecodeAsync(pair.Value, cancellationToken));
        return await Task.Run(() =>
        {
            var definition = GeometryCodec.FromWkb(geometry);
            var first = Align(baseline);
            var second = Align(comparison);
            var unavailable = !first.IsAvailable ? first : !second.IsAvailable ? second : null;
            var samples = unavailable is null ? SegmentComparisonCalculator.BuildSamples(first, second, cancellationToken) : [];
            if (samples.Count > 0 && Math.Abs(samples[^1].DeltaSeconds - (comparison.ElapsedSeconds - baseline.ElapsedSeconds)) > SegmentComparisonCalculator.DurationToleranceSeconds)
                return new SegmentReadComparison(summary, baseline, comparison, [], samples.Count, SegmentComparisonUnavailableReason.DurationMismatch, "Recorded timing differs from the saved efforts.");
            var indices = Enumerable.Range(0, Math.Min(100, samples.Count))
                .Select(index => samples.Count <= 100 ? index : (int)((long)index * (samples.Count - 1) / 99));
            var bounded = indices.Select(index => new SegmentTimingSample(samples[index].DistanceMeters,
                samples[index].Baseline.ElapsedSeconds, samples[index].Comparison.ElapsedSeconds, samples[index].DeltaSeconds)).ToArray();
            return new SegmentReadComparison(summary, baseline, comparison, bounded, samples.Count,
                unavailable?.UnavailableReason ?? SegmentComparisonUnavailableReason.None, unavailable?.Message);

            SegmentEffortAlignment Align(SegmentEffortSummary effort) => sources.TryGetValue(effort.ActivityId, out var points)
                ? SegmentComparisonCalculator.Align(definition, points, effort, summary.ToleranceMeters, cancellationToken)
                : new(effort.Id, [], new([]), SegmentComparisonUnavailableReason.MissingStream, "The detailed recording is unavailable.");
        }, cancellationToken);
    }
}
