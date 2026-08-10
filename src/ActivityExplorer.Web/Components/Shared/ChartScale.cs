using ActivityExplorer.Core.Models;
using System.Globalization;

#pragma warning disable CA1716 // Shared is the established component namespace.
namespace ActivityExplorer.Web.Components.Shared;

internal sealed record ChartAxisTick(double Value, double Position, string Label);

internal sealed record ChartScaleGeometry(
    double Minimum,
    double Maximum,
    IReadOnlyList<ChartAxisTick> Ticks)
{
    public double Project(double value, double height)
    {
        var scale = Math.Max(Math.Abs(Minimum), Math.Abs(Maximum));
        if (!double.IsFinite(value) || !double.IsFinite(height) || scale <= 0) return height / 2;
        var scaledMinimum = Minimum / scale;
        var scaledMaximum = Maximum / scale;
        var scaledSpan = scaledMaximum - scaledMinimum;
        if (!double.IsFinite(scaledSpan) || scaledSpan <= 0) return height / 2;
        var fraction = (value / scale - scaledMinimum) / scaledSpan;
        return height - height * Math.Clamp(fraction, 0, 1);
    }
}

internal static class ChartScaleBuilder
{
    public static ChartScaleGeometry Build(
        double minimum,
        double maximum,
        bool includeZero = false,
        double height = 180,
        int targetTickCount = 5)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
            return new ChartScaleGeometry(0, 1, []);

        if (maximum < minimum) (minimum, maximum) = (maximum, minimum);
        targetTickCount = Math.Clamp(targetTickCount, 4, 5);

        var rawSpan = maximum - minimum;
        if (rawSpan == 0)
            return BuildConstantScale(minimum, includeZero, height, targetTickCount);

        if (!double.IsFinite(rawSpan) || Math.Max(Math.Abs(minimum), Math.Abs(maximum)) > 1e300)
            return BuildExtremeScale(minimum, maximum, includeZero, height, targetTickCount);

        var padding = Math.Max(rawSpan * 0.06, double.Epsilon);
        var paddedMinimum = minimum - padding;
        var paddedMaximum = maximum + padding;

        if (includeZero)
        {
            paddedMinimum = Math.Min(0, paddedMinimum);
            paddedMaximum = Math.Max(0, paddedMaximum);
            if (minimum >= 0) paddedMinimum = 0;
            if (maximum <= 0) paddedMaximum = 0;
        }

        var step = NiceStep((paddedMaximum - paddedMinimum) / (targetTickCount - 1));
        var axisMinimum = Math.Floor(paddedMinimum / step) * step;
        var axisMaximum = Math.Ceiling(paddedMaximum / step) * step;
        var intervals = IntervalCount(axisMinimum, axisMaximum, step);
        while (intervals + 1 > targetTickCount)
        {
            step = NextNiceStep(step);
            axisMinimum = Math.Floor(paddedMinimum / step) * step;
            axisMaximum = Math.Ceiling(paddedMaximum / step) * step;
            intervals = IntervalCount(axisMinimum, axisMaximum, step);
        }

        var minimumIntervals = 3;
        if (intervals < minimumIntervals)
        {
            var missing = minimumIntervals - intervals;
            var below = missing / 2;
            var above = missing - below;
            if (includeZero && minimum >= 0)
            {
                below = 0;
                above = missing;
            }
            else if (includeZero && maximum <= 0)
            {
                below = missing;
                above = 0;
            }
            axisMinimum -= below * step;
            axisMaximum += above * step;
            intervals = minimumIntervals;
        }

        var values = Enumerable.Range(0, intervals + 1)
            .Select(index => NormalizeZero(axisMinimum + index * step))
            .ToArray();
        var labels = FormatTickLabels(values, step);
        var ticks = values.Select((value, index) => new ChartAxisTick(
            value,
            height - height * index / intervals,
            labels[index])).ToArray();

