using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using ActivityExplorer.Core.Domain;
using Dynastream.Fit;

namespace ActivityExplorer.Infrastructure.Import;

public sealed record SegmentPathData(IReadOnlyList<TrackPoint> Points, string Format);

public interface ISegmentPathReader
{
    Task<SegmentPathData> ReadAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads user-supplied path geometry without retaining provider identifiers, leaderboard data, or original files.
/// </summary>
public sealed class SegmentPathReader : ISegmentPathReader
{
    private const int MaximumPoints = 250_000;
    private const double SemicirclesToDegrees = 180d / 2_147_483_648d;

    public async Task<SegmentPathData> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".gpx" => new SegmentPathData(ValidateSinglePath(ParseGpx(await ReadXmlAsync(path, cancellationToken)), "GPX"), "GPX"),
            ".tcx" => new SegmentPathData(ValidateSinglePath(ParseTcx(await ReadXmlAsync(path, cancellationToken)), "TCX"), "TCX"),
            ".kml" => new SegmentPathData(ValidateSinglePath(ParseKml(await ReadXmlAsync(path, cancellationToken)), "KML"), "KML"),
            ".geojson" or ".json" => new SegmentPathData(
                ValidateSinglePath(await ParseGeoJsonAsync(path, cancellationToken), "GeoJSON"), "GEOJSON"),
            ".fit" => ParseFit(path, cancellationToken),
            _ => throw new InvalidDataException("Supported segment path files are GPX, FIT segment/course, TCX, KML, and GeoJSON.")
        };
    }

    private static async Task<XDocument> ReadXmlAsync(string path, CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = 64L * 1024 * 1024
        };
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = XmlReader.Create(stream, settings);
        return await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
    }

    private static IReadOnlyList<IReadOnlyList<TrackPoint>> ParseGpx(XDocument document)
    {
        var tracks = document.Descendants()
            .Where(element => Is(element, "trkseg"))
            .Select(segment => (IReadOnlyList<TrackPoint>)segment.Elements()
                .Where(element => Is(element, "trkpt"))
                .Select(ParseGpxPoint)
                .ToArray())
            .Where(points => points.Count > 0)
            .ToArray();
        if (tracks.Length > 0) return tracks;

        return document.Descendants()
            .Where(element => Is(element, "rte"))
            .Select(route => (IReadOnlyList<TrackPoint>)route.Elements()
                .Where(element => Is(element, "rtept"))
                .Select(ParseGpxPoint)
                .ToArray())
            .Where(points => points.Count > 0)
            .ToArray();
    }

    private static TrackPoint ParseGpxPoint(XElement element) => new(
        null,
        Number(element.Attribute("lat")?.Value, "GPX latitude"),
        Number(element.Attribute("lon")?.Value, "GPX longitude"),
        null,
        OptionalNumber(element.Elements().FirstOrDefault(child => Is(child, "ele"))?.Value, "GPX elevation"),
        null, null, null, null, null);

    private static IReadOnlyList<IReadOnlyList<TrackPoint>> ParseTcx(XDocument document)
    {
        var containers = document.Descendants()
            .Where(element => Is(element, "Course") || Is(element, "Activity"))
            .ToArray();
        if (containers.Length != 1)
            throw new InvalidDataException("The TCX file must contain exactly one course or activity path.");

        var points = containers[0].Descendants()
            .Where(element => Is(element, "Trackpoint"))
            .Select(ParseTcxPoint)
            .Where(point => point is not null)
            .Cast<TrackPoint>()
            .ToArray();
        return [points];
    }

    private static TrackPoint? ParseTcxPoint(XElement element)
    {
        var position = element.Elements().FirstOrDefault(child => Is(child, "Position"));
        if (position is null) return null;
        var latitude = position.Elements().FirstOrDefault(child => Is(child, "LatitudeDegrees"))?.Value;
        var longitude = position.Elements().FirstOrDefault(child => Is(child, "LongitudeDegrees"))?.Value;
        if (string.IsNullOrWhiteSpace(latitude) || string.IsNullOrWhiteSpace(longitude)) return null;
        return new TrackPoint(
            null,
            Number(latitude, "TCX latitude"),
            Number(longitude, "TCX longitude"),
            OptionalNumber(element.Elements().FirstOrDefault(child => Is(child, "DistanceMeters"))?.Value, "TCX distance"),
            OptionalNumber(element.Elements().FirstOrDefault(child => Is(child, "AltitudeMeters"))?.Value, "TCX elevation"),
            null, null, null, null, null);
    }

    private static IReadOnlyList<IReadOnlyList<TrackPoint>> ParseKml(XDocument document) =>
        document.Descendants()
            .Where(element => Is(element, "LineString"))
            .Select(line => (IReadOnlyList<TrackPoint>)ParseKmlCoordinates(
                line.Elements().FirstOrDefault(child => Is(child, "coordinates"))?.Value))
            .Where(points => points.Count > 0)
            .ToArray();

    private static IReadOnlyList<TrackPoint> ParseKmlCoordinates(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var result = new List<TrackPoint>();
        foreach (var tuple in text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var values = tuple.Split(',');
            if (values.Length < 2) throw new InvalidDataException("A KML coordinate is incomplete.");
            result.Add(new TrackPoint(
                null,
                Number(values[1], "KML latitude"),
                Number(values[0], "KML longitude"),
                null,
                values.Length > 2 ? OptionalNumber(values[2], "KML elevation") : null,
                null, null, null, null, null));
        }
        return result;
    }

    private static async Task<IReadOnlyList<IReadOnlyList<TrackPoint>>> ParseGeoJsonAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(
            stream,
            new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 },
            cancellationToken);
        var paths = new List<IReadOnlyList<TrackPoint>>();
        AddGeoJsonPaths(document.RootElement, paths);
        return paths;
    }

    private static void AddGeoJsonPaths(JsonElement value, ICollection<IReadOnlyList<TrackPoint>> paths)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("type", out var typeProperty) ||
            typeProperty.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The GeoJSON object has no valid type.");

        switch (typeProperty.GetString())
        {
            case "Feature":
                if (!value.TryGetProperty("geometry", out var geometry) || geometry.ValueKind == JsonValueKind.Null)
                    throw new InvalidDataException("The GeoJSON feature has no path geometry.");
                AddGeoJsonPaths(geometry, paths);
                break;
            case "FeatureCollection":
                if (!value.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("The GeoJSON feature collection is invalid.");
                foreach (var feature in features.EnumerateArray()) AddGeoJsonPaths(feature, paths);
                break;
            case "LineString":
                if (!value.TryGetProperty("coordinates", out var line))
                    throw new InvalidDataException("The GeoJSON line has no coordinates.");
                paths.Add(ParseGeoJsonLine(line));
                break;
            case "MultiLineString":
                if (!value.TryGetProperty("coordinates", out var lines) || lines.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("The GeoJSON multiline path is invalid.");
                foreach (var child in lines.EnumerateArray()) paths.Add(ParseGeoJsonLine(child));
                break;
            default:
                throw new InvalidDataException("GeoJSON segment imports require LineString geometry.");
        }
    }

    private static IReadOnlyList<TrackPoint> ParseGeoJsonLine(JsonElement coordinates)
    {
        if (coordinates.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The GeoJSON line coordinates are invalid.");
        var points = new List<TrackPoint>();
        foreach (var coordinate in coordinates.EnumerateArray())
        {
            if (coordinate.ValueKind != JsonValueKind.Array || coordinate.GetArrayLength() < 2)
                throw new InvalidDataException("A GeoJSON coordinate is incomplete.");
            var values = coordinate.EnumerateArray().ToArray();
            points.Add(new TrackPoint(
                null,
                JsonNumber(values[1], "GeoJSON latitude"),
                JsonNumber(values[0], "GeoJSON longitude"),
                null,
                values.Length > 2 && values[2].ValueKind != JsonValueKind.Null
                    ? JsonNumber(values[2], "GeoJSON elevation")
                    : null,
                null, null, null, null, null));
        }
        return points;
    }

    private static SegmentPathData ParseFit(string path, CancellationToken cancellationToken)
    {
        var segmentPoints = new List<(ushort Index, int Order, TrackPoint Point)>();
        var coursePoints = new List<TrackPoint>();
        Dynastream.Fit.File? fileType = null;
        var segmentIds = 0;

        var broadcaster = new MesgBroadcaster();
        broadcaster.FileIdMesgEvent += (_, args) => fileType = new FileIdMesg(args.mesg).GetType();
        broadcaster.SegmentIdMesgEvent += (_, _) => segmentIds++;
        broadcaster.SegmentPointMesgEvent += (_, args) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = new SegmentPointMesg(args.mesg);
            var point = FitPoint(
                message.GetPositionLat(), message.GetPositionLong(), message.GetDistance(),
                message.GetEnhancedAltitude() ?? message.GetAltitude());
            if (point is not null)
                segmentPoints.Add((message.GetMessageIndex() ?? (ushort)segmentPoints.Count, segmentPoints.Count, point));
        };
        broadcaster.RecordMesgEvent += (_, args) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = new RecordMesg(args.mesg);
            var point = FitPoint(
                message.GetPositionLat(), message.GetPositionLong(), message.GetDistance(),
                message.GetEnhancedAltitude() ?? message.GetAltitude());
            if (point is not null) coursePoints.Add(point);
        };

        var decoder = new Decode();
        decoder.MesgEvent += broadcaster.OnMesg;
        decoder.MesgDefinitionEvent += broadcaster.OnMesgDefinition;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!decoder.IsFIT(stream)) throw new InvalidDataException("The file is not a valid FIT file.");
            stream.Position = 0;
            if (!decoder.CheckIntegrity(stream)) throw new InvalidDataException("The FIT file failed its CRC integrity check.");
            stream.Position = 0;
            decoder.Read(stream);
        }
        catch (FitException exception)
        {
            throw new InvalidDataException("The FIT path could not be decoded safely.", exception);
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (fileType == Dynastream.Fit.File.Course)
            return new SegmentPathData(ValidatePath(coursePoints, "FIT course"), "FIT COURSE");
        if (fileType is not (Dynastream.Fit.File.Segment or Dynastream.Fit.File.SegmentList))
            throw new InvalidDataException("Choose a FIT segment or FIT course file, not a FIT activity file.");
        if (segmentIds > 1)
            throw new InvalidDataException("The FIT file contains multiple segment definitions. Import one directional path at a time.");

        var ordered = segmentPoints.OrderBy(item => item.Index).ThenBy(item => item.Order).Select(item => item.Point).ToArray();
        return new SegmentPathData(ValidatePath(ordered, "FIT segment"), "FIT SEGMENT");
    }

    private static TrackPoint? FitPoint(int? latitude, int? longitude, float? distance, float? elevation)
    {
        if (!latitude.HasValue || !longitude.HasValue) return null;
        return new TrackPoint(
            null,
            latitude.Value * SemicirclesToDegrees,
            longitude.Value * SemicirclesToDegrees,
            distance,
            elevation,
            null, null, null, null, null);
    }

    private static IReadOnlyList<TrackPoint> ValidateSinglePath(
        IReadOnlyList<IReadOnlyList<TrackPoint>> paths,
        string format)
    {
        var nonEmpty = paths.Where(path => path.Count > 0).ToArray();
        if (nonEmpty.Length != 1)
            throw new InvalidDataException($"The {format} file must contain exactly one directional path.");
        return ValidatePath(nonEmpty[0], format);
    }

    private static IReadOnlyList<TrackPoint> ValidatePath(IReadOnlyList<TrackPoint> points, string format)
    {
        if (points.Count < 2) throw new InvalidDataException($"The {format} path needs at least two map points.");
        if (points.Count > MaximumPoints)
            throw new InvalidDataException($"The {format} path exceeds the {MaximumPoints:N0}-point limit.");
        if (points.Any(point =>
                !point.Latitude.HasValue || !point.Longitude.HasValue ||
                !double.IsFinite(point.Latitude.Value) || point.Latitude is < -90 or > 90 ||
                !double.IsFinite(point.Longitude.Value) || point.Longitude is < -180 or > 180 ||
                point.ElevationMeters.HasValue && !double.IsFinite(point.ElevationMeters.Value)))
            throw new InvalidDataException($"The {format} path contains invalid coordinates.");
        return points;
    }

    private static bool Is(XElement element, string localName) =>
        element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase);

    private static double Number(string? text, string label)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new InvalidDataException($"{label} must be a finite number.");
        return value;
    }

    private static double? OptionalNumber(string? text, string label) =>
        string.IsNullOrWhiteSpace(text) ? null : Number(text, label);

    private static double JsonNumber(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new InvalidDataException($"{label} must be a finite number.");
        return number;
    }
}
