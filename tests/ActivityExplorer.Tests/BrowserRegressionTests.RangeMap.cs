using System.Text;
using Microsoft.Playwright;

namespace ActivityExplorer.Tests;

public sealed partial class BrowserRegressionTests
{
    private static readonly double[] GapMapSourcePositions = [3, 4];
    private static readonly double[] FractionalMapSourcePositions = [0.5, 3.5];
    [Fact]
    public async Task Activity_map_keeps_source_identity_gap_runs_and_fractional_selection_without_refitting()
    {
        if (Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_BROWSER_TESTS") != "1") return;
        var root = FindRepositoryRoot();
        var dataRoot = TestSupport.NewDirectory();
        var origin = $"http://127.0.0.1:{ReservePort()}";
        var output = new StringBuilder();
        using var process = StartApplication(FindWebAssembly(root), dataRoot, origin, output);
        try
        {
            await WaitUntilReadyAsync(origin, process, output);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync();
            var errors = new List<string>();
            var externalRequests = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            page.Request += (_, request) =>
            {
                if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && uri.Host != "127.0.0.1")
                    externalRequests.Add(request.Url);
            };
            await page.GotoAsync(origin + "/activities", new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.EvaluateAsync("""
                async () => {
                    const maplibre = await import('/vendor/maplibre-gl.mjs');
                    const originalAddSource = maplibre.Map.prototype.addSource;
                    const originalFitBounds = maplibre.Map.prototype.fitBounds;
                    window.testMaps = {};
                    window.testFits = 0;
                    maplibre.Map.prototype.addSource = function (...args) {
                        window.testMaps[this.getContainer().id] = this;
                        return originalAddSource.apply(this, args);
                    };
                    maplibre.Map.prototype.fitBounds = function (...args) {
                        window.testFits++;
                        return originalFitBounds.apply(this, args);
                    };
                    const group = document.createElement('div');
                    group.id = 'range-map-inspection';
                    document.body.append(group);
                    for (const id of ['range-map', 'legacy-map']) {
                        const container = document.createElement('div');
                        container.id = id;
                        container.style.cssText = 'width:500px;height:300px';
                        group.append(container);
                    }
                    const point = (sourcePosition, longitude) => ({ sourcePosition, latitude: 55, longitude });
                    window.testTrack = { runs: [[point(0, 10), point(1, 10.01)], [point(3, 10.03), point(4, 10.04)]] };
                    window.testSelection = {
                        runs: [[point(0.5, 10.005), point(1, 10.01)], [point(3, 10.03), point(3.5, 10.035)]],
                        start: point(0.5, 10.005), end: point(3.5, 10.035)
                    };
                    await activityExplorerMap.create('range-map', {
                        blankBaseMap: true, activityTrack: window.testTrack, inspectionGroupId: group.id
                    }, null);
                    await activityExplorerMap.create('legacy-map', {
                        blankBaseMap: true, inlineCoordinates: [[10, 55], [11, 55], [12, 55], [13, 55]],
                        selectionStartIndex: 1, selectionEndIndex: 2, selectionReversed: true, showSelectionEndpoints: true
                    }, null);
                }
                """);
            await page.WaitForFunctionAsync("() => testMaps['range-map']?.getSource('selection-endpoints') && testMaps['legacy-map']?.getSource('selection-endpoints') && testFits === 2");
            await page.WaitForFunctionAsync("() => testMaps['range-map'].loaded() && testMaps['legacy-map'].loaded()");
            Assert.Equal(2, await page.EvaluateAsync<int>("testMaps['range-map'].getSource('inline').serialize().data.features.length"));
            Assert.Equal(GapMapSourcePositions, await page.EvaluateAsync<double[]>("testMaps['range-map'].getSource('inline').serialize().data.features[1].properties.sourcePositions"));
            Assert.Equal(12, await page.EvaluateAsync<double>("testMaps['legacy-map'].getSource('selection-endpoints').serialize().data.features[0].geometry.coordinates[0]"));
            await page.EvaluateAsync("""
                () => {
                    window.testFits = 0;
                    const source = testMaps['range-map'].getSource('inline');
                    const originalSetData = source.setData;
                    window.testTrackUpdates = 0;
                    source.setData = function (...args) { window.testTrackUpdates++; return originalSetData.apply(this, args); };
                    activityExplorerMap.updateActivitySelection('range-map', window.testSelection);
                }
                """);
            await page.WaitForFunctionAsync("() => testMaps['range-map'].getSource('selected-effort').serialize().data.features.length === 2");
            Assert.Equal(FractionalMapSourcePositions, await page.EvaluateAsync<double[]>("testMaps['range-map'].getSource('selection-endpoints').serialize().data.features.map(feature => feature.properties.sourcePosition)"));
            Assert.Equal(0.38, await page.EvaluateAsync<double>("testMaps['range-map'].getPaintProperty('inline', 'line-opacity')"));
            Assert.Equal(0, await page.EvaluateAsync<int>("testFits"));
            Assert.Equal(0, await page.EvaluateAsync<int>("testTrackUpdates"));
            await page.EvaluateAsync("document.getElementById('range-map-inspection').dispatchEvent(new CustomEvent('activity-explorer:segment-inspection', { detail: { sourceIndex: 3 } }))");
            await page.WaitForFunctionAsync("() => testMaps['range-map'].getSource('inspection-point').serialize().data.features.length === 1");
            Assert.Equal(10.03, await page.EvaluateAsync<double>("testMaps['range-map'].getSource('inspection-point').serialize().data.features[0].geometry.coordinates[0]"));
            await page.EvaluateAsync("document.getElementById('range-map-inspection').dispatchEvent(new CustomEvent('activity-explorer:segment-inspection', { detail: { sourceIndex: 2 } }))");
            await page.WaitForFunctionAsync("() => testMaps['range-map'].getSource('inspection-point').serialize().data.features.length === 0");
            Assert.Equal(0, await page.EvaluateAsync<int>("testMaps['range-map'].getSource('inspection-point').serialize().data.features.length"));
            await page.EvaluateAsync("activityExplorerMap.updateActivitySelection('range-map', { runs: [] })");
            await page.WaitForFunctionAsync("() => testMaps['range-map'].getSource('selected-effort').serialize().data.features.length === 0");
            Assert.Equal(0, await page.EvaluateAsync<int>("testMaps['range-map'].getSource('selection-endpoints').serialize().data.features.length"));
            await page.EvaluateAsync("activityExplorerMap.updateActivitySelection('range-map', null)");
            await page.WaitForFunctionAsync("() => testMaps['range-map'].getPaintProperty('inline', 'line-opacity') === 0.95");
            await page.EvaluateAsync("activityExplorerMap.updateSelection('legacy-map', 0, 3, false)");
            await page.WaitForFunctionAsync("() => testMaps['legacy-map'].getSource('selected-effort').serialize().data.geometry.coordinates.length === 4");
            Assert.Equal(10, await page.EvaluateAsync<double>("testMaps['legacy-map'].getSource('selection-endpoints').serialize().data.features[0].geometry.coordinates[0]"));
            Assert.Equal(0, await page.EvaluateAsync<int>("testFits"));
            await page.EvaluateAsync("""
                () => {
                    activityExplorerMap.destroy('range-map');
                    activityExplorerMap.destroy('legacy-map');
                    document.getElementById('range-map-inspection').dispatchEvent(new CustomEvent('activity-explorer:segment-inspection', { detail: { sourceIndex: 0 } }));
                }
                """);
            Assert.Empty(errors);
            Assert.Empty(externalRequests);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await DeleteDirectoryAsync(dataRoot);
        }
    }
}
