using System.Buffers.Binary;

namespace ActivityExplorer.Infrastructure.Processing;

internal static class MapLineWkb
{
    private readonly record struct Point(double Longitude, double Latitude, double ContinuousLongitude)
    {
        public double[] Coordinates() => [Longitude, Latitude];
    }

    // Read a bounded sample directly from the WKB coordinate sequence. This display-only reader
    // accepts standard, ISO dimensional, and extended lines without allocating the original geometry.
    internal static double[][] Read(Stream stream, int limit, CancellationToken cancellationToken)
    {
        if (limit < 2 || stream.Length < 9) return [];
        Span<byte> header = stackalloc byte[5];
        stream.ReadExactly(header);
        if (header[0] > 1) return [];
        var littleEndian = header[0] == 1;
        var type = ReadUInt32(header[1..], littleEndian);
        var lineType = type & 0x1FFFFFFF;
        if (lineType is not (2 or 1002 or 2002 or 3002)) return [];
        var dimensions = lineType switch
        {
            3002 => 4,
            1002 or 2002 => 3,
            _ => 2 + ((type & 0x80000000) != 0 ? 1 : 0) + ((type & 0x40000000) != 0 ? 1 : 0)
        };
        var offset = (type & 0x20000000) != 0 ? 13 : 9;
        if (stream.Length < offset) return [];
        stream.Position = offset - 4;
        stream.ReadExactly(header[..4]);
        var count = ReadUInt32(header[..4], littleEndian);
        var stride = dimensions * sizeof(double);
        if (count < 2 || stream.Length != offset + (long)count * stride) return [];

        var sample = new Point[(int)Math.Min(count, 3L * limit)];
        Span<byte> coordinate = stackalloc byte[16];
        for (var index = 0; index < sample.Length; index++)
        {
            if (index % 32 == 0) cancellationToken.ThrowIfCancellationRequested();
            var sourceIndex = (long)index * (count - 1) / (sample.Length - 1);
            stream.Position = offset + sourceIndex * stride;
            stream.ReadExactly(coordinate);
            var longitude = ReadDouble(coordinate, littleEndian);
            var latitude = ReadDouble(coordinate[8..], littleEndian);
            if (!double.IsFinite(longitude) || longitude is < -180 or > 180 ||
                !double.IsFinite(latitude) || latitude is < -90 or > 90) return [];

            var continuous = index == 0 ? longitude : sample[index - 1].ContinuousLongitude +
                (longitude - sample[index - 1].Longitude + 540) % 360 - 180;
            sample[index] = new(longitude, latitude, continuous);
        }

        return sample.Length <= limit ? sample.Select(point => point.Coordinates()).ToArray() : Reduce(sample, limit);
    }

    // Largest-triangle three buckets selects representative turns in the sampled path while keeping
    // source order and both endpoints. Continuous longitude avoids treating the date line as a turn.
    private static double[][] Reduce(Point[] points, int limit)
    {
        var result = new double[limit][];
        result[0] = points[0].Coordinates();
        result[^1] = points[^1].Coordinates();
        var previous = points[0];
        var buckets = limit - 2;
        for (var bucket = 0; bucket < buckets; bucket++)
        {
            var start = 1 + bucket * (points.Length - 2) / buckets;
            var nextStart = 1 + (bucket + 1) * (points.Length - 2) / buckets;
            var nextEnd = Math.Min(points.Length, 1 + (bucket + 2) * (points.Length - 2) / buckets);
            var meanLongitude = 0d;
            var meanLatitude = 0d;
            for (var index = nextStart; index < nextEnd; index++)
            {
                meanLongitude += points[index].ContinuousLongitude;
                meanLatitude += points[index].Latitude;
            }
            meanLongitude /= nextEnd - nextStart;
            meanLatitude /= nextEnd - nextStart;
            var best = start;
            var largest = -1d;
            for (var index = start; index < nextStart; index++)
            {
                var candidate = points[index];
                var area = Math.Abs((candidate.ContinuousLongitude - previous.ContinuousLongitude) * (meanLatitude - previous.Latitude) -
                    (meanLongitude - previous.ContinuousLongitude) * (candidate.Latitude - previous.Latitude));
                if (area <= largest) continue;
                largest = area;
                best = index;
            }
            previous = points[best];
            result[bucket + 1] = previous.Coordinates();
        }
        return result;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static double ReadDouble(ReadOnlySpan<byte> bytes, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadDoubleLittleEndian(bytes) : BinaryPrimitives.ReadDoubleBigEndian(bytes);
}
