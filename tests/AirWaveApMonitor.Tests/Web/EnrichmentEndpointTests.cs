using System.Net;
using System.Text.Json;
using AirWaveApMonitor.App.Endpoints;
using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Demo;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Infrastructure.Storage;
using AirWaveApMonitor.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AirWaveApMonitor.Tests.Web;

public sealed class EnrichmentEndpointTests
{
    [Fact]
    public async Task DemoCapabilityShowsFieldEvidenceWithoutImplicitlyEnablingCollection()
    {
        await using var host = await Host.Start();
        var response = await host.Client.GetAsync("/api/enrichment/capabilities");
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("demo", body.GetProperty("mode").GetString());
        var kinds = body.GetProperty("kinds").EnumerateArray().ToArray();
        Assert.Equal(2, kinds.Length); Assert.Equal(13, kinds.Sum(k => k.GetProperty("fields").GetArrayLength()));
        foreach (var kind in kinds)
        {
            Assert.False(kind.GetProperty("enabled").GetBoolean());
            Assert.Equal("AvailableConfirmed", kind.GetProperty("decision").GetProperty("availability").GetString());
            Assert.False(kind.GetProperty("decision").GetProperty("canCollect").GetBoolean());
            foreach (var field in kind.GetProperty("fields").EnumerateArray())
            {
                Assert.Equal("DemoFixture", field.GetProperty("evidence").GetProperty("verification").GetString());
                Assert.Equal("Demo", field.GetProperty("evidence").GetProperty("key").GetProperty("profile").GetProperty("source").GetString());
            }
        }
        Assert.Equal(0, host.Reads.Calls);
    }
    [Fact]
    public async Task LiveCapabilitiesRemainUnverifiedWithoutCallingCollection()
    {
        await using var host = await Host.Start(AppMode.Live);
        var body = JsonDocument.Parse(await host.Client.GetStringAsync("/api/enrichment/capabilities")).RootElement;
        foreach (var kind in body.GetProperty("kinds").EnumerateArray())
        {
            Assert.Equal("Unverified", kind.GetProperty("decision").GetProperty("availability").GetString());
            Assert.False(kind.GetProperty("decision").GetProperty("canCollect").GetBoolean());
            foreach (var field in kind.GetProperty("fields").EnumerateArray())
            {
                Assert.Equal("Unverified", field.GetProperty("decision").GetProperty("availability").GetString());
                Assert.Equal(JsonValueKind.Null, field.GetProperty("evidence").GetProperty("verifiedAtUtc").ValueKind);
                Assert.False(field.GetProperty("evidence").GetProperty("implemented").GetBoolean());
            }
        }
        Assert.Equal(0, host.Reads.Calls);
    }
    [Fact]
    public async Task PowerQueryKeepsExactHistoricalScopeTargetsAndZeroCommitWatermark()
    {
        await using var host = await Host.Start();
        var current = host.Profiles.Current!;
        var historical = new ProfileSnapshot(current.InstanceId, current.DatasetId, current.Origin, 7, 7, [], BaselineStatus.Unverified, new());
        var context = await host.Store.BeginRunAsync(historical, CollectionKind.ApStatus, RunTrigger.Manual, default);
        await host.Store.ApplyApBatchAsync(context, new(RunOutcome.Failed, [], [], DateTimeOffset.UtcNow, ObservationCompleteness.Unknown, []), default);
        var ap = Guid.NewGuid(); var generation = Guid.NewGuid();
        var response = await host.Client.GetAsync($"/api/enrichment/power?throughRunId={context.RunId}&throughCommitSequence=0&apId={ap}&deviceGenerationId={generation}&metric=PortActual&sourceDeviceId=switch-1&sourceInterfaceId=1%2F2&endpointRole=Switch&page=2&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var query = host.Reads.Power!;
        Assert.Equal(7, query.ScopeVersion); Assert.Equal(context.RunId, query.ThroughRunId); Assert.Equal(0, query.ThroughCommitSequence);
        Assert.Equal(ap, query.ApId); Assert.Equal(generation, query.DeviceGenerationId); Assert.Equal(PowerMetric.PortActual, query.Metric);
        Assert.Equal("switch-1", query.SourceDeviceId); Assert.Equal("1/2", query.SourceInterfaceId); Assert.Equal(EndpointRole.Switch, query.EndpointRole);
        Assert.Equal(2, query.Page); Assert.Equal(10, query.PageSize);
        var wrong = await host.Client.GetAsync($"/api/enrichment/power?throughRunId={context.RunId}&scopeVersion=1");
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode); Assert.Equal(1, host.Reads.Calls);
    }
    [Fact]
    public async Task CurrentPowerQueryUsesCurrentScopeAndDoesNotInventAThroughRun()
    {
        await using var host = await Host.Start();
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/enrichment/power")).StatusCode);
        Assert.Equal(host.Profiles.Current!.ScopeVersion, host.Reads.Power!.ScopeVersion);
        Assert.Null(host.Reads.Power.ThroughRunId); Assert.Null(host.Reads.Power.ThroughCommitSequence);
    }
    [Theory]
    [InlineData("/api/enrichment/runs?kind=ApStatus")]
    [InlineData("/api/enrichment/runs?kind=1")]
    [InlineData("/api/enrichment/runs?kind=Power&kind=ConnectionNeighbor")]
    [InlineData("/api/enrichment/power?throughCommitSequence=-1")]
    [InlineData("/api/enrichment/power?throughCommitSequence=1&throughCommitSequence=2")]
    [InlineData("/api/enrichment/power?apId=00000000-0000-0000-0000-000000000000")]
    [InlineData("/api/enrichment/power?metric=99")]
    [InlineData("/api/enrichment/power?pageSize=501")]
    [InlineData("/api/enrichment/power?fromUtc=2026-11-17&toUtc=2026-11-16")]
    public async Task InvalidQueriesDoNotReachTheEvidenceStore(string path)
    {
        await using var host = await Host.Start();
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync(path)).StatusCode);
        Assert.Equal(0, host.Reads.Calls);
    }
    [Fact]
    public async Task StorageErrorsAreReportedWithoutLeakingDatabasePaths()
    {
        await using var host = await Host.Start(); host.Reads.Error = new IOException("secret database path");
        var response = await host.Client.GetAsync("/api/enrichment/runs");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("secret", await response.Content.ReadAsStringAsync());
    }
    private sealed class FakeReads : IEnrichmentReadStore
    {
        public int Calls; public PowerSampleQuery? Power; public Exception? Error;
        private void Count() { Calls++; if (Error is not null) throw Error; }
        public Task<EnrichmentRunPage> GetEnrichmentRunsAsync(EnrichmentRunQuery query, CancellationToken ct)
        { Count(); return Task.FromResult(new EnrichmentRunPage([], 0, query.ThroughRunId, query.Page, query.PageSize)); }
        public Task<EnrichmentRunDetail> GetEnrichmentRunAsync(Guid datasetId, long runId, CancellationToken ct)
        { Count(); throw new KeyNotFoundException(); }
        public Task<PowerSamplePage> GetPowerSamplesAsync(PowerSampleQuery query, CancellationToken ct)
        { Count(); Power = query; return Task.FromResult(new PowerSamplePage([], 0, query.ThroughRunId, query.ThroughCommitSequence ?? 0, query.Page, query.PageSize, null, false)); }
    }
    private sealed class Host(TemporaryDirectory temp, SemaphoreSlim gate, WebApplication app, HttpClient client,
        FakeReads reads, SqliteCollectionStore store, ProfileStore profiles) : IAsyncDisposable
    {
        public HttpClient Client => client; public FakeReads Reads => reads; public SqliteCollectionStore Store => store; public ProfileStore Profiles => profiles;
        public static async Task<Host> Start(AppMode mode = AppMode.Demo)
        {
            var temp = new TemporaryDirectory(); var gate = new SemaphoreSlim(1, 1);
            var location = StoragePaths.Open(mode, temp.Path); var store = new SqliteCollectionStore(location, gate);
            await store.InitializeAsync(default); var profiles = new ProfileStore(location);
            if (mode == AppMode.Live) await profiles.UpdateAsync(new("https://airwave.example.invalid"), 5, new(ProxyMode.Direct), 0, default);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
            builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
            var reads = new FakeReads(); builder.Services.AddSingleton<IEnrichmentReadStore>(reads);
            builder.Services.AddSingleton<IMonitoringReadStore>(store); builder.Services.AddSingleton(profiles); builder.Services.AddSingleton(location);
            builder.Services.AddSingleton(new RuntimeOptions(mode, temp.Path, 8765));
            builder.Services.AddSingleton<IBoundedEnrichmentSource>(mode == AppMode.Demo ? new DemoEnrichmentSource(TimeProvider.System) : new EnrichmentSource(TimeProvider.System));
            var app = builder.Build(); app.MapEnrichment(); await app.StartAsync();
            return new(temp, gate, app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()) }, reads, store, profiles);
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); gate.Dispose(); temp.Dispose(); }
    }
}
