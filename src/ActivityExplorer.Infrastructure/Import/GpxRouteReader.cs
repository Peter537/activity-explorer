using System.Globalization;
using System.Xml;
using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Infrastructure.Import;

public sealed class GpxRouteReader
{
    private const string MultiplePaths = "The GPX file must contain exactly one directional path: one route or one track with one track segment.";

    public Task<IReadOnlyList<TrackPoint>> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        ReadPathAsync(path, 1_000_000, 512L * 1024 * 1024, cancellationToken);

    internal static async Task<IReadOnlyList<TrackPoint>> ReadPathAsync(
        string path, int maximumPoints, long maximumCharacters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = maximumCharacters
        };

        var result = new List<TrackPoint>();
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = XmlReader.Create(input, settings);
        if (await reader.MoveToContentAsync() != XmlNodeType.Element || reader.LocalName != "gpx")
            throw new InvalidDataException("The file must have a GPX document root.");

        var gpxNamespace = reader.NamespaceURI;
        var hasPath = false;
        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsChild(reader, gpxNamespace) || reader.LocalName is not ("rte" or "trk")) continue;
            if (hasPath) throw new InvalidDataException(MultiplePaths);
            hasPath = true;

            var isRoute = reader.LocalName == "rte";
            using var pathReader = reader.ReadSubtree();
            if (isRoute)
                await ReadPointsAsync(pathReader, gpxNamespace, "rtept", result, maximumPoints, cancellationToken);
            else
            {
                var hasSegment = false;
                while (await pathReader.ReadAsync())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsChild(pathReader, gpxNamespace) || pathReader.LocalName != "trkseg") continue;
                    if (hasSegment) throw new InvalidDataException(MultiplePaths);
                    hasSegment = true;
                    using var segmentReader = pathReader.ReadSubtree();
                    await ReadPointsAsync(segmentReader, gpxNamespace, "trkpt", result, maximumPoints, cancellationToken);
                }
                if (!hasSegment) throw new InvalidDataException(MultiplePaths);
            }
        }

        if (!hasPath) throw new InvalidDataException(MultiplePaths);
        if (result.Count < 2) throw new InvalidDataException("The GPX path needs at least two map points.");
        return result;
    }

    private static async Task ReadPointsAsync(
        XmlReader reader, string gpxNamespace, string pointName, List<TrackPoint> result, int maximumPoints,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsChild(reader, gpxNamespace) || reader.LocalName != pointName) continue;
            if (result.Count >= maximumPoints)
                throw new InvalidDataException($"The GPX path exceeds the {maximumPoints:N0}-point limit.");

            if (!TryFiniteNumber(reader.GetAttribute("lat"), out var latitude) ||
                !TryFiniteNumber(reader.GetAttribute("lon"), out var longitude) ||
                latitude is < -90 or > 90 || longitude is < -180 or > 180)
                throw new InvalidDataException("The GPX path contains an invalid coordinate.");

            double? elevation = null;
            using var pointReader = reader.ReadSubtree();
            while (await pointReader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (elevation.HasValue || !IsChild(pointReader, gpxNamespace) || pointReader.LocalName != "ele") continue;
                var text = await pointReader.ReadElementContentAsStringAsync();
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (!TryFiniteNumber(text, out var value))
                    throw new InvalidDataException("The GPX path contains an invalid elevation.");
                elevation = value;
            }
            result.Add(new TrackPoint(null, latitude, longitude, null, elevation, null, null, null, null, null));
        }
    }

    private static bool IsChild(XmlReader reader, string gpxNamespace) =>
        reader.NodeType == XmlNodeType.Element && reader.Depth == 1 && reader.NamespaceURI == gpxNamespace;

    private static bool TryFiniteNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}
