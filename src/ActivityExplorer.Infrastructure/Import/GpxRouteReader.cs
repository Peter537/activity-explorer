using System.Globalization;
using System.Xml;
using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Infrastructure.Import;

public sealed class GpxRouteReader
{
    private const int MaxPoints = 1_000_000;

    public async Task<IReadOnlyList<TrackPoint>> ReadAsync(
        string path, CancellationToken cancellationToken = default)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = 512L * 1024 * 1024
        };

        var result = new List<TrackPoint>();
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        using var reader = XmlReader.Create(input, settings);
        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element ||
                reader.LocalName is not ("trkpt" or "rtept"))
            {
                continue;
            }

            if (!double.TryParse(reader.GetAttribute("lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
                !double.TryParse(reader.GetAttribute("lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude) ||
                latitude is < -90 or > 90 || longitude is < -180 or > 180)
            {
                throw new InvalidDataException("The GPX path contains an invalid coordinate.");
            }

            double? elevation = null;
            using (var pointReader = reader.ReadSubtree())
            {
                while (await pointReader.ReadAsync())
                {
                    if (pointReader.NodeType == XmlNodeType.Element && pointReader.LocalName == "ele")
                    {
                        var value = await pointReader.ReadElementContentAsStringAsync();
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                            elevation = parsed;
                    }
                }
            }

            result.Add(new TrackPoint(null, latitude, longitude, null, elevation, null, null, null, null, null));
            if (result.Count > MaxPoints)
                throw new InvalidDataException($"The GPX path exceeds the {MaxPoints:N0} point safety limit.");
        }

        if (result.Count < 2) throw new InvalidDataException("The GPX file has fewer than two route or track points.");
        return result;
    }
}
