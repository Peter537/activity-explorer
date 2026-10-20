namespace ActivityExplorer.Web.Services;

public sealed class McpWriteOptions
{
    public bool Enabled { get; init; }
    public Guid[] ProfileIds { get; init; } = [];
    public bool AllowDestructive { get; init; }
    public bool AllowAdministration { get; init; }
    public int PlanLifetimeMinutes { get; init; } = 15;
    public int MaxPendingPlans { get; init; } = 100;

    public static McpWriteOptions Read(IConfiguration configuration)
    {
        var options = configuration.GetSection("McpWrites").Get<McpWriteOptions>(binder => binder.ErrorOnUnknownConfiguration = true) ?? new();
        if (options.Enabled && (options.ProfileIds.Length == 0 || options.ProfileIds.Contains(Guid.Empty)))
            throw new ArgumentException("McpWrites:ProfileIds must explicitly select permitted profiles.");
        if (options.PlanLifetimeMinutes is < 1 or > 60 || options.MaxPendingPlans is < 1 or > 1000)
            throw new ArgumentException("MCP write limits are outside their supported ranges.");
        return options;
    }
}
