using System.IO.Compression;
using System.Text.Json;
using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Infrastructure.Processing;

public static class TrackCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] Encode(IReadOnlyList<TrackPoint> points)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            JsonSerializer.Serialize(brotli, points, Options);
        }

        return output.ToArray();
    }

    public static IReadOnlyList<TrackPoint> Decode(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return [];
        }

        using var input = new MemoryStream(payload);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<List<TrackPoint>>(brotli, Options) ?? [];
    }
}
