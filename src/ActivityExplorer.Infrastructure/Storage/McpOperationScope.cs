namespace ActivityExplorer.Infrastructure.Storage;

public sealed class McpOperationScope : IDisposable
{
    private static readonly AsyncLocal<McpOperationScope?> CurrentScope = new();
    private readonly McpOperationScope? _previous = CurrentScope.Value;
    public static McpOperationScope? Current => CurrentScope.Value;
    public Guid Id { get; }
    public Type? EntityType { get; }
    public bool Recorded { get; set; }

    public McpOperationScope(Guid id, Type? entityType)
    {
        Id = id;
        EntityType = entityType;
        CurrentScope.Value = this;
    }
    public void Dispose() => CurrentScope.Value = _previous;
}
