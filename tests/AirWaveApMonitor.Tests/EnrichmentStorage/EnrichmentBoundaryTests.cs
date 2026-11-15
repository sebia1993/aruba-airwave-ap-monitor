using System.Collections.Immutable;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Storage;

namespace AirWaveApMonitor.Tests.Storage;

public sealed partial class SqliteCollectionStoreTests
{
    [Fact]
    public async Task EnrichmentUnknownBeginReceipt_BlocksNetworkUntilTypedRestartRecovery()
    {
        await SeedOperations();
        var failure = NewStore(b => { if (b == StorageTransactionBoundary.AfterBeginCommit) throw new IOException("begin receipt lost"); });
        await Assert.ThrowsAsync<IOException>(() => BeginEnrichment(CollectionKind.Power, failure));
        Assert.True((await failure.GetEnrichmentHealthAsync(Profile(), CollectionKind.Power, default)).StorageRecoveryPending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => BeginEnrichment(CollectionKind.Power, failure));
        var reopened = NewStore(); await reopened.InitializeAsync(default);
        var health = await reopened.GetEnrichmentHealthAsync(Profile(), CollectionKind.Power, default);
        Assert.False(health.StorageRecoveryPending); Assert.Equal(RunOutcome.Failed, health.LastOutcome); Assert.True(health.HasQualityGap);
        Assert.Equal(2, Count("historical_ap_rows")); Assert.Equal(1, Count("run_history_frames"));
    }
    [Fact]
    public async Task EnrichmentAllUnknownSucceededInput_FailsAndPreservesRawMissingPower()
    {
        await SeedOperations(); var run = await BeginEnrichment(CollectionKind.Power); var batch = Enriched(run, await store.GetFrozenEnrichmentTargetsAsync(run, default));
        batch = batch with { Detail = batch.Detail! with { PowerSamples = batch.Detail.PowerSamples.Select(s => s with { Value = null }).ToImmutableArray(), PowerStates = [] } };
        var receipt = await store.ApplyEnrichmentBatchAsync(run, batch, default);
        Assert.Equal(RunOutcome.Failed, receipt.Outcome); Assert.Equal(4, Count("power_samples"));
        var detail = await store.GetEnrichmentRunAsync(location.DatasetId, run.RunId, default);
        Assert.All(detail.Evidence.PowerSamples, s => Assert.Null(s.Normalized.Watts));
        Assert.Null((await store.GetEnrichmentHealthAsync(Profile(), CollectionKind.Power, default)).LastValidCommittedAtUtc);
    }
    [Fact]
    public async Task EnrichmentPartialNeighbor_KeepsConnectionUntilCompleteCurrentAbsence()
    {
        await SeedOperations(); await ApplyEnrichment(CollectionKind.ConnectionNeighbor);
        var partial = await BeginEnrichment(CollectionKind.ConnectionNeighbor); var batch = Enriched(partial, await store.GetFrozenEnrichmentTargetsAsync(partial, default));
        var missing = batch.Detail!.Neighbors.Select(n => n with { Neighbors = [], Outcome = RunOutcome.Partial, Completeness = ObservationCompleteness.Partial }).ToImmutableArray();
        await store.ApplyEnrichmentBatchAsync(partial, batch with { Detail = new(missing, [], []) }, default);
        var held = await store.GetEnrichmentRunAsync(location.DatasetId, partial.RunId, default);
        Assert.All(held.Evidence.Connections, c => { Assert.True(c.HasQualityGap); Assert.Single(c.RetainedConnections); Assert.False(c.Assessment.MayClosePreviousConnection); });
        var complete = await BeginEnrichment(CollectionKind.ConnectionNeighbor); batch = Enriched(complete, await store.GetFrozenEnrichmentTargetsAsync(complete, default));
        await store.ApplyEnrichmentBatchAsync(complete, batch with { Detail = new(batch.Detail!.Neighbors.Select(n => n with { Neighbors = [] }).ToImmutableArray(), [], []) }, default);
        var closed = await store.GetEnrichmentRunAsync(location.DatasetId, complete.RunId, default);
        Assert.All(closed.Evidence.Connections, c => { Assert.Empty(c.RetainedConnections); Assert.Equal(NeighborPresence.ExpectedNeighborNotObserved, c.Assessment.Presence); });
        Assert.Equal(1, (await Dashboard()).Summary!.ConfirmedDown); // seeded AP incidents remain untouched
    }
    [Fact]
    public async Task EnrichmentWrongFrozenGeneration_RejectsWithoutRetargetingOrClaimingSuccess()
    {
        await SeedOperations(); var run = await BeginEnrichment(CollectionKind.Power); var batch = Enriched(run, await store.GetFrozenEnrichmentTargetsAsync(run, default));
        var raw = batch.Detail!.PowerSamples[0]; batch = batch with { Detail = batch.Detail with { PowerSamples = [raw with { DirectTarget = raw.DirectTarget! with { DeviceGenerationId = Guid.NewGuid() } }], PowerStates = [] } };
        var receipt = await store.ApplyEnrichmentBatchAsync(run, batch, default);
        Assert.Equal(EnrichmentApplicationDisposition.RejectedBinding, receipt.Disposition); Assert.False(receipt.Applied);
        Assert.Equal(RunOutcome.Failed, receipt.Outcome); Assert.Equal(0, Count("power_samples")); Assert.Equal(2, Count("generations"));
    }
    [Fact]
    public async Task EnrichmentLateOldGeneration_RemainsHistoricalAndNeverUpdatesNewApSnapshot()
    {
        await SeedOperations(); var run = await BeginEnrichment(CollectionKind.Power); var targets = await store.GetFrozenEnrichmentTargetsAsync(run, default);
        var old = targets.Single(t => t.SourceApId == "100");
        clock.Advance(); // The historical measurement precedes the recorded replacement boundary.
        await Apply(Observation("100", ApState.Up, serial: "replacement"), Observation("200", ApState.Up));
        var before = JsonSerializer.Serialize(await Dashboard());
        var receipt = await store.ApplyEnrichmentBatchAsync(run, Enriched(run, targets), default);
        Assert.Equal(EnrichmentApplicationDisposition.HistoricalOnly, receipt.Disposition); Assert.Equal(before, JsonSerializer.Serialize(await Dashboard()));
        Assert.Null((await store.GetEnrichmentHealthAsync(Profile(), CollectionKind.Power, default)).LastValidCommittedAtUtc);
        var samples = await store.GetPowerSamplesAsync(new(location.DatasetId, 1, ApId: old.ApId, DeviceGenerationId: old.DeviceGenerationId), default);
        Assert.Contains(samples.Rows, s => s.Attribution.Target?.DeviceGenerationId == old.DeviceGenerationId);
    }
    [Fact]
    public async Task EnrichmentSourceUniqueKey_DeduplicatesOnlyVerifiedIdentity_AndConflictsRollback()
    {
        await SeedOperations(); var run = await BeginEnrichment(CollectionKind.Power); var batch = Enriched(run, await store.GetFrozenEnrichmentTargetsAsync(run, default));
        var original = batch.Detail!.PowerSamples[0] with { SourceIdentity = new("source-sample-1", true) };
        await store.ApplyEnrichmentBatchAsync(run, batch with { Detail = new([], [], [original]) }, default);
        var repeated = await BeginEnrichment(CollectionKind.Power); var repeat = original with { Id = Guid.NewGuid(), RunId = repeated.RunId, Timing = original.Timing with { ReadAtUtc = clock.GetUtcNow().AddMinutes(1) } };
        await store.ApplyEnrichmentBatchAsync(repeated, new(CollectionKind.Power, RunOutcome.Succeeded, [], [], ObservationCompleteness.Complete, clock.GetUtcNow(), []) { Detail = new([], [], [repeat]) }, default);
        Assert.Equal(1, Count("power_samples")); Assert.Equal(2, Count("enrichment_power_reads"));
        var conflict = await BeginEnrichment(CollectionKind.Power); var wrong = repeat with { Id = Guid.NewGuid(), RunId = conflict.RunId, Value = 123 };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ApplyEnrichmentBatchAsync(conflict, new(CollectionKind.Power, RunOutcome.Succeeded, [], [], ObservationCompleteness.Complete, clock.GetUtcNow(), []) { Detail = new([], [], [wrong]) }, default));
        Assert.Equal(1, Count("power_samples")); Assert.Equal(2, Count("enrichment_power_reads")); Assert.Equal(3, Count("run_results"));
    }
    [Fact]
    public async Task EnrichmentExpiredUniqueSample_DoesNotReviveNumericHistoryOrSuccessClock()
    {
        await SeedOperations(); var run = await BeginEnrichment(CollectionKind.Power); var batch = Enriched(run, await store.GetFrozenEnrichmentTargetsAsync(run, default));
        var original = batch.Detail!.PowerSamples[0] with { SourceIdentity = new("expired-source-1", true) };
        await store.ApplyEnrichmentBatchAsync(run, batch with { Detail = new([], [], [original]) }, default);
        var before = await store.GetEnrichmentHealthAsync(Profile(), CollectionKind.Power, default);
        await new RetentionService(location, gate, store).PruneAsync(clock.GetUtcNow().AddDays(31), default); clock.Set(clock.GetUtcNow().AddDays(31));
        var repeated = await BeginEnrichment(CollectionKind.Power); var repeat = original with { Id = Guid.NewGuid(), RunId = repeated.RunId, Timing = original.Timing with { ReadAtUtc = clock.GetUtcNow() } };
        var result = await store.ApplyEnrichmentBatchAsync(repeated, new(CollectionKind.Power, RunOutcome.Succeeded, [], [], ObservationCompleteness.Complete, clock.GetUtcNow(), []) { Detail = new([], [], [repeat]) }, default);
        Assert.Equal(RunOutcome.Failed, result.Outcome); Assert.Equal(0, Count("power_samples"));
        Assert.Equal(before.LastValidCommittedAtUtc, (await store.GetEnrichmentHealthAsync(Profile(), CollectionKind.Power, default)).LastValidCommittedAtUtc);
        Assert.True((await store.GetEnrichmentRunAsync(location.DatasetId, repeated.RunId, default)).Evidence.PowerSamplesExpired);
    }
    [Fact]
    public async Task EnrichmentSnapshots_DeduplicateReferencesWithoutChangingMetadataVersions()
    {
        await SeedOperations(); await ApplyEnrichment(CollectionKind.ConnectionNeighbor); await ApplyEnrichment(CollectionKind.Power);
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Down));
        var metadata = Count("metadata_versions"); var references = Count("enrichment_snapshot_refs");
        for (var i = 0; i < 3; i++) { clock.Advance(); await Apply(Observation("100", ApState.Down), Observation("200", ApState.Down)); }
        Assert.Equal(metadata, Count("metadata_versions")); Assert.Equal(references, Count("enrichment_snapshot_refs"));
    }
    [Fact]
    public async Task EnrichmentRestore_PreservesSamplesAndRecordsEachKindGapWithoutFakeApSummary()
    {
        await SeedOperations(); await ApplyEnrichment(CollectionKind.ConnectionNeighbor); await ApplyEnrichment(CollectionKind.Power);
        var backup = new BackupService(location, gate, clock); var saved = await backup.CreateAsync(default);
        clock.Set(clock.GetUtcNow().AddHours(1)); await backup.RestoreAsync(saved.BackupPath, default);
        var reopened = NewStore(); await reopened.InitializeAsync(default);
        Assert.Equal(2, Count("enrichment_restore_events")); Assert.Equal(4, Count("power_samples"));
        foreach (var kind in new[] { CollectionKind.ConnectionNeighbor, CollectionKind.Power })
        {
            var health = await reopened.GetEnrichmentHealthAsync(Profile(), kind, default);
            Assert.True(health.HasQualityGap); Assert.Equal(RunOutcome.Failed, health.LastOutcome);
            var run = await reopened.GetEnrichmentRunAsync(location.DatasetId, health.LastFinalRunId!.Value, default);
            Assert.Equal(saved.CreatedAtUtc, run.Evidence.Gaps.Single(g => g.Kind == kind).FirstMissingAtUtc);
        }
        var count = Count("runs"); await NewStore().InitializeAsync(default); Assert.Equal(count, Count("runs"));
    }
}
