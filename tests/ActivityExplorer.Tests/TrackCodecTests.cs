using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Processing;

namespace ActivityExplorer.Tests;

public sealed class TrackCodecTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Every_sample_field_and_encoded_json_remain_compatible_with_the_web_contract()
    {
        TrackPoint[] points =
        [
            new(new(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)), 55.25, 12.5, 1_234.5, -4.5, 5.25, 142, 87, 312.5, -2.5, 18.25),
            new(null, null, null, null, null, null, null, null, null, null, null)
        ];
        var payload = TrackCodec.Encode(points);
        using var input = new MemoryStream(payload);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var json = new MemoryStream();
        brotli.CopyTo(json);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(points, WebOptions), json.ToArray());
        Assert.Equal(points, TrackCodec.Decode(payload));
        Assert.Equal(points, await TrackCodec.DecodeAsync(payload));
    }

    [Theory]
    [InlineData("[{}]")]
    [InlineData("[{\"timestamp\":null,\"latitude\":null,\"longitude\":null,\"distanceMeters\":null,\"elevationMeters\":null,\"speedMetersPerSecond\":null,\"heartRate\":null,\"cadence\":null,\"powerWatts\":null,\"temperatureCelsius\":null,\"respirationRate\":null}]")]
    [InlineData("[{\"TIMESTAMP\":\"2026-01-02T03:04:05+02:30\",\"LATITUDE\":\"55.25\",\"Longitude\":\"12.5\",\"distanceMeters\":\"1.25e3\",\"heartRate\":\"142\",\"unknown\":{\"nested\":[1,2]}}]")]
    [InlineData("[{\"latitude\":1,\"Latitude\":2,\"latitude\":null,\"powerWatts\":230}]")]
    [InlineData("[{\"\\u0074ime\\u0073tamp\":\"2026-01-02T03:04:05+02:30\",\"\\u006catitude\":55,\"long\\u0069tude\":12,\"unknown\":{\"latitude\":999,\"nested\":[{\"timestamp\":\"ignored\"}]}}]")]
    [InlineData("[{\"respirationRate\":18,\"temperatureCelsius\":15,\"powerWatts\":300,\"cadence\":80,\"heartRate\":140,\"speedMetersPerSecond\":4,\"elevationMeters\":12,\"distanceMeters\":50,\"longitude\":12,\"latitude\":55,\"timestamp\":\"2026-01-02T03:04:05Z\",\"LATITUDE\":56,\"TIMESTAMP\":null,\"PowerWatts\":null}]")]
    [InlineData("[{\"latitude\":\"NaN\",\"longitude\":\"Infinity\",\"distanceMeters\":\"-Infinity\",\"elevationMeters\":\"-0\",\"speedMetersPerSecond\":\"1.25e2\"}]")]
    [InlineData("[{\"latitude\":-0,\"longitude\":1.2345678901234567,\"distanceMeters\":1e400,\"elevationMeters\":-1e400,\"respirationRate\":5e-324}]")]
    [InlineData("[null,{}]")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task Missing_null_case_insensitive_and_string_number_values_match_the_existing_decoder(string json)
    {
        var expected = JsonSerializer.Deserialize<List<TrackPoint>>(json, WebOptions) ?? [];
        var payload = Compress(json);
        Assert.Equal(expected, TrackCodec.Decode(payload));
        Assert.Equal(expected, await TrackCodec.DecodeAsync(payload));
    }

    [Theory]
    [InlineData("[{\"latitude\":\"not a number\"}]")]
    [InlineData("[{\"timestamp\":\"not a timestamp\"}]")]
    [InlineData("{\"latitude\":1}")]
    [InlineData("[{\"powerWatts\":true}]")]
    [InlineData("[{\"cadence\":1}")]
    [InlineData("[0]")]
    [InlineData("[true]")]
    [InlineData("[[]]")]
    [InlineData("[{\"latitude\":{}}]")]
    [InlineData("[{\"latitude\":[1]}]")]
    [InlineData("[{\"timestamp\":7}]")]
    [InlineData("[{\"timestamp\":false}]")]
    [InlineData("[{}]{}")]
    [InlineData("")]
    public async Task Invalid_sample_json_keeps_the_existing_failure_behavior(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<List<TrackPoint>>(json, WebOptions));
        var payload = Compress(json);
        Assert.Throws<JsonException>(() => TrackCodec.Decode(payload));
        await Assert.ThrowsAsync<JsonException>(() => TrackCodec.DecodeAsync(payload).AsTask());
    }

    [Fact]
    public async Task Empty_payloads_corrupt_compression_and_precancellation_keep_the_existing_behavior()
    {
        Assert.Empty(TrackCodec.Decode([]));
        Assert.Empty(await TrackCodec.DecodeAsync([]));
        Assert.ThrowsAny<Exception>(() => TrackCodec.Decode([0, 1, 2]));
        await Assert.ThrowsAnyAsync<Exception>(() => TrackCodec.DecodeAsync(new byte[] { 0, 1, 2 }).AsTask());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TrackCodec.DecodeAsync([], cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TrackCodec.DecodeAsync(Compress("[{}]"), cancellation.Token).AsTask());
    }

    [Fact]
    public async Task Samples_and_escaped_properties_crossing_stream_buffers_match_the_web_decoder()
    {
        var json = "[{\"unknown\":" + JsonSerializer.Serialize(new string('x', 20_000)) + ",\"\\u006catitude\":1}," +
            string.Join(',', Enumerable.Repeat("{\"latitude\":-0,\"longitude\":\"12.5\",\"unknown\":[{\"nested\":null}]}", 512)) + "]";
        var expected = JsonSerializer.Deserialize<List<TrackPoint>>(json, WebOptions)!;
        var payload = Compress(json);
        Assert.Equal(expected, TrackCodec.Decode(payload));
        Assert.Equal(expected, await TrackCodec.DecodeAsync(payload));
        using var source = new SmallCompressedReads(payload);
        var actual = await TrackCodec.DecodeAsync(source, default);
        Assert.Equal(expected, actual);
        Assert.True(source.Reads > 1);
        Assert.True(source.CanRead);
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected[1].Latitude!.Value), BitConverter.DoubleToInt64Bits(actual[1].Latitude!.Value));
    }

    [Fact]
    public void Decoder_avoids_the_per_sample_constructor_argument_allocations()
    {
        var timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var points = Enumerable.Range(0, 2_048).Select(index => new TrackPoint(timestamp.AddSeconds(index),
            55.25, 12.5, index * 5.25, -4.5, 5.25, 142, 87, 312.5, -2.5, 18.25)).ToArray();
        var payload = TrackCodec.Encode(points);
        DefaultDecode(payload);
        TrackCodec.Decode(payload);
        var baseline = Allocated(() => DefaultDecode(payload));
        var optimized = Allocated(() => TrackCodec.Decode(payload));
        TestContext.Current.TestOutputHelper!.WriteLine($"2,048 points: default decoder {baseline:N0} allocated bytes; codec {optimized:N0} bytes.");
        Assert.True(optimized < baseline * 0.8, $"Codec allocated {optimized:N0} bytes versus the default decoder's {baseline:N0} bytes.");
    }

    private static byte[] Compress(string json)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            brotli.Write(Encoding.UTF8.GetBytes(json));
        return output.ToArray();
    }

    private static IReadOnlyList<TrackPoint> DefaultDecode(byte[] payload)
    {
        using var input = new MemoryStream(payload);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<List<TrackPoint>>(brotli, WebOptions) ?? [];
    }

    private static long Allocated(Func<IReadOnlyList<TrackPoint>> decode)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var points = decode();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(points);
        return allocated;
    }

    private sealed class SmallCompressedReads(byte[] payload) : MemoryStream(payload)
    {
        public int Reads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return ValueTask.FromResult(base.Read(buffer.Span[..Math.Min(buffer.Length, 7)]));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
