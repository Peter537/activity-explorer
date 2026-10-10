using System.ComponentModel.DataAnnotations;

namespace ActivityExplorer.Core.Domain;

public sealed class Tag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public OwnerProfile? Owner { get; set; }
    [MaxLength(80)] public string Name { get; set; } = string.Empty;
    [MaxLength(80)] public string NormalizedName { get; set; } = string.Empty;
}

public sealed class ActivityTag
{
    public Guid ActivityId { get; set; }
    public Activity? Activity { get; set; }
    public Guid TagId { get; set; }
    public Tag? Tag { get; set; }
}

public sealed class SavedSearch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public OwnerProfile? Owner { get; set; }
    [MaxLength(120)] public string Name { get; set; } = string.Empty;
    [MaxLength(120)] public string NormalizedName { get; set; } = string.Empty;
    public string CriteriaJson { get; set; } = string.Empty;
}
