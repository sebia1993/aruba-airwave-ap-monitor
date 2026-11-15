using System.Net;
using System.Text.Json;
using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Demo;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Infrastructure.Storage;
using AirWaveApMonitor.Tests.Settings;
using AirWaveApMonitor.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace AirWaveApMonitor.Tests.Web;

[Collection("Host environment")]
public sealed class ProductionMonitoringTests
{
    [Fact]
    public async Task ProductionReadRoutes_KeepDisplayedRunAcrossLaterFailureAndExport()
    {
        using var directory = new TemporaryDirectory();
        var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", directory.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync();
        using var client = SettingsEndpointTests.Client(port);
        var store = app.Services.GetRequiredService<SqliteCollectionStore>();
        var profile = app.Services.GetRequiredService<ProfileStore>().Current!;
        var source = new ScenarioApSource(TimeProvider.System, store);
        long normalRun = 0;
        for (var frame = 0; frame <= 2; frame++)
        {
            var context = await store.BeginRunAsync(profile, CollectionKind.ApStatus, RunTrigger.Manual, default);
            await store.ApplyApBatchAsync(context, await source.CollectAsync(context, default), default);
            normalRun = context.RunId;
        }
        using var normalResponse = await client.GetAsync("/api/dashboard");
        Assert.Equal(HttpStatusCode.OK, normalResponse.StatusCode);
        Assert.Equal("no-store", normalResponse.Headers.CacheControl!.ToString());
        var normal = JsonDocument.Parse(await normalResponse.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(normalRun, normal.GetProperty("runId").GetInt64());
        Assert.Equal(8, normal.GetProperty("summary").GetProperty("confirmedDown").GetInt32());
        Assert.Equal(8, normal.GetProperty("totalRows").GetInt32());
        var ap = normal.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("sourceApId").GetString() == "2");
        var apId = ap.GetProperty("apId").GetGuid();
        var incidentId = ap.GetProperty("openIncidentId").GetGuid();
        Assert.Equal("Down", ap.GetProperty("currentState").GetString());

        for (var frame = 3; frame <= 5; frame++)
        {
            var context = await store.BeginRunAsync(profile, CollectionKind.ApStatus, RunTrigger.Manual, default);
            await store.ApplyApBatchAsync(context, await source.CollectAsync(context, default), default);
        }
        var latest = JsonDocument.Parse(await client.GetStringAsync("/api/dashboard")).RootElement;
        Assert.Equal("Failed", latest.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, latest.GetProperty("summary").GetProperty("confirmedDown").ValueKind);
        Assert.Equal(8, latest.GetProperty("summary").GetProperty("openIncidents").GetInt32());
        var detail = JsonDocument.Parse(await client.GetStringAsync($"/api/aps/{apId}?runId={normalRun}")).RootElement;
        Assert.Equal(normalRun, detail.GetProperty("selection").GetProperty("runId").GetInt64());
        Assert.Equal("Down", detail.GetProperty("row").GetProperty("currentState").GetString());
        var incident = JsonDocument.Parse(await client.GetStringAsync($"/api/incidents/{incidentId}?asOfRunId={normalRun}")).RootElement;
        Assert.Equal("Open", incident.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, incident.GetProperty("recovery").ValueKind);
        using var csv = await client.GetAsync($"/api/export.csv?runId={normalRun}&scopeVersion=1&filter=Down&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal(normalRun.ToString(), csv.Headers.GetValues("X-AirWave-Run-Id").Single());
        Assert.Equal("8", csv.Headers.GetValues("X-AirWave-Row-Count").Single());
        var bytes = await csv.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, bytes.Take(3));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/dashboard?runId={normalRun}&scopeVersion=99")).StatusCode);
    }

    [Theory]
    [InlineData("/api/dashboard", "Host", "attacker.invalid", HttpStatusCode.BadRequest)]
    [InlineData("/api/runs", "Origin", "https://attacker.invalid", HttpStatusCode.Forbidden)]
    [InlineData("/api/export.csv?runId=1", "Origin", "https://attacker.invalid", HttpStatusCode.Forbidden)]
    public async Task MonitoringRoutes_RemainInsideProductionRequestBoundary(string path, string header, string value, HttpStatusCode expected)
    {
        using var directory = new TemporaryDirectory();
        var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", directory.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(header, value);
        Assert.Equal(expected, (await client.SendAsync(request)).StatusCode);
    }
}
