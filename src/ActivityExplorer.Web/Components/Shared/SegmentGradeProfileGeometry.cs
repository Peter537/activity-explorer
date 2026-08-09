using System.Globalization;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Web.Components.Shared;

internal enum SegmentGradeBucket
{
    Downhill,
    Flat,
    Gentle,
    Moderate,
    Steep,
    VerySteep
}

internal sealed record SegmentGradeProfileSample(
    int SourceIndex,
    double DistanceMeters,
    double ElevationMeters,
    double? GradePercent,
    double X,
    double Y);

internal sealed record SegmentGradeProfileSpan(
    int StartSourceIndex,
    int EndSourceIndex,
    double GradePercent,
    SegmentGradeBucket Bucket,
    string AreaPath,
    string LinePath);

internal sealed record SegmentGradeProfileGeometryResult(
    IReadOnlyList<string> AreaPaths,
    IReadOnlyList<SegmentGradeProfileSpan> GradeSpans,
    IReadOnlyList<SegmentGradeProfileSample> Samples,
    IReadOnlyList<SegmentChartTick> DistanceTicks,
    IReadOnlyList<SegmentChartTick> ElevationTicks,
    double PlotLeft,
    double PlotTop,
    double PlotWidth,
    double PlotHeight,
    double? MinimumElevationMeters,
    double? MaximumElevationMeters,
    double? MinimumGradePercent,
    double? MaximumGradePercent,
    double ElevationCoveragePercent,
    double GradeCoveragePercent,
    bool HasData);

internal static class SegmentGradeProfileGeometry
{
    internal const double GradeWindowMeters = 50;
    internal const double Width = 800;
    internal const double Height = 230;
    private const double PlotLeft = 58;
    private const double PlotTop = 12;
    private const double PlotWidth = 728;
    private const double PlotHeight = 178;
    private const double DistanceEpsilon = 1e-9;

