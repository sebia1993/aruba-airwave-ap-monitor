using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Tests.Storage;

public sealed partial class SqliteCollectionStoreTests
{
    [Fact]
    public async Task EnrichmentCache_MultipleSampleTimesReuseBoundsWithoutReusingAttributionDecision()
    {
        var old = await SeedOperations(); await ApplyEnrichment(CollectionKind.ConnectionNeighbor); clock.Advance(); var endedAt = clock.GetUtcNow();
        await Apply(Observation("100", ApState.Up, serial: "NEW"), Observation("200", ApState.Up));
        var run = await BeginEnrichment(CollectionKind.Power); var profile = EnrichmentProfileBinding.From(run.Profile, EvidenceSource.Demo);
        var samples = new[] { endedAt.AddMinutes(1), endedAt.AddMinutes(-1), endedAt, endedAt.AddMinutes(-2) }.Select(at =>
            new PowerSample(Guid.NewGuid(), run.RunId, profile, new("switch-1", "100", EndpointRole.Switch), PowerMetric.PortActual, 8m, "W", true,
                new(clock.GetUtcNow(), at, SourceTimeMeaning.Measurement, true), new(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.Demo, clock.GetUtcNow()))).ToImmutableArray();
        await store.ApplyEnrichmentBatchAsync(run, new(CollectionKind.Power, RunOutcome.Succeeded, [], [], ObservationCompleteness.Complete, clock.GetUtcNow(), []) { Detail = new([], [], samples) }, default);
        var evidence = (await store.GetEnrichmentRunAsync(location.DatasetId, run.RunId, default)).Evidence.PowerSamples;
        Assert.Equal(4, evidence.Length);
        foreach (var row in evidence)
            if (row.Sample.Timing.SourceAtUtc < endedAt) Assert.Equal(old.DeviceGenerationId, row.Attribution.Target!.DeviceGenerationId);
            else Assert.Null(row.Attribution.Target);
    }
    [Fact]
    public async Task EnrichmentCache_EndpointIndexKeepsAllCandidatesIncludingReverseEndpoint()
    {
        await SeedOperations(); var run = await BeginEnrichment(CollectionKind.ConnectionNeighbor);
        var targets = await store.GetFrozenEnrichmentTargetsAsync(run, default); var switchPort = new ConnectionEndpoint("shared-switch", "1", EndpointRole.Switch);
        var at = clock.GetUtcNow(); var timing = new SourceTiming(at, at, SourceTimeMeaning.LastObserved, true);
        var field = new FieldEvidence(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.Demo, at);
        var queries = targets.Select(t =>
        {
            var ap = new ConnectionEndpoint(t.SourceApId, "eth0", EndpointRole.AccessPoint);
            var connection = new ConnectionObservation(Guid.NewGuid(), run.RunId, t.Profile, ap, switchPort, DiscoveryProtocol.Lldp,
                ObservationDirection.RemoteToLocal, timing, TimeSpan.FromHours(1), field, true, new(at, at.AddHours(1), true), t);
            return new NeighborObservation(new(t.Profile, ap, switchPort), RunOutcome.Succeeded, ObservationCompleteness.Complete, true, false, timing, TimeSpan.FromHours(1), [connection]);
        }).ToImmutableArray();
        await store.ApplyEnrichmentBatchAsync(run, new(CollectionKind.ConnectionNeighbor, RunOutcome.Succeeded, [], [], ObservationCompleteness.Complete, at, []) { Detail = new(queries, [], []) }, default);
        var power = await BeginEnrichment(CollectionKind.Power); var sample = new PowerSample(Guid.NewGuid(), power.RunId, targets[0].Profile, switchPort, PowerMetric.PortActual,
            9m, "W", true, new(at, at, SourceTimeMeaning.Measurement, true), field);
        await store.ApplyEnrichmentBatchAsync(power, new(CollectionKind.Power, RunOutcome.Succeeded, [], [], ObservationCompleteness.Complete, at, []) { Detail = new([], [], [sample]) }, default);
        var saved = Assert.Single((await store.GetEnrichmentRunAsync(location.DatasetId, power.RunId, default)).Evidence.PowerSamples);
        Assert.Equal(PowerAttributionStatus.Ambiguous, saved.Attribution.Status); Assert.Null(saved.Attribution.Target);
    }
}
