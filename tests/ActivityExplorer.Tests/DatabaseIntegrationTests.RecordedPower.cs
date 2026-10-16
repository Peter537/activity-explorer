using ActivityExplorer.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Activity_detail_preserves_recorded_zero_and_missing_power_summaries()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Recorded summaries");
        var zero = await setup.SeedActivityAsync(owner, "Recorded zero", SportKind.Cycling);
        var positive = await setup.SeedActivityAsync(owner, "Recorded power", SportKind.Cycling);
        var missing = await setup.SeedActivityAsync(owner, "Missing summaries", SportKind.Running);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            await db.Activities.Where(x => x.Id == zero).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.AveragePowerWatts, 0d).SetProperty(x => x.MaxPowerWatts, 0d)
                .SetProperty(x => x.NormalizedPowerWatts, 0d).SetProperty(x => x.Kilojoules, 0d));
            await db.Activities.Where(x => x.Id == missing).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.AveragePowerWatts, (double?)null).SetProperty(x => x.MaxPowerWatts, (double?)null)
                .SetProperty(x => x.NormalizedPowerWatts, (double?)null).SetProperty(x => x.Kilojoules, (double?)null));
            await db.Activities.Where(x => x.Id == positive).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.AveragePowerWatts, 210d).SetProperty(x => x.MaxPowerWatts, 520d)
                .SetProperty(x => x.NormalizedPowerWatts, 235d).SetProperty(x => x.Kilojoules, 756.5d));
        }
        var service = ReportingService(setup);
        var recorded = Assert.IsType<ActivityExplorer.Core.Models.ActivityDetail>(await service.GetAsync(zero));
        Assert.Equal(0, recorded.Summary.AveragePowerWatts);
        Assert.Equal(0, recorded.MaxPower);
        Assert.Equal(0, recorded.NormalizedPower);
        Assert.Equal(0, recorded.Kilojoules);
        var absent = Assert.IsType<ActivityExplorer.Core.Models.ActivityDetail>(await service.GetAsync(missing));
        Assert.Null(absent.Summary.AveragePowerWatts);
        Assert.Null(absent.MaxPower);
        Assert.Null(absent.NormalizedPower);
        Assert.Null(absent.Kilojoules);
        var source = Assert.IsType<ActivityExplorer.Core.Models.ActivityDetail>(await service.GetAsync(positive));
        Assert.Equal(210, source.Summary.AveragePowerWatts);
        Assert.Equal(520, source.MaxPower);
        Assert.Equal(235, source.NormalizedPower);
        Assert.Equal(756.5, source.Kilojoules);
    }
}
