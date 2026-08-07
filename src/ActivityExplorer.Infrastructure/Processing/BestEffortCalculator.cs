using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Infrastructure.Processing;

internal readonly record struct EffortResult(double Value, double CoveragePercent);

internal static class BestEffortCalculator
{
    public static EffortResult? BestDistance(
        IReadOnlyList<TrackPoint> input,
        double targetMeters,
        SportKind sport)
    {
        if (targetMeters <= 0) return null;
        var segment = new List<DistanceSample>();
        TrackPoint? previous = null;

        double? bestSeconds = null;
        foreach (var point in input)
        {
            if (!IsValidDistancePoint(point))
            {
                EvaluateDistanceSegment(segment, targetMeters, ref bestSeconds);
                segment.Clear();
                previous = null;
                continue;
            }

            if (previous is null)
            {
                segment.Add(new DistanceSample(point.Timestamp!.Value, 0));
                previous = point;
                continue;
            }

            var interval = (point.Timestamp!.Value - previous.Timestamp!.Value).TotalSeconds;
            if (interval <= 0 ||
                !TryEdgeDistance(previous, point, out var edgeDistance) ||
                edgeDistance / interval > MaximumSpeedMetersPerSecond(sport))
            {
                EvaluateDistanceSegment(segment, targetMeters, ref bestSeconds);
                segment.Clear();
                segment.Add(new DistanceSample(point.Timestamp.Value, 0));
                previous = point;
                continue;
            }

            segment.Add(new DistanceSample(point.Timestamp.Value, segment[^1].CumulativeMeters + edgeDistance));
            previous = point;
        }

        EvaluateDistanceSegment(segment, targetMeters, ref bestSeconds);
        return bestSeconds.HasValue ? new EffortResult(bestSeconds.Value, 100) : null;
    }

    public static EffortResult? BestPower(
        IReadOnlyList<TrackPoint> input,
        double targetSeconds,
        double minimumCoverage = 0.98,
        double maximumGapSeconds = 5)
    {
        if (targetSeconds <= 0 || minimumCoverage is <= 0 or > 1) return null;
        var points = input.Where(point => point.Timestamp.HasValue && point.PowerWatts.HasValue).ToArray();
        if (points.Length < 2) return null;

        EffortResult? best = null;
        var segmentStart = 0;
        for (var index = 1; index <= points.Length; index++)
        {
            var endsSegment = index == points.Length;
            if (!endsSegment)
            {
                var interval = (points[index].Timestamp!.Value - points[index - 1].Timestamp!.Value).TotalSeconds;
                endsSegment = interval <= 0 || interval > maximumGapSeconds;
            }

            if (!endsSegment) continue;
            EvaluatePowerSegment(points, segmentStart, index, targetSeconds, minimumCoverage, ref best);
            segmentStart = index;
        }

        return best;
    }

    private static void EvaluateDistanceSegment(
        IReadOnlyList<DistanceSample> points,
        double targetMeters,
        ref double? bestSeconds)
    {
        var count = points.Count;
        if (count < 2) return;

        var cumulative = new double[count];
        var times = new double[count];
        var firstTimestamp = points[0].Timestamp;
        for (var index = 1; index < count; index++)
        {
            cumulative[index] = points[index].CumulativeMeters;
            times[index] = (points[index].Timestamp - firstTimestamp).TotalSeconds;
        }
        if (cumulative[^1] < targetMeters) return;

        var finishIndex = 1;
        for (var beginIndex = 0; beginIndex < count - 1; beginIndex++)
        {
            finishIndex = Math.Max(finishIndex, beginIndex + 1);
            while (finishIndex < count && cumulative[finishIndex] - cumulative[beginIndex] < targetMeters)
                finishIndex++;
            if (finishIndex == count) break;

            var edgeDistance = cumulative[finishIndex] - cumulative[finishIndex - 1];
            if (edgeDistance <= 0) continue;
            var distanceBeforeEdge = cumulative[finishIndex - 1] - cumulative[beginIndex];
            var fraction = Math.Clamp((targetMeters - distanceBeforeEdge) / edgeDistance, 0, 1);
            var finishTime = times[finishIndex - 1] + fraction * (times[finishIndex] - times[finishIndex - 1]);
            KeepLower(ref bestSeconds, finishTime - times[beginIndex]);
        }

        var beginEdge = 0;
        for (var finish = 1; finish < count; finish++)
        {
            while (beginEdge + 1 < finish && cumulative[finish] - cumulative[beginEdge + 1] >= targetMeters)
                beginEdge++;
            if (cumulative[finish] - cumulative[beginEdge] < targetMeters) continue;

            var edgeDistance = cumulative[beginEdge + 1] - cumulative[beginEdge];
            if (edgeDistance <= 0) continue;
            var desiredStartDistance = cumulative[finish] - targetMeters;
            var fraction = Math.Clamp((desiredStartDistance - cumulative[beginEdge]) / edgeDistance, 0, 1);
            var beginTime = times[beginEdge] + fraction * (times[beginEdge + 1] - times[beginEdge]);
            KeepLower(ref bestSeconds, times[finish] - beginTime);
        }
    }


