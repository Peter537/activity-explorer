using System.ComponentModel.DataAnnotations;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Core.Domain;

public sealed class PersonalGoal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public OwnerProfile? Owner { get; set; }
    public GoalMetric Metric { get; set; }
    public GoalRecurrence Recurrence { get; set; }
    public DateOnly Start { get; set; }
    public DateOnly? End { get; set; }
    public DateOnly? ArchiveFromEdition { get; set; }
    public long MutationVersion { get; set; }
    public List<GoalDefinitionRevision> Definitions { get; set; } = [];
}

public sealed class GoalDefinitionRevision
{
    public Guid GoalId { get; set; }
    public PersonalGoal? Goal { get; set; }
    public DateOnly EffectiveFromEdition { get; set; }
    [MaxLength(120)] public string Name { get; set; } = string.Empty;
    public SportKind? Sport { get; set; }
    public double Target { get; set; }
}
