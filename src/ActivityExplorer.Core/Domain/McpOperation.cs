namespace ActivityExplorer.Core.Domain;

public enum McpExecutionState { Queued, Running, Completed, Failed, Interrupted, CommittedWithWarnings }

public static class McpExecutionStates
{
    public static string ToValue(McpExecutionState state) => state switch
    {
        McpExecutionState.Queued => "queued",
        McpExecutionState.Running => "running",
        McpExecutionState.Completed => "completed",
        McpExecutionState.Failed => "failed",
        McpExecutionState.Interrupted => "interrupted",
        McpExecutionState.CommittedWithWarnings => "committed_with_warnings",
        _ => throw new InvalidOperationException("The MCP operation has an unsupported execution state.")
    };

    public static McpExecutionState Parse(string value) => value switch
    {
        "queued" => McpExecutionState.Queued,
        "running" => McpExecutionState.Running,
        "completed" => McpExecutionState.Completed,
        "failed" => McpExecutionState.Failed,
        "interrupted" => McpExecutionState.Interrupted,
        "committed_with_warnings" => McpExecutionState.CommittedWithWarnings,
        _ => throw new InvalidOperationException("The stored MCP operation state is incompatible with this application version.")
    };
}

// No profile foreign key: receipts must survive deletion of their target profile.
public sealed class McpOperation
{
    public Guid Id { get; set; }
    public string Action { get; set; } = "";
    public string ProfileIdsJson { get; set; } = "[]";
    public string RequestJson { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public McpExecutionState Status { get; private set; } = McpExecutionState.Queued;
    public Guid? EntityId { get; set; }
    public string? ResultJson { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }

    public void StartExecution()
    {
        if (Status != McpExecutionState.Queued)
            throw new InvalidOperationException("Only a queued MCP operation can start execution.");
        Status = McpExecutionState.Running;
    }

    public void Finish(McpExecutionState outcome, DateTimeOffset completedAt)
    {
        if (Status is not (McpExecutionState.Queued or McpExecutionState.Running) ||
            outcome is not (McpExecutionState.Completed or McpExecutionState.Failed or
                McpExecutionState.Interrupted or McpExecutionState.CommittedWithWarnings) ||
            Status == McpExecutionState.Queued && outcome == McpExecutionState.Completed)
            throw new InvalidOperationException("The MCP operation cannot make this execution transition.");
        Status = outcome;
        RequestJson = "";
        CompletedAtUtc = completedAt;
    }
}

public sealed class McpEffect
{
    public Guid Id { get; set; }
    public Guid? EntityId { get; set; }
}
