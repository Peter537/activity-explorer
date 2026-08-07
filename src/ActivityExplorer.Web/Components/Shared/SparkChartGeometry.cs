using System.Globalization;

namespace ActivityExplorer.Web.Components.Shared;

internal sealed record SparkChartGeometryResult(
    string Points,
    string AreaPath,
    int FirstIndex,
    int LastIndex,
    double Minimum,
    double Maximum,
    string Trend,
    int PointCount);

internal static class SparkChartGeometry
{
    private const double Width = 800;
    private const double Height = 180;
    private const double PlotBottom = 170;
    private const double PlotHeight = 150;

    public static SparkChartGeometryResult Build(IReadOnlyList<double?> values)
    {
        var indexed = values
            .Select((value, index) => (Value: value, Index: index))
            .Where(item => item.Value.HasValue)
            .ToArray();

        if (indexed.Length > 500)
        {
            var stride = (int)Math.Ceiling(indexed.Length / 499d);
            indexed = indexed
                .Where((_, index) => index % stride == 0 || index == indexed.Length - 1)
                .ToArray();
        }

        if (indexed.Length < 2)
            return new SparkChartGeometryResult("", "", 0, 0, 0, 0, "unavailable", indexed.Length);

        var minimum = indexed.Min(item => item.Value!.Value);
        var maximum = indexed.Max(item => item.Value!.Value);
        var span = Math.Max(maximum - minimum, 1);
        var lastValueIndex = Math.Max(values.Count - 1, 1);
        var points = indexed.Select(item => (
            X: Width * item.Index / lastValueIndex,
            Y: PlotBottom - PlotHeight * (item.Value!.Value - minimum) / span)).ToArray();
        var pointString = string.Join(" ", points.Select(point =>
            $"{Invariant(point.X)},{Invariant(point.Y)}"));
        var firstX = Invariant(points[0].X);
        var lastX = Invariant(points[^1].X);
        var baseline = Invariant(Height);
        var change = indexed[^1].Value!.Value - indexed[0].Value!.Value;
        var threshold = Math.Max(Math.Abs(indexed[0].Value!.Value) * 0.01, 0.01);
        var trend = Math.Abs(change) <= threshold ? "steady" : change > 0 ? "rising" : "falling";

        return new SparkChartGeometryResult(
            pointString,
            $"M {firstX},{baseline} L {pointString.Replace(" ", " L ", StringComparison.Ordinal)} L {lastX},{baseline} Z",
            indexed[0].Index,
            indexed[^1].Index,
            minimum,
            maximum,
            trend,
            points.Length);
    }

    private static string Invariant(double value) =>
        value.ToString("0.0", CultureInfo.InvariantCulture);
}
