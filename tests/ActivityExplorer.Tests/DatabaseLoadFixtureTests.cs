using ActivityExplorer.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed class DatabaseLoadFixtureTests
{
    [Theory]
    [InlineData("history", 2)]
    [InlineData("profiles", 4)]
    public async Task Generated_fixture_preserves_workload_and_frozen_count_contracts(string dimension, int ownerCount)
    {
        const int scale = 2;
        var directory = Directory.CreateTempSubdirectory("activity-explorer-load-fixture-");
        var path = Path.Combine(directory.FullName, $"load-{dimension}-{scale}.db");
        try
        {
            IReadOnlyDictionary<string, long> originalCounts;
            await using (var fixture = await DatabaseLoadFixture.CreateAsync(directory.FullName, scale, dimension))
            {
                Assert.Equal(4, fixture.Counts["WatchedImportAdmissions"]);
                Assert.Equal(4, fixture.Counts["ImportBatchCheckpoints"]);
                Assert.Equal(ownerCount, fixture.Counts["OwnerProjectionVersions"]);
                Assert.Equal(8, fixture.Counts["SegmentProjectionVersions"]);
                originalCounts = fixture.Counts;
            }

            await using (var reopened = await DatabaseLoadFixture.OpenAsync(directory.FullName, scale, dimension))
            {
                var records = await new StatisticsReadService(reopened.Factory).GetRecordsAsync(reopened.OwnerId);
                Assert.NotEmpty(records);
                var efforts = await new SegmentReadService(reopened.Factory).GetEffortsAsync(reopened.OwnerId, reopened.SegmentId, new(), new());
                Assert.NotNull(efforts);
                Assert.NotEmpty(efforts.Items);

                // Reproduce the physical shape of a retained v1 fixture.
                await using var db = await reopened.Factory.CreateDbContextAsync();
                await db.Database.ExecuteSqlRawAsync("""
                    DROP TABLE WatchedImportAdmissions;
                    DROP TABLE ImportBatchCheckpoints;
                    DROP TABLE OwnerProjectionVersions;
                    DROP TABLE SegmentProjectionVersions;
                    """);
            }
            await File.WriteAllTextAsync(path + ".manifest", $"activity-explorer-load-v1:{dimension}:{scale}");
            await using var legacy = await DatabaseLoadFixture.OpenAsync(directory.FullName, scale, dimension);
            Assert.Equal(
                originalCounts.Where(pair => !DatabaseLoadFixture.IsLifecycleTable(pair.Key)).OrderBy(pair => pair.Key),
                legacy.Counts.OrderBy(pair => pair.Key));
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal", ".manifest" }) File.Delete(path + suffix);
            directory.Delete();
        }
    }
}
