using System.Globalization;

#pragma warning disable CA1716 // Shared is the established component namespace.
namespace ActivityExplorer.Web.Components.Shared;

internal sealed record SparkChartSample(
    int SourceIndex,
    double Value,
    double X,
    double Y);

internal sealed record SparkChartGeometryResult(
    string Points,
    string AreaPath,
    int FirstIndex,
    int LastIndex,
    double Minimum,
    double Maximum,
    string Trend,
    int PointCount,
    IReadOnlyList<SparkChartSample> Samples,
    ChartScaleGeometry Scale);

internal static class SparkChartGeometry
{
    private const double Width = 800;
    private const double Height = 180;

    public static SparkChartGeometryResult Build(
        IReadOnlyList<double?> values,
        bool includeZero = false)
    {
        var indexed = values
            .Select((value, index) => (Value: value, Index: index))
            .Where(item => item.Value.HasValue && double.IsFinite(item.Value.Value))
            .Select(item => (Value: item.Value!.Value, item.Index))
            .ToArray();

        if (indexed.Length == 0)
        {
            return new SparkChartGeometryResult(
                "", "", 0, 0, 0, 0, "unavailable", 0, [],
                ChartScaleBuilder.Build(0, 0, includeZero, Height));
        }

        if (indexed.Length > 500)
        {
            var stride = (int)Math.Ceiling(indexed.Length / 499d);
            indexed = indexed
                .Where((_, index) => index % stride == 0 || index == indexed.Length - 1)
                .ToArray();
        }

        var minimum = indexed.Min(item => item.Value);
        var maximum = indexed.Max(item => item.Value);
        var scale = ChartScaleBuilder.Build(minimum, maximum, includeZero, Height);
        var lastValueIndex = Math.Max(values.Count - 1, 1);
        var samples = indexed.Select(item => new SparkChartSample(
            item.Index,
            item.Value,
            Width * item.Index / lastValueIndex,
            scale.Project(item.Value, Height))).ToArray();
        var pointString = string.Join(" ", samples.Select(sample =>
            $"{Invariant(sample.X)},{Invariant(sample.Y)}"));

        var areaPath = "";
        if (samples.Length > 1)
        {
            var firstX = Invariant(samples[0].X);
            var lastX = Invariant(samples[^1].X);
            var baseline = Invariant(Height);
            areaPath = $"M {firstX},{baseline} L {pointString.Replace(" ", " L ", StringComparison.Ordinal)} L {lastX},{baseline} Z";
        }

        var trend = "unavailable";
        if (samples.Length > 1)
        {
            var change = samples[^1].Value - samples[0].Value;
            var threshold = Math.Max(Math.Abs(samples[0].Value) * 0.01, 0.01);
            trend = Math.Abs(change) <= threshold ? "steady" : change > 0 ? "rising" : "falling";
        }

        return new SparkChartGeometryResult(
            pointString,
            areaPath,
            samples[0].SourceIndex,
            samples[^1].SourceIndex,
            minimum,
            maximum,
            trend,
            samples.Length,
            samples,
            scale);
    }

    private static string Invariant(double value) =>
        value.ToString("0.0", CultureInfo.InvariantCulture);
}
