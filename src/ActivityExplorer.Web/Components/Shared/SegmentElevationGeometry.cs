using System.Globalization;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Web.Components.Shared;

internal sealed record SegmentChartTick(double Position, string Label);

internal sealed record SegmentElevationGeometryResult(
    IReadOnlyList<string> AreaPaths,
    IReadOnlyList<string> LinePaths,
    IReadOnlyList<SegmentChartTick> DistanceTicks,
    IReadOnlyList<SegmentChartTick> ElevationTicks,
    double PlotLeft,
    double PlotTop,
    double PlotWidth,
    double PlotHeight,
    int RenderedSampleCount,
    bool HasData);

internal static class SegmentElevationGeometry
{
    internal const double Width = 800;
    internal const double Height = 230;
    private const double PlotLeft = 58;
    private const double PlotTop = 12;
    private const double PlotWidth = 728;
    private const double PlotHeight = 178;

    public static SegmentElevationGeometryResult Build(
        IReadOnlyList<TrackPoint> points,
        TrackPathAnalysis analysis,
        int maximumSamples = 800)
    {
        if (points.Count < 2 || !analysis.HasElevation)
            return Empty();

        var sequences = ElevationSequences(points, analysis);
        var drawable = sequences.Where(sequence => sequence.Count >= 2).ToArray();
        if (drawable.Length == 0) return Empty();

        var minimum = analysis.MinimumElevationMeters!.Value;
        var maximum = analysis.MaximumElevationMeters!.Value;
        var span = Math.Max(maximum - minimum, 1);
        var step = NiceStep(span / 4d);
        var axisMinimum = Math.Floor((minimum - Math.Max(span * 0.06, 0.5)) / step) * step;
        var axisMaximum = Math.Ceiling((maximum + Math.Max(span * 0.06, 0.5)) / step) * step;
        if (axisMaximum <= axisMinimum) axisMaximum = axisMinimum + step;

        var selectedSequences = Downsample(drawable, Math.Max(20, maximumSamples));
        var areaPaths = new List<string>();
        var linePaths = new List<string>();
        var baseline = PlotTop + PlotHeight;
        foreach (var selected in selectedSequences)
        {
            var coordinates = selected.Select(sample => (
                X: X(sample.Distance, analysis.TotalDistanceMeters),
                Y: Y(sample.Elevation, axisMinimum, axisMaximum))).ToArray();
            var line = string.Join(" ", coordinates.Select(coordinate =>
                $"{Invariant(coordinate.X)},{Invariant(coordinate.Y)}"));
            linePaths.Add($"M {line.Replace(" ", " L ", StringComparison.Ordinal)}");
            areaPaths.Add(
                $"M {Invariant(coordinates[0].X)},{Invariant(baseline)} L {line.Replace(" ", " L ", StringComparison.Ordinal)} " +
                $"L {Invariant(coordinates[^1].X)},{Invariant(baseline)} Z");
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

        return new SegmentElevationGeometryResult(
            areaPaths,
            linePaths,
            distanceTicks,
            elevationTicks,
            PlotLeft,
            PlotTop,
            PlotWidth,
            PlotHeight,
            selectedSequences.Sum(sequence => sequence.Count),
            true);
    }

    private static List<List<(int Index, double Distance, double Elevation)>> ElevationSequences(
        IReadOnlyList<TrackPoint> points,
        TrackPathAnalysis analysis)
    {
        var result = new List<List<(int, double, double)>>();
        List<(int, double, double)>? current = null;
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
            current.Add((index, analysis.DistanceAt(index), elevation.Value));
        }
        return result;
    }

    private static IReadOnlyList<IReadOnlyList<(int Index, double Distance, double Elevation)>> Downsample(
        IReadOnlyList<List<(int Index, double Distance, double Elevation)>> sequences,
        int maximumSamples)
    {
        var flattened = sequences
            .SelectMany((sequence, sequenceIndex) => sequence.Select((sample, sampleIndex) =>
                new ElevationSamplePosition(sequenceIndex, sampleIndex, sample)))
            .ToArray();
        if (flattened.Length <= maximumSamples)
            return sequences.Select(sequence => (IReadOnlyList<(int, double, double)>)sequence).ToArray();

        var selected = new HashSet<(int SequenceIndex, int SampleIndex)>();
        var mandatoryPositions = new[]
        {
            flattened[0],
            flattened[^1],
            flattened.MinBy(position => position.Sample.Elevation)!,
            flattened.MaxBy(position => position.Sample.Elevation)!
        }.DistinctBy(position => (position.SequenceIndex, position.SampleIndex)).ToArray();
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
        var candidates = flattened
            .Where(position => activeSequences.Contains(position.SequenceIndex) &&
                               !selected.Contains((position.SequenceIndex, position.SampleIndex)))
            .ToArray();
        var remaining = Math.Min(candidates.Length, Math.Max(0, maximumSamples - selected.Count));
        foreach (var position in EvenlySpaced(candidates, remaining)) Add(position);

        return sequences.Select((sequence, sequenceIndex) => (IReadOnlyList<(int, double, double)>)sequence
                .Where((_, sampleIndex) => selected.Contains((sequenceIndex, sampleIndex)))
                .ToArray())
            .Where(sequence => sequence.Count >= 2)
            .ToArray();

        void AddSequenceEndpoints(int sequenceIndex)
        {
            Add(new ElevationSamplePosition(sequenceIndex, 0, sequences[sequenceIndex][0]));
            Add(new ElevationSamplePosition(sequenceIndex, sequences[sequenceIndex].Count - 1, sequences[sequenceIndex][^1]));
        }

        void Add(ElevationSamplePosition position)
        {
            if (selected.Count < maximumSamples)
                selected.Add((position.SequenceIndex, position.SampleIndex));
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

    private static double X(double distance, double totalDistance) =>
        PlotLeft + PlotWidth * distance / Math.Max(totalDistance, 1e-9);

    private static double Y(double elevation, double minimum, double maximum) =>
        PlotTop + PlotHeight - PlotHeight * (elevation - minimum) / Math.Max(maximum - minimum, 1e-9);

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

    private static SegmentElevationGeometryResult Empty() =>
        new([], [], [], [], PlotLeft, PlotTop, PlotWidth, PlotHeight, 0, false);

    private sealed record ElevationSamplePosition(
        int SequenceIndex,
        int SampleIndex,
        (int Index, double Distance, double Elevation) Sample);
}
