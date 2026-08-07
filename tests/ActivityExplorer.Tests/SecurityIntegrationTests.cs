using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ActivityExplorer.Core.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ActivityExplorer.Tests;

public sealed class SecurityIntegrationTests
{
    private static readonly string[] MapEndpoints = ["activities", "routes", "segments"];

    [Fact]
    public async Task Responses_apply_local_security_headers_and_blank_map_csp()
    {
        await WithApplicationAsync(async (_, client) =>
        {
            using var response = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("DENY", Header(response, "X-Frame-Options"));
            Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
            Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
            Assert.Contains("frame-ancestors 'none'", Header(response, "Content-Security-Policy"), StringComparison.Ordinal);
            Assert.DoesNotContain("openfreemap.org", Header(response, "Content-Security-Policy"), StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Host_filter_rejects_non_loopback_host()
    {
        await WithApplicationAsync(async (_, client) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.Host = "activity.example";
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        });
    }

    [Fact]
    public async Task Upload_rejects_missing_and_invalid_antiforgery_tokens()
    {
        await WithApplicationAsync(async (_, client) =>
        {
            using (var missing = ImportRequest("missing"))
            using (var response = await client.SendAsync(missing))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            using var invalid = ImportRequest("invalid");
            invalid.Headers.Add("X-CSRF-TOKEN", "not-a-token");
            invalid.Headers.Add("Origin", "https://activity.example");
            using var invalidResponse = await client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        });
    }

    [Fact]
    public async Task Valid_antiforgery_token_allows_streamed_import_and_token_is_not_cached()
    {
        await WithApplicationAsync(async (factory, client) =>
        {
            var ownerId = await factory.Services.GetRequiredService<IProfileService>().CreateAsync("Uploader");
            var token = await GetTokenAsync(client);

            using var request = ImportRequest("valid", ownerId);
            request.Headers.Add("X-CSRF-TOKEN", token);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        });
    }

    [Fact]
    public async Task Map_endpoints_accept_normalized_bounds_and_reject_invalid_bounds_with_400()
    {
        await WithApplicationAsync(async (_, client) =>
        {
            foreach (var endpoint in MapEndpoints)
            {
                using var valid = await client.GetAsync(
                    $"/internal/map/{endpoint}?west=170&south=-10&east=-170&north=10&zoom=3");
                Assert.Equal(HttpStatusCode.OK, valid.StatusCode);

                using var invalid = await client.GetAsync(
                    $"/internal/map/{endpoint}?west=-181&south=-10&east=20&north=10&zoom=3");
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                Assert.Contains("invalid latitude or longitude range", await invalid.Content.ReadAsStringAsync(),
                    StringComparison.OrdinalIgnoreCase);
            }
        });
    }

    [Fact]
    public async Task Antiforgery_token_uses_framework_cache_headers_without_override_warning()
    {
        var recorder = new RecordingLoggerProvider();
        await WithApplicationAsync(
            async (_, client) => await GetTokenAsync(client),
            configureBuilder: builder => builder.ConfigureLogging(logging => logging.AddProvider(recorder)));

        Assert.DoesNotContain(recorder.Entries, entry =>
            entry.Category == "Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery" &&
            entry.EventId == 8 && entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Streamed_route_upload_returns_413_and_cleans_partial_staging()
    {
        await WithApplicationAsync(async (factory, client) =>
        {
            var ownerId = await factory.Services.GetRequiredService<IProfileService>().CreateAsync("Router");
            var token = await GetTokenAsync(client);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/internal/routes/import?ownerId={ownerId}&sport=Running&name=Large");
            request.Headers.Add("X-Activity-Explorer", "1");
            request.Headers.Add("X-CSRF-TOKEN", token);
            request.Content = FileContent("large.gpx", new byte[256]);

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

            var root = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_DATA")!;
            var staging = Path.Combine(root, "staging");
            Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
        }, configuration: new Dictionary<string, string?> { ["Routes:MaxGpxUploadBytes"] = "32" });
    }

    private static HttpRequestMessage ImportRequest(string payload, Guid? ownerId = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/internal/imports?ownerId={ownerId ?? Guid.NewGuid()}");
        request.Headers.Add("X-Activity-Explorer", "1");
        request.Content = FileContent("activity.gpx", Encoding.UTF8.GetBytes(TestSupport.Gpx()));
        return request;
    }

    private static MultipartFormDataContent FileContent(string fileName, byte[] bytes)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", fileName);
        return content;
    }

    private static async Task<string> GetTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/internal/antiforgery/token");
        response.EnsureSuccessStatusCode();
        Assert.Contains("no-store", Header(response, "Cache-Control"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-cache", Header(response, "Cache-Control"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-cache", Header(response, "Pragma"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("token").GetString()!;
    }

    private static string Header(HttpResponseMessage response, string name) =>
        string.Join(",", response.Headers.GetValues(name));

    private static async Task WithApplicationAsync(
        Func<WebApplicationFactory<Program>, HttpClient, Task> test,
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IWebHostBuilder>? configureBuilder = null)
    {
        var dataRoot = TestSupport.NewDirectory();
        var previousDataRoot = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_DATA");
        Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", dataRoot);
        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Production");
                    if (configuration is not null) builder.UseSetting("Routes:MaxGpxUploadBytes", configuration["Routes:MaxGpxUploadBytes"]);
                    configureBuilder?.Invoke(builder);
                });
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await test(factory, client);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", previousDataRoot);
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<RecordedLog> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(string category, ConcurrentQueue<RecordedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new RecordedLog(category, eventId.Id, logLevel));
    }

    private sealed record RecordedLog(string Category, int EventId, LogLevel Level);
}
