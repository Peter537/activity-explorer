using System.Globalization;
using System.Text;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

#pragma warning disable CA1716 // Shared is the established component namespace.
namespace ActivityExplorer.Web.Components.Shared;

internal sealed record BenchmarkProgressSample(RecordHistoryPoint Point, double X, double Y, double BestY);

internal sealed record BenchmarkProgressGeometry(
    IReadOnlyList<BenchmarkProgressSample> Samples, ChartScaleGeometry Scale, string BestPath)
{
    public static BenchmarkProgressGeometry Build(RecordBenchmark benchmark, IReadOnlyList<RecordHistoryPoint> points)
    {
        if (points.Count == 0) return new([], ChartScaleBuilder.Build(0, 0), "");
        var values = points.Select(point => DisplayValue(benchmark, point.Attempt.Value)).ToArray();
        var scale = ChartScaleBuilder.Build(values.Min(), values.Max());
        var first = points[0].Attempt.ActivityDate;
        var span = (points[^1].Attempt.ActivityDate - first).TotalSeconds;
        var samples = points.Select((point, index) => new BenchmarkProgressSample(point,
            span > 0 ? (point.Attempt.ActivityDate - first).TotalSeconds / span * 800 : 400,
            scale.Project(values[index], 180), scale.Project(DisplayValue(benchmark, point.BestSoFar), 180))).ToArray();
        var path = new StringBuilder();
        foreach (var sample in samples)
        {
            path.Append(path.Length == 0
                ? string.Create(CultureInfo.InvariantCulture, $"M {sample.X:0.###},{sample.BestY:0.###}")
                : string.Create(CultureInfo.InvariantCulture, $" H {sample.X:0.###} V {sample.BestY:0.###}"));
        }
        return new(samples, scale, path.ToString());
    }

    public static double DisplayValue(RecordBenchmark benchmark, double value) => benchmark.Kind switch
    {
        RecordKind.Distance or RecordKind.TimedDistanceEffort => value / 1000,
        RecordKind.Duration or RecordKind.DistanceEffort => value / 60,
        RecordKind.AverageSpeed => benchmark.Sport == SportKind.Rowing ? 500 / value / 60 : value * 3.6,
        _ => value
    };

    public static string Unit(RecordBenchmark benchmark) => benchmark.Kind switch
    {
        RecordKind.Distance or RecordKind.TimedDistanceEffort => "km",
        RecordKind.Duration or RecordKind.DistanceEffort => "min",
        RecordKind.AverageSpeed => benchmark.Sport == SportKind.Rowing ? "min/500 m" : "km/h",
        RecordKind.PowerCurve => "W",
        RecordKind.Elevation => "m",
        _ => ""
    };
}
