using System.Text;

namespace ActivityExplorer.Infrastructure.Storage;

public static class ManagedStagingDirectory
{
    private const string MarkerName = ".activity-explorer-stage";
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Create(string root, Guid? id = null)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Directory.CreateDirectory(root);
        RejectReparsePoint(root);
        var name = (id ?? Guid.NewGuid()).ToString("N");
        var directory = ManagedPathGuard.ResolveUnder(root, name);
        if (Path.Exists(directory)) throw new IOException("The staging directory already exists.");

        Directory.CreateDirectory(directory);
        WriteMarker(Path.Combine(directory, MarkerName), name);
        return directory;
    }

    public static bool DeleteOwned(string root, string directory)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        directory = ManagedPathGuard.ResolveUnder(root, directory);
        var name = Path.GetFileName(directory);
        if (!string.Equals(Path.GetDirectoryName(directory), root, PathComparison) ||
            !Guid.TryParseExact(name, "N", out _))
            throw new InvalidDataException("A staging directory must be a direct GUID-named child of its root.");
        if (!Path.Exists(directory)) return true;
        RejectReparsePoint(root);
        RejectReparsePoint(directory);

        var marker = Path.Combine(directory, MarkerName);
        if (!Path.Exists(marker)) return false;
        RejectReparsePoint(marker);
        using (var stream = File.OpenRead(marker))
        {
            if (stream.Length != 32) return false;
            Span<byte> contents = stackalloc byte[32];
            stream.ReadExactly(contents);
            if (!contents.SequenceEqual(Encoding.ASCII.GetBytes(name))) return false;
        }

        DeleteContents(directory, marker);
        File.Delete(marker);
        try
        {
            Directory.Delete(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (Directory.Exists(directory))
            {
                RejectReparsePoint(directory);
                WriteMarker(marker, name);
            }
            throw;
        }
        return true;
    }

    private static void WriteMarker(string marker, string name)
    {
        using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write);
        stream.Write(Encoding.ASCII.GetBytes(name));
    }

    private static void DeleteContents(string directory, string? retainedFile = null)
    {
        foreach (var child in Directory.EnumerateFileSystemEntries(directory))
        {
            if (string.Equals(child, retainedFile, PathComparison)) continue;
            var attributes = RejectReparsePoint(child);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                File.Delete(child);
                continue;
            }

            DeleteContents(child);
            Directory.Delete(child);
        }
    }

    private static FileAttributes RejectReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Managed staging cannot traverse symbolic links or reparse points.");
        return attributes;
    }
}
