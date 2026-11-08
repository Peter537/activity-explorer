using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Infrastructure.Processing;

public static class TrackCodec
{
    private static readonly JsonSerializerOptions LegacyWebOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions Options = new(LegacyWebOptions)
    {
        Converters = { new TrackPointConverter() }
    };

    public static string Fingerprint(ActivityStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return string.Create(CultureInfo.InvariantCulture, $"v{stream.SchemaVersion}-{Convert.ToHexString(SHA256.HashData(stream.CompressedPayload))}");
    }

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

    public static async ValueTask<IReadOnlyList<TrackPoint>> DecodeAsync(byte[] payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (payload.Length == 0) return [];
        using var input = new MemoryStream(payload);
        return await DecodeAsync(input, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<IReadOnlyList<TrackPoint>> DecodeAsync(Stream input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true);
        return await JsonSerializer.DeserializeAsync<List<TrackPoint>>(brotli, Options, cancellationToken).ConfigureAwait(false) ?? [];
    }

    private sealed class TrackPointConverter : JsonConverter<TrackPoint>
    {
        private enum Field { Unknown, Timestamp, Latitude, Longitude, Distance, Elevation, Speed, HeartRate, Cadence, Power, Temperature, Respiration }
        private static readonly Dictionary<string, Field> Fields = new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(TrackPoint.Timestamp)] = Field.Timestamp,
            [nameof(TrackPoint.Latitude)] = Field.Latitude,
            [nameof(TrackPoint.Longitude)] = Field.Longitude,
            [nameof(TrackPoint.DistanceMeters)] = Field.Distance,
            [nameof(TrackPoint.ElevationMeters)] = Field.Elevation,
            [nameof(TrackPoint.SpeedMetersPerSecond)] = Field.Speed,
            [nameof(TrackPoint.HeartRate)] = Field.HeartRate,
            [nameof(TrackPoint.Cadence)] = Field.Cadence,
            [nameof(TrackPoint.PowerWatts)] = Field.Power,
            [nameof(TrackPoint.TemperatureCelsius)] = Field.Temperature,
            [nameof(TrackPoint.RespirationRate)] = Field.Respiration
        };

        public override TrackPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
            DateTimeOffset? timestamp = null;
            double? latitude = null, longitude = null, distance = null, elevation = null, speed = null,
                heartRate = null, cadence = null, power = null, temperature = null, respiration = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return new(timestamp, latitude, longitude, distance, elevation, speed, heartRate, cadence, power, temperature, respiration);
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
                var field = ReadField(ref reader);
                if (!reader.Read()) throw new JsonException();
                switch (field)
                {
                    case Field.Timestamp:
                        timestamp = reader.TokenType switch
                        {
                            JsonTokenType.Null => null,
                            JsonTokenType.String => reader.GetDateTimeOffset(),
                            _ => JsonSerializer.Deserialize<DateTimeOffset?>(ref reader, LegacyWebOptions)
                        };
                        break;
                    case Field.Latitude: latitude = ReadNumber(ref reader); break;
                    case Field.Longitude: longitude = ReadNumber(ref reader); break;
                    case Field.Distance: distance = ReadNumber(ref reader); break;
                    case Field.Elevation: elevation = ReadNumber(ref reader); break;
                    case Field.Speed: speed = ReadNumber(ref reader); break;
                    case Field.HeartRate: heartRate = ReadNumber(ref reader); break;
                    case Field.Cadence: cadence = ReadNumber(ref reader); break;
                    case Field.Power: power = ReadNumber(ref reader); break;
                    case Field.Temperature: temperature = ReadNumber(ref reader); break;
                    case Field.Respiration: respiration = ReadNumber(ref reader); break;
                    default:
                        if (!reader.TrySkip()) throw new JsonException("Incomplete track-point property value.");
                        break;
                }
            }
            throw new JsonException();
        }

        public override void Write(Utf8JsonWriter writer, TrackPoint value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, LegacyWebOptions);

        private static double? ReadNumber(ref Utf8JsonReader reader) => reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetDouble(),
            JsonTokenType.Null => null,
            _ => JsonSerializer.Deserialize<double?>(ref reader, LegacyWebOptions)
        };

        private static Field ReadField(ref Utf8JsonReader reader)
        {
            if (!reader.HasValueSequence && !reader.ValueSpan.IsEmpty)
            {
                var field = reader.ValueSpan[0] switch
                {
                    (byte)'t' when reader.ValueTextEquals("timestamp"u8) => Field.Timestamp,
                    (byte)'t' when reader.ValueTextEquals("temperatureCelsius"u8) => Field.Temperature,
                    (byte)'l' when reader.ValueTextEquals("latitude"u8) => Field.Latitude,
                    (byte)'l' when reader.ValueTextEquals("longitude"u8) => Field.Longitude,
                    (byte)'d' when reader.ValueTextEquals("distanceMeters"u8) => Field.Distance,
                    (byte)'e' when reader.ValueTextEquals("elevationMeters"u8) => Field.Elevation,
                    (byte)'s' when reader.ValueTextEquals("speedMetersPerSecond"u8) => Field.Speed,
                    (byte)'h' when reader.ValueTextEquals("heartRate"u8) => Field.HeartRate,
                    (byte)'c' when reader.ValueTextEquals("cadence"u8) => Field.Cadence,
                    (byte)'p' when reader.ValueTextEquals("powerWatts"u8) => Field.Power,
                    (byte)'r' when reader.ValueTextEquals("respirationRate"u8) => Field.Respiration,
                    _ => Field.Unknown
                };
                if (field != Field.Unknown) return field;
            }
            // Noncanonical casing and escaped names retain the Web contract's ordinal case-insensitive matching.
            return Fields.GetValueOrDefault(reader.GetString()!);
        }
    }
}
