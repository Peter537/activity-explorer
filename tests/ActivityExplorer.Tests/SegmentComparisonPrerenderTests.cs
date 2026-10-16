using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace ActivityExplorer.Tests;

public sealed class SegmentComparisonPrerenderTests
{
    [Fact]
    public async Task Segment_comparison_static_render_disposal_does_not_attempt_Javascript()
    {
        var js = new PrerenderJsRuntime();
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(js).BuildServiceProvider();
        await using (var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>()))
        {
            var detail = new SegmentDetail(new(Guid.NewGuid(), Guid.NewGuid(), "Athlete", "Section", SportKind.Running,
                1000, 30, 0, null, null, null, null), [], [], null, [], [], []);
            var result = new SegmentComparisonResult(detail, null, null, null, null, [], [],
                SegmentComparisonUnavailableReason.InvalidEfforts, "Choose two efforts.");
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var component = await renderer.RenderComponentAsync<SegmentComparisonView>(ParameterView.FromDictionary(
                    new Dictionary<string, object?> { ["Result"] = result, ["InspectionGroupId"] = "static-comparison" }));
                Assert.Contains("Aligned comparison unavailable", component.ToHtmlString(), StringComparison.Ordinal);
            });
        }
        Assert.Empty(js.Calls);
    }

    private sealed class PrerenderJsRuntime : IJSRuntime
    {
        public List<string> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Add(identifier);
            throw new InvalidOperationException("JavaScript interop cannot be issued while statically rendering.");
        }
    }
}
