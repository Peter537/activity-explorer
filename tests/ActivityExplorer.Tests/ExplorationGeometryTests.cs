using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;

namespace ActivityExplorer.Tests;

public sealed class ExplorationGeometryTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Traversal_visits_unsampled_cells_and_is_independent_of_sampling_laps_and_direction()
    {
        var sparse = new[] { Point(1000.2, 1000.4, 0), Point(1003.8, 1000.4, 30) };
        var dense = Enumerable.Range(0, 13).Select(index => Point(1000.2 + index * 0.3, 1000.4, index * 2.5)).ToArray();
        var reverse = sparse.Reverse().Select((point, index) => point with { Timestamp = Start.AddSeconds(index * 30) }).ToArray();
        var laps = sparse.Concat(reverse.Select(point => point with { Timestamp = point.Timestamp!.Value.AddSeconds(30) }))
            .ToArray();

        Assert.Equal(Enumerable.Range(1000, 4).Select(x => x + 1000 * ExplorationGrid.Size), Cells(sparse));
        Assert.Equal(Cells(sparse), Cells(dense));
        Assert.Equal(Cells(sparse), Cells(reverse));
        Assert.Equal(Cells(sparse), Cells(laps));
    }

    [Fact]
    public void Half_open_corner_and_boundary_ownership_is_stable_when_a_corner_sample_is_inserted()
    {
        var sparse = new[] { Point(1001.4, 1000.6, 0), Point(1000.6, 1001.4, 20) };
        var withCorner = new[] { sparse[0], Point(1001, 1001, 10), sparse[1] };
        var reverse = sparse.Reverse().Select((point, index) => point with { Timestamp = Start.AddSeconds(index * 20) }).ToArray();
        Assert.Equal(Cells(sparse), Cells(withCorner));
        Assert.Equal(Cells(sparse), Cells(reverse));
        Assert.Contains(1001 + 1001 * ExplorationGrid.Size, Cells(sparse));

        var boundary = new[] { Point(1001, 1000.2, 0), Point(1001, 1003.8, 30) };
        Assert.All(Cells(boundary), cell => Assert.Equal(1001, cell % ExplorationGrid.Size));
    }

    [Fact]
    public void Dateline_traversal_wraps_cell_ids_without_filling_the_world()
    {
        var points = new[] { Point(ExplorationGrid.Size - 1.2, 1000.4, 0), Point(1.2, 1000.4, 30) };
        var cells = Cells(points);
        Assert.Equal(new[] { 0, 1, ExplorationGrid.Size - 2, ExplorationGrid.Size - 1 },
            cells.Select(cell => cell % ExplorationGrid.Size));
        Assert.Equal(cells, Cells(points.Reverse().Select((point, index) => point with
        {
            Timestamp = Start.AddSeconds(index * 30)
        }).ToArray()));
        Assert.Equal(Cells([Raw(0, -180)]), Cells([Raw(0, 180)]));
    }

    [Fact]
    public void Invalid_and_out_of_projection_coordinates_neither_visit_cells_nor_bridge_valid_samples()
    {
        var before = Point(1000.2, 1000.4, 0);
        var after = Point(1003.8, 1000.4, 20);
        foreach (var invalid in new[]
        {
            Raw(null, 0), Raw(0, null), Raw(double.NaN, 0), Raw(0, double.PositiveInfinity),
            Raw(90, 0), Raw(-90, 0), Raw(0, 181), Raw(0, -181)
        })
        {
            Assert.Equal(Cells([before, after], verified: false), Cells([before, invalid, after]));
            Assert.Empty(Cells([invalid]));
        }
        Assert.Single(Cells([Raw(ExplorationGrid.LatitudeLimit, 0)]));
        Assert.Single(Cells([Raw(-ExplorationGrid.LatitudeLimit, 0)]));
    }

    [Fact]
    public void Explicit_breaks_unknown_continuity_time_gaps_resets_and_teleports_keep_only_endpoint_cells()
    {
        var before = Point(1000.2, 1000.4, 0);
        var after = Point(1003.8, 1000.4, 30);
        var endpoints = Cells([before, after], verified: false);
        Assert.Equal(endpoints, ExplorationGrid.Extract([before, after], new HashSet<int> { 1 }, true));
        foreach (var timestamp in new DateTimeOffset?[] { null, Start, Start.AddSeconds(-1), Start.AddSeconds(30.001) })
            Assert.Equal(endpoints, Cells([before, after with { Timestamp = timestamp }]));
        Assert.Equal(endpoints, Cells([before with { Timestamp = null }, after]));
        Assert.Equal(endpoints, Cells([before with { DistanceMeters = 10 }, after with { DistanceMeters = 0 }]));
        Assert.Equal(endpoints, Cells([before, after with { Timestamp = Start.AddSeconds(1) }]));
        Assert.True(Cells([before, after]).Count > endpoints.Count);
    }

    [Fact]
    public void Zero_length_track_visits_one_cell_and_cancellation_interrupts_extraction()
    {
        var point = Point(1000.2, 1000.4, 0);
        Assert.Single(Cells([point, point with { Timestamp = Start.AddSeconds(10) }]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ExplorationGrid.Extract([point], new HashSet<int>(), true,
            cancellation.Token));
    }

    [Fact]
    public void Speed_ceiling_accepts_the_first_representable_eligible_time_and_rejects_one_tick_faster()
    {
        var before = Point(1000.2, 1000.4, 0);
        var after = Point(1003.8, 1000.4, 30);
        var meters = TrackPathAnalysis.HaversineMeters(before.Latitude!.Value, before.Longitude!.Value,
            after.Latitude!.Value, after.Longitude!.Value);
        var minimumTicks = (long)Math.Ceiling(meters / ExplorationGrid.MaximumSpeedMetersPerSecond * TimeSpan.TicksPerSecond);
        var allowed = after with { Timestamp = Start.AddTicks(minimumTicks) };
        var tooFast = after with { Timestamp = Start.AddTicks(minimumTicks - 1) };

        Assert.True(Cells([before, allowed]).Count > 2);
        Assert.Equal(Cells([before, after], verified: false), Cells([before, tooFast]));
    }

    [Fact]
    public void Display_bounds_keep_fixed_identity_and_valid_mercator_edges()
    {
        var bounds = ExplorationGrid.Bounds(14, 0, 0);
        Assert.Equal(-180, bounds.West);
        Assert.InRange(bounds.North, ExplorationGrid.LatitudeLimit - 1e-10, ExplorationGrid.LatitudeLimit + 1e-10);
        var world = ExplorationGrid.Bounds(0, 0, 0);
        Assert.Equal(180, world.East);
        Assert.Equal(-world.North, world.South, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => ExplorationGrid.Bounds(15, 0, 0));
    }

    private static IReadOnlyList<int> Cells(IReadOnlyList<TrackPoint> points, bool verified = true) =>
        ExplorationGrid.Extract(points, new HashSet<int>(), verified);

    private static TrackPoint Point(double x, double y, double seconds) => Raw(
        Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y / ExplorationGrid.Size))) * 180 / Math.PI,
        x * 360 / ExplorationGrid.Size - 180) with
    { Timestamp = Start.AddSeconds(seconds) };

    private static TrackPoint Raw(double? latitude, double? longitude) =>
        new(Start, latitude, longitude, null, null, null, null, null, null, null);
}
