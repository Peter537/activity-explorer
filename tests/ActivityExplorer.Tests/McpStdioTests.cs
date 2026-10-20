using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ActivityExplorer.Mcp;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ActivityExplorer.Tests;

public sealed class McpStdioTests
{
    private static string ServerPath => Path.GetFullPath(typeof(McpStdioTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "McpTargetPath").Value!);
    [Theory]
    [InlineData("2026-07-28")]
    [InlineData("2025-11-25")]
    public async Task Published_protocol_generations_list_and_call_read_only_tools_without_mutation(string version)
    {
        await using var fixture = await McpFixture.CreateAsync();
        var before = SHA256.HashData(await File.ReadAllBytesAsync(fixture.DatabasePath));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using (var client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = "dotnet",
            Arguments = [ServerPath],
            EnvironmentVariables = ChildEnvironment(fixture.DirectoryPath),
            InheritEnvironmentVariables = false,
            ShutdownTimeout = TimeSpan.FromSeconds(8)
        }), new() { ProtocolVersion = version }, cancellationToken: deadline.Token))
        {
            var listing = await client.ListToolsAsync(new ListToolsRequestParams(), deadline.Token);
            Assert.Equal(15, listing.Tools.Count);
            Assert.Equal(listing.Tools.Select(tool => tool.Name).Order(StringComparer.Ordinal), listing.Tools.Select(tool => tool.Name));
            Assert.All(listing.Tools, tool =>
            {
                Assert.True(tool.Annotations?.ReadOnlyHint);
                Assert.False(tool.Annotations?.DestructiveHint);
                Assert.NotNull(tool.OutputSchema);
            });
            var status = McpReadOnlyTests.Data(await client.CallToolAsync("get_server_status", cancellationToken: deadline.Token));
            Assert.Equal("ReadOnly", status.GetProperty("accessMode").GetString());
            var result = await client.CallToolAsync("search_activities", new Dictionary<string, object?> { ["profileId"] = fixture.Owner, ["pageSize"] = 1 }, cancellationToken: deadline.Token);
            var data = McpReadOnlyTests.Data(result);
            Assert.Equal(2, data.GetProperty("total").GetInt32());
            Assert.Equal(3000, data.GetProperty("totals").GetProperty("distanceMeters").GetDouble());
            Assert.Single(data.GetProperty("items").EnumerateArray());
            var denied = await client.CallToolAsync("get_activity", new Dictionary<string, object?> { ["profileId"] = fixture.Owner, ["activityId"] = fixture.OtherActivity }, cancellationToken: deadline.Token);
            Assert.Equal("not_found", McpReadOnlyTests.Error(denied));
            var forbidden = await client.CallToolAsync("create_segment", new Dictionary<string, object?> { ["profileId"] = fixture.Owner }, cancellationToken: deadline.Token);
            Assert.Equal("read_only", McpReadOnlyTests.Error(forbidden));
        }
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(fixture.DatabasePath)));
        await using var check = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(Core.Domain.ImportStatus.Running, (await check.ImportBatches.SingleAsync()).Status);
        Assert.Equal(Core.Domain.FileOperationState.Pending, (await check.FileOperations.SingleAsync()).State);
        Assert.Empty(Directory.GetDirectories(fixture.DirectoryPath));
    }

    [Fact]
    public async Task Eof_exits_cleanly_and_startup_errors_do_not_create_or_upgrade_data()
    {
        await using var fixture = await McpFixture.CreateAsync();
        var (exit, output, _) = await RunToEofAsync(fixture.DirectoryPath);
        Assert.Equal(0, exit);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var message = JsonDocument.Parse(line);
            Assert.Equal("2.0", message.RootElement.GetProperty("jsonrpc").GetString());
        }
        var missing = Path.Combine(fixture.DirectoryPath, "does-not-exist");
        var failed = await RunToEofAsync(missing);
        Assert.Equal(3, failed.Exit);
        Assert.Equal("", failed.Output);
        Assert.Contains("ACTIVITY_EXPLORER_DATA", failed.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(missing));
        var invalid = await RunToEofAsync(fixture.DirectoryPath, "--Mcp:AccessMode", "ReadWrite");
        Assert.Equal(2, invalid.Exit);
        Assert.Contains("ReadOnly", invalid.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mixed_valid_and_invalid_profile_ids_fail_before_database_access()
    {
        await using var fixture = await McpFixture.CreateAsync();
        var missing = Path.Combine(fixture.DirectoryPath, "does-not-exist");
        var result = await RunToEofAsync(missing,
            "--Mcp:AllowedProfileIds:0", fixture.Owner.ToString(),
            "--Mcp:AllowedProfileIds:1", "not-a-guid");

        Assert.Equal(2, result.Exit);
        Assert.Empty(result.Output);
        Assert.StartsWith("Invalid MCP configuration:", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(missing));
    }

    private static Dictionary<string, string?> ChildEnvironment(string dataRoot)
    {
        var environment = new Dictionary<string, string?> { ["ACTIVITY_EXPLORER_DATA"] = dataRoot, ["DOTNET_NOLOGO"] = "1" };
        foreach (var name in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "DOTNET_ROOT", "USERPROFILE", "LOCALAPPDATA" })
            if (Environment.GetEnvironmentVariable(name) is { } value) environment[name] = value;
        return environment;
    }

    private static async Task<(int Exit, string Output, string Error)> RunToEofAsync(string directory, params string[] arguments)
    {
        using var process = new Process { StartInfo = new("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add(ServerPath);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment.Clear();
        foreach (var pair in ChildEnvironment(directory)) process.StartInfo.Environment[pair.Key] = pair.Value;
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
}
