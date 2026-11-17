using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Storage;
using AirWaveApMonitor.Infrastructure.Maintenance;

namespace AirWaveApMonitor.Tests.Storage;

public sealed partial class SqliteCollectionStoreTests
{
    [Fact]
    public async Task EnrichmentRepeatedBackwardsNeighborTimes_NeverReplaceLastAcceptedEvidence()
    {
        await SeedOperations(); var initial = await ApplyEnrichment(CollectionKind.ConnectionNeighbor);
        var original = (await store.GetEnrichmentRunAsync(location.DatasetId, initial.RunId, default)).Evidence.Connections.SelectMany(c => c.RetainedConnections).Select(c => c.Id).Order().ToArray();
        foreach (var age in new[] { 2, 1 })
        {
            var run = await BeginEnrichment(CollectionKind.ConnectionNeighbor); var batch = Enriched(run, await store.GetFrozenEnrichmentTargetsAsync(run, default));
            var rows = batch.Detail!.Neighbors.Select(n => n with
            {
                Timing = n.Timing with { SourceAtUtc = initial.StartedAtUtc.AddMinutes(-age) },
                Neighbors = n.Neighbors.Select(r => r with { Timing = r.Timing with { SourceAtUtc = initial.StartedAtUtc.AddMinutes(-age) } }).ToImmutableArray()
            }).ToImmutableArray();
            var receipt = await store.ApplyEnrichmentBatchAsync(run, batch with { Detail = new(rows, [], []) }, default);
            Assert.Equal(RunOutcome.Failed, receipt.Outcome);
            var detail = await store.GetEnrichmentRunAsync(location.DatasetId, run.RunId, default);
            Assert.Equal(original, detail.Evidence.Connections.SelectMany(c => c.RetainedConnections).Select(c => c.Id).Order());
            Assert.All(detail.Evidence.Connections, c => { Assert.False(c.Assessment.HasCurrentLldpObservation); Assert.True(c.HasQualityGap); });
        }
    }
    [Fact]
    public async Task EnrichmentFailedBatchCannotClearPreviouslyAcceptedNeighbor()
    {
        await SeedOperations(); await ApplyEnrichment(CollectionKind.ConnectionNeighbor);
        var failed = await BeginEnrichment(CollectionKind.ConnectionNeighbor); var batch = Enriched(failed, await store.GetFrozenEnrichmentTargetsAsync(failed, default));
        await store.ApplyEnrichmentBatchAsync(failed, batch with { Outcome = RunOutcome.Failed, Detail = new(batch.Detail!.Neighbors.Select(n => n with { Neighbors = [] }).ToImmutableArray(), [], []) }, default);
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        foreach (var row in (await Dashboard()).Rows)
        {
            Assert.NotEmpty(row.EnrichmentDetail!.Connections.SelectMany(c => c.RetainedConnections));
            Assert.Contains(row.EnrichmentDetail.Gaps, g => g.Kind == CollectionKind.ConnectionNeighbor && g.ResumedRunId is null);
        }
    }
    [Fact]
    public async Task EnrichmentInvalidPortField_PreservesThatFieldAndAcceptsIndependentValidField()
    {
        await SeedOperations(); await ApplyEnrichment(CollectionKind.ConnectionNeighbor);
        var run = await BeginEnrichment(CollectionKind.ConnectionNeighbor); var batch = Enriched(run, await store.GetFrozenEnrichmentTargetsAsync(run, default));
        var ports = batch.Detail!.Ports.Select(p => p with { AdministrativeState = PortAdministrativeState.Down,
            OperationalState = PortOperationalState.Down, OperationalEvidence = p.OperationalEvidence with { Validity = FieldValidity.Invalid } }).ToImmutableArray();
        var receipt = await store.ApplyEnrichmentBatchAsync(run, batch with { Detail = batch.Detail with { Ports = ports } }, default);
        Assert.Equal(RunOutcome.Partial, receipt.Outcome);
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        Assert.All((await Dashboard()).Rows.SelectMany(r => r.EnrichmentDetail!.Ports), p =>
        { Assert.Equal(PortAdministrativeState.Down, p.Assessment.AdministrativeState); Assert.Equal(PortOperationalState.Up, p.Assessment.OperationalState); Assert.True(p.HasQualityGap); });
    }
    [Fact]
    public async Task EnrichmentRepeatedStatesAndNumericChanges_DoNotCreateStateEvents()
    {
        await SeedOperations(); await ApplyEnrichment(CollectionKind.ConnectionNeighbor); await ApplyEnrichment(CollectionKind.Power);
        var before = Count("operation_events");
        await ApplyEnrichment(CollectionKind.ConnectionNeighbor); await ApplyEnrichment(CollectionKind.Power, 99m);
        Assert.Equal(before, Count("operation_events")); Assert.Equal(8, Count("power_samples"));
        var run = await BeginEnrichment(CollectionKind.Power); var batch = Enriched(run, await store.GetFrozenEnrichmentTargetsAsync(run, default));
        await store.ApplyEnrichmentBatchAsync(run, batch with { Detail = batch.Detail! with { PowerStates = batch.Detail.PowerStates.Select(s => s with { SupplyState = PowerSupplyState.Off }).ToImmutableArray() } }, default);
        Assert.Equal(before + 2, Count("operation_events"));
        Assert.Equal(2, (await store.GetTimelineAsync(new(Kind: OperationHistoryKind.Connection), default)).TotalRows);
    }
    [Fact]
    public async Task EnrichmentLateOldScope_DoesNotPromoteNewScopeHealth()
    {
        await SeedOperations(); var old = await BeginEnrichment(CollectionKind.Power); var targets = await store.GetFrozenEnrichmentTargetsAsync(old, default);
        var next = await Begin(profile: Profile(2)); await store.ApplyApBatchAsync(next, Batch(Observation("100", ApState.Up), Observation("200", ApState.Up)), default);
        var receipt = await store.ApplyEnrichmentBatchAsync(old, Enriched(old, targets), default);
        Assert.Equal(EnrichmentApplicationDisposition.HistoricalOnly, receipt.Disposition);
        Assert.Null((await store.GetEnrichmentHealthAsync(Profile(2), CollectionKind.Power, default)).LastValidCommittedAtUtc);
        Assert.Equal(4, Count("power_samples")); Assert.Empty((await store.GetPowerSamplesAsync(new(location.DatasetId, 2), default)).Rows);
    }
    [Fact]
    public async Task LiveEnrichmentTargets_HaveNoInventedIdentityContinuityWindow()
    {
        var livePath = Path.Combine(directory, "live"); var liveLocation = StoragePaths.Open(AppMode.Live, livePath);
        var live = new SqliteCollectionStore(liveLocation, gate, clock);
        var profile = new ProfileSnapshot(instance, liveLocation.DatasetId, new("https://airwave.example.invalid/"), 1, 1, [new("100")], BaselineStatus.Unverified, new()) { CredentialsVersion = 1 };
        var ap = await live.BeginRunAsync(profile, CollectionKind.ApStatus, RunTrigger.Manual, default);
        var observation = Observation("100", ApState.Up); observation = observation with { Fields = observation.Fields.ToImmutableDictionary(p => p.Key, p => p.Value with { Source = EvidenceSource.AirWave }) };
        await live.ApplyApBatchAsync(ap, Batch(observation), default);
        var run = await live.BeginRunAsync(profile, CollectionKind.Power, RunTrigger.Manual, default);
        var target = Assert.Single(await live.GetFrozenEnrichmentTargetsAsync(run, default));
        Assert.Null(target.IdentityWindow); Assert.Equal(EvidenceSource.AirWave, target.Profile.Source);
    }
}