        return new ChartScaleGeometry(axisMinimum, axisMaximum, ticks);
    }

    private static ChartScaleGeometry BuildConstantScale(
        double value,
        bool includeZero,
        double height,
        int targetTickCount)
    {
        if (value == 0)
        {
            var zeroValues = new[] { 0d, 0.25, 0.5, 0.75, 1d };
            var zeroLabels = FormatTickLabels(zeroValues, 0.25);
            return new ChartScaleGeometry(0, 1, zeroValues.Select((tick, index) =>
                new ChartAxisTick(tick, height - height * index / 4d, zeroLabels[index])).ToArray());
        }

        if (includeZero)
        {
            return value > 0
                ? Build(0, value, true, height, targetTickCount)
                : Build(value, 0, true, height, targetTickCount);
        }

        var padding = Math.Max(Math.Abs(value) * 0.05, 1);
        var step = NiceStep(2 * padding / 4d);
        var minimum = value - 2 * step;
        var maximum = value + 2 * step;
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
            return BuildExtremeScale(value, value, false, height, 5);

        var values = Enumerable.Range(-2, 5).Select(offset => NormalizeZero(value + offset * step)).ToArray();
        var labels = FormatTickLabels(values, step);
        return new ChartScaleGeometry(minimum, maximum, values.Select((tick, index) =>
            new ChartAxisTick(tick, height - height * index / 4d, labels[index])).ToArray());
    }

    private static ChartScaleGeometry BuildExtremeScale(
        double minimum,
        double maximum,
        bool includeZero,
        double height,
        int tickCount)
    {
        if (includeZero)
        {
            minimum = Math.Min(0, minimum);
            maximum = Math.Max(0, maximum);
        }

        if (minimum == maximum)
        {
            if (minimum > 0) minimum /= 2;
            else if (maximum < 0) maximum /= 2;
            else maximum = 1;
        }

        var values = Enumerable.Range(0, tickCount)
            .Select(index => StableLerp(minimum, maximum, index / (double)(tickCount - 1)))
            .ToArray();
        var labels = FormatGeneralLabels(values);
        var ticks = values.Select((value, index) => new ChartAxisTick(
            value,
            height - height * index / (tickCount - 1d),
            labels[index])).ToArray();
        return new ChartScaleGeometry(minimum, maximum, ticks);
    }

    private static int IntervalCount(double minimum, double maximum, double step) =>
        Math.Max(1, (int)Math.Round((maximum - minimum) / step));

    private static double NiceStep(double roughStep)
    {
        if (!double.IsFinite(roughStep) || roughStep <= 0) return 1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(roughStep)));
        if (!double.IsFinite(power) || power <= 0) return roughStep;
        var normalized = roughStep / power;
        var multiplier = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        var result = multiplier * power;
        return double.IsFinite(result) && result > 0 ? result : roughStep;
    }

    private static double NextNiceStep(double step)
    {
        var power = Math.Pow(10, Math.Floor(Math.Log10(step)));
        if (!double.IsFinite(power) || power <= 0) return step * 2;
        var normalized = step / power;
        if (normalized < 1.5) return 2 * power;
        if (normalized < 3.5) return 5 * power;
        if (normalized < 7.5) return 10 * power;
        return 20 * power;
    }

    private static IReadOnlyList<string> FormatTickLabels(IReadOnlyList<double> values, double step)
    {
        var maximumMagnitude = values.Count == 0 ? 0 : values.Max(value => Math.Abs(value));
        if (maximumMagnitude >= 1e9 || Math.Abs(step) >= 1e9)
            return FormatGeneralLabels(values);

        var decimals = Math.Clamp((int)Math.Max(0, Math.Ceiling(-Math.Log10(Math.Abs(step)))), 0, 15);
        while (decimals < 15 && Math.Abs(step * Math.Pow(10, decimals) -
               Math.Round(step * Math.Pow(10, decimals))) > 1e-9)
            decimals++;
        for (; decimals <= 15; decimals++)
        {
            var labels = values.Select(value => value.ToString(
                $"N{decimals}", CultureInfo.CurrentCulture)).ToArray();
            if (labels.Distinct(StringComparer.CurrentCulture).Count() == labels.Length) return labels;
        }
        return values.Select(value => value.ToString("G17", CultureInfo.CurrentCulture)).ToArray();
    }

    private static IReadOnlyList<string> FormatGeneralLabels(IReadOnlyList<double> values)
    {
        var labels = values.Select(value => value.ToString("G6", CultureInfo.CurrentCulture)).ToArray();
        return labels.Distinct(StringComparer.CurrentCulture).Count() == labels.Length
            ? labels
            : values.Select(value => value.ToString("G17", CultureInfo.CurrentCulture)).ToArray();
    }

    private static double StableLerp(double minimum, double maximum, double fraction) =>
        minimum * (1 - fraction) + maximum * fraction;

    private static double NormalizeZero(double value) => value == 0 ? 0 : value;
}

