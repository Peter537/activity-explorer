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

    public async Task<IReadOnlyList<ImportCandidate>> ReadAsync(
        string path,
        SourceKind sourceKind,
        CancellationToken cancellationToken = default)
    {
        var sha = await Fingerprint.Sha256Async(path, cancellationToken);
        var parsed = Parse(path, cancellationToken);
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

    private static ParsedActivity Parse(string path, CancellationToken cancellationToken)
    {
        var points = new List<TrackPoint>();
        var laps = new List<LapCandidate>();
        string? activityName = null;
        string? sportText = null;
        DateTimeOffset? currentTime = null;
        double? currentLat = null;
        double? currentLon = null;
        double? currentEle = null;
        double? currentDistance = null;
        double? currentHr = null;
        double? currentCadence = null;
        double? currentPower = null;
        double? currentTemp = null;
        var inPoint = false;
        var lapStartIndex = 0;

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
                if (local is "trkpt" or "Trackpoint")
                {
                    inPoint = true;
                    currentTime = null;
                    currentEle = currentDistance = currentHr = currentCadence = currentPower = currentTemp = null;
                    currentLat = Parse(reader.GetAttribute("lat"));
                    currentLon = Parse(reader.GetAttribute("lon"));
                }
                else if (local == "Position")
                {
                    currentLat = currentLon = null;
                }
                else if (local is "name" or "Name" && !inPoint && string.IsNullOrWhiteSpace(activityName))
                {
                    activityName = ReadText(reader, ref readerAdvanced);
                }
                else if (local == "Activity" && !inPoint && reader.GetAttribute("Sport") is { } activitySport)
                {
                    sportText = activitySport;
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
                    currentDistance = Parse(ReadText(reader, ref readerAdvanced));
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
                else if (local == "Lap")
                {
                    lapStartIndex = points.Count;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName is "trkpt" or "Trackpoint")
                {
                    points.Add(new TrackPoint(currentTime, currentLat, currentLon, currentDistance, currentEle, null, currentHr, currentCadence, currentPower, currentTemp));
                    inPoint = false;
                }
                else if (reader.LocalName == "Lap" && points.Count > lapStartIndex)
                {
                    var slice = points.Skip(lapStartIndex).ToArray();
                    var first = slice.FirstOrDefault(x => x.Timestamp.HasValue)?.Timestamp;
                    var last = slice.LastOrDefault(x => x.Timestamp.HasValue)?.Timestamp;
                    var seconds = first.HasValue && last.HasValue ? (last.Value - first.Value).TotalSeconds : 0;
                    laps.Add(new LapCandidate(laps.Count + 1, Distance(slice), seconds, seconds, Average(slice, x => x.HeartRate), Average(slice, x => x.PowerWatts)));
                }
            }

            if (readerAdvanced)
            {
                continue;
            }
            reader.Read();
        }

        if (points.Count == 0)
        {
            throw new InvalidDataException("The XML activity contains no track points.");
        }

        var sport = MapSport(sportText);
        var start = points.FirstOrDefault(x => x.Timestamp.HasValue)?.Timestamp
            ?? throw new InvalidDataException("The XML activity has no timestamps.");
        var elapsed = (points.LastOrDefault(x => x.Timestamp.HasValue)?.Timestamp - start)?.TotalSeconds ?? 0;
        var distance = Distance(points);
        var elevations = points.Where(x => x.ElevationMeters.HasValue).Select(x => x.ElevationMeters!.Value).ToArray();

        return new ParsedActivity
        {
            Sport = sport,
            IsIndoor = ParseIndoor(sportText),
            Title = string.IsNullOrWhiteSpace(activityName) ? $"{sport} on {start:yyyy-MM-dd}" : activityName,
            StartTimeUtc = start.ToUniversalTime(),
            OriginalUtcOffset = start.Offset,
            DistanceMeters = distance,
            MovingTimeSeconds = elapsed,
            ElapsedTimeSeconds = elapsed,
            ElevationGainMeters = ElevationGain(elevations),
            AverageSpeedMetersPerSecond = elapsed > 0 ? distance / elapsed : null,
            AverageHeartRate = Average(points, x => x.HeartRate),
            MaxHeartRate = Max(points, x => x.HeartRate),
            AverageCadence = Average(points, x => x.Cadence),
            AveragePowerWatts = Average(points, x => x.PowerWatts),
            MaxPowerWatts = Max(points, x => x.PowerWatts),
            Points = FillDistancesAndSpeeds(points),
            Laps = laps
        };
    }

    private static SportKind MapSport(string? text)
    {
        var value = text?.Trim().ToLowerInvariant();
        if (value?.Contains("cycl") == true || value?.Contains("bik") == true) return SportKind.Cycling;
        if (value?.Contains("run") == true) return SportKind.Running;
        if (value?.Contains("walk") == true || value?.Contains("hik") == true) return SportKind.Walking;
        throw new UnsupportedActivityException("The GPX/TCX sport is missing or outside cycling, running, and walking.");
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

    private static IReadOnlyList<TrackPoint> FillDistancesAndSpeeds(IReadOnlyList<TrackPoint> points)
    {
        var result = new List<TrackPoint>(points.Count);
        double cumulative = 0;
        TrackPoint? previous = null;
        foreach (var point in points)
        {
            if (previous?.Latitude is not null && previous.Longitude is not null && point.Latitude is not null && point.Longitude is not null)
            {
                cumulative += GeometryCodec.HaversineMeters(previous.Latitude.Value, previous.Longitude.Value, point.Latitude.Value, point.Longitude.Value);
            }

            var distance = point.DistanceMeters ?? cumulative;
            double? speed = null;
            if (previous?.Timestamp is not null && point.Timestamp is not null)
            {
                var seconds = (point.Timestamp.Value - previous.Timestamp.Value).TotalSeconds;
                if (seconds > 0)
                {
                    speed = (distance - (previous.DistanceMeters ?? Math.Max(0, cumulative))) / seconds;
                }
            }

            result.Add(point with { DistanceMeters = distance, SpeedMetersPerSecond = point.SpeedMetersPerSecond ?? speed });
            previous = result[^1];
        }

        return result;
    }

    private static double Distance(IReadOnlyList<TrackPoint> points) =>
        points.LastOrDefault(x => x.DistanceMeters.HasValue)?.DistanceMeters ?? GeometryCodec.DistanceMeters(points);

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
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result) ? result : null;
}
