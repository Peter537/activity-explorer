using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Infrastructure.Processing;

internal sealed record EffortWindow(
    double Value, double CoveragePercent, double StartPosition, double FinishPosition,
    DateTimeOffset StartTime, DateTimeOffset FinishTime, double StreamStartSeconds, double StreamFinishSeconds,
    SourceBoundary StartBoundary, SourceBoundary FinishBoundary);

internal readonly record struct EffortValue(double Value, double CoveragePercent, double StartPosition, double FinishPosition);

internal static partial class BestEffortCalculator
{
    public static IReadOnlyList<EffortWindow> Attempts(
        IReadOnlyList<TrackPoint> input, SportKind sport, RecordKind kind, double target,
        bool multiple, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(target) || target <= 0) return [];
        cancellationToken.ThrowIfCancellationRequested();
        return FindAttempts(PrepareStreams(input, sport, kind, cancellationToken), kind, target, multiple, cancellationToken);
    }

    public static IReadOnlyList<(RecordBenchmark Benchmark, EffortWindow Window)> BestAttempts(
        IReadOnlyList<TrackPoint> input, SportKind sport, IReadOnlyList<RecordBenchmark> benchmarks,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<(RecordBenchmark Benchmark, EffortWindow Window)>();
        foreach (var group in benchmarks.Where(benchmark => benchmark.Target.HasValue).GroupBy(benchmark => benchmark.Kind))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var streams = PrepareStreams(input, sport, group.Key, cancellationToken);
            foreach (var benchmark in group)
            {
                var target = benchmark.Target!.Value;
                if (!double.IsFinite(target) || target <= 0) continue;
                var window = FindBest(streams, group.Key, target, cancellationToken);
                if (window is not null) results.Add((benchmark, window));
            }
        }
        return results;
    }

    // Each group contains one kind; callers can prepare these groups once for an entire request.
    internal static IReadOnlyList<(RecordBenchmark Benchmark, EffortValue Value)> BestValues(
        IReadOnlyList<TrackPoint> input, SportKind sport, IReadOnlyList<RecordBenchmark[]> groups,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<(RecordBenchmark Benchmark, EffortValue Value)>();
        List<AttemptStream>? distance = null, timedDistance = null;
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = groups[groupIndex];
            if (group.Length == 0) continue;
            var kind = group[0].Kind;
            List<AttemptStream> streams;
            if (kind == RecordKind.DistanceEffort)
            {
                distance ??= timedDistance is not null && CanShareDistanceStreams(input, sport, cancellationToken)
                    ? timedDistance : PrepareStreams(input, sport, kind, cancellationToken);
                streams = distance;
            }
            else if (kind == RecordKind.TimedDistanceEffort)
            {
                timedDistance ??= distance is not null && CanShareDistanceStreams(input, sport, cancellationToken)
                    ? distance : PrepareStreams(input, sport, kind, cancellationToken);
                streams = timedDistance;
            }
            else streams = PrepareStreams(input, sport, kind, cancellationToken);
            foreach (var benchmark in group)
            {
                if (benchmark.Target is not { } target || !double.IsFinite(target) || target <= 0) continue;
                if (FindBestCandidate(streams, kind, target, cancellationToken) is not { } best) continue;
                var winner = best.Candidate;
                results.Add((benchmark, new(winner.Value, winner.CoveragePercent, winner.StartPosition, winner.FinishPosition)));
            }
        }
        return results;
    }

    private static bool CanShareDistanceStreams(IReadOnlyList<TrackPoint> input, SportKind sport, CancellationToken cancellationToken)
    {
        if (sport == SportKind.Rowing) return true;
        for (var index = 0; index < input.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var point = input[index];
            if (!point.Timestamp.HasValue) continue;
            // Distance efforts reject nonfinite recorded edges; timed efforts can fall back to GPS instead.
            if (!HasValidCoordinates(point) || point.DistanceMeters is { } distance && !double.IsFinite(distance)) return false;
        }
        return true;
    }

    private static List<AttemptStream> PrepareStreams(
        IReadOnlyList<TrackPoint> input, SportKind sport, RecordKind kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var streams = new List<AttemptStream>();
        if (kind == RecordKind.PowerCurve)
        {
            var samples = new List<(TrackPoint Point, int Position)>();
            for (var index = 0; index < input.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

        return streams;
    }

    private readonly record struct EffortCandidate(double Value, double CoveragePercent,
        double StartPosition, double FinishPosition, double BeginSeconds, double FinishSeconds);

    private static int CompareCandidates(EffortCandidate left, EffortCandidate right, RecordKind kind)
    {
        var value = (kind == RecordKind.DistanceEffort ? left.Value : -left.Value)
            .CompareTo(kind == RecordKind.DistanceEffort ? right.Value : -right.Value);
        if (value != 0) return value;
        value = left.StartPosition.CompareTo(right.StartPosition);
        return value != 0 ? value : left.FinishPosition.CompareTo(right.FinishPosition);
    }

    private static EffortWindow? FindBest(
        IReadOnlyList<AttemptStream> streams, RecordKind kind, double target, CancellationToken cancellationToken)
    {
        var best = FindBestCandidate(streams, kind, target, cancellationToken);
        return best is { } winner ? winner.Stream.Materialize(winner.Candidate) : null;
    }

    private static (AttemptStream Stream, EffortCandidate Candidate)? FindBestCandidate(
        IReadOnlyList<AttemptStream> streams, RecordKind kind, double target, CancellationToken cancellationToken)
    {
        EffortCandidate? best = null;
        AttemptStream? selected = null;
        foreach (var stream in streams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = stream.Best(target, kind, cancellationToken);
            if (candidate is not { } current) continue;
            if (best is { } previous && CompareCandidates(current, previous, kind) >= 0) continue;
            best = current;
            selected = stream;
        }
        return best is { } winner ? (selected!, winner) : null;
    }

    private static IReadOnlyList<EffortWindow> FindAttempts(
        IReadOnlyList<AttemptStream> streams, RecordKind kind, double target, bool multiple, CancellationToken cancellationToken)
    {
        if (!multiple)
        {
            var best = FindBest(streams, kind, target, cancellationToken);
            return best is null ? [] : [best];
        }
        var queue = new PriorityQueue<(EffortWindow Window, AttemptStream Stream), (double Value, double Start, double Finish)>();
        var seen = new HashSet<(double Start, double Finish)>();
        void Enqueue(AttemptStream stream, double anchor, bool start)
        {
            var window = stream.Window(anchor, start, target, kind);
            if (window is null) return;
            if (seen.Add((window.StartPosition, window.FinishPosition)))
                queue.Enqueue((window, stream), Priority(window));
        }
        (double, double, double) Priority(EffortWindow window) =>
            (kind == RecordKind.DistanceEffort ? window.Value : -window.Value, window.StartPosition, window.FinishPosition);

        foreach (var stream in streams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!stream.CanMatch(target, kind)) continue;
            foreach (var time in stream.Times)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Enqueue(stream, time, true);
                Enqueue(stream, time, false);
            }
        }
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
        private readonly bool strictlyIncreasingTimes = StrictlyIncreasing(times);
        public DateTimeOffset Origin { get; } = origin;
        public double[] Times { get; } = times;

        public bool CanMatch(double target, RecordKind kind) => kind == RecordKind.DistanceEffort
            ? target <= values[^1]
            : Times[^1] >= (kind == RecordKind.PowerCurve ? target * .98 : target);

        public EffortCandidate? Best(double target, RecordKind kind, CancellationToken cancellationToken)
        {
            if (!CanMatch(target, kind)) return null;
            // Distinct timestamps can collapse to equal relative doubles at extreme date spans.
            if (!strictlyIncreasingTimes) return BestBySearch(target, kind, cancellationToken);
            EffortCandidate? best = null;
            var finishDistance = 0;
            var beginDistance = 0;
            var finishTime = 0;
            var beginTime = 0;
            for (var index = 0; index < Times.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Consider(index, true, ref finishDistance, ref finishTime);
                Consider(index, false, ref beginDistance, ref beginTime);
            }
            return best;

            void Consider(int index, bool start, ref int distanceCursor, ref int timeCursor)
            {
                var anchor = Times[index];
                double begin, finish;
                if (kind == RecordKind.DistanceEffort)
                {
                    var other = start ? values[index] + target : values[index] - target;
                    if (other < 0 || other > values[^1]) return;
                    begin = start ? anchor : TimeAtDistanceForward(other, true, ref distanceCursor);
                    finish = start ? TimeAtDistanceForward(other, false, ref distanceCursor) : anchor;
                }
                else
                {
                    begin = start ? anchor : Math.Max(0, anchor - target);
                    finish = start ? Math.Min(Times[^1], anchor + target) : anchor;
                }
                var duration = finish - begin;
                var minimum = kind == RecordKind.PowerCurve ? target * .98 : target;
                if (duration <= 0 || (kind != RecordKind.DistanceEffort && duration < minimum)) return;
                var value = kind == RecordKind.DistanceEffort ? duration
                    : ((start ? InterpolateForward(values, finish, ref timeCursor) : values[index])
                       - (start ? values[index] : InterpolateForward(values, begin, ref timeCursor)))
                      / (kind == RecordKind.PowerCurve ? duration : 1);
                if (value <= 0 || !double.IsFinite(value)) return;
                var candidate = new EffortCandidate(value, kind == RecordKind.PowerCurve ? duration / target * 100 : 100,
                    start ? positions[index] : InterpolateForward(positions, begin, ref timeCursor),
                    start ? InterpolateForward(positions, finish, ref timeCursor) : positions[index], begin, finish);
                if (best is null || CompareCandidates(candidate, best.Value, kind) < 0) best = candidate;
            }
        }

        private static bool StrictlyIncreasing(double[] sequence)
        {
            for (var index = 1; index < sequence.Length; index++)
                if (!(sequence[index] > sequence[index - 1])) return false;
            return true;
        }

        private EffortCandidate? BestBySearch(double target, RecordKind kind, CancellationToken cancellationToken)
        {
            EffortCandidate? best = null;
            foreach (var time in Times)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Consider(time, true);
                Consider(time, false);
            }
            return best;

            void Consider(double time, bool start)
            {
                var candidate = Candidate(time, start, target, kind);
                if (candidate is { } value && (best is null || CompareCandidates(value, best.Value, kind) < 0)) best = value;
            }
        }

        private double TimeAtDistanceForward(double distance, bool latest, ref int cursor)
        {
            while (cursor > 0 && (values[cursor - 1] > distance || (!latest && values[cursor - 1] == distance))) cursor--;
            while (cursor < values.Length && (values[cursor] < distance || (latest && values[cursor] == distance))) cursor++;
            if (latest && cursor > 0 && values[cursor - 1] == distance) return Times[cursor - 1];
            if (cursor == 0) return Times[0];
            if (cursor >= values.Length) return Times[^1];
            return Times[cursor - 1] + (Times[cursor] - Times[cursor - 1])
                * (distance - values[cursor - 1]) / (values[cursor] - values[cursor - 1]);
        }

        private double InterpolateForward(double[] data, double time, ref int cursor)
        {
            // Recheck the bracket at rounded endpoints before advancing monotonically.
            while (cursor > 0 && Times[cursor - 1] >= time) cursor--;
            while (cursor < Times.Length && Times[cursor] < time) cursor++;
            if (cursor < Times.Length && Times[cursor] == time) return data[cursor];
            if (cursor == 0) return data[0];
            if (cursor >= Times.Length) return data[^1];
            var index = cursor - 1;
            if (ReferenceEquals(data, values) && powers is not null)
                return data[index] + powers[index] * (time - Times[index]);
            return data[index] + (data[index + 1] - data[index]) * (time - Times[index]) / (Times[index + 1] - Times[index]);
        }

        public EffortWindow? Window(double anchor, bool start, double target, RecordKind kind)
        {
            var candidate = Candidate(anchor, start, target, kind);
            return candidate is { } value ? Materialize(value) : null;
        }

        public EffortWindow Materialize(EffortCandidate candidate) => new(candidate.Value, candidate.CoveragePercent,
            candidate.StartPosition, candidate.FinishPosition, Origin.AddSeconds(candidate.BeginSeconds),
            Origin.AddSeconds(candidate.FinishSeconds), candidate.BeginSeconds, candidate.FinishSeconds,
            Boundary(candidate.BeginSeconds), Boundary(candidate.FinishSeconds));

        public EffortCandidate? Candidate(double anchor, bool start, double target, RecordKind kind)
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
            return new EffortCandidate(value, kind == RecordKind.PowerCurve ? duration / target * 100 : 100,
                Interpolate(positions, begin), Interpolate(positions, finish),
                begin, finish);
        }

        private SourceBoundary Boundary(double time)
        {
            var index = Array.BinarySearch(Times, time);
            if (index >= 0) return new((int)positions[index], (int)positions[index], 0, Origin.AddSeconds(time));
            index = Math.Clamp(~index - 1, 0, Times.Length - 2);
            return new((int)positions[index], (int)positions[index + 1],
                (time - Times[index]) / (Times[index + 1] - Times[index]), Origin.AddSeconds(time));
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
