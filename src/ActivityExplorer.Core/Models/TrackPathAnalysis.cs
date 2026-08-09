using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Core.Models;

public sealed record TrackPathSliceMetrics(
    double DistanceMeters,
    double? ElevationGainMeters,
    double? ElevationLossMeters,
    double? AverageGradePercent,
    double? StartElevationMeters,
    double? EndElevationMeters,
    int ElevationSampleCount);

public sealed class TrackPathAnalysis
{
    private readonly double[] _cumulativeDistances;
    private readonly int[] _positionPointIndices;
    private readonly int[] _elevationPointIndices;
    private readonly double[] _elevations;
    private readonly double[] _cumulativeElevationGains;
    private readonly double[] _cumulativeElevationLosses;

    public TrackPathAnalysis(IReadOnlyList<TrackPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        PointCount = points.Count;
        _cumulativeDistances = new double[points.Count];
        var positionPointIndices = new List<int>(points.Count);
        TrackPoint? previousPosition = null;

        for (var index = 0; index < points.Count; index++)
        {
            if (index > 0) _cumulativeDistances[index] = _cumulativeDistances[index - 1];
            var current = points[index];
            if (!HasFiniteCoordinates(current)) continue;

            positionPointIndices.Add(index);
            if (previousPosition is not null)
            {
                _cumulativeDistances[index] += HaversineMeters(
                    previousPosition.Latitude!.Value,
                    previousPosition.Longitude!.Value,
                    current.Latitude!.Value,
                    current.Longitude!.Value);
            }

            previousPosition = current;
        }
        _positionPointIndices = positionPointIndices.ToArray();

        var elevationSamples = points
            .Select((point, index) => (Index: index, Elevation: point.ElevationMeters))
            .Where(sample => sample.Elevation.HasValue && double.IsFinite(sample.Elevation.Value))
            .Select(sample => (sample.Index, Elevation: sample.Elevation!.Value))
            .ToArray();
        _elevationPointIndices = elevationSamples.Select(sample => sample.Index).ToArray();
        _elevations = elevationSamples.Select(sample => sample.Elevation).ToArray();
        _cumulativeElevationGains = new double[elevationSamples.Length];
        _cumulativeElevationLosses = new double[elevationSamples.Length];
        for (var index = 1; index < elevationSamples.Length; index++)
        {
            _cumulativeElevationGains[index] = _cumulativeElevationGains[index - 1];
            _cumulativeElevationLosses[index] = _cumulativeElevationLosses[index - 1];
            var change = elevationSamples[index].Elevation - elevationSamples[index - 1].Elevation;
            if (change > 0) _cumulativeElevationGains[index] += change;
            else _cumulativeElevationLosses[index] -= change;
        }

        MinimumElevationMeters = _elevations.Length == 0 ? null : _elevations.Min();
        MaximumElevationMeters = _elevations.Length == 0 ? null : _elevations.Max();
    }

    public int PointCount { get; }
    public double TotalDistanceMeters => _cumulativeDistances.LastOrDefault();
    public double? MinimumElevationMeters { get; }
    public double? MaximumElevationMeters { get; }
    public bool HasElevation => _elevations.Length > 0;
    public IReadOnlyList<double> CumulativeDistances => _cumulativeDistances;

    public double DistanceAt(int pointIndex)
    {
        if (pointIndex < 0 || pointIndex >= PointCount) throw new ArgumentOutOfRangeException(nameof(pointIndex));
        return _cumulativeDistances[pointIndex];
    }

    public int FindNearestPointIndex(double distanceMeters)
    {
        if (PointCount == 0) return -1;
        var target = Math.Clamp(distanceMeters, 0, TotalDistanceMeters);
        var result = Array.BinarySearch(_cumulativeDistances, target);
        if (result >= 0) return result;

        var after = ~result;
        if (after <= 0) return 0;
        if (after >= PointCount) return PointCount - 1;
        var before = after - 1;
        return target - _cumulativeDistances[before] <= _cumulativeDistances[after] - target ? before : after;
    }

    public TrackPathSliceMetrics Slice(int startIndex, int endIndex, bool reverseDirection = false)
    {
        if (startIndex < 0 || startIndex >= PointCount) throw new ArgumentOutOfRangeException(nameof(startIndex));
        if (endIndex < startIndex || endIndex >= PointCount) throw new ArgumentOutOfRangeException(nameof(endIndex));

        var firstPosition = LowerBound(_positionPointIndices, startIndex);
        var afterLastPosition = UpperBound(_positionPointIndices, endIndex);
        var positionCount = Math.Max(0, afterLastPosition - firstPosition);
        var distance = positionCount < 2
            ? 0
            : Math.Max(0,
                _cumulativeDistances[_positionPointIndices[afterLastPosition - 1]] -
                _cumulativeDistances[_positionPointIndices[firstPosition]]);
        var firstElevation = LowerBound(_elevationPointIndices, startIndex);
        var afterLastElevation = UpperBound(_elevationPointIndices, endIndex);
        var elevationCount = Math.Max(0, afterLastElevation - firstElevation);
        if (elevationCount == 0)
            return new TrackPathSliceMetrics(distance, null, null, null, null, null, 0);

        var lastElevation = afterLastElevation - 1;
        var startElevation = _elevations[firstElevation];
        var endElevation = _elevations[lastElevation];
        double? gain = null;
        double? loss = null;
        double? grade = null;
        if (elevationCount >= 2)
        {
            gain = _cumulativeElevationGains[lastElevation] - _cumulativeElevationGains[firstElevation];
            loss = _cumulativeElevationLosses[lastElevation] - _cumulativeElevationLosses[firstElevation];
            if (distance > 0) grade = (endElevation - startElevation) / distance * 100d;
        }

        if (reverseDirection)
        {
            (startElevation, endElevation) = (endElevation, startElevation);
            (gain, loss) = (loss, gain);
            grade = grade.HasValue ? -grade.Value : null;
        }

        return new TrackPathSliceMetrics(
            distance,
            gain,
            loss,
            grade,
            startElevation,
            endElevation,
            elevationCount);
    }

    public static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double radius = 6_371_000;
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2))
            * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return radius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static bool HasFiniteCoordinates(TrackPoint point) =>
        point.Latitude.HasValue && point.Longitude.HasValue &&
        double.IsFinite(point.Latitude.Value) && double.IsFinite(point.Longitude.Value);

    private static int LowerBound(IReadOnlyList<int> values, int target)
    {
        var low = 0;
        var high = values.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (values[middle] < target) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int UpperBound(IReadOnlyList<int> values, int target)
    {
        var low = 0;
        var high = values.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (values[middle] <= target) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}
