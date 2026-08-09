using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.Simplify;

namespace ActivityExplorer.Infrastructure.Processing;

public static class GeometryCodec
{
    private static readonly GeometryFactory GeometryFactory =
        NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    public static byte[]? ToWkb(IReadOnlyList<TrackPoint> points, double simplifyToleranceDegrees = 0)
    {
        var coordinates = points
            .Where(x => x.Latitude.HasValue && x.Longitude.HasValue)
            .Select(x => new CoordinateZ(x.Longitude!.Value, x.Latitude!.Value, x.ElevationMeters ?? double.NaN))
            .ToArray();

        if (coordinates.Length < 2)
        {
            return null;
        }

        Geometry geometry = GeometryFactory.CreateLineString(coordinates);
        if (simplifyToleranceDegrees > 0)
        {
            geometry = DouglasPeuckerSimplifier.Simplify(geometry, simplifyToleranceDegrees);
        }

        return new WKBWriter(ByteOrder.LittleEndian, handleSRID: true, emitZ: true).Write(geometry);
    }

    public static IReadOnlyList<TrackPoint> FromWkb(byte[]? wkb)
    {
        if (wkb is null || wkb.Length == 0)
        {
            return [];
        }

        var geometry = new WKBReader(NtsGeometryServices.Instance).Read(wkb);
        return geometry.Coordinates
            .Select(x => new TrackPoint(null, x.Y, x.X, null, double.IsNaN(x.Z) ? null : x.Z, null, null, null, null, null))
            .ToArray();
    }

    public static (double? MinLat, double? MinLon, double? MaxLat, double? MaxLon) Bounds(IReadOnlyList<TrackPoint> points)
    {
        var gps = points.Where(x => x.Latitude.HasValue && x.Longitude.HasValue).ToArray();
        if (gps.Length == 0)
        {
            return (null, null, null, null);
        }

        var longitudes = gps.Select(x => x.Longitude!.Value).Distinct().Order().ToArray();
        var largestGap = double.NegativeInfinity;
        var minLongitude = longitudes[0];
        var maxLongitude = longitudes[0];
        for (var index = 0; index < longitudes.Length; index++)
        {
            var current = longitudes[index];
            var next = index == longitudes.Length - 1 ? longitudes[0] + 360d : longitudes[index + 1];
            var gap = next - current;
            if (gap <= largestGap) continue;
            largestGap = gap;
            minLongitude = next > 180d ? next - 360d : next;
            maxLongitude = current;
        }

        return (gps.Min(x => x.Latitude), minLongitude, gps.Max(x => x.Latitude), maxLongitude);
    }

    public static double DistanceMeters(IReadOnlyList<TrackPoint> points)
        => new TrackPathAnalysis(points).TotalDistanceMeters;

    public static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
        => TrackPathAnalysis.HaversineMeters(lat1, lon1, lat2, lon2);
}
