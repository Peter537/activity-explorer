using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Infrastructure.Processing;

public static class ExplorationGrid
{
    public const int Zoom = 14;
    public const int Size = 1 << Zoom;
    public const double LatitudeLimit = 85.0511287798066;
    public const double MaximumIntervalSeconds = 30;
    public const double MaximumSpeedMetersPerSecond = 200d / 3.6;
    private const double GridTolerance = 1e-9;
    private const int MaximumCrossings = 64;

    public static IReadOnlyList<int> Extract(
        IReadOnlyList<TrackPoint> points,
        IReadOnlySet<int> recoveredBreakIndices,
        bool verifiedContinuity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(recoveredBreakIndices);
        var cells = new HashSet<int>();
        for (var index = 0; index < points.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var point = points[index];
            if (!HasCoordinates(point)) continue;
            var current = Project(point);
            AddCell(cells, current.X, current.Y);
            if (!verifiedContinuity || index == 0 || recoveredBreakIndices.Contains(index)) continue;
            var previous = points[index - 1];
            if (!CanTraverse(previous, point)) continue;
            Traverse(cells, Project(previous), current, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return cells.Order().ToArray();
    }

    public static (double West, double South, double East, double North) Bounds(int zoom, int x, int y)
    {
        if (zoom is < 0 or > Zoom) throw new ArgumentOutOfRangeException(nameof(zoom));
        var size = 1 << zoom;
        if (x < 0 || x >= size) throw new ArgumentOutOfRangeException(nameof(x));
        if (y < 0 || y >= size) throw new ArgumentOutOfRangeException(nameof(y));
        return (x * 360d / size - 180d, Latitude(y + 1, size),
            (x + 1) * 360d / size - 180d, Latitude(y, size));
    }

    private static bool HasCoordinates(TrackPoint point) =>
        point.Latitude is >= -LatitudeLimit and <= LatitudeLimit &&
        point.Longitude is >= -180 and <= 180;

    private static bool CanTraverse(TrackPoint before, TrackPoint after)
    {
        if (!HasCoordinates(before) || before.Timestamp is not { } start || after.Timestamp is not { } end)
            return false;
        var seconds = (end - start).TotalSeconds;
        if (seconds is <= 0 or > MaximumIntervalSeconds) return false;
        if (before.DistanceMeters is { } from && after.DistanceMeters is { } to &&
            double.IsFinite(from) && double.IsFinite(to) && to < from)
            return false;
        var meters = TrackPathAnalysis.HaversineMeters(before.Latitude!.Value, before.Longitude!.Value,
            after.Latitude!.Value, after.Longitude!.Value);
        return double.IsFinite(meters) && meters / seconds <= MaximumSpeedMetersPerSecond;
    }

    private static (double X, double Y) Project(TrackPoint point)
    {
        var longitude = point.Longitude == 180 ? -180 : point.Longitude!.Value;
        var radians = point.Latitude!.Value * Math.PI / 180d;
        return ((longitude + 180d) / 360d * Size,
            Math.Clamp((1d - Math.Asinh(Math.Tan(radians)) / Math.PI) / 2d * Size, 0, Size));
    }

    private static void Traverse(HashSet<int> cells, (double X, double Y) start, (double X, double Y) end,
        CancellationToken cancellationToken)
    {
        var dx = end.X - start.X;
        if (dx > Size / 2d) dx -= Size;
        if (dx < -Size / 2d) dx += Size;
        var dy = end.Y - start.Y;
        var crossings = new List<double> { 0, 1 };
        AddCrossings(crossings, start.X, dx);
        AddCrossings(crossings, start.Y, dy);
        crossings.Sort();
        var scale = Math.Max(1, Math.Max(Math.Abs(dx), Math.Abs(dy)));
        var previous = 0d;
        foreach (var crossing in crossings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((crossing - previous) * scale > GridTolerance)
            {
                var middle = (crossing + previous) / 2d;
                AddCell(cells, start.X + dx * middle, start.Y + dy * middle);
            }
            // Include the half-open cell owning a corner, even when a sample is not recorded there.
            AddCell(cells, start.X + dx * crossing, start.Y + dy * crossing);
            previous = crossing;
        }
    }

    private static void AddCrossings(List<double> crossings, double start, double delta)
    {
        if (delta == 0) return;
        var minimum = Math.Min(start, start + delta);
        var maximum = Math.Max(start, start + delta);
        for (var line = Math.Floor(minimum) + 1; line < maximum; line++)
        {
            if (crossings.Count >= MaximumCrossings)
                throw new InvalidDataException("An exploration edge exceeded the grid traversal limit.");
            var fraction = (line - start) / delta;
            if (fraction is > 0 and < 1) crossings.Add(fraction);
        }
    }

    private static void AddCell(HashSet<int> cells, double x, double y)
    {
        var column = (int)Math.Floor(Snap(x));
        column = (column % Size + Size) % Size;
        var row = Math.Clamp((int)Math.Floor(Snap(y)), 0, Size - 1);
        cells.Add(column + row * Size);
    }

    private static double Snap(double value) =>
        Math.Abs(value - Math.Round(value)) <= GridTolerance ? Math.Round(value) : value;

    private static double Latitude(int y, int size) =>
        Math.Atan(Math.Sinh(Math.PI * (1 - 2d * y / size))) * 180d / Math.PI;
}
