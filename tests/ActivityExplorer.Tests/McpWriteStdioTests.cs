using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Mcp;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ActivityExplorer.Tests;

public sealed class McpWriteStdioTests
{
    internal static string ServerPath => Path.GetFullPath(typeof(McpStdioTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "McpTargetPath").Value!);

    [Theory]
    [InlineData("2026-07-28", true)]
    [InlineData("2025-11-25", true)]
    [InlineData("2026-07-28", false)]
    [InlineData("2025-11-25", false)]
    public async Task Stdio_writes_require_real_protocol_confirmation_and_retry_without_duplicates(string version, bool accept)
    {
        await using var runtime = await McpWriteRuntime.StartAsync();
        var prompts = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await McpClient.CreateAsync(Transport(runtime.Fixture), new()
        {
            ProtocolVersion = version,
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = (request, _) =>
                {
                    Interlocked.Increment(ref prompts);
                    Assert.NotNull(request);
                    Assert.Contains("create tag", request.Message, StringComparison.Ordinal);
                    Assert.Contains("Protocol reviewed tag", request.Message, StringComparison.Ordinal);
                    return ValueTask.FromResult(new ElicitResult
                    {
                        Action = accept ? "accept" : "decline",
                        Content = accept ? new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(true) } : null
                    });
                }
            }
        }, cancellationToken: deadline.Token);
        var tools = await client.ListToolsAsync(new ListToolsRequestParams(), deadline.Token);
        Assert.DoesNotContain(tools.Tools, tool => tool.Name == "prepare_delete_profile");
        Assert.True(tools.Tools.Single(tool => tool.Name == "apply_prepared_change").Annotations?.DestructiveHint);
        Assert.Equal("permission_denied", McpReadOnlyTests.Error(await client.CallToolAsync("prepare_delete_profile", new Dictionary<string, object?> { ["profileId"] = runtime.Fixture.Owner }, cancellationToken: deadline.Token)));
        Assert.Contains(tools.Tools, tool => tool.Name == "prepare_create_segment_from_activity" && tool.Annotations?.ReadOnlyHint == true);
        var preparation = await client.CallToolAsync("prepare_create_tag", new Dictionary<string, object?>
        { ["profileId"] = runtime.Fixture.Owner, ["name"] = "Protocol reviewed tag" }, cancellationToken: deadline.Token);
        Assert.False(preparation.IsError, JsonSerializer.Serialize(preparation));
        var prepared = McpReadOnlyTests.Data(preparation);
        var planId = prepared.GetProperty("planId").GetGuid();
        await using (var check = await runtime.Fixture.Factory.CreateDbContextAsync())
            Assert.False(await check.Tags.AnyAsync(tag => tag.Name == "Protocol reviewed tag"));
        var application = await client.CallToolAsync("apply_prepared_change", new Dictionary<string, object?> { ["planId"] = planId }, cancellationToken: deadline.Token);
        Assert.False(application.IsError, JsonSerializer.Serialize(application));
        var applied = McpReadOnlyTests.Data(application);
        Assert.Equal(1, prompts);
        if (accept)
        {
            JsonElement status;
            do
            {
                await Task.Delay(20, deadline.Token);
                status = McpReadOnlyTests.Data(await client.CallToolAsync("get_operation_status", new Dictionary<string, object?> { ["operationId"] = planId }, cancellationToken: deadline.Token));
            } while (status.GetProperty("status").GetString() is "queued" or "running");
            Assert.Equal("completed", status.GetProperty("status").GetString());
            _ = McpReadOnlyTests.Data(await client.CallToolAsync("apply_prepared_change", new Dictionary<string, object?> { ["planId"] = planId }, cancellationToken: deadline.Token));
            Assert.Equal(1, prompts);
        }
        else Assert.Equal("declined", applied.GetProperty("status").GetString());
        await using var db = await runtime.Fixture.Factory.CreateDbContextAsync();
        Assert.Equal(accept ? 1 : 0, await db.Tags.CountAsync(tag => tag.Name == "Protocol reviewed tag"));
        Assert.Equal("ReadWrite", McpReadOnlyTests.Data(await client.CallToolAsync("get_server_status", cancellationToken: deadline.Token)).GetProperty("accessMode").GetString());
        Assert.Equal(runtime.Fixture.Activity, McpReadOnlyTests.Data(await client.CallToolAsync("get_activity", new Dictionary<string, object?> { ["profileId"] = runtime.Fixture.Owner, ["activityId"] = runtime.Fixture.Activity }, cancellationToken: deadline.Token)).GetProperty("summary").GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_without_form_elicitation_gets_a_non_mutating_web_review_fallback(bool urlOnly)
    {
        await using var runtime = await McpWriteRuntime.StartAsync();
        await using var client = await McpClient.CreateAsync(Transport(runtime.Fixture), new()
        {
            Capabilities = new() { Elicitation = urlOnly ? new() { Url = new() } : null },
            Handlers = urlOnly ? new() { ElicitationHandler = (_, _) => throw new InvalidOperationException("A URL-only client must receive the Web fallback.") } : new()
        });
        var preview = McpReadOnlyTests.Data(await client.CallToolAsync("prepare_create_tag", new Dictionary<string, object?> { ["profileId"] = runtime.Fixture.Owner, ["name"] = "Awaiting Web review" }));
        var result = McpReadOnlyTests.Data(await client.CallToolAsync("apply_prepared_change", new Dictionary<string, object?> { ["planId"] = preview.GetProperty("planId").GetGuid() }));
        Assert.Equal("awaiting_web_approval", result.GetProperty("status").GetString());
        using var http = new HttpClient();
        using var page = await http.GetAsync(result.GetProperty("reviewUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Approve and apply change", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var db = await runtime.Fixture.Factory.CreateDbContextAsync();
        Assert.False(await db.Tags.AnyAsync(tag => tag.Name == "Awaiting Web review"));
        Assert.Empty(await db.McpOperations.ToArrayAsync());
    }

    [Fact]
    public async Task Native_confirmation_releases_capacity_until_the_continuation_is_approved()
    {
        await using var runtime = await McpWriteRuntime.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var prompted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = await McpClient.CreateAsync(Transport(runtime.Fixture,
            "--Mcp:MaxConcurrentCalls", "1", "--Mcp:CapacityWaitSeconds", "1"), new()
            {
                Handlers = new McpClientHandlers
                {
                    ElicitationHandler = async (_, token) =>
                    {
                        prompted.SetResult();
                        await answer.Task.WaitAsync(token);
                        return new ElicitResult { Action = "accept", Content = new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(true) } };
                    }
                }
            }, cancellationToken: deadline.Token);
        var preview = McpReadOnlyTests.Data(await client.CallToolAsync("prepare_create_tag", new Dictionary<string, object?>
        { ["profileId"] = runtime.Fixture.Owner, ["name"] = "Confirmed after another call" }, cancellationToken: deadline.Token));
        var planId = preview.GetProperty("planId").GetGuid();
        var pending = client.CallToolAsync("apply_prepared_change", new Dictionary<string, object?> { ["planId"] = planId }, cancellationToken: deadline.Token);
        CallToolResult application;
        try
        {
            await prompted.Task.WaitAsync(deadline.Token);
            Assert.False(pending.IsCompleted);
            Assert.False((await client.CallToolAsync("get_server_status", cancellationToken: deadline.Token)).IsError);
            Assert.False((await client.CallToolAsync("get_write_context", new Dictionary<string, object?>
            { ["profileId"] = runtime.Fixture.Owner, ["kind"] = "tags" }, cancellationToken: deadline.Token)).IsError);
            await using var before = await runtime.Fixture.Factory.CreateDbContextAsync();
            Assert.False(await before.Tags.AnyAsync(tag => tag.Name == "Confirmed after another call", deadline.Token));
        }
        finally { answer.TrySetResult(); application = await pending; }
        Assert.False(application.IsError, JsonSerializer.Serialize(application));
        JsonElement status;
        do
        {
            status = McpReadOnlyTests.Data(await client.CallToolAsync("get_operation_status", new Dictionary<string, object?> { ["operationId"] = planId }, cancellationToken: deadline.Token));
            if (status.GetProperty("status").GetString() is "queued" or "running") await Task.Delay(20, deadline.Token);
        } while (status.GetProperty("status").GetString() is "queued" or "running");
        Assert.Equal("completed", status.GetProperty("status").GetString());
        await using var db = await runtime.Fixture.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.Tags.CountAsync(tag => tag.Name == "Confirmed after another call", deadline.Token));
    }

    internal static StdioClientTransport Transport(McpFixture fixture, params string[] options) => new(new()
    {
        Command = "dotnet",
        Arguments = [ServerPath, "--Mcp:AccessMode", "ReadWrite", "--Mcp:AllowedWriteProfileIds:0", fixture.Owner.ToString(), .. options],
        EnvironmentVariables = EnvironmentFor(fixture.DirectoryPath),
        InheritEnvironmentVariables = false,
        ShutdownTimeout = TimeSpan.FromSeconds(8)
    });

    private static Dictionary<string, string?> EnvironmentFor(string root)
    {
        var environment = new Dictionary<string, string?> { ["ACTIVITY_EXPLORER_DATA"] = root, ["DOTNET_NOLOGO"] = "1" };
        foreach (var name in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "DOTNET_ROOT", "USERPROFILE", "LOCALAPPDATA" })
            if (Environment.GetEnvironmentVariable(name) is { } value) environment[name] = value;
        return environment;
    }
}

