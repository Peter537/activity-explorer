using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class SegmentMatcher : ISegmentMatcher
{
    public Task<IReadOnlyList<SegmentMatch>> MatchAsync(
        IReadOnlyList<TrackPoint> activity,
        IReadOnlyList<TrackPoint> segment,
        double toleranceMeters,
        CancellationToken cancellationToken = default)
    {
        var activityGps = activity.Select((point, index) => (point, index))
            .Where(x => x.point.Latitude.HasValue && x.point.Longitude.HasValue)
            .ToArray();
        var segmentSamples = Resample(segment, 10);
        if (activityGps.Length < 2 || segmentSamples.Count < 2)
        {
            return Task.FromResult<IReadOnlyList<SegmentMatch>>([]);
        }

        var matches = new List<SegmentMatch>();
        var searchFrom = 0;
        while (searchFrom < activityGps.Length - 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = FindStart(activityGps, segmentSamples[0], searchFrom, toleranceMeters);
            if (start < 0) break;

            var current = start;
            var matched = 0;
            var sumDistance = 0d;
            foreach (var sample in segmentSamples)
            {
                var bestIndex = -1;
                var bestDistance = double.MaxValue;
                var upper = Math.Min(activityGps.Length, current + 500);
                for (var index = current; index < upper; index++)
                {
                    var distance = Distance(activityGps[index].point, sample);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestIndex = index;
                    }
                }

                if (bestIndex >= 0 && bestDistance <= toleranceMeters)
                {
                    matched++;
                    sumDistance += bestDistance;
                    current = bestIndex;
                }
            }

            var coverage = matched / (double)segmentSamples.Count;
            var endDistance = Distance(activityGps[current].point, segmentSamples[^1]);
            var directionOk = SameDirection(activityGps[start].point, activityGps[current].point, segmentSamples[0], segmentSamples[^1]);
            if (coverage >= 0.95 && endDistance <= toleranceMeters && directionOk && current > start)
            {
                matches.Add(new SegmentMatch(
                    activityGps[start].index,
                    activityGps[current].index,
                    coverage * 100,
                    matched == 0 ? 0 : sumDistance / matched));
                searchFrom = current + 1;
            }
            else
            {
                searchFrom = start + 1;
            }
        }

        return Task.FromResult<IReadOnlyList<SegmentMatch>>(matches);
    }

    private static int FindStart(
        IReadOnlyList<(TrackPoint point, int index)> activity,
        TrackPoint start,
        int from,
        double tolerance)
    {
        for (var index = from; index < activity.Count; index++)
        {
            if (Distance(activity[index].point, start) <= tolerance)
            {
                return index;
            }
        }

        return -1;
    }

    private static IReadOnlyList<TrackPoint> Resample(IReadOnlyList<TrackPoint> input, double intervalMeters)
    {
        var gps = input.Where(x => x.Latitude.HasValue && x.Longitude.HasValue).ToArray();
        if (gps.Length < 2) return gps;

        var result = new List<TrackPoint> { gps[0] };
        var carry = 0d;
        for (var index = 1; index < gps.Length; index++)
        {
            var a = gps[index - 1];
            var b = gps[index];
            var distance = Distance(a, b);
            if (distance <= 0) continue;
            var walked = intervalMeters - carry;
            while (walked < distance)
            {
                var ratio = walked / distance;
                result.Add(new TrackPoint(
                    null,
                    a.Latitude + (b.Latitude - a.Latitude) * ratio,
                    a.Longitude + (b.Longitude - a.Longitude) * ratio,
                    null,
                    a.ElevationMeters.HasValue && b.ElevationMeters.HasValue ? a.ElevationMeters + (b.ElevationMeters - a.ElevationMeters) * ratio : null,
                    null, null, null, null, null));
                walked += intervalMeters;
            }

            carry = distance - (walked - intervalMeters);
        }

        if (Distance(result[^1], gps[^1]) > 1)
        {
            result.Add(gps[^1]);
        }

        return result;
    }

    private static bool SameDirection(TrackPoint activityStart, TrackPoint activityEnd, TrackPoint segmentStart, TrackPoint segmentEnd)
    {
        var ax = activityEnd.Longitude!.Value - activityStart.Longitude!.Value;
        var ay = activityEnd.Latitude!.Value - activityStart.Latitude!.Value;
        var sx = segmentEnd.Longitude!.Value - segmentStart.Longitude!.Value;
        var sy = segmentEnd.Latitude!.Value - segmentStart.Latitude!.Value;
        return ax * sx + ay * sy > 0;
    }

    private static double Distance(TrackPoint a, TrackPoint b) =>
        GeometryCodec.HaversineMeters(a.Latitude!.Value, a.Longitude!.Value, b.Latitude!.Value, b.Longitude!.Value);
}
