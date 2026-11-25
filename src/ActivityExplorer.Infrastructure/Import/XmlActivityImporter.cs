using System.Globalization;
using System.Xml;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Processing;

namespace ActivityExplorer.Infrastructure.Import;

public sealed class XmlActivityImporter : IActivityImporter
{
    public string Name => "GPX/TCX";

    public bool CanImport(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".gpx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tcx", StringComparison.OrdinalIgnoreCase);
    }

    public Task<IReadOnlyList<ImportCandidate>> ReadAsync(
        string path,
        SourceKind sourceKind,
        CancellationToken cancellationToken = default) => ReadAsync(path, sourceKind, null, cancellationToken);

    internal static async Task<IReadOnlyList<ImportCandidate>> ReadAsync(
        string path,
        SourceKind sourceKind,
        string? rowingFallback,
        CancellationToken cancellationToken)
    {
        var sha = await Fingerprint.Sha256Async(path, cancellationToken);
        var parsed = Parse(path, rowingFallback, cancellationToken);
        var provider = sourceKind switch
        {
            SourceKind.GarminArchive => SourceProvider.Garmin,
            SourceKind.StravaArchive => SourceProvider.Strava,
            _ => SourceProvider.Unknown
        };
        var acquisition = sourceKind is SourceKind.GarminArchive or SourceKind.StravaArchive
            ? AcquisitionMethod.AccountExport
            : sourceKind == SourceKind.WatchedFolder ? AcquisitionMethod.WatchedFolder : AcquisitionMethod.DirectUpload;
        return [new ImportCandidate(path, Path.GetFileName(path), sourceKind, sha, new FileInfo(path).Length, parsed,
            null, provider, acquisition)];
    }

    private static ParsedActivity Parse(string path, string? rowingFallback, CancellationToken cancellationToken)
    {
        var track = new TrackMeasurements();
        string? activityName = null;
        string? sportText = null;
        DateTimeOffset? currentTime = null;
        double? currentLat = null;
        double? currentLon = null;
        double? currentEle = null;
        double? currentDistance = null;
        double? currentSpeed = null;
        double? currentHr = null;
        double? currentCadence = null;
        double? currentPower = null;
        double? currentTemp = null;
        var inPoint = false;
        var activitiesDepth = -1;
        var activityDepth = -1;
        var isTcx = Path.GetExtension(path).Equals(".tcx", StringComparison.OrdinalIgnoreCase);
        var activityCount = 0;
        var lapDepth = -1;
        double? declaredLapDistance = null;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = 512L * 1024 * 1024
        };

        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = XmlReader.Create(input, settings);
        while (!reader.EOF)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var readerAdvanced = false;
            if (reader.NodeType == XmlNodeType.Element)
            {
                var local = reader.LocalName;
                if (reader.Depth == 0 && local == "TrainingCenterDatabase") isTcx = true;
                if (isTcx && activityDepth < 0 &&
                    !(local == "Activities" && reader.Depth == 1) &&
                    !(local == "Activity" && activitiesDepth >= 0 && reader.Depth == activitiesDepth + 1))
                {
                    reader.Read();
                    continue;
                }
                if (local is "trkpt" or "Trackpoint")
                {
                    inPoint = !reader.IsEmptyElement;
                    if (!inPoint) track.Break();
                    currentTime = null;
                    currentEle = currentDistance = currentSpeed = currentHr = currentCadence = currentPower = currentTemp = null;
                    currentLat = Parse(reader.GetAttribute("lat"));
                    currentLon = Parse(reader.GetAttribute("lon"));
                }
                else if (inPoint && local == "Position")
                {
                    currentLat = currentLon = null;
                }
                else if (local is "name" or "Name" && !inPoint && string.IsNullOrWhiteSpace(activityName))
                {
                    activityName = ReadText(reader, ref readerAdvanced);
                }
                else if (local == "Activities" && reader.Depth == 1 && !reader.IsEmptyElement)
                {
                    activitiesDepth = reader.Depth;
                }
                else if (local == "Activity" && activitiesDepth >= 0 && reader.Depth == activitiesDepth + 1)
                {
                    if (++activityCount > 1)
                        throw new UnsupportedActivityException("TCX files with multiple activities are not supported. Export each activity as a separate file.");
                    sportText = reader.GetAttribute("Sport");
                    if (!reader.IsEmptyElement) activityDepth = reader.Depth;
                }
                else if (local == "Lap" && activityDepth >= 0 && reader.Depth == activityDepth + 1 && !reader.IsEmptyElement)
                {
                    lapDepth = reader.Depth;
                    declaredLapDistance = null;
                    track.BeginLap();
                }
                else if (local == "DistanceMeters" && lapDepth >= 0 && reader.Depth == lapDepth + 1)
                {
                    declaredLapDistance = Nonnegative(Parse(ReadText(reader, ref readerAdvanced)));
                }
                else if (!inPoint && local is "trk" or "trkseg" or "Track")
                {
                    track.Break();
                }
                else if (local is "type" or "Sport" && !inPoint)
                {
                    sportText = reader.GetAttribute("Sport");
                    if (sportText is null)
                    {
                        sportText = ReadText(reader, ref readerAdvanced);
                    }
                }
                else if (inPoint && local is "time" or "Time")
                {
                    currentTime = ParseDate(ReadText(reader, ref readerAdvanced));
                }
                else if (inPoint && local is "ele" or "AltitudeMeters")
                {
                    currentEle = Parse(ReadText(reader, ref readerAdvanced));
                }
                else if (inPoint && local == "DistanceMeters")
                {
                    currentDistance = Nonnegative(Parse(ReadText(reader, ref readerAdvanced)));
                }
                else if (inPoint && local is "speed" or "Speed")
                {
                    currentSpeed = Nonnegative(Parse(ReadText(reader, ref readerAdvanced)));
                }
                else if (inPoint && local == "LatitudeDegrees")
                {
                    currentLat = Parse(ReadText(reader, ref readerAdvanced));
                }
                else if (inPoint && local == "LongitudeDegrees")
                {
                    currentLon = Parse(ReadText(reader, ref readerAdvanced));
                }
                else if (inPoint && local is "hr" or "HeartRateBpm")
                {
                    currentHr = ReadNestedNumber(reader);
                }
                else if (inPoint && local is "cad" or "Cadence")
                {
                    currentCadence = ReadNestedNumber(reader);
                }
                else if (inPoint && local is "watts" or "Watts")
                {
                    currentPower = ReadNestedNumber(reader);
                }
                else if (inPoint && local is "atemp" or "Temperature")
                {
                    currentTemp = ReadNestedNumber(reader);
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (inPoint && reader.LocalName is "trkpt" or "Trackpoint")
                {
                    track.Add(new TrackPoint(currentTime,
                        currentLat is >= -90 and <= 90 ? currentLat : null,
                        currentLon is >= -180 and <= 180 ? currentLon : null,
                        currentDistance, currentEle, currentSpeed, currentHr, currentCadence, currentPower, currentTemp));
                    inPoint = false;
                }
                else if (reader.LocalName == "Lap" && reader.Depth == lapDepth)
                {
                    track.EndLap(declaredLapDistance);
                    lapDepth = -1;
                }
                else if (reader.LocalName == "Activity" && reader.Depth == activityDepth) activityDepth = -1;
                else if (reader.LocalName == "Activities" && reader.Depth == activitiesDepth) activitiesDepth = -1;
            }

            if (readerAdvanced)
            {
                continue;
            }
            reader.Read();
        }

        var points = track.Points;
        if (points.Count == 0)
        {
            throw new InvalidDataException("The XML activity contains no track points.");
        }

        if ((string.IsNullOrWhiteSpace(sportText) || string.Equals(sportText.Trim(), "Other", StringComparison.OrdinalIgnoreCase)) &&
            RowingLabels.IsRowing(rowingFallback))
            sportText = rowingFallback;
        var sport = MapSport(sportText);
        var start = track.FirstTime
            ?? throw new InvalidDataException("The XML activity has no timestamps.");
        var elapsed = (track.LatestTime - start)?.TotalSeconds ?? 0;
        var distance = track.DistanceMeters;
        if (!double.IsFinite(distance)) throw new InvalidDataException("The XML activity distance is outside the supported numeric range.");
        var elevations = points.Where(x => x.ElevationMeters.HasValue).Select(x => x.ElevationMeters!.Value).ToArray();

        return new ParsedActivity
        {
            Sport = sport,
            IsIndoor = ParseIndoor(sportText),
            Title = string.IsNullOrWhiteSpace(activityName) ? $"{sport} on {start:yyyy-MM-dd}" : activityName,
            StartTimeUtc = start.ToUniversalTime(),
            OriginalUtcOffset = start.Offset,
            DistanceMeters = distance,
            MovingTimeSeconds = track.MovingSeconds,
            MovingTimeSource = track.HasMovementEvidence ? MovingTimeSource.EstimatedFromRecords : MovingTimeSource.Unavailable,
            ElapsedTimeSeconds = elapsed,
            ElevationGainMeters = ElevationGain(elevations),
            AverageSpeedMetersPerSecond = elapsed > 0 ? distance / elapsed : null,
            AverageHeartRate = Average(points, x => x.HeartRate),
            MaxHeartRate = Max(points, x => x.HeartRate),
            AverageCadence = Average(points, x => x.Cadence),
            AveragePowerWatts = Average(points, x => x.PowerWatts),
            MaxPowerWatts = Max(points, x => x.PowerWatts),
            Points = points,
            Laps = track.Laps
        };
    }

    private static SportKind MapSport(string? text)
    {
        var value = text?.Trim().ToLowerInvariant();
        if (value?.Contains("cycl") == true || value?.Contains("bik") == true) return SportKind.Cycling;
        if (value?.Contains("run") == true) return SportKind.Running;
        if (value?.Contains("walk") == true || value?.Contains("hik") == true) return SportKind.Walking;
        if (RowingLabels.IsRowing(text)) return SportKind.Rowing;
        throw new UnsupportedActivityException("The GPX/TCX sport is missing or outside cycling, running, walking, and rowing.");
    }

    private static bool? ParseIndoor(string? text)
    {
        var value = text?.Trim().ToLowerInvariant();
        if (value is null) return null;
        if (value.Contains("indoor", StringComparison.Ordinal) ||
            value.Contains("virtual", StringComparison.Ordinal) ||
            value.Contains("treadmill", StringComparison.Ordinal) ||
            value.Contains("trainer", StringComparison.Ordinal) ||
            value.Contains("spin", StringComparison.Ordinal))
            return true;
        return value.Contains("outdoor", StringComparison.Ordinal) ? false : null;
    }

    private sealed class TrackMeasurements
    {
        private const double MaxMovementSampleGapSeconds = 30;
        private const double MinMovingSpeedMetersPerSecond = 0.3;
        private double _sampledMeters;
        private double _observedLapMeters;
        private double _totalLapMeters;
        private TrackPoint? _previous;
        private double? _previousRecordedDistance;
        private double _sampledAtPreviousRecordedDistance;
        private int _lapStart;
        private double _lapStartingMeters;
        private double _lapStartingMovingSeconds;

        public List<TrackPoint> Points { get; } = [];
        public List<LapCandidate> Laps { get; } = [];
        public DateTimeOffset? FirstTime { get; private set; }
        public DateTimeOffset? LatestTime { get; private set; }
        public double DistanceMeters => (_sampledMeters - _observedLapMeters) + _totalLapMeters;
        public double MovingSeconds { get; private set; }
        public bool HasMovementEvidence { get; private set; }

        public void Break()
        {
            _previous = null;
            _previousRecordedDistance = null;
        }

        public void BeginLap()
        {
            Break();
            _lapStart = Points.Count;
            _lapStartingMeters = _sampledMeters;
            _lapStartingMovingSeconds = MovingSeconds;
        }

        public void EndLap(double? declaredDistance)
        {
            Break();
            if (_lapStart == Points.Count && declaredDistance is null) return;
            var samples = Points.Skip(_lapStart);
            var first = samples.FirstOrDefault(x => x.Timestamp.HasValue)?.Timestamp;
            var last = samples.Max(x => x.Timestamp);
            var elapsed = (last - first)?.TotalSeconds ?? 0;
            var observedDistance = _sampledMeters - _lapStartingMeters;
            var lapDistance = declaredDistance ?? observedDistance;
            _observedLapMeters += observedDistance;
            _totalLapMeters += lapDistance;
            Laps.Add(new LapCandidate(Laps.Count + 1, lapDistance, elapsed, MovingSeconds - _lapStartingMovingSeconds,
                Average(samples, x => x.HeartRate), Average(samples, x => x.PowerWatts)));
        }

        public void Add(TrackPoint point)
        {
            var edgeMeters = _previous is null ? null : EdgeDistance(_previous, point);
            var additionalMeters = edgeMeters ?? 0;
            if (point.DistanceMeters is { } recorded)
            {
                if (_previousRecordedDistance is { } baseline && recorded >= baseline)
                {
                    // The counter can establish distance across missing samples without establishing movement in the gap.
                    var distanceSinceBaseline = _sampledMeters - _sampledAtPreviousRecordedDistance;
                    additionalMeters = Math.Max(0, recorded - baseline - distanceSinceBaseline);
                }
                _previousRecordedDistance = recorded;
                _sampledAtPreviousRecordedDistance = _sampledMeters + additionalMeters;
            }
            _sampledMeters += additionalMeters;

            var speed = point.SpeedMetersPerSecond;
            var interval = (point.Timestamp - _previous?.Timestamp)?.TotalSeconds;
            if (interval is > 0 and <= MaxMovementSampleGapSeconds && _previous?.Timestamp >= LatestTime)
            {
                speed ??= edgeMeters / interval.Value;
                HasMovementEvidence |= speed.HasValue;
                if (speed > MinMovingSpeedMetersPerSecond) MovingSeconds += interval.Value;
            }

            FirstTime ??= point.Timestamp;
            if (LatestTime is null || point.Timestamp > LatestTime) LatestTime = point.Timestamp;
            Points.Add(point with
            {
                DistanceMeters = point.DistanceMeters.HasValue || HasPosition(point) ? _sampledMeters : null,
                SpeedMetersPerSecond = speed
            });
            _previous = point;
        }

        private static double? EdgeDistance(TrackPoint previous, TrackPoint current)
        {
            if (previous.DistanceMeters is { } start && current.DistanceMeters is { } end)
                return end >= start ? end - start : null;
            if (!HasPosition(previous) || !HasPosition(current)) return null;
            return Nonnegative(GeometryCodec.HaversineMeters(previous.Latitude!.Value, previous.Longitude!.Value,
                current.Latitude!.Value, current.Longitude!.Value));
        }

        private static bool HasPosition(TrackPoint point) =>
            point.Latitude is >= -90 and <= 90 && point.Longitude is >= -180 and <= 180;
    }

    private static double ElevationGain(IReadOnlyList<double> values)
    {
        var result = 0d;
        for (var index = 1; index < values.Count; index++)
        {
            result += Math.Max(0, values[index] - values[index - 1]);
        }

        return result;
    }

    private static double? Average(IEnumerable<TrackPoint> points, Func<TrackPoint, double?> selector)
    {
        var values = points.Select(selector).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return values.Length == 0 ? null : values.Average();
    }

    private static double? Max(IEnumerable<TrackPoint> points, Func<TrackPoint, double?> selector)
    {
        var values = points.Select(selector).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return values.Length == 0 ? null : values.Max();
    }

    private static string ReadText(XmlReader reader, ref bool readerAdvanced)
    {
        readerAdvanced = true;
        return reader.ReadElementContentAsString();
    }

    private static double? ReadNestedNumber(XmlReader reader)
    {
        if (reader.IsEmptyElement) return null;
        var depth = reader.Depth;
        double? result = null;
        while (reader.Read())
        {
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
            {
                result ??= Parse(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }
        }
        return result;
    }

    private static double? Parse(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result) ? result : null;

    private static double? Nonnegative(double? value) =>
        value is >= 0 && double.IsFinite(value.Value) ? value : null;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result) ? result : null;
}
