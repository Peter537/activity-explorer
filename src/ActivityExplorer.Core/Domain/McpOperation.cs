namespace ActivityExplorer.Core.Domain;

// No profile foreign key: receipts must survive deletion of their target profile.
public sealed class McpOperation
{
    public Guid Id { get; set; }
    public string Action { get; set; } = "";
    public string ProfileIdsJson { get; set; } = "[]";
    public string RequestJson { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Status { get; set; } = "queued";
    public Guid? EntityId { get; set; }
    public string? ResultJson { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed class McpEffect
{
    public Guid Id { get; set; }
    public Guid? EntityId { get; set; }
}