    public static SegmentGradeProfileGeometryResult Build(
        IReadOnlyList<TrackPoint> points,
        int maximumSamples = 800)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2) return Empty();

        var analysis = new TrackPathAnalysis(points);
        var sequences = ElevationSequences(points, analysis);
        var drawable = sequences.Where(sequence => sequence.Count >= 2).ToArray();
        if (drawable.Length == 0) return Empty(
            elevationCoveragePercent: sequences.Sum(sequence => sequence.Count) * 100d / points.Count);

        foreach (var sequence in sequences)
            PopulateLocalGrades(sequence);

        var finiteElevations = sequences.SelectMany(sequence => sequence).Select(sample => sample.ElevationMeters).ToArray();
        var finiteGrades = sequences.SelectMany(sequence => sequence)
            .Where(sample => sample.GradePercent.HasValue)
            .Select(sample => sample.GradePercent!.Value)
            .ToArray();
        var minimumElevation = finiteElevations.Min();
        var maximumElevation = finiteElevations.Max();
        var (axisMinimum, axisMaximum, step) = ElevationAxis(minimumElevation, maximumElevation);
        var selectedSequences = Downsample(drawable, Math.Max(20, maximumSamples));
        var baseline = PlotTop + PlotHeight;
        var areaPaths = new List<string>();
        var gradeSpans = new List<SegmentGradeProfileSpan>();
        var samples = new List<SegmentGradeProfileSample>();

        foreach (var sequence in selectedSequences.Where(sequence => sequence.Count >= 2))
        {
            var rendered = sequence.Select(sample => new SegmentGradeProfileSample(
                sample.SourceIndex,
                sample.DistanceMeters,
                sample.ElevationMeters,
                sample.GradePercent,
                X(sample.DistanceMeters, analysis.TotalDistanceMeters),
                Y(sample.ElevationMeters, axisMinimum, axisMaximum))).ToArray();
            samples.AddRange(rendered);
            var line = string.Join(" ", rendered.Select(sample => $"{Invariant(sample.X)},{Invariant(sample.Y)}"));
            areaPaths.Add(
                $"M {Invariant(rendered[0].X)},{Invariant(baseline)} L {line.Replace(" ", " L ", StringComparison.Ordinal)} " +
                $"L {Invariant(rendered[^1].X)},{Invariant(baseline)} Z");

            for (var index = 1; index < rendered.Length; index++)
            {
                var before = rendered[index - 1];
                var after = rendered[index];
                var grade = SpanGrade(before.GradePercent, after.GradePercent);
                if (!grade.HasValue) continue;
                gradeSpans.Add(new SegmentGradeProfileSpan(
                    before.SourceIndex,
                    after.SourceIndex,
                    grade.Value,
                    BucketFor(grade.Value),
                    $"M {Invariant(before.X)},{Invariant(baseline)} L {Invariant(before.X)},{Invariant(before.Y)} " +
                    $"L {Invariant(after.X)},{Invariant(after.Y)} L {Invariant(after.X)},{Invariant(baseline)} Z",
                    $"M {Invariant(before.X)},{Invariant(before.Y)} L {Invariant(after.X)},{Invariant(after.Y)}"));
            }
        }

        var distanceTicks = Enumerable.Range(0, 5)
            .Select(index =>
            {
                var distance = analysis.TotalDistanceMeters * index / 4d;
                return new SegmentChartTick(X(distance, analysis.TotalDistanceMeters), FormatDistance(distance));
            })
            .ToArray();
        var elevationTicks = new List<SegmentChartTick>();
        for (var value = axisMinimum; value <= axisMaximum + step / 2 && elevationTicks.Count < 8; value += step)
            elevationTicks.Add(new SegmentChartTick(Y(value, axisMinimum, axisMaximum), $"{value:N0} m"));

        return new SegmentGradeProfileGeometryResult(
            areaPaths,
            gradeSpans,
            samples,
            distanceTicks,
            elevationTicks,
            PlotLeft,
            PlotTop,
            PlotWidth,
            PlotHeight,
            minimumElevation,
            maximumElevation,
            finiteGrades.Length == 0 ? null : finiteGrades.Min(),
            finiteGrades.Length == 0 ? null : finiteGrades.Max(),
            finiteElevations.Length * 100d / points.Count,
            finiteGrades.Length * 100d / points.Count,
            areaPaths.Count > 0 && samples.Count >= 2);
    }

    public static SegmentGradeBucket BucketFor(double gradePercent) => gradePercent switch
    {
        < -1 => SegmentGradeBucket.Downhill,
        < 2 => SegmentGradeBucket.Flat,
        < 5 => SegmentGradeBucket.Gentle,
        < 8 => SegmentGradeBucket.Moderate,
        < 12 => SegmentGradeBucket.Steep,
        _ => SegmentGradeBucket.VerySteep
    };

    private static List<List<GradeSourceSample>> ElevationSequences(
        IReadOnlyList<TrackPoint> points,
        TrackPathAnalysis analysis)
    {
        var result = new List<List<GradeSourceSample>>();
        List<GradeSourceSample>? current = null;
        for (var index = 0; index < points.Count; index++)
        {
            var elevation = points[index].ElevationMeters;
            if (!elevation.HasValue || !double.IsFinite(elevation.Value))
            {
                current = null;
                continue;
            }

            current ??= [];
            if (current.Count == 0) result.Add(current);
            current.Add(new GradeSourceSample(index, analysis.DistanceAt(index), elevation.Value));
        }
        return result;
    }

    private static void PopulateLocalGrades(IReadOnlyList<GradeSourceSample> sequence)
    {
        if (sequence.Count < 2) return;
        var runStart = sequence[0].DistanceMeters;
        var runEnd = sequence[^1].DistanceMeters;
        if (runEnd - runStart <= DistanceEpsilon) return;

        foreach (var sample in sequence)
        {
            var windowStart = sample.DistanceMeters - GradeWindowMeters / 2d;
            var windowEnd = sample.DistanceMeters + GradeWindowMeters / 2d;
            if (windowStart < runStart)
            {
                windowEnd += runStart - windowStart;
                windowStart = runStart;
            }
            if (windowEnd > runEnd)
            {
                windowStart -= windowEnd - runEnd;
                windowEnd = runEnd;
            }
            windowStart = Math.Max(runStart, windowStart);
            windowEnd = Math.Min(runEnd, windowEnd);
            var distance = windowEnd - windowStart;
            if (distance <= DistanceEpsilon) continue;
            var startElevation = InterpolateElevation(sequence, windowStart);
            var endElevation = InterpolateElevation(sequence, windowEnd);
            sample.GradePercent = (endElevation - startElevation) / distance * 100d;
        }
    }

    private static double InterpolateElevation(IReadOnlyList<GradeSourceSample> sequence, double distance)
    {
        if (distance <= sequence[0].DistanceMeters) return sequence[0].ElevationMeters;
        if (distance >= sequence[^1].DistanceMeters) return sequence[^1].ElevationMeters;

        var low = 0;
        var high = sequence.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (sequence[middle].DistanceMeters <= distance) low = middle + 1;
            else high = middle;
        }

        var after = Math.Min(low, sequence.Count - 1);
        var before = Math.Max(0, after - 1);
        var span = sequence[after].DistanceMeters - sequence[before].DistanceMeters;
        if (span <= DistanceEpsilon) return sequence[before].ElevationMeters;
        var fraction = (distance - sequence[before].DistanceMeters) / span;
        return sequence[before].ElevationMeters +
               (sequence[after].ElevationMeters - sequence[before].ElevationMeters) * fraction;
    }

    private static IReadOnlyList<IReadOnlyList<GradeSourceSample>> Downsample(
        IReadOnlyList<List<GradeSourceSample>> sequences,
        int maximumSamples)
    {
        var flattened = sequences
            .SelectMany((sequence, sequenceIndex) => sequence.Select((sample, sampleIndex) =>
                new SamplePosition(sequenceIndex, sampleIndex, sample)))
            .ToArray();
        if (flattened.Length <= maximumSamples)
            return sequences.Select(sequence => (IReadOnlyList<GradeSourceSample>)sequence).ToArray();

        var selected = new HashSet<(int SequenceIndex, int SampleIndex)>();
        var mandatoryPositions = new[]
        {
            flattened[0],
            flattened[^1],
            flattened.MinBy(position => position.Sample.ElevationMeters),
            flattened.MaxBy(position => position.Sample.ElevationMeters),
            flattened.Where(position => position.Sample.GradePercent.HasValue)
                .MinBy(position => position.Sample.GradePercent),
            flattened.Where(position => position.Sample.GradePercent.HasValue)
                .MaxBy(position => position.Sample.GradePercent)
        }.Where(position => position is not null).Select(position => position!).ToArray();

        var mandatorySequenceIndices = mandatoryPositions
            .Select(position => position.SequenceIndex)
            .Distinct()
            .Order()
            .ToArray();
        foreach (var sequenceIndex in mandatorySequenceIndices) AddSequenceEndpoints(sequenceIndex);
        foreach (var position in mandatoryPositions) Add(position);

        var optionalSequences = Enumerable.Range(0, sequences.Count)
            .Where(sequenceIndex => !mandatorySequenceIndices.Contains(sequenceIndex))
            .ToArray();
        var optionalSequenceCount = Math.Min(optionalSequences.Length, Math.Max(0, maximumSamples - selected.Count) / 2);
        foreach (var sequenceIndex in EvenlySpaced(optionalSequences, optionalSequenceCount))
            AddSequenceEndpoints(sequenceIndex);

        var activeSequences = selected.Select(position => position.SequenceIndex).Distinct().ToHashSet();
        var transitions = new List<SamplePosition>();
        foreach (var sequenceIndex in activeSequences.Order())
        {
            var sequence = sequences[sequenceIndex];
            for (var sampleIndex = 1; sampleIndex < sequence.Count; sampleIndex++)
            {
                if (NullableBucket(sequence[sampleIndex - 1].GradePercent) ==
                    NullableBucket(sequence[sampleIndex].GradePercent)) continue;
                transitions.Add(new SamplePosition(sequenceIndex, sampleIndex - 1, sequence[sampleIndex - 1]));
                transitions.Add(new SamplePosition(sequenceIndex, sampleIndex, sequence[sampleIndex]));
            }
        }
        AddEvenly(transitions);

        AddEvenly(flattened.Where(position => activeSequences.Contains(position.SequenceIndex)).ToArray());

        return sequences.Select((sequence, sequenceIndex) => (IReadOnlyList<GradeSourceSample>)sequence
            .Where((_, sampleIndex) => selected.Contains((sequenceIndex, sampleIndex)))
            .ToArray()).Where(sequence => sequence.Count >= 2).ToArray();

        void AddSequenceEndpoints(int sequenceIndex)
        {
            Add(new SamplePosition(sequenceIndex, 0, sequences[sequenceIndex][0]));
            Add(new SamplePosition(sequenceIndex, sequences[sequenceIndex].Count - 1, sequences[sequenceIndex][^1]));
        }

        void Add(SamplePosition position)
        {
            if (selected.Count < maximumSamples)
                selected.Add((position.SequenceIndex, position.SampleIndex));
        }

        void AddEvenly(IReadOnlyList<SamplePosition> candidates)
        {
            var available = candidates
                .Where(position => !selected.Contains((position.SequenceIndex, position.SampleIndex)))
                .ToArray();
            var count = Math.Min(available.Length, Math.Max(0, maximumSamples - selected.Count));
            foreach (var position in EvenlySpaced(available, count)) Add(position);
        }
    }

    private static IReadOnlyList<T> EvenlySpaced<T>(IReadOnlyList<T> candidates, int count)
    {
        if (count <= 0 || candidates.Count == 0) return [];
        if (count >= candidates.Count) return candidates;
        return Enumerable.Range(0, count)
            .Select(slot => candidates[Math.Min(
                candidates.Count - 1,
                (int)Math.Floor((slot + 0.5) * candidates.Count / count))])
            .ToArray();
    }

    private static int IndexOfMinimum(
        IReadOnlyList<GradeSourceSample> sequence,
        Func<GradeSourceSample, double> selector) =>
        sequence.Select((sample, index) => (Value: selector(sample), Index: index)).MinBy(item => item.Value).Index;

    private static int IndexOfMaximum(
        IReadOnlyList<GradeSourceSample> sequence,
        Func<GradeSourceSample, double> selector) =>
        sequence.Select((sample, index) => (Value: selector(sample), Index: index)).MaxBy(item => item.Value).Index;

    private static SegmentGradeBucket? NullableBucket(double? gradePercent) =>
        gradePercent.HasValue ? BucketFor(gradePercent.Value) : null;

    private static double? SpanGrade(double? before, double? after)
    {
        if (before.HasValue && after.HasValue) return (before.Value + after.Value) / 2d;
        return before ?? after;
    }

    private static (double Minimum, double Maximum, double Step) ElevationAxis(double minimum, double maximum)
    {
        var span = Math.Max(maximum - minimum, 1);
        var step = NiceStep(span / 4d);
        var axisMinimum = Math.Floor((minimum - Math.Max(span * 0.06, 0.5)) / step) * step;
        var axisMaximum = Math.Ceiling((maximum + Math.Max(span * 0.06, 0.5)) / step) * step;
        if (axisMaximum <= axisMinimum) axisMaximum = axisMinimum + step;
        return (axisMinimum, axisMaximum, step);
    }

    private static double X(double distance, double totalDistance) =>
        PlotLeft + PlotWidth * distance / Math.Max(totalDistance, DistanceEpsilon);

    private static double Y(double elevation, double minimum, double maximum) =>
        PlotTop + PlotHeight - PlotHeight * (elevation - minimum) / Math.Max(maximum - minimum, DistanceEpsilon);

    private static double NiceStep(double roughStep)
    {
        if (!double.IsFinite(roughStep) || roughStep <= 0) return 1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(roughStep)));
        var normalized = roughStep / power;
        var multiplier = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return multiplier * power;
    }

    private static string FormatDistance(double meters) => meters >= 1000
        ? $"{meters / 1000:N1} km"
        : $"{meters:N0} m";

    private static string Invariant(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static SegmentGradeProfileGeometryResult Empty(double elevationCoveragePercent = 0) => new(
        [], [], [], [], [], PlotLeft, PlotTop, PlotWidth, PlotHeight,
        null, null, null, null, elevationCoveragePercent, 0, false);

    private sealed record SamplePosition(int SequenceIndex, int SampleIndex, GradeSourceSample Sample);

    private sealed class GradeSourceSample(int sourceIndex, double distanceMeters, double elevationMeters)
    {
        public int SourceIndex { get; } = sourceIndex;
        public double DistanceMeters { get; } = distanceMeters;
        public double ElevationMeters { get; } = elevationMeters;
        public double? GradePercent { get; set; }
    }
}
