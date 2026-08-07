using System.Net;
using ActivityExplorer.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace ActivityExplorer.Tests;

public sealed class WebHostIntegrationTests
{
    [Fact]
    public void AntiforgeryCookieNameIsStableAndRootScoped()
    {
        var firstRoot = Path.Combine(Path.GetTempPath(), "activity-explorer-security-one");
        var secondRoot = Path.Combine(Path.GetTempPath(), "activity-explorer-security-two");

        var first = AppSecurityIdentifiers.GetAntiforgeryCookieName(firstRoot);

        Assert.Equal(first, AppSecurityIdentifiers.GetAntiforgeryCookieName(firstRoot));
        Assert.NotEqual(first, AppSecurityIdentifiers.GetAntiforgeryCookieName(secondRoot));
        Assert.StartsWith(AppSecurityIdentifiers.AntiforgeryCookiePrefix, first, StringComparison.Ordinal);
        Assert.DoesNotContain(firstRoot, first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WebHostServesInteractiveBlazorAssetsAndUsesScopedAntiforgeryCookie()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"activity-explorer-web-{Guid.NewGuid():N}");
        var previousDataRoot = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_DATA");
        Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", dataRoot);

        try
        {
            await using (var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.UseEnvironment("Production")))
            {
                using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

                using var profilesResponse = await client.GetAsync("/profiles");
                Assert.Equal(HttpStatusCode.OK, profilesResponse.StatusCode);
                var profilesHtml = await profilesResponse.Content.ReadAsStringAsync();
                Assert.Contains("_framework/blazor.web.js", profilesHtml, StringComparison.Ordinal);

                using var frameworkResponse = await client.GetAsync("/_framework/blazor.web.js");
                Assert.Equal(HttpStatusCode.OK, frameworkResponse.StatusCode);
                Assert.Contains("javascript", frameworkResponse.Content.Headers.ContentType?.MediaType ?? "", StringComparison.OrdinalIgnoreCase);
                var frameworkScript = await frameworkResponse.Content.ReadAsStringAsync();
                Assert.True(frameworkScript.Length > 100_000);
                Assert.DoesNotContain("<!DOCTYPE", frameworkScript, StringComparison.OrdinalIgnoreCase);

                using var negotiateResponse = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", content: null);
                Assert.Equal(HttpStatusCode.OK, negotiateResponse.StatusCode);

                var expectedCookieName = AppSecurityIdentifiers.GetAntiforgeryCookieName(dataRoot);
                var setCookies = profilesResponse.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];
                Assert.Contains(setCookies, value => value.StartsWith(expectedCookieName + "=", StringComparison.Ordinal));
                Assert.DoesNotContain(setCookies, value => value.Contains(dataRoot, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", previousDataRoot);
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }
}
