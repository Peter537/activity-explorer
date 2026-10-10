using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using Dynastream.Fit;

namespace ActivityExplorer.Infrastructure.Services;

public sealed record ExplorationSourceEvidence(
    bool VerifiedContinuity, IReadOnlySet<int> BreakBeforeIndices, string? Diagnostic);

public sealed class ExplorationSourceReader(IOriginalStore originals)
{
    private const double SemicirclesToDegrees = 180d / 2_147_483_648d;
    private const string LimitedDiagnostic =
        "Original recording breaks could not be verified; only recorded GPS samples are counted for this activity.";

    public async Task<ExplorationSourceEvidence> ReadAsync(
        IReadOnlyList<TrackPoint> canonicalPoints,
        IReadOnlyList<SourceFile> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(canonicalPoints);
        ArgumentNullException.ThrowIfNull(sources);
        var verified = false;
        var breaks = new HashSet<int>();
        foreach (var source in sources.DistinctBy(source => (source.StoredPath, source.Sha256)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(source.OriginalName).ToLowerInvariant();
            if (extension is not (".fit" or ".gpx" or ".tcx")) continue;
            try
            {
                var path = originals.ResolveStoredPath(source.StoredPath);
                await using var input = new CancellableFileStream(path, cancellationToken);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
                if (!string.Equals(hash, source.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                input.Position = 0;
                var match = new SourceMatch(canonicalPoints, cancellationToken);
                await Task.Run(() =>
                {
                    if (extension == ".fit") ReadFit(input, match, cancellationToken);
                    else ReadXml(input, match, cancellationToken);
                }, cancellationToken).ConfigureAwait(false);
                if (!match.Complete) continue;
                verified = true;
                breaks.UnionWith(match.Breaks);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or XmlException or FitException or
                                               UnauthorizedAccessException or ArgumentException or SourceMismatchException)
            {
                // A missing or unreadable original must never create inferred connecting edges.
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(verified, breaks, verified ? null : LimitedDiagnostic);
    }

    private static void ReadXml(Stream input, SourceMatch match, CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = 512L * 1024 * 1024
        };
        using var reader = XmlReader.Create(input, settings);
        var inPoint = false;
        var breakBefore = false;
        DateTimeOffset? timestamp = null;
        double? latitude = null;
        double? longitude = null;
        while (!reader.EOF)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var advanced = false;
            if (reader.NodeType == XmlNodeType.Element)
            {
                var local = reader.LocalName;
                if (local is "trk" or "trkseg" or "Track") breakBefore = true;
                else if (local is "trkpt" or "Trackpoint")
                {
                    inPoint = true;
                    timestamp = null;
                    latitude = Number(reader.GetAttribute("lat"));
                    longitude = Number(reader.GetAttribute("lon"));
                    // The canonical importer omits empty points; their missing timing/position still breaks an edge.
                    if (reader.IsEmptyElement) breakBefore = true;
                }
                else if (local == "Position") latitude = longitude = null;
                else if (inPoint && local is "time" or "Time")
                {
                    timestamp = DateTimeOffset.TryParse(reader.ReadElementContentAsString(),
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;
                    advanced = true;
                }
                else if (inPoint && local is "LatitudeDegrees" or "LongitudeDegrees")
                {
                    var value = Number(reader.ReadElementContentAsString());
                    if (local == "LatitudeDegrees") latitude = value;
                    else longitude = value;
                    advanced = true;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName is "trkpt" or "Trackpoint")
            {
                match.Add(timestamp, latitude, longitude, breakBefore);
                breakBefore = false;
                inPoint = false;
            }
            if (!advanced) reader.Read();
        }
    }

    private static void ReadFit(Stream input, SourceMatch match, CancellationToken cancellationToken)
    {
        var broadcaster = new MesgBroadcaster();
        var timerEvents = new List<(DateTimeOffset Timestamp, bool Stopped, int Sequence)>();
        var stopped = false;
        var untimedState = false;
        var breakBefore = false;
        DateTimeOffset? sourceStop = null;
        DateTimeOffset? sourceStart = null;
        broadcaster.RecordMesgEvent += (_, args) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = new RecordMesg(args.mesg);
            var timestamp = Timestamp(record.GetTimestamp());
            var timedPause = sourceStop.HasValue && timestamp > sourceStop ||
                sourceStart.HasValue && match.PreviousTimestamp < sourceStart && timestamp >= sourceStart;
            match.Add(timestamp, record.GetPositionLat() * SemicirclesToDegrees,
                record.GetPositionLong() * SemicirclesToDegrees, breakBefore || (untimedState && stopped) || timedPause);
            breakBefore = false;
            sourceStart = null;
        };
        broadcaster.EventMesgEvent += (_, args) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = new EventMesg(args.mesg);
            if (message.GetEvent() != Event.Timer) return;
            var type = message.GetEventType();
            var starts = type is EventType.Start or EventType.BeginDepreciated;
            if (!starts && type is not (EventType.Stop or EventType.StopAll or
                    EventType.StopDisable or EventType.StopDisableAll or EventType.EndDepreciated or
                    EventType.EndAllDepreciated)) return;
            if (Timestamp(message.GetTimestamp()) is { } timestamp)
            {
                timerEvents.Add((timestamp, !starts, timerEvents.Count));
                sourceStop = starts ? null : timestamp;
                sourceStart = starts ? timestamp : null;
                if (untimedState) breakBefore = true;
                untimedState = false;
            }
            else
            {
                stopped = !starts;
                untimedState = true;
                breakBefore = true;
                sourceStop = sourceStart = null;
            }
        };
        var decoder = new Decode();
        decoder.MesgEvent += (_, args) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            broadcaster.OnMesg(null, args);
        };
        decoder.MesgDefinitionEvent += broadcaster.OnMesgDefinition;
        if (!decoder.IsFIT(input)) throw new InvalidDataException("The original is not a FIT file.");
        input.Position = 0;
        if (!decoder.CheckIntegrity(input)) throw new InvalidDataException("The original FIT checksum is invalid.");
        input.Position = 0;
        decoder.Read(input);
        if (!match.Complete || timerEvents.Count == 0) return;
        var ordered = timerEvents.OrderBy(item => item.Timestamp).ThenBy(item => item.Sequence).ToArray();
        for (var index = 1; index < match.Points.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (match.Points[index - 1].Timestamp is not { } before || match.Points[index].Timestamp is not { } after ||
                after <= before) continue;
            var low = 0;
            var high = ordered.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (ordered[middle].Timestamp <= before) low = middle + 1;
                else high = middle;
            }
            // An edge ending at a stop still describes recorded travel; the following paused edge does not.
            if (low > 0 && ordered[low - 1].Stopped || low < ordered.Length &&
                (ordered[low].Timestamp < after || ordered[low].Timestamp == after && !ordered[low].Stopped))
                match.Breaks.Add(index);
        }
    }

    private static double? Number(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static DateTimeOffset? Timestamp(Dynastream.Fit.DateTime? value) => value is null ? null :
        new DateTimeOffset(System.DateTime.SpecifyKind(value.GetDateTime(), DateTimeKind.Utc));

    private sealed class SourceMatch(IReadOnlyList<TrackPoint> points, CancellationToken cancellationToken)
    {
        private int _count;
        public IReadOnlyList<TrackPoint> Points => points;
        public DateTimeOffset? PreviousTimestamp => _count == 0 ? null : points[_count - 1].Timestamp;
        public HashSet<int> Breaks { get; } = [];
        public bool Complete => _count == points.Count;

        public void Add(DateTimeOffset? timestamp, double? latitude, double? longitude, bool breakBefore)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_count >= points.Count) throw new SourceMismatchException();
            var point = points[_count];
            if (point.Timestamp?.UtcTicks != timestamp?.UtcTicks || point.Latitude != latitude || point.Longitude != longitude)
                throw new SourceMismatchException();
            if (_count > 0 && breakBefore) Breaks.Add(_count);
            _count++;
        }
    }

    private sealed class SourceMismatchException : Exception;

    private sealed class CancellableFileStream(string path, CancellationToken cancellationToken)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return base.Read(buffer);
        }

        public override int ReadByte()
        {
            cancellationToken.ThrowIfCancellationRequested();
            return base.ReadByte();
        }
    }
}
