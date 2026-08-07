namespace ActivityExplorer.Infrastructure.Import;

public sealed class UnsupportedActivityException(string message) : Exception(message);
public sealed class UnsafeArchiveException(string message) : Exception(message);
