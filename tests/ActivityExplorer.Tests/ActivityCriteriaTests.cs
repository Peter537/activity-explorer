using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Tests;

public sealed class ActivityCriteriaTests
{
    [Fact]
    public void Activity_criteria_validate_numeric_ranges_modes_and_contradictory_selections()
    {
        var tag = Guid.NewGuid();
        ActivityCriteria[] invalid =
        [
            new(MinDistanceMeters: -1), new(MaxDistanceMeters: double.PositiveInfinity),
            new(MinDistanceMeters: 2, MaxDistanceMeters: 1), new(MinMovingSeconds: double.NaN),
            new(MaxMovingSeconds: -1), new(MinMovingSeconds: 2, MaxMovingSeconds: 1),
            new(MinAscentMeters: double.NegativeInfinity), new(MaxAscentMeters: double.NaN),
            new(MinAscentMeters: 2, MaxAscentMeters: 1), new(Sport: (SportKind)999),
            new(TextMode: (ActivityTextMode)999), new(TagMode: (ActivityTagMode)999),
            new(TagPresence: (ActivityTagPresence)999), new(TagIds: [Guid.Empty]),
            new(ExcludedTagIds: [Guid.Empty]), new(TagIds: [tag], ExcludedTagIds: [tag]),
            new(TagIds: [tag], TagPresence: ActivityTagPresence.Untagged), new(Gear: "Bike", GearUnassigned: true),
            new(Search: "unclosed \"phrase", TextMode: ActivityTextMode.Words),
            new(Search: "\"\"", TextMode: ActivityTextMode.Words)
        ];
        foreach (var criteria in invalid) Assert.Throws<ArgumentException>(() => ActivityCriteriaValidation.Normalize(criteria));
        var zero = ActivityCriteriaValidation.Normalize(new(MinDistanceMeters: 0, MaxDistanceMeters: 0,
            MinMovingSeconds: 0, MaxMovingSeconds: 0, MinAscentMeters: 0, MaxAscentMeters: 0));
        Assert.Equal(0, zero.MinDistanceMeters);
        Assert.Equal(0, zero.MaxMovingSeconds);
        Assert.Equal(0, zero.MinAscentMeters);
        Assert.Throws<ArgumentNullException>(() => ActivityCriteriaValidation.Normalize(null!));
    }

    [Fact]
    public void Activity_criteria_normalize_terms_and_keep_legacy_filter_mapping_source_compatible()
    {
        var tag = Guid.NewGuid();
        var criteria = ActivityCriteriaValidation.Normalize(new(Search: "  Words  ", Device: "  Device ", Gear: " Gear ",
            TagIds: [tag, tag], ExcludedTagIds: [], TextMode: ActivityTextMode.Words));
        Assert.Equal("Words", criteria.Search);
        Assert.Equal("Device", criteria.Device);
        Assert.Equal("Gear", criteria.Gear);
        Assert.Equal([tag], criteria.TagIds);
        Assert.Empty(criteria.ExcludedTagIds!);
        Assert.Null(ActivityCriteriaValidation.Normalize(new(Search: "  ", Device: " ", Gear: " ")).Search);
        Assert.Equal(["æble", "ved søen", "Århus"], ActivityCriteriaValidation.ParseTextTerms(" æble \"ved søen\" Århus ÆBLE "));
        Assert.Empty(ActivityCriteriaValidation.ParseTextTerms(null));
        var filter = new ActivityFilter(Sport: SportKind.Rowing, Search: " Literal phrase ", HasPower: true, Device: "Watch", TagIds: [tag]);
        Assert.Equal(ActivityTextMode.Literal, filter.GetCriteria().TextMode);
        Assert.Equal(SportKind.Rowing, filter.GetCriteria().Sport);
        Assert.Equal([tag], filter.GetCriteria().TagIds);
        Assert.Same(criteria, (filter with { Criteria = criteria }).GetCriteria());
    }
}
