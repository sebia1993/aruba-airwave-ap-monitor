using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using AirWaveApMonitor.App.Collection;
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
public sealed class ProductionEnrichmentTests
{
    [Fact]
    public async Task ActualRunnerStoreAndHttpKeepApFactsAndHistoricalSnapshotsWhenPowerIsPartial()
    {
        using var directory = new TemporaryDirectory(); var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", directory.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync();
        var runner = Assert.IsType<EnrichmentRunner>(app.Services.GetRequiredService<IEnrichmentRunner>());
        var source = app.Services.GetRequiredService<DemoEnrichmentSource>();
        var profiles = app.Services.GetRequiredService<ProfileStore>();
        var store = app.Services.GetRequiredService<SqliteCollectionStore>();
        using var client = SettingsEndpointTests.Client(port);
        // The coordinator is paused to make the two-device fixture deterministic. The
        // real runner executes under its required operation gate; HTTP remains real.
        await profiles.OperationGate.WaitAsync();
        try { await profiles.UpdateEnrichmentAsync(15, true, true, profiles.Current!.ConfigurationVersion, default); }
        finally { profiles.OperationGate.Release(); }
        var profile = profiles.Current!;
        await ApRun(ApState.Up);
        await Enrich(CollectionKind.ConnectionNeighbor);
        await Enrich(CollectionKind.Power);
        var down = await ApRun(ApState.Down);
        var apSuccess = await store.GetSuccessTimesAsync(profile.DatasetId, profile.ScopeVersion, default);
        var dashboard = JsonDocument.Parse(await client.GetStringAsync("/api/dashboard")).RootElement;
        var ap = dashboard.GetProperty("rows")[0]; var apId = ap.GetProperty("apId").GetGuid();
        var generation = ap.GetProperty("deviceGenerationId").GetGuid(); var incidentId = ap.GetProperty("openIncidentId").GetGuid();
        var detailPath = $"/api/aps/{apId}?runId={down}&scopeVersion=1";
        var detail = JsonDocument.Parse(await client.GetStringAsync(detailPath)).RootElement;
        var snapshot = detail.GetProperty("row").GetProperty("enrichmentDetail");
        Assert.Equal(6, snapshot.GetProperty("powerSamples").GetArrayLength());
        Assert.True(snapshot.GetProperty("connections")[0].GetProperty("assessment").GetProperty("hasCurrentLldpObservation").GetBoolean());
        var frozen = snapshot.GetRawText();
        var incidentPath = $"/api/incidents/{incidentId}?asOfRunId={down}&scopeVersion=1";
        var incident = JsonDocument.Parse(await client.GetStringAsync(incidentPath)).RootElement;
        var detection = incident.GetProperty("detection").GetRawText();
        Assert.Equal(6, incident.GetProperty("detection").GetProperty("device").GetProperty("enrichmentDetail").GetProperty("powerSamples").GetArrayLength());
        source.Scenario = DemoEnrichmentScenario.MissingPower;
        await Enrich(CollectionKind.Power);
        Assert.Equal(apSuccess, await store.GetSuccessTimesAsync(profile.DatasetId, profile.ScopeVersion, default));
        var later = JsonDocument.Parse(await client.GetStringAsync("/api/dashboard")).RootElement;
        Assert.Equal(down, later.GetProperty("runId").GetInt64());
        Assert.Equal(2, later.GetProperty("summary").GetProperty("confirmedDown").GetInt32());
        Assert.Equal("Succeeded", later.GetProperty("outcome").GetString());
        var history = JsonDocument.Parse(await client.GetStringAsync("/api/enrichment/runs?kind=Power")).RootElement;
        Assert.Equal("Partial", history.GetProperty("rows")[0].GetProperty("outcome").GetString());
        Assert.True(history.GetProperty("rows")[0].GetProperty("hasQualityGap").GetBoolean());
        var samples = JsonDocument.Parse(await client.GetStringAsync("/api/enrichment/power?pageSize=50")).RootElement;
        Assert.Equal(24, samples.GetProperty("totalRows").GetInt32());
        var missing = samples.GetProperty("rows").EnumerateArray().Where(row => row.GetProperty("normalized").GetProperty("reason").GetString() == "Missing").ToArray();
        Assert.Equal(2, missing.Length);
        Assert.All(missing, row => Assert.Equal(JsonValueKind.Null, row.GetProperty("normalized").GetProperty("watts").ValueKind));
        var selected = JsonDocument.Parse(await client.GetStringAsync($"/api/enrichment/power?throughRunId={down}&scopeVersion=1&apId={apId}&deviceGenerationId={generation}")).RootElement;
        Assert.Equal(6, selected.GetProperty("totalRows").GetInt32());
        var retained = JsonDocument.Parse(await client.GetStringAsync(detailPath)).RootElement;
        Assert.Equal(frozen, retained.GetProperty("row").GetProperty("enrichmentDetail").GetRawText());
        var retainedIncident = JsonDocument.Parse(await client.GetStringAsync(incidentPath)).RootElement;
        Assert.Equal(detection, retainedIncident.GetProperty("detection").GetRawText());
        Assert.False(runner.RecoveryPending);
        await app.StopAsync();

        async Task<long> ApRun(ApState state)
        {
            var context = await store.BeginRunAsync(profile, CollectionKind.ApStatus, RunTrigger.Manual, default);
            var now = DateTimeOffset.UtcNow;
            var evidence = new FieldEvidence(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.Demo, now);
            var fields = new[] { "is_up", "name", "serial_number", "lan_mac" }.ToImmutableDictionary(key => key, _ => evidence);
            var rows = Enumerable.Range(1, 2).Select(id => new ApObservation(id.ToString(), state,
                new("synthetic-" + id, "02:00:00:00:00:0" + id), new("AP " + id, "192.0.2." + id, "synthetic", "fixture"),
                new(), new(), now, null, null, fields)).ToImmutableArray();
            var receipt = await store.ApplyApBatchAsync(context, new(RunOutcome.Succeeded, rows, [], now, ObservationCompleteness.Complete, []), default);
            Assert.Equal(RunOutcome.Succeeded, receipt.Outcome); return context.RunId;
        }
        async Task Enrich(CollectionKind kind)
        {
            await profiles.OperationGate.WaitAsync();
            try { await runner.ExecuteAsync(profile, kind, RunTrigger.Manual, () => false, _ => { }, default); }
            finally { profiles.OperationGate.Release(); }
            Assert.False(runner.RecoveryPending);
        }
    }
}
