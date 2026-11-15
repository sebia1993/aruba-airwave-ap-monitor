using System.Collections.Immutable;
using System.Globalization;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Enrichment;
using AirWaveApMonitor.Infrastructure.AirWave;
namespace AirWaveApMonitor.Infrastructure.Demo;

public enum DemoEnrichmentScenario { Normal, MissingPower, StaleNeighbor, UnknownProtocol }

/// <summary>Synthetic scenario only. These values and continuity windows are authored Demo facts,
/// never examples of an AirWave LLDP/PoE API response or proof of Live field availability.</summary>
public sealed class DemoEnrichmentSource(TimeProvider clock) : IBoundedEnrichmentSource
{
    private int scenario;
    public DemoEnrichmentScenario Scenario
    {
        get => (DemoEnrichmentScenario)Volatile.Read(ref scenario);
        set { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); Volatile.Write(ref scenario, (int)value); }
    }
    public CapabilityDecision Capability(ProfileSnapshot profile, CollectionKind kind)
    {
        if (profile.Origin != ScenarioApSource.DemoOrigin || kind is not (CollectionKind.ConnectionNeighbor or CollectionKind.Power))
            return new(CapabilityAvailability.Unverified, false, CapabilityDecisionReason.Unverified);
        var key = new CapabilityKey(EnrichmentProfileBinding.From(profile, EvidenceSource.Demo), "synthetic-demo", kind.ToString(), "demo-v1");
        return CapabilityStatus.Evaluate(new(key, CapabilityAvailability.AvailableConfirmed, true, true,
            CapabilityVerification.DemoFixture, clock.GetUtcNow(), "synthetic-demo-only"), key,
            kind == CollectionKind.ConnectionNeighbor ? profile.Settings.ConnectionCollectionEnabled : profile.Settings.PowerCollectionEnabled);
    }
    public ImmutableArray<CapabilityEvidence> DescribeCapabilities(ProfileSnapshot profile, CollectionKind kind)
    {
        if (profile.CredentialsVersion <= 0 || profile.Origin != ScenarioApSource.DemoOrigin) return [];
        var binding = EnrichmentProfileBinding.From(profile, EvidenceSource.Demo);
        return EnrichmentCapabilityCatalog.Fields(kind).Select(field => new CapabilityEvidence(
            new(binding, "synthetic-demo", field, "demo-v1"), CapabilityAvailability.AvailableConfirmed, true, true,
            CapabilityVerification.DemoFixture, clock.GetUtcNow(), "synthetic-demo-only")).ToImmutableArray();
    }
    public Task<EnrichmentBatch> CollectAsync(CollectionContext context, CancellationToken ct) => CollectAsync(context, [], () => false, ct);
    public Task<EnrichmentBatch> CollectAsync(CollectionContext context, ImmutableArray<EnrichmentTargetBinding> targets, Func<bool> shouldYield, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Capability(context.Profile, context.Kind).CanCollect) return Failure(CollectionErrorCategory.Unverified);
        var binding = EnrichmentProfileBinding.From(context.Profile, EvidenceSource.Demo);
        if (targets.IsDefaultOrEmpty || targets.Any(t => t.Profile != binding || t.ApId == Guid.Empty || t.DeviceGenerationId == Guid.Empty ||
            t.MonitoringIntervalId == Guid.Empty || !int.TryParse(t.SourceApId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) ||
            targets.GroupBy(t => t.SourceApId).Any(g => g.Distinct().Count() > 1)) return Failure(CollectionErrorCategory.Configuration);
        var frozenScenario = Scenario;
        // One request per synthetic 48-port switch, not one request per AP. Freeze before execution.
        var queries = targets.Distinct().GroupBy(t => (int.Parse(t.SourceApId, CultureInfo.InvariantCulture) - 1) / 48)
            .OrderBy(g => g.Key).Select(group =>
            {
                var frozen = group.ToImmutableArray();
                return new EnrichmentQuery($"demo-switch-{group.Key + 1}", async token =>
                {
                    await Task.Yield(); token.ThrowIfCancellationRequested();
                    return Build(context, frozen, frozenScenario, clock.GetUtcNow());
                });
            }).ToArray();
        return new BoundedEnrichmentQueryRunner(clock).CollectAsync(context, queries, shouldYield, ct);
        Task<EnrichmentBatch> Failure(CollectionErrorCategory category) => Task.FromResult(new EnrichmentBatch(context.Kind, RunOutcome.Failed,
            [], [], ObservationCompleteness.Unknown, clock.GetUtcNow(), [new(category, 1, SafeMessages.For(category))]));
    }

    private static EnrichmentPayload Build(CollectionContext c, ImmutableArray<EnrichmentTargetBinding> targets,
        DemoEnrichmentScenario scenario, DateTimeOffset readAt)
    {
        var neighbors = ImmutableArray.CreateBuilder<NeighborObservation>();
        var ports = ImmutableArray.CreateBuilder<PortStateObservation>();
        var samples = ImmutableArray.CreateBuilder<PowerSample>();
        var states = ImmutableArray.CreateBuilder<PowerStateObservation>();
        foreach (var target in targets)
        {
            var id = int.Parse(target.SourceApId, CultureInfo.InvariantCulture) - 1;
            var local = new ConnectionEndpoint($"demo-switch-{id / 48 + 1}", (id % 48 + 1).ToString(CultureInfo.InvariantCulture), EndpointRole.Switch);
            var remote = new ConnectionEndpoint(target.SourceApId, "eth0", EndpointRole.AccessPoint);
            var sourceAt = scenario == DemoEnrichmentScenario.StaleNeighbor ? c.StartedAtUtc.AddHours(-2) : c.StartedAtUtc;
            var neighborTime = new SourceTiming(readAt, sourceAt, SourceTimeMeaning.LastObserved, true);
            var evidence = new FieldEvidence(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.Demo, readAt);
            if (c.Kind == CollectionKind.ConnectionNeighbor)
            {
                var connection = new ConnectionObservation(Guid.NewGuid(), c.RunId, target.Profile, local, remote,
                    scenario == DemoEnrichmentScenario.UnknownProtocol ? DiscoveryProtocol.Unknown : DiscoveryProtocol.Lldp,
                    ObservationDirection.LocalToRemote, neighborTime, TimeSpan.FromMinutes(15), evidence, true,
                    // An explicitly authored synthetic interval; real repeated observations cannot assert this.
                    new(c.StartedAtUtc, c.StartedAtUtc.AddMinutes(15), true), target);
                neighbors.Add(new(new(target.Profile, local, remote), RunOutcome.Succeeded, ObservationCompleteness.Complete, true, false,
                    neighborTime, TimeSpan.FromMinutes(15), [connection]));
                ports.Add(new(Guid.NewGuid(), c.RunId, target.Profile, local, PortAdministrativeState.Up, PortOperationalState.Up, evidence, evidence, "Up", "Up"));
            }
            else
            {
                var measured = new SourceTiming(readAt, c.StartedAtUtc, SourceTimeMeaning.Measurement, true);
                foreach (var (metric, value) in new (PowerMetric, decimal?)[]
                {
                    (PowerMetric.ApConsumption, 8.5m), (PowerMetric.PortActual, scenario == DemoEnrichmentScenario.MissingPower ? null : 9.2m),
                    (PowerMetric.Requested, 12m), (PowerMetric.Allocated, 15.4m), (PowerMetric.Reserved, 15.4m), (PowerMetric.Maximum, 30m)
                })
                    samples.Add(new(Guid.NewGuid(), c.RunId, target.Profile, metric == PowerMetric.ApConsumption ? remote : local,
                        metric, value, "W", true, measured, value is null ? new(FieldAvailability.NotProvided, FieldValidity.Unknown, EvidenceSource.Demo, readAt) : evidence,
                        metric == PowerMetric.ApConsumption ? target : null,
                        new($"demo:{c.RunId}:{target.SourceApId}:{metric}", true)));
                states.Add(new(Guid.NewGuid(), c.RunId, target.Profile, local, PowerSupplyState.On, "On", PowerSupplyMethod.Poe, "PoE", 0, true,
                    evidence, evidence, evidence, evidence));
            }
        }
        return new(neighbors.ToImmutable(), ports.ToImmutable(), samples.ToImmutable(), states.ToImmutable());
    }
}
