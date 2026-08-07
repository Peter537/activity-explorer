using System.Reflection;

namespace ActivityExplorer.Tests;

public sealed class VersionTests
{
    [Fact]
    public void Product_version_is_pinned_to_0_1_0()
    {
        var assembly = typeof(ActivityExplorer.Core.Domain.Activity).Assembly;
        Assert.Equal("0.1.0.0", assembly.GetName().Version?.ToString());
        Assert.Equal("0.1.0", assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    }
}
