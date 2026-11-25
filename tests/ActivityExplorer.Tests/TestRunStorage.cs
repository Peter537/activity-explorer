using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ActivityExplorer.Tests;

public sealed class TestRunStorage : IAsyncDisposable
{
    private static readonly Lazy<DirectoryInfo> Root = new(CreateRoot);
    internal static bool ShutdownUnconfirmed { get; set; }

    internal static string NewDirectory() =>
        Root.Value.CreateSubdirectory(Guid.NewGuid().ToString("N")).FullName;

    private static DirectoryInfo CreateRoot()
    {
        var pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var root = Directory.CreateTempSubdirectory($"ae-tests-{pid}-");
        Console.WriteLine($"Test runtime scratch for PID {pid}: {root.Name}");
        return root;
    }

    internal static Task DeleteFixtureAsync(string path)
    {
        var fixture = new DirectoryInfo(path);
        if (!Root.IsValueCreated || fixture.Parent?.FullName != Root.Value.FullName)
            throw new InvalidOperationException("The fixture escaped the test run directory.");
        return DeleteAsync(fixture);
    }

    public async ValueTask DisposeAsync()
    {
        if (!Root.IsValueCreated) return;
        if (ShutdownUnconfirmed)
            throw new IOException($"Test scratch retained because host shutdown was unconfirmed: {Root.Value.Name}");
        SqliteConnection.ClearAllPools();
        await DeleteAsync(Root.Value);
    }

    private static async Task DeleteAsync(DirectoryInfo directory)
    {
        for (var attempt = 0; ; attempt++)
        {
            directory.Refresh();
            if (!directory.Exists) return;
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The test scratch directory became a link.");

            try
            {
                directory.Delete(recursive: true);
                return;
            }
            catch (IOException exception) when (OperatingSystem.IsWindows() && attempt < 10 && (exception.HResult & 0xffff) is 32 or 33)
            {
                await Task.Delay(100);
            }
        }
    }
}