internal sealed class McpWriteRuntime(McpFixture fixture, Process process, string origin) : IAsyncDisposable
{
    public McpFixture Fixture => fixture;
    public string Origin => origin;
    public static async Task<McpWriteRuntime> StartAsync()
    {
        var fixture = await McpFixture.CreateAsync();
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            await db.ImportBatches.ExecuteDeleteAsync(); await db.FileOperations.ExecuteDeleteAsync();
        }
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var origin = "http://127.0.0.1:" + port;
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ActivityExplorer.Web.dll"));
        start.Environment["ACTIVITY_EXPLORER_DATA"] = fixture.DirectoryPath;
        start.Environment["Urls"] = origin;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["McpWrites__Enabled"] = "true";
        start.Environment["McpWrites__ProfileIds__0"] = fixture.Owner.ToString();
        var output = new StringBuilder();
        var process = Process.Start(start)!;
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) lock (output) output.AppendLine(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) lock (output) output.AppendLine(args.Data); };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        var runtime = new McpWriteRuntime(fixture, process, origin);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            while (true)
            {
                if (process.HasExited) throw new InvalidOperationException("Web test host exited: " + output);
                try { using var response = await http.GetAsync(origin + "/", deadline.Token); if (response.IsSuccessStatusCode && File.Exists(Path.Combine(fixture.DirectoryPath, "mcp-connection.json"))) return runtime; }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(100, deadline.Token);
            }
        }
        catch { await runtime.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        process.Dispose(); await fixture.DisposeAsync();
    }
}
