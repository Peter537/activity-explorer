using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ActivityExplorer.Core.Models;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ActivityExplorer.Tests;

internal sealed class McpWriteRuntime(McpFixture fixture) : IAsyncDisposable
{
    private readonly McpStdioProcess _stdio = new(fixture);
    private Process? _web;

    public McpFixture Fixture => fixture;
    public string Origin { get; private set; } = "";

    public static async Task RunAsync(Func<McpWriteRuntime, Task> test) =>
        await RunWithCleanupAsync(await StartAsync(), test);

    internal static async Task RunWithCleanupAsync<T>(T resource, Func<T, Task> test) where T : IAsyncDisposable
    {
        try { await test(resource); }
        catch (Exception failure)
        {
            await DisposeAfterFailureAsync(resource, failure);
            throw;
        }
        await resource.DisposeAsync();
    }

    public static async Task<McpWriteRuntime> StartAsync()
    {
        var runtime = new McpWriteRuntime(await McpFixture.CreateAsync());
        try
        {
            await runtime.StartWebAsync();
            return runtime;
        }
        catch (Exception failure)
        {
            await DisposeAfterFailureAsync(runtime, failure);
            throw;
        }
    }

    public Task<McpClient> CreateClientAsync(McpClientOptions? options = null, CancellationToken cancellationToken = default,
        params string[] serverOptions) => _stdio.ConnectAsync(options, cancellationToken, serverOptions);

    private async Task StartWebAsync()
    {
        await using (var db = await Fixture.Factory.CreateDbContextAsync())
        {
            await db.ImportBatches.ExecuteDeleteAsync();
            await db.FileOperations.ExecuteDeleteAsync();
        }
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ActivityExplorer.Web.dll"));
        start.Environment["ACTIVITY_EXPLORER_DATA"] = Fixture.DirectoryPath;
        start.Environment["Urls"] = "http://127.0.0.1:0";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["McpWrites__Enabled"] = "true";
        start.Environment["McpWrites__ProfileIds__0"] = Fixture.Owner.ToString();
        var output = new StringBuilder();
        _web = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Web test host.");
        TestContext.Current.TestOutputHelper?.WriteLine($"Started Web test host PID {_web.Id}.");
        void CaptureOutput(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is not null) lock (output) output.AppendLine(args.Data);
        }
        _web.OutputDataReceived += CaptureOutput;
        _web.ErrorDataReceived += CaptureOutput;
        _web.BeginOutputReadLine();
        _web.BeginErrorReadLine();

        var connectionFile = Path.Combine(Fixture.DirectoryPath, "mcp-connection.json");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        // The Web host publishes the connection atomically after Kestrel starts listening.
        while (!File.Exists(connectionFile))
        {
            if (_web.HasExited)
            {
                lock (output) throw new InvalidOperationException("Web test host exited: " + output);
            }
            await Task.Delay(100, deadline.Token);
        }
        var connection = JsonSerializer.Deserialize<McpConnection>(await File.ReadAllTextAsync(connectionFile, deadline.Token),
            McpChangeJson.Options);
        if (!Uri.TryCreate(connection?.Endpoint, UriKind.Absolute, out var endpoint) ||
            !endpoint.IsLoopback || endpoint.Scheme != "http" || endpoint.Port <= 0)
            throw new InvalidOperationException("The Web test host did not publish a bound loopback HTTP address.");
        Origin = endpoint.GetLeftPart(UriPartial.Authority);
    }

    private static async Task DisposeAfterFailureAsync(IAsyncDisposable resource, Exception failure)
    {
        try { await resource.DisposeAsync(); }
        catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        try { await _stdio.DisposeAsync(); }
        catch (Exception failure) { failures.Add(failure); }
        try
        {
            if (_web is not null)
            {
                using (_web) await McpStdioProcess.StopAsync(_web, "Web test host");
            }
        }
        catch (Exception failure) { failures.Add(failure); }
        try { await Fixture.DisposeAsync(); }
        catch (Exception failure) { failures.Add(failure); }
        if (failures.Count > 0) throw new AggregateException("MCP fixture cleanup failed.", failures);
    }
}

internal sealed class McpStdioProcess(McpFixture fixture) : IAsyncDisposable
{
    private Process? _process;
    private McpClient? _client;
    private Task<string>? _standardError;

    public async Task<McpClient> ConnectAsync(McpClientOptions? options = null, CancellationToken cancellationToken = default,
        params string[] serverOptions)
    {
        if (_process is not null) throw new InvalidOperationException("The MCP fixture already owns a stdio process.");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        string[] arguments = [McpWriteStdioTests.ServerPath, "--Mcp:AccessMode", "ReadWrite",
            "--Mcp:AllowedWriteProfileIds:0", fixture.Owner.ToString(), .. serverOptions];
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "TEMP", "TMP", "DOTNET_ROOT", "USERPROFILE", "LOCALAPPDATA" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["ACTIVITY_EXPLORER_DATA"] = fixture.DirectoryPath;
        start.Environment["DOTNET_NOLOGO"] = "1";

        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the MCP stdio process.");
        TestContext.Current.TestOutputHelper?.WriteLine($"Started MCP stdio process PID {_process.Id}.");
        _standardError = _process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        _client = await McpClient.CreateAsync(new StreamClientTransport(_process.StandardInput.BaseStream,
            _process.StandardOutput.BaseStream), options, cancellationToken: deadline.Token);
        return _client;
    }

    public async ValueTask DisposeAsync()
    {
        Exception? clientFailure = null;
        try
        {
            if (_client is not null) await _client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (Exception failure) { clientFailure = failure; }

        try
        {
            if (_process is not null)
            {
                using (_process)
                {
                    await StopAsync(_process, "MCP stdio process", closeInput: true);
                    if (_standardError is not null)
                    {
                        var output = await _standardError.WaitAsync(TimeSpan.FromSeconds(8));
                        if (_client is null && output.Length > 0) TestContext.Current.TestOutputHelper?.WriteLine(output);
                    }
                }
            }
        }
        catch (Exception failure) when (clientFailure is not null)
        {
            throw new AggregateException(clientFailure, failure);
        }
        if (clientFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(clientFailure);
    }

    internal static async Task StopAsync(Process process, string label, bool closeInput = false)
    {
        var id = process.Id;
        Exception? gracefulFailure = null;
        try
        {
            if (closeInput && !process.HasExited)
            {
                process.StandardInput.BaseStream.Dispose();
                using var graceful = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try { await process.WaitForExitAsync(graceful.Token); }
                catch (OperationCanceledException) when (graceful.IsCancellationRequested) { }
            }
        }
        catch (Exception failure) { gracefulFailure = failure; }
        try
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (Exception failure)
        {
            TestRunStorage.ShutdownUnconfirmed = true;
            throw new InvalidOperationException($"Could not confirm exit of {label} PID {id}.",
                gracefulFailure is null ? failure : new AggregateException(gracefulFailure, failure));
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"Confirmed exit of {label} PID {id} with code {process.ExitCode}.");
        if (gracefulFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(gracefulFailure);
    }
}
