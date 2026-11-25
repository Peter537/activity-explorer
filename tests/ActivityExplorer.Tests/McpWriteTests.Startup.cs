using System.Text.Json;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;

namespace ActivityExplorer.Tests;

public sealed partial class McpWriteTests
{
    [Fact]
    public async Task Bridge_publishes_credentials_before_the_loopback_host_is_ready()
    {
        await using var setup = await Setup.CreateAsync();
        await using var input = File.OpenRead(setup.Writes.ConnectionFile);
        var connection = await JsonSerializer.DeserializeAsync<McpConnection>(input, McpChangeJson.Options);
        Assert.NotNull(connection);
        Assert.True(new Uri(connection.Endpoint).IsLoopback);
        Assert.Equal(setup.Writes.Token, connection.Token);
        Assert.NotEmpty(connection.Token);
    }

    [Fact]
    public async Task Bridge_configuration_with_a_non_loopback_listener_fails_host_startup()
    {
        var error = await Record.ExceptionAsync(async () =>
        {
            await using var setup = await Setup.CreateAsync(configure: builder =>
                builder.UseSetting("Urls", "http://0.0.0.0:8342"));
        });
        Assert.NotNull(error);
        Assert.Contains("All Web listeners must be loopback-only", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bridge_credential_publication_failure_fails_host_startup()
    {
        var error = await Record.ExceptionAsync(async () =>
        {
            await using var setup = await Setup.CreateAsync(configure: _ =>
                Directory.CreateDirectory(Path.Combine(new AppDataPaths().Root, "mcp-connection.json")));
        });
        Assert.NotNull(error);
        Assert.Contains("mcp-connection.json", error.ToString(), StringComparison.Ordinal);
    }
}
