using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Web.Components.Shared;

public sealed record SegmentCreatorSubmission(
    string Name,
    double ToleranceMeters,
    int StartIndex,
    int EndIndex,
    bool ReverseDirection);

internal sealed class SegmentCreatorPath
{
    private SegmentCreatorPath(IReadOnlyList<TrackPoint> points, IReadOnlyList<int> sourcePointIndices)
    {
        Points = points;
        SourcePointIndices = sourcePointIndices;
    }

    public IReadOnlyList<TrackPoint> Points { get; }
    public IReadOnlyList<int> SourcePointIndices { get; }
    public int Count => Points.Count;

    public int SourceIndexAt(int visualIndex)
    {
        if (visualIndex < 0 || visualIndex >= SourcePointIndices.Count)
            throw new ArgumentOutOfRangeException(nameof(visualIndex));
        return SourcePointIndices[visualIndex];
    }

    public static SegmentCreatorPath FromActivity(IReadOnlyList<TrackPoint> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var projected = source
            .Select((point, index) => (Point: point, SourceIndex: index))
            .Where(item => item.Point.Latitude.HasValue && item.Point.Longitude.HasValue)
            .ToArray();
        return new SegmentCreatorPath(
            projected.Select(item => item.Point).ToArray(),
            projected.Select(item => item.SourceIndex).ToArray());
    }
}