internal static class ChartAxisLabels
{
    public static IReadOnlyList<ChartAxisTick> BuildSeriesTicks(
        double maximum,
        ChartAxisKind axis,
        double width = 800,
        int tickCount = 3)
    {
        tickCount = Math.Max(2, tickCount);
        maximum = double.IsFinite(maximum) ? Math.Max(0, maximum) : 0;
        if (maximum == 0) maximum = 1;
        var step = maximum / (tickCount - 1);
        return Enumerable.Range(0, tickCount)
            .Select(index =>
            {
                var fraction = index / (double)(tickCount - 1);
                var value = maximum * fraction;
                return new ChartAxisTick(
                    value,
                    width * fraction,
                    FormatPosition(value, axis, maximum, step));
            })
            .ToArray();
    }

    public static IReadOnlyList<ChartAxisTick> BuildCategoryTicks(
        IReadOnlyList<string> labels,
        double width = 800,
        int tickCount = 5)
    {
        if (labels.Count == 0) return [];
        tickCount = Math.Clamp(tickCount, 1, labels.Count);
        if (tickCount == 1) return [new ChartAxisTick(0, 0, labels[0])];

        var lastIndex = labels.Count - 1;
        return Enumerable.Range(0, tickCount)
            .Select(slot => (Slot: slot, Index: (int)Math.Round(
                slot * lastIndex / (double)(tickCount - 1),
                MidpointRounding.AwayFromZero)))
            .DistinctBy(item => item.Index)
            .Select(item => new ChartAxisTick(
                item.Index,
                width * item.Slot / (tickCount - 1d),
                labels[item.Index]))
            .ToArray();
    }

    public static string FormatPosition(double value, ChartAxisKind axis)
    {
        if (axis == ChartAxisKind.Distance)
            return value >= 1000 ? $"{value / 1000:N2} km" : $"{value:N0} m";

        var duration = TimeSpan.FromSeconds(Math.Max(0, value));
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    }

    private static string FormatPosition(
        double value,
        ChartAxisKind axis,
        double displayMaximum,
        double step)
    {
        if (displayMaximum > 2) return FormatPosition(value, axis);

        var decimals = FractionDigits(step);
        if (axis == ChartAxisKind.Distance)
            return $"{value.ToString($"N{decimals}", CultureInfo.CurrentCulture)} m";

        var secondsPattern = decimals == 0 ? "00" : $"00.{new string('0', decimals)}";
        return $"0:{value.ToString(secondsPattern, CultureInfo.CurrentCulture)}";
    }

    private static int FractionDigits(double step)
    {
        var tolerance = Math.Max(Math.Abs(step) * 1e-10, 1e-15);
        for (var decimals = 0; decimals <= 12; decimals++)
        {
            if (Math.Abs(step - Math.Round(step, decimals)) <= tolerance) return decimals;
        }
        return 12;
    }
}
