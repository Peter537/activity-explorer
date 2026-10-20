namespace ActivityExplorer.Infrastructure.Storage;

public sealed class WebWriterLease : IDisposable
{
    private readonly FileStream _lease;
    public WebWriterLease(AppDataPaths paths)
    {
        paths.EnsureCreated();
        try { _lease = new FileStream(Path.Combine(paths.Root, "web-writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) { throw new InvalidOperationException("Another Web app is already writing this data root. Use that instance or stop it first.", exception); }
    }
    public void Dispose() => _lease.Dispose();
}
