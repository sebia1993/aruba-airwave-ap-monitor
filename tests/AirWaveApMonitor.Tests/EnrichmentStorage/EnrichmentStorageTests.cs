using System.Collections.Immutable;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Storage;

namespace AirWaveApMonitor.Tests.Storage;

public sealed partial class SqliteCollectionStoreTests
{
    private Task<CollectionContext> BeginEnrichment(CollectionKind kind, SqliteCollectionStore? target = null)
        => (target ?? store).BeginRunAsync(Profile(), kind, RunTrigger.Manual, default);
    private EnrichmentBatch Enriched(CollectionContext context, ImmutableArray<EnrichmentTargetBinding> targets, decimal watts = 9.2m)
    {
        var neighbors = ImmutableArray.CreateBuilder<NeighborObservation>();
        var ports = ImmutableArray.CreateBuilder<PortStateObservation>();
        var power = ImmutableArray.CreateBuilder<PowerSample>();
        var states = ImmutableArray.CreateBuilder<PowerStateObservation>();
        var at = clock.GetUtcNow(); var evidence = new FieldEvidence(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.Demo, at, context.StartedAtUtc);
        foreach (var target in targets)
        {
            var local = new ConnectionEndpoint("switch-1", target.SourceApId, EndpointRole.Switch);
            var remote = new ConnectionEndpoint(target.SourceApId, "eth0", EndpointRole.AccessPoint);
            if (context.Kind == CollectionKind.ConnectionNeighbor)
            {
                var timing = new SourceTiming(at, context.StartedAtUtc, SourceTimeMeaning.LastObserved, true);
                var connection = new ConnectionObservation(Guid.NewGuid(), context.RunId, target.Profile, local, remote,
                    DiscoveryProtocol.Lldp, ObservationDirection.LocalToRemote, timing, TimeSpan.FromHours(1), evidence, true,
                    new(context.StartedAtUtc, context.StartedAtUtc.AddHours(1), true), target);
                neighbors.Add(new(new(target.Profile, local, remote), RunOutcome.Succeeded, ObservationCompleteness.Complete, true, false, timing, TimeSpan.FromHours(1), [connection]));
                ports.Add(new(Guid.NewGuid(), context.RunId, target.Profile, local, PortAdministrativeState.Up, PortOperationalState.Up, evidence, evidence));
            }
            else
            {
                var timing = new SourceTiming(at, context.StartedAtUtc, SourceTimeMeaning.Measurement, true);
                power.Add(new(Guid.NewGuid(), context.RunId, target.Profile, remote, PowerMetric.ApConsumption, watts, "W", true, timing, evidence, target));
                power.Add(new(Guid.NewGuid(), context.RunId, target.Profile, local, PowerMetric.PortActual, watts, "W", true, timing, evidence));
                states.Add(new(Guid.NewGuid(), context.RunId, target.Profile, local, PowerSupplyState.On, "On", PowerSupplyMethod.Poe, "PoE", 0, true, evidence, evidence, evidence, evidence));
            }
        }
        return new(context.Kind, RunOutcome.Succeeded, [], [], ObservationCompleteness.Complete, at, [])
        { Detail = new(neighbors.ToImmutable(), ports.ToImmutable(), power.ToImmutable(), states.ToImmutable()) };
    }
    private async Task<CollectionContext> ApplyEnrichment(CollectionKind kind, decimal watts = 9.2m)
    {
        var run = await BeginEnrichment(kind); var targets = await store.GetFrozenEnrichmentTargetsAsync(run, default);
        await store.ApplyEnrichmentBatchAsync(run, Enriched(run, targets, watts), default); return run;
    }

