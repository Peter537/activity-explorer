namespace ActivityExplorer.Core.Domain;

public sealed class ActivityExplorationIndex
{
    public Guid ActivityId { get; set; }
    public Activity? Activity { get; set; }
    public long InputVersion { get; set; }
    public int ComputationVersion { get; set; }
    public int CellCount { get; set; }
    public bool IsLimited { get; set; }
    public string? Diagnostic { get; set; }
}

public sealed class ActivityExplorationCell
{
    public Guid ActivityId { get; set; }
    public Activity? Activity { get; set; }
    public int CellId { get; set; }
}