    private static bool IsValidDistancePoint(TrackPoint point)
    {
        return point.Timestamp.HasValue &&
               point.Latitude is >= -90 and <= 90 && double.IsFinite(point.Latitude.Value) &&
               point.Longitude is >= -180 and <= 180 && double.IsFinite(point.Longitude.Value);
    }

    private static bool TryEdgeDistance(TrackPoint previous, TrackPoint current, out double distanceMeters)
    {
        if (previous.DistanceMeters.HasValue && current.DistanceMeters.HasValue)
        {
            var previousDistance = previous.DistanceMeters.Value;
            var currentDistance = current.DistanceMeters.Value;
            distanceMeters = currentDistance - previousDistance;
            return double.IsFinite(previousDistance) &&
                   double.IsFinite(currentDistance) &&
                   distanceMeters >= 0;
        }

        distanceMeters = GeometryCodec.HaversineMeters(
            previous.Latitude!.Value,
            previous.Longitude!.Value,
            current.Latitude!.Value,
            current.Longitude!.Value);
        return double.IsFinite(distanceMeters) && distanceMeters >= 0;
    }

    private static double MaximumSpeedMetersPerSecond(SportKind sport)
    {
        var kilometersPerHour = sport switch
        {
            SportKind.Cycling => 200,
            SportKind.Running => 60,
            SportKind.Walking => 30,
            _ => 30
        };
        return kilometersPerHour / 3.6;
    }

    private readonly record struct DistanceSample(DateTimeOffset Timestamp, double CumulativeMeters);
    private static void EvaluatePowerSegment(
        IReadOnlyList<TrackPoint> points,
        int start,
        int endExclusive,
        double targetSeconds,
        double minimumCoverage,
        ref EffortResult? best)
    {
        var count = endExclusive - start;
        if (count < 2) return;

        var times = new double[count];
        var powers = new double[count];
        var energy = new double[count];
        var firstTimestamp = points[start].Timestamp!.Value;
        for (var index = 0; index < count; index++)
        {
            times[index] = (points[start + index].Timestamp!.Value - firstTimestamp).TotalSeconds;
            powers[index] = points[start + index].PowerWatts!.Value;
            if (index > 0)
                energy[index] = energy[index - 1] + powers[index - 1] * (times[index] - times[index - 1]);
        }
        if (times[^1] < targetSeconds * minimumCoverage) return;

        var finishIndex = 0;
        for (var beginIndex = 0; beginIndex < count - 1; beginIndex++)
        {
            finishIndex = Math.Max(finishIndex, beginIndex);
            var requestedFinish = times[beginIndex] + targetSeconds;
            while (finishIndex + 1 < count && times[finishIndex + 1] <= requestedFinish)
                finishIndex++;

            var finishTime = Math.Min(requestedFinish, times[^1]);
            var duration = finishTime - times[beginIndex];
            if (duration < targetSeconds * minimumCoverage) continue;
            var finishEnergy = energy[finishIndex];
            if (finishIndex < count - 1)
                finishEnergy += powers[finishIndex] * (finishTime - times[finishIndex]);
            KeepHigher(ref best, (finishEnergy - energy[beginIndex]) / duration, duration / targetSeconds * 100);
        }

        var beginEdge = 0;
        for (var finish = 1; finish < count; finish++)
        {
            var beginTime = Math.Max(0, times[finish] - targetSeconds);
            while (beginEdge + 1 < finish && times[beginEdge + 1] <= beginTime)
                beginEdge++;
            var duration = times[finish] - beginTime;
            if (duration < targetSeconds * minimumCoverage) continue;
            var beginEnergy = energy[beginEdge] + powers[beginEdge] * (beginTime - times[beginEdge]);
            KeepHigher(ref best, (energy[finish] - beginEnergy) / duration, duration / targetSeconds * 100);
        }
    }

    private static void KeepLower(ref double? current, double candidate)
    {
        if (candidate <= 0 || double.IsNaN(candidate) || double.IsInfinity(candidate)) return;
        if (!current.HasValue || candidate < current.Value) current = candidate;
    }

    private static void KeepHigher(ref EffortResult? current, double value, double coverage)
    {
        if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value)) return;
        if (!current.HasValue || value > current.Value.Value)
            current = new EffortResult(value, Math.Clamp(coverage, 0, 100));
    }
}
