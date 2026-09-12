using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Infrastructure.Processing;

internal sealed record EffortWindow(
    double Value, double CoveragePercent, double StartPosition, double FinishPosition,
    DateTimeOffset StartTime, DateTimeOffset FinishTime, double StreamStartSeconds, double StreamFinishSeconds);

internal static partial class BestEffortCalculator
{
    public static IReadOnlyList<EffortWindow> Attempts(
        IReadOnlyList<TrackPoint> input, SportKind sport, RecordKind kind, double target,
        bool multiple, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(target) || target <= 0) return [];
        cancellationToken.ThrowIfCancellationRequested();
        var streams = new List<AttemptStream>();
        if (kind == RecordKind.PowerCurve)
        {
            var samples = new List<(TrackPoint Point, int Position)>();
            for (var index = 0; index < input.Count; index++)
            {
                var point = input[index];
                if (!point.Timestamp.HasValue || !point.PowerWatts.HasValue) continue;
                if (samples.Count > 0)
                {
                    var gap = (point.Timestamp.Value - samples[^1].Point.Timestamp!.Value).TotalSeconds;
                    if (gap <= 0 || gap > 5)
                    {
                        AddPowerStream(samples, streams);
                        samples.Clear();
                    }
                }
                samples.Add((point, index));
            }
            AddPowerStream(samples, streams);
        }
        else if (kind is RecordKind.DistanceEffort or RecordKind.TimedDistanceEffort)
        {
            VisitDistanceSegments(input, sport,
                kind == RecordKind.DistanceEffort && sport != SportKind.Rowing
                    ? DistanceEligibility.RequireGps : DistanceEligibility.AllowRecordedDistanceWithoutGps,
                samples =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (samples.Count < 2) return;
                    streams.Add(new AttemptStream(samples[0].Timestamp,
                        samples.Select(point => (point.Timestamp - samples[0].Timestamp).TotalSeconds).ToArray(),
                        samples.Select(point => point.CumulativeMeters).ToArray(),
                        samples.Select(point => (double)point.Position).ToArray(), null));
                });
        }

        var queue = new PriorityQueue<(EffortWindow Window, AttemptStream Stream), (double Value, double Start, double Finish)>();
        EffortWindow? best = null;
        var seen = new HashSet<(double Start, double Finish)>();
        void Enqueue(AttemptStream stream, double anchor, bool start)
        {
            var window = stream.Window(anchor, start, target, kind);
            if (window is null) return;
            if (!multiple)
            {
                if (best is null || Priority(window).CompareTo(Priority(best)) < 0) best = window;
                return;
            }
            if (seen.Add((window.StartPosition, window.FinishPosition)))
                queue.Enqueue((window, stream), Priority(window));
        }
        (double, double, double) Priority(EffortWindow window) =>
            (kind == RecordKind.DistanceEffort ? window.Value : -window.Value, window.StartPosition, window.FinishPosition);

        foreach (var stream in streams)
        {
            foreach (var time in stream.Times)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Enqueue(stream, time, true);
                Enqueue(stream, time, false);
            }
        }
        if (!multiple) return best is null ? [] : [best];

        var accepted = new SortedSet<EffortWindow>(Comparer<EffortWindow>.Create((left, right) => left.StartPosition.CompareTo(right.StartPosition)));
        var results = new List<EffortWindow>();
        while (queue.TryDequeue(out var candidate, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = candidate.Window;
            var preceding = accepted.GetViewBetween(window with { StartPosition = double.NegativeInfinity }, window).Max;
            if (preceding is not null && preceding.FinishPosition > window.StartPosition) continue;
            var following = accepted.GetViewBetween(window, window with { StartPosition = double.PositiveInfinity }).Min;
            if (following is not null && following.StartPosition < window.FinishPosition) continue;
            accepted.Add(window);
            results.Add(window);
            // Exact cut boundaries are new anchors: neither endpoint need coincide with a source sample.
            Enqueue(candidate.Stream, window.StreamFinishSeconds, true);
            Enqueue(candidate.Stream, window.StreamStartSeconds, false);
        }
        return results;
    }

    private static void AddPowerStream(List<(TrackPoint Point, int Position)> samples, List<AttemptStream> streams)
    {
        if (samples.Count < 2) return;
        var origin = samples[0].Point.Timestamp!.Value;
        var times = samples.Select(sample => (sample.Point.Timestamp!.Value - origin).TotalSeconds).ToArray();
        var powers = samples.Select(sample => sample.Point.PowerWatts!.Value).ToArray();
        var energy = new double[samples.Count];
        for (var index = 1; index < energy.Length; index++)
            energy[index] = energy[index - 1] + powers[index - 1] * (times[index] - times[index - 1]);
        streams.Add(new AttemptStream(origin, times, energy, samples.Select(sample => (double)sample.Position).ToArray(), powers));
    }

    private sealed class AttemptStream(DateTimeOffset origin, double[] times, double[] values, double[] positions, double[]? powers)
    {
        public DateTimeOffset Origin { get; } = origin;
        public double[] Times { get; } = times;

        public EffortWindow? Window(double anchor, bool start, double target, RecordKind kind)
        {
            double begin, finish;
            if (kind == RecordKind.DistanceEffort)
            {
                var distance = Interpolate(values, anchor);
                var other = start ? distance + target : distance - target;
                if (other < 0 || other > values[^1]) return null;
                begin = start ? anchor : TimeAtDistance(other, latest: true);
                finish = start ? TimeAtDistance(other, latest: false) : anchor;
            }
            else
            {
                begin = start ? anchor : Math.Max(0, anchor - target);
                finish = start ? Math.Min(Times[^1], anchor + target) : anchor;
            }
            var duration = finish - begin;
            var minimum = kind == RecordKind.PowerCurve ? target * 0.98 : target;
            if (duration <= 0 || (kind != RecordKind.DistanceEffort && duration < minimum)) return null;
            var value = kind == RecordKind.DistanceEffort ? duration
                : (Interpolate(values, finish) - Interpolate(values, begin)) / (kind == RecordKind.PowerCurve ? duration : 1);
            if (value <= 0 || !double.IsFinite(value)) return null;
            return new EffortWindow(value, kind == RecordKind.PowerCurve ? duration / target * 100 : 100,
                Interpolate(positions, begin), Interpolate(positions, finish),
                Origin.AddSeconds(begin), Origin.AddSeconds(finish), begin, finish);
        }

        private double Interpolate(double[] data, double time)
        {
            var index = Array.BinarySearch(Times, time);
            if (index >= 0) return data[index];
            index = ~index - 1;
            if (index < 0) return data[0];
            if (index >= Times.Length - 1) return data[^1];
            if (ReferenceEquals(data, values) && powers is not null)
                return data[index] + powers[index] * (time - Times[index]);
            return data[index] + (data[index + 1] - data[index]) * (time - Times[index]) / (Times[index + 1] - Times[index]);
        }

        private double TimeAtDistance(double distance, bool latest)
        {
            var low = 0;
            var high = values.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (values[middle] < distance || (latest && values[middle] == distance)) low = middle + 1;
                else high = middle;
            }
            if (latest && low > 0 && values[low - 1] == distance) return Times[low - 1];
            if (low == 0) return Times[0];
            if (low >= values.Length) return Times[^1];
            return Times[low - 1] + (Times[low] - Times[low - 1]) * (distance - values[low - 1]) / (values[low] - values[low - 1]);
        }
    }
}
