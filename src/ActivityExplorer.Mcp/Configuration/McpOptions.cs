using Microsoft.Extensions.Configuration;

namespace ActivityExplorer.Mcp.Configuration;

public sealed class McpOptions
{
    public string AccessMode { get; init; } = "ReadOnly";
    public Guid[]? AllowedProfileIds { get; init; }
    public int RequestTimeoutSeconds { get; init; } = 60;
    public int MaxConcurrentCalls { get; init; } = 4;
    public int MaxConcurrentAnalyses { get; init; } = 1;
    public int CapacityWaitSeconds { get; init; } = 5;
    public int MaxResponseBytes { get; init; } = 1024 * 1024;

    public static McpOptions Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("Mcp");
        var known = typeof(McpOptions).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (section.GetChildren().Any(child => !known.Contains(child.Key))) throw new ArgumentException("Unknown MCP configuration option.");
        var options = section.Get<McpOptions>(binder => binder.ErrorOnUnknownConfiguration = true) ?? new();
        if (!string.Equals(options.AccessMode, "ReadOnly", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("This release supports ReadOnly access only. Writable tools are not available.");
        if (section.GetSection(nameof(AllowedProfileIds)).Exists() && options.AllowedProfileIds is not { Length: > 0 } || options.AllowedProfileIds?.Contains(Guid.Empty) == true)
            throw new ArgumentException("AllowedProfileIds must contain valid, nonempty profile IDs when specified.");
        if (options.RequestTimeoutSeconds is < 1 or > 3600 || options.MaxConcurrentCalls is < 1 or > 32 ||
            options.MaxConcurrentAnalyses < 1 || options.MaxConcurrentAnalyses > options.MaxConcurrentCalls ||
            options.CapacityWaitSeconds is < 1 or > 60 || options.MaxResponseBytes is < 1024 or > 16 * 1024 * 1024)
            throw new ArgumentException("MCP execution limits are outside their supported ranges.");
        return options;
    }
}
