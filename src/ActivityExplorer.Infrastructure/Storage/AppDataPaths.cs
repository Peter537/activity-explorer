using ActivityExplorer.Core.Contracts;

namespace ActivityExplorer.Infrastructure.Storage;

public sealed class AppDataPaths : IAppDataPaths
{
    public AppDataPaths()
    {
        var configured = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_DATA");
        Root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Activity Explorer")
            : Path.GetFullPath(configured);
    }

    public string Root { get; }
    public string DatabasePath => Path.Combine(Root, "activity-explorer.db");
    public string OriginalsPath => Path.Combine(Root, "originals");
    public string StagingPath => Path.Combine(Root, "staging");
    public string LogsPath => Path.Combine(Root, "logs");
    public string QuarantinePath => Path.Combine(Root, "quarantine");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(OriginalsPath);
        Directory.CreateDirectory(StagingPath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(QuarantinePath);
    }

    public string GetOwnerOriginalsPath(Guid ownerId)
    {
        var result = Path.Combine(OriginalsPath, ownerId.ToString("N"));
        Directory.CreateDirectory(result);
        return result;
    }
}