    [Fact]
    public async Task EnrichmentTargets_FreezePlacementAndIdentityBeforeNetwork()
    {
        var row = await SeedOperations(); var firstPoint = Guid.NewGuid(); var secondPoint = Guid.NewGuid();
        await store.SavePointAsync(new(Guid.NewGuid(), firstPoint, 0, "첫 지점", null, new()), default);
        await store.SavePointAsync(new(Guid.NewGuid(), secondPoint, 0, "다음 지점", null, new()), default);
        await store.MovePlacementAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 0, firstPoint, 1, "등록"), default);
        var run = await BeginEnrichment(CollectionKind.ConnectionNeighbor);
        var frozen = await store.GetFrozenEnrichmentTargetsAsync(run, default);
        await store.MovePlacementAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 1, secondPoint, 1, "이동"), default);
        var again = await store.GetFrozenEnrichmentTargetsAsync(run, default);
        Assert.Equal(JsonSerializer.Serialize(frozen), JsonSerializer.Serialize(again));
        var target = frozen.Single(t => t.ApId == row.ApId);
        Assert.Equal(firstPoint, target.Placement!.InstallationPointId); Assert.Equal(row.MonitoringIntervalId, target.MonitoringIntervalId);
        Assert.True(target.IdentityWindow!.ContinuityVerified); Assert.Equal(EvidenceSource.Demo, target.Profile.Source);
    }

    [Fact]
    public async Task EnrichmentFailure_DoesNotRollbackApRunOrItsSuccessClocks()
    {
        await SeedOperations(); var ap = JsonSerializer.Serialize(await Dashboard());
        var success = await store.GetSuccessTimesAsync(location.DatasetId, 1, default);
        var run = await BeginEnrichment(CollectionKind.Power);
        var receipt = await store.ApplyEnrichmentBatchAsync(run, new(CollectionKind.Power, RunOutcome.Failed, [], [], ObservationCompleteness.Unknown,
            clock.GetUtcNow(), [new(CollectionErrorCategory.Transport, 1, "연결 실패")]), default);
        Assert.Equal(RunOutcome.Failed, receipt.Outcome); Assert.Equal(EnrichmentApplicationDisposition.Current, receipt.Disposition);
        Assert.Equal(ap, JsonSerializer.Serialize(await Dashboard())); Assert.Equal(success, await store.GetSuccessTimesAsync(location.DatasetId, 1, default));
        var health = await store.GetEnrichmentHealthAsync(Profile(), CollectionKind.Power, default);
        Assert.True(health.HasQualityGap); Assert.False(health.StorageRecoveryPending); Assert.Null(health.LastValidCommittedAtUtc);
        Assert.Null((await store.GetRunAsync(location.DatasetId, run.RunId, default)).Summary);
    }

    [Fact]
    public async Task EnrichmentRunningKinds_RestartRecoversIndependentlyWithoutFakeApRows()
    {
        await SeedOperations(); var apRun = (await Dashboard()).RunId;
        var connection = await BeginEnrichment(CollectionKind.ConnectionNeighbor); var power = await BeginEnrichment(CollectionKind.Power);
        Assert.True((await store.GetEnrichmentHealthAsync(Profile(), CollectionKind.ConnectionNeighbor, default)).StorageRecoveryPending);
        Assert.True((await store.GetEnrichmentHealthAsync(Profile(), CollectionKind.Power, default)).StorageRecoveryPending);
        var reopened = NewStore(); await reopened.InitializeAsync(default);
        foreach (var run in new[] { connection, power })
        {
            var result = await reopened.GetEnrichmentRunAsync(location.DatasetId, run.RunId, default);
            Assert.Equal(RunLifecycle.Interrupted, result.Run.Lifecycle); Assert.Equal(RunOutcome.Failed, result.Run.Outcome);
            Assert.False((await reopened.GetEnrichmentHealthAsync(Profile(), run.Kind, default)).StorageRecoveryPending);
        }
        Assert.Equal(apRun, (await Dashboard(target: reopened)).RunId); Assert.Equal(2, Count("run_rows"));
    }

    [Fact]
    public async Task EnrichmentLostReceipt_ReplaysOnce_AndConflictingPayloadCannotRewriteResult()
    {
        await SeedOperations();
        var failure = NewStore(b => { if (b == StorageTransactionBoundary.AfterFinalCommit) throw new IOException("receipt lost"); });
        var run = await BeginEnrichment(CollectionKind.ConnectionNeighbor, failure);
        var batch = Enriched(run, await failure.GetFrozenEnrichmentTargetsAsync(run, default));
        await Assert.ThrowsAsync<IOException>(() => failure.ApplyEnrichmentBatchAsync(run, batch, default));
        Assert.True((await failure.ApplyEnrichmentBatchAsync(run, batch, default)).AlreadyApplied);
        Assert.True((await failure.InterruptEnrichmentRunAsync(run, default)).AlreadyApplied);
        await Assert.ThrowsAsync<InvalidDataException>(() => failure.ApplyEnrichmentBatchAsync(run, batch with { Outcome = RunOutcome.Failed }, default));
        Assert.Equal(2, Count("run_results"));
    }

    [Fact]
    public async Task EnrichmentBeforeCommitFailure_RollsBackFactsEventsAndTypedReceipt()
    {
        await SeedOperations(); var beforeEvents = Count("operation_events");
        var failure = NewStore(b => { if (b == StorageTransactionBoundary.BeforeFinalCommit) throw new IOException("transaction failed"); });
        var run = await BeginEnrichment(CollectionKind.ConnectionNeighbor, failure);
        await Assert.ThrowsAsync<IOException>(async () => await failure.ApplyEnrichmentBatchAsync(run, Enriched(run, await failure.GetFrozenEnrichmentTargetsAsync(run, default)), default));
        Assert.Equal(1, Count("run_results")); Assert.Equal(beforeEvents, Count("operation_events"));
        Assert.True((await failure.GetEnrichmentHealthAsync(Profile(), run.Kind, default)).StorageRecoveryPending);
    }

    [Fact]
    public async Task LowerRunIdEnrichmentCommittedLater_NeverLeaksIntoPastApOrPowerQuery()
    {
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        await ApplyEnrichment(CollectionKind.ConnectionNeighbor); await ApplyEnrichment(CollectionKind.Power);
        var late = await BeginEnrichment(CollectionKind.Power); var targets = await store.GetFrozenEnrichmentTargetsAsync(late, default);
        var ap = await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        Assert.True(late.RunId < ap.RunId);
        var before = await Dashboard(ap.RunId); var row = before.Rows.Single(r => r.SourceApId == "100");
        var incidentBefore = await store.GetIncidentAsync(new(location.DatasetId, 1, ap.RunId), row.OpenIncidentId!.Value, default);
        Assert.NotEmpty(row.EnrichmentDetail!.PowerSamples);
        await store.ApplyEnrichmentBatchAsync(late, Enriched(late, targets, 88m), default);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await Dashboard(ap.RunId)));
        Assert.Equal(JsonSerializer.Serialize(incidentBefore), JsonSerializer.Serialize(await store.GetIncidentAsync(new(location.DatasetId, 1, ap.RunId), row.OpenIncidentId.Value, default)));
        var past = await store.GetPowerSamplesAsync(new(location.DatasetId, 1, ap.RunId, row.ApId, row.DeviceGenerationId), default);
        Assert.NotEmpty(past.Rows); Assert.DoesNotContain(past.Rows, sample => sample.Normalized.Watts == 88m);
        var now = await store.GetPowerSamplesAsync(new(location.DatasetId, 1, ApId: row.ApId, DeviceGenerationId: row.DeviceGenerationId), default);
        Assert.Contains(now.Rows, sample => sample.Normalized.Watts == 88m);
    }

    [Fact]
    public async Task PowerRetention_ExpiresNumericDetailButKeepsPermanentIncidentEvidence()
    {
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        await ApplyEnrichment(CollectionKind.ConnectionNeighbor); await ApplyEnrichment(CollectionKind.Power);
        var ap = await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        var row = (await Dashboard()).Rows.Single(r => r.SourceApId == "100");
        var incident = await store.GetIncidentAsync(new(location.DatasetId, 1, ap.RunId), row.OpenIncidentId!.Value, default);
        var before = JsonSerializer.Serialize(incident);
        await new RetentionService(location, gate, store).PruneAsync(clock.GetUtcNow().AddDays(31), default);
        Assert.Equal(before, JsonSerializer.Serialize(await store.GetIncidentAsync(new(location.DatasetId, 1, ap.RunId), row.OpenIncidentId.Value, default)));
        Assert.True((await Dashboard(ap.RunId)).Rows.Single(r => r.ApId == row.ApId).EnrichmentDetail!.PowerSamplesExpired);
        var page = await store.GetPowerSamplesAsync(new(location.DatasetId, 1, ap.RunId, row.ApId, row.DeviceGenerationId), default);
        Assert.Empty(page.Rows); Assert.True(page.MayIncludeExpiredSamples); Assert.NotNull(page.ExpiredBeforeUtc);
    }
}
