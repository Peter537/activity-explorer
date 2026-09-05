using ActivityExplorer.Core.Contracts;
using System.Text.RegularExpressions;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Processing;
using Dynastream.Fit;
using FitDateTime = Dynastream.Fit.DateTime;

namespace ActivityExplorer.Infrastructure.Import;

public sealed class FitActivityImporter : IActivityImporter
{
    private const double SemicirclesToDegrees = 180d / 2_147_483_648d;
    public const int CurrentParserVersion = 2;
    private static readonly Regex GarminActivityName = new(@"(?<!\d)(?<id>\d{6,})_ACTIVITY\.fit$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);


    public string Name => "FIT";
    public bool CanImport(string path) => string.Equals(Path.GetExtension(path), ".fit", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<ImportCandidate>> ReadAsync(
        string path,
        SourceKind sourceKind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sha = await Fingerprint.Sha256Async(path, cancellationToken);
        var originalName = Path.GetFileName(path);
        var garminId = ExtractGarminActivityId(originalName);
        var provider = sourceKind switch
        {
            SourceKind.GarminArchive => SourceProvider.Garmin,
            SourceKind.StravaArchive => SourceProvider.Strava,
            _ when garminId is not null => SourceProvider.Garmin,
            _ => SourceProvider.Unknown
        };
        var acquisition = sourceKind is SourceKind.GarminArchive or SourceKind.StravaArchive
            ? AcquisitionMethod.AccountExport
            : sourceKind == SourceKind.WatchedFolder ? AcquisitionMethod.WatchedFolder : AcquisitionMethod.DirectUpload;
        var externalId = provider == SourceProvider.Garmin ? garminId : null;
        var parsed = Decode(path, externalId);
        return [new ImportCandidate(path, originalName, sourceKind, sha, new FileInfo(path).Length, parsed,
            externalId, provider, acquisition, CurrentParserVersion)];
    }

    private static ParsedActivity Decode(string path, string? externalId)
    {
        var records = new List<TrackPoint>();
        var laps = new List<LapCandidate>();
        SessionMesg? session = null;
        FileIdMesg? fileId = null;

        var broadcaster = new MesgBroadcaster();
        broadcaster.RecordMesgEvent += (_, args) =>
        {
            var message = new RecordMesg(args.mesg);
            records.Add(new TrackPoint(
                ToTimestamp(message.GetTimestamp()),
                message.GetPositionLat() * SemicirclesToDegrees,
                message.GetPositionLong() * SemicirclesToDegrees,
                message.GetDistance(),
                message.GetEnhancedAltitude() ?? message.GetAltitude(),
                message.GetEnhancedSpeed() ?? message.GetSpeed(),
                message.GetHeartRate(),
                message.GetCadence(),
                message.GetPower(),
                message.GetTemperature(),
                message.GetEnhancedRespirationRate() ?? message.GetRespirationRate()));
        };
        broadcaster.LapMesgEvent += (_, args) =>
        {
            var message = new LapMesg(args.mesg);
            laps.Add(new LapCandidate(
                laps.Count + 1,
                message.GetTotalDistance() ?? 0,
                message.GetTotalElapsedTime() ?? 0,
                message.GetTotalMovingTime() ?? message.GetTotalTimerTime() ?? 0,
                message.GetAvgHeartRate(),
                message.GetAvgPower()));
        };
        broadcaster.SessionMesgEvent += (_, args) => session = new SessionMesg(args.mesg);
        broadcaster.FileIdMesgEvent += (_, args) => fileId = new FileIdMesg(args.mesg);

        var decoder = new Decode();
        decoder.MesgEvent += broadcaster.OnMesg;
        decoder.MesgDefinitionEvent += broadcaster.OnMesgDefinition;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!decoder.IsFIT(stream))
        {
            throw new InvalidDataException("The file is not a valid FIT file.");
        }

        stream.Position = 0;
        if (!decoder.CheckIntegrity(stream))
        {
            throw new InvalidDataException("The FIT file failed its CRC integrity check.");
        }

        stream.Position = 0;
        decoder.Read(stream);

        var sport = MapSport(session?.GetSport(), session?.GetSubSport());
        var isIndoor = ClassifyIndoor(sport, session?.GetSubSport());
        var start = ToTimestamp(session?.GetStartTime())
            ?? records.FirstOrDefault(x => x.Timestamp.HasValue)?.Timestamp
            ?? throw new InvalidDataException("The FIT activity has no usable start time.");
        var distance = session?.GetTotalDistance() ?? records.LastOrDefault()?.DistanceMeters ?? GeometryCodec.DistanceMeters(records);
        var elapsed = session?.GetTotalElapsedTime() ?? Elapsed(records);
        var timer = session?.GetTotalTimerTime();
        var fitMoving = session?.GetTotalMovingTime();
        var estimatedMoving = EstimateMoving(records);
        if (timer.HasValue)
        {
            estimatedMoving = Math.Min(estimatedMoving, timer.Value);
        }
        var moving = fitMoving ?? estimatedMoving;
        var movingSource = fitMoving.HasValue
            ? MovingTimeSource.FitSession
            : estimatedMoving > 0 ? MovingTimeSource.EstimatedFromRecords : MovingTimeSource.Unavailable;
        var device = fileId?.GetProductNameAsString();
        var elevations = Values(records, x => x.ElevationMeters);
        var speeds = Values(records, x => x.SpeedMetersPerSecond);
        var heartRates = Values(records, x => x.HeartRate);
        var cadences = Values(records, x => x.Cadence);
        var powers = Values(records, x => x.PowerWatts);
        var temperatures = Values(records, x => x.TemperatureCelsius);
        var respiration = Values(records, x => x.RespirationRate);
        var totalCalories = session?.GetTotalCalories();
        var restingCalories = session?.GetMetabolicCalories();
        double? activeCalories = totalCalories.HasValue && restingCalories.HasValue
            ? Math.Max(0, totalCalories.Value - restingCalories.Value)
            : null;

        return new ParsedActivity
        {
            Sport = sport,
            IsIndoor = isIndoor,
            Title = $"{sport} on {start:yyyy-MM-dd}",
            DeviceName = string.IsNullOrWhiteSpace(device) ? null : device,
            ExternalId = externalId,
            StartTimeUtc = start.ToUniversalTime(),
            DistanceMeters = distance,
            MovingTimeSeconds = moving,
            TimerTimeSeconds = timer,
            MovingTimeSource = movingSource,
            ElapsedTimeSeconds = elapsed,
            ElevationGainMeters = session?.GetTotalAscent() ?? ElevationGain(records),
            ElevationLossMeters = session?.GetTotalDescent() ?? ElevationLoss(records),
            MinElevationMeters = session?.GetEnhancedMinAltitude() ?? session?.GetMinAltitude() ?? Min(elevations),
            MaxElevationMeters = session?.GetEnhancedMaxAltitude() ?? session?.GetMaxAltitude() ?? Max(elevations),
            Calories = totalCalories,
            RestingCalories = restingCalories,
            ActiveCalories = activeCalories,
            AverageSpeedMetersPerSecond = session?.GetEnhancedAvgSpeed() ?? session?.GetAvgSpeed() ?? Average(speeds),
            MaxSpeedMetersPerSecond = session?.GetEnhancedMaxSpeed() ?? session?.GetMaxSpeed() ?? Max(speeds),
            AverageHeartRate = session?.GetAvgHeartRate() ?? Average(heartRates),
            MaxHeartRate = session?.GetMaxHeartRate() ?? Max(heartRates),
            AverageCadence = session?.GetAvgCadence() ?? Average(cadences),
            MaxCadence = session?.GetMaxCadence() ?? Max(cadences),
            PedalRevolutions = sport == SportKind.Rowing ? null : session?.GetTotalCycles(),
            AveragePowerWatts = session?.GetAvgPower() ?? Average(powers),
            MaxPowerWatts = session?.GetMaxPower() ?? Max(powers),
            NormalizedPowerWatts = session?.GetNormalizedPower(),
            Kilojoules = session?.GetTotalWork() / 1000d,
            AverageTemperatureCelsius = Average(temperatures) ?? session?.GetAvgTemperature(),
            MinTemperatureCelsius = Min(temperatures) ?? session?.GetMinTemperature(),
            MaxTemperatureCelsius = Max(temperatures) ?? session?.GetMaxTemperature(),
            AverageRespirationRate = session?.GetEnhancedAvgRespirationRate() ?? session?.GetAvgRespirationRate()
                ?? Average(respiration),
            MinRespirationRate = session?.GetEnhancedMinRespirationRate() ?? session?.GetMinRespirationRate()
                ?? Min(respiration),
            MaxRespirationRate = session?.GetEnhancedMaxRespirationRate() ?? session?.GetMaxRespirationRate()
                ?? Max(respiration),
            AerobicTrainingEffect = session?.GetTotalTrainingEffect(),
            AnaerobicTrainingEffect = session?.GetTotalAnaerobicTrainingEffect(),
            TrainingLoad = session?.GetTrainingLoadPeak(),
            Metrics = SessionMetrics(session, sport),
            Points = records,
            Laps = laps
        };
    }

    private static DateTimeOffset? ToTimestamp(FitDateTime? value)
    {
        if (value is null)
        {
            return null;
        }

        var date = System.DateTime.SpecifyKind(value.GetDateTime(), DateTimeKind.Utc);
        return new DateTimeOffset(date);
    }

    private static SportKind MapSport(Sport? sport, SubSport? subSport) => sport switch
    {
        Sport.Cycling => SportKind.Cycling,
        Sport.Running => SportKind.Running,
        Sport.Walking => SportKind.Walking,
        Sport.Rowing => SportKind.Rowing,
        Sport.FitnessEquipment when subSport == SubSport.IndoorRowing => SportKind.Rowing,
        _ => throw new UnsupportedActivityException($"FIT sport '{sport?.ToString() ?? "unknown"}' is outside v0.1.0.")
    };

    private static bool? ClassifyIndoor(SportKind sport, SubSport? subSport)
    {
        if (!subSport.HasValue || subSport is SubSport.Generic or SubSport.All or SubSport.Invalid)
        {
            return null;
        }

        var value = subSport.Value;
        if (value.ToString().StartsWith("Indoor", StringComparison.Ordinal) ||
            value is SubSport.Treadmill or SubSport.Spin or SubSport.Elliptical or
                SubSport.StairClimbing or SubSport.VirtualActivity or SubSport.Esport)
        {
            return true;
        }

        return sport switch
        {
            SportKind.Cycling when value is SubSport.Road or SubSport.Mountain or SubSport.Downhill or
                SubSport.Cyclocross or SubSport.HandCycling or SubSport.GravelCycling or SubSport.EBikeFitness or
                SubSport.EBikeMountain or SubSport.Commuting or SubSport.MixedSurface or SubSport.Bmx => false,
            SportKind.Running when value is SubSport.Street or SubSport.Trail or SubSport.Obstacle or
                SubSport.Ultra or SubSport.Rucking => false,
            SportKind.Walking when value is SubSport.CasualWalking or SubSport.SpeedWalking or
                SubSport.Street or SubSport.Trail => false,
            _ => null
        };
    }

    private static double Elapsed(IReadOnlyList<TrackPoint> points)
    {
        var first = points.FirstOrDefault(x => x.Timestamp.HasValue)?.Timestamp;
        var last = points.LastOrDefault(x => x.Timestamp.HasValue)?.Timestamp;
        return first.HasValue && last.HasValue ? Math.Max(0, (last.Value - first.Value).TotalSeconds) : 0;
    }

    private static double ElevationGain(IReadOnlyList<TrackPoint> points)
    {
        var total = 0d;
        double? previous = null;
        foreach (var elevation in points.Select(x => x.ElevationMeters).Where(x => x.HasValue).Select(x => x!.Value))
        {
            if (previous.HasValue && elevation > previous.Value)
            {
                total += elevation - previous.Value;
            }

            previous = elevation;
        }

        return total;
    }

    public static string? ExtractGarminActivityId(string? fileName)
    {
        var match = GarminActivityName.Match(Path.GetFileName(fileName ?? string.Empty));
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static double ElevationLoss(IReadOnlyList<TrackPoint> points)
    {
        var total = 0d;
        double? previous = null;
        foreach (var elevation in points.Select(x => x.ElevationMeters).Where(x => x.HasValue).Select(x => x!.Value))
        {
            if (previous.HasValue && elevation < previous.Value)
            {
                total += previous.Value - elevation;
            }
            previous = elevation;
        }
        return total;
    }

    private static double EstimateMoving(IReadOnlyList<TrackPoint> points)
    {
        if (points.Count < 2) return 0;
        var moving = 0d;
        for (var index = 1; index < points.Count; index++)
        {
            var previous = points[index - 1];
            var current = points[index];
            if (!previous.Timestamp.HasValue || !current.Timestamp.HasValue) continue;
            var seconds = (current.Timestamp.Value - previous.Timestamp.Value).TotalSeconds;
            if (seconds is <= 0 or > 30) continue;
            var speed = current.SpeedMetersPerSecond
                ?? ((current.DistanceMeters - previous.DistanceMeters) / seconds);
            if (speed is > 0.3) moving += seconds;
        }
        return moving;
    }

    private static IReadOnlyList<ActivityMetricCandidate> SessionMetrics(SessionMesg? session, SportKind sport)
    {
        var metrics = new List<ActivityMetricCandidate>();
        void Add(string key, string label, double? value, string? unit = null)
        {
            if (value.HasValue)
                metrics.Add(new ActivityMetricCandidate(key, label, value, Unit: unit));
        }

        Add("fit.total_fat_calories", "Fat calories", session?.GetTotalFatCalories(), "kcal");
        Add("fit.threshold_power", "Threshold power", session?.GetThresholdPower(), "W");
        Add("fit.training_stress_score", "Training stress score", session?.GetTrainingStressScore());
        Add("fit.intensity_factor", "Intensity factor", session?.GetIntensityFactor());
        if (sport == SportKind.Rowing)
            Add("fit.total_strokes", "Total strokes", session?.GetTotalCycles(), "strokes");
        return metrics;
    }

    private static double[] Values(IEnumerable<TrackPoint> points, Func<TrackPoint, double?> selector) =>
        points.Select(selector).Where(x => x.HasValue).Select(x => x!.Value).ToArray();

    private static double? Average(IReadOnlyCollection<double> values) =>
        values.Count == 0 ? null : values.Average();

    private static double? Min(IReadOnlyCollection<double> values) =>
        values.Count == 0 ? null : values.Min();

    private static double? Max(IReadOnlyCollection<double> values) =>
        values.Count == 0 ? null : values.Max();
}
