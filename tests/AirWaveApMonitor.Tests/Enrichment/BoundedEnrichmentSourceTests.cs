using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Tests.Support;
namespace AirWaveApMonitor.Tests.Enrichment;
public sealed class BoundedEnrichmentSourceTests
{
    internal static CollectionContext Context(TimeProvider clock) => new(17, new(Guid.NewGuid(), Guid.NewGuid(), new Uri("https://demo.example.invalid"), 1, 1, [], BaselineStatus.Unverified, new(connectionCollectionEnabled: true, powerCollectionEnabled: true)), CollectionKind.ConnectionNeighbor, RunTrigger.Manual, clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(2));
    internal static EnrichmentPayload Valid(CollectionContext c) => new([], [new(Guid.NewGuid(), c.RunId, EnrichmentProfileBinding.From(c.Profile, EvidenceSource.Demo), new("switch", "1", EndpointRole.Switch), PortAdministrativeState.Up, PortOperationalState.Up, Evidence(c), Evidence(c))], []);
    private static FieldEvidence Evidence(CollectionContext c) => new(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.Demo, c.StartedAtUtc);
    [Fact]
    public async Task PendingApAfterFirstRequest_CommitsPartialAndNeverStartsNextRequest()
    {
        var clock = new ManualTimeProvider(); var c = Context(clock); var calls = 0; var yield = false;
        var query = new EnrichmentQuery("switch-a", _ => { calls++; yield = true; return Task.FromResult(Valid(c)); });
        var batch = await new BoundedEnrichmentQueryRunner(clock).CollectAsync(c, [query, new("switch-b", _ => { calls++; return Task.FromResult(Valid(c)); })], () => yield, default);
        Assert.Equal(1, calls); Assert.Equal(RunOutcome.Partial, batch.Outcome);
        Assert.Single(batch.Detail!.Ports); Assert.Contains(batch.Errors, e => e.Category == CollectionErrorCategory.Interrupted);
    }
    [Fact]
    public async Task YieldBeforeAnyRequest_IsFailedNotEmptySuccess()
    {
        var clock = new ManualTimeProvider(); var calls = 0;
        var batch = await new BoundedEnrichmentQueryRunner(clock).CollectAsync(Context(clock), [new("a", _ => { calls++; return Task.FromResult(new EnrichmentPayload([], [], [])); })], () => true, default);
        Assert.Equal(0, calls); Assert.Equal(RunOutcome.Failed, batch.Outcome);
        Assert.Contains(batch.Errors, e => e.Category == CollectionErrorCategory.Interrupted);
    }
    [Fact]
    public async Task DuplicateSwitchQueries_AreReadOnlyOnce()
    {
        var clock = new ManualTimeProvider(); var c = Context(clock); var calls = 0;
        var query = new EnrichmentQuery("a", _ => { calls++; return Task.FromResult(Valid(c)); });
        var batch = await new BoundedEnrichmentQueryRunner(clock).CollectAsync(c, [query, query], () => false, default);
        Assert.Equal(1, calls); Assert.Equal(RunOutcome.Succeeded, batch.Outcome); Assert.Single(batch.Detail!.Ports);
    }
    [Fact]
    public async Task RequestBudget_StopsAfterFortyFiveSeconds_WithoutStartingNext()
    {
        var clock = new ManualTimeProvider(); var c = Context(clock); var entered = new TaskCompletionSource(); var calls = 0;
        var work = new BoundedEnrichmentQueryRunner(clock).CollectAsync(c, [new("a", async ct => { calls++; entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Valid(c); }), new("b", _ => { calls++; return Task.FromResult(Valid(c)); })], () => false, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); clock.Advance(TimeSpan.FromSeconds(45));
        var batch = await work.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, calls); Assert.Equal(RunOutcome.Failed, batch.Outcome); Assert.Contains(batch.Errors, e => e.Category == CollectionErrorCategory.Timeout);
    }
    [Fact]
    public async Task WholeUnitBudget_PreservesPriorValidRequestsAsPartial()
    {
        var clock = new ManualTimeProvider(); var c = Context(clock); var calls = 0;
        EnrichmentQuery Query(string key) => new(key, _ => { calls++; clock.Advance(TimeSpan.FromSeconds(40)); return Task.FromResult(Valid(c)); });
        var batch = await new BoundedEnrichmentQueryRunner(clock).CollectAsync(c, [Query("a"), Query("b"), Query("c"), Query("d")], () => false, default);
        Assert.Equal(3, calls); Assert.Equal(RunOutcome.Partial, batch.Outcome); Assert.Equal(2, batch.Detail!.Ports.Length);
        Assert.Contains(batch.Errors, e => e.Category == CollectionErrorCategory.Timeout);
    }
    [Fact]
    public async Task CallerCancellation_IsPropagatedForTypedInterruption()
    {
        var clock = new ManualTimeProvider(); var c = Context(clock); using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BoundedEnrichmentQueryRunner(clock).CollectAsync(c,
            [new("a", _ => { cancel.Cancel(); return Task.FromResult(Valid(c)); })], () => false, cancel.Token));
    }
    [Fact]
    public async Task OnlyUnknownPortStates_AreNotAValidSuccessfulObservation()
    {
        var clock = new ManualTimeProvider(); var c = Context(clock); var detail = Valid(c);
        detail = detail with { Ports = [detail.Ports[0] with { AdministrativeState = PortAdministrativeState.Unknown, OperationalState = PortOperationalState.Unknown }] };
        var batch = await new BoundedEnrichmentQueryRunner(clock).CollectAsync(c, [new("a", _ => Task.FromResult(detail))], () => false, default);
        Assert.Equal(RunOutcome.Failed, batch.Outcome); Assert.Single(batch.Detail!.Ports);
    }
    [Theory]
    [InlineData("conflicting-neighbor")]
    [InlineData("failed-neighbor")]
    [InlineData("unknown-power-state")]
    [InlineData("undefined-port-state")]
    public async Task PolicyRejectedFactsAlone_CannotBecomeSuccessfulOrComplete(string scenario)
    {
        var clock = new ManualTimeProvider(); var c = Context(clock) with { Kind = ScenarioKind(scenario) };
        var payload = PolicyPayload(c, scenario, false);
        var batch = await new BoundedEnrichmentQueryRunner(clock).CollectAsync(c, [new("one", _ => Task.FromResult(payload))], () => false, default);
        Assert.Equal(RunOutcome.Failed, batch.Outcome); Assert.NotEqual(ObservationCompleteness.Complete, batch.Completeness);
        Assert.Contains(batch.Errors, e => e.Category == CollectionErrorCategory.InvalidField);
        AssertPayloadPreserved(payload, batch.Detail!);
    }
    [Theory]
    [InlineData("conflicting-neighbor")]
    [InlineData("failed-neighbor")]
    [InlineData("unknown-power-state")]
    [InlineData("undefined-port-state")]
    public async Task ValidAndPolicyRejectedFacts_KeepValidEvidenceAsPartial(string scenario)
    {
        var clock = new ManualTimeProvider(); var c = Context(clock) with { Kind = ScenarioKind(scenario) };
        var payload = PolicyPayload(c, scenario, true);
        var batch = await new BoundedEnrichmentQueryRunner(clock).CollectAsync(c, [new("one", _ => Task.FromResult(payload))], () => false, default);
        Assert.Equal(RunOutcome.Partial, batch.Outcome); Assert.Equal(ObservationCompleteness.Partial, batch.Completeness);
        Assert.Contains(batch.Errors, e => e.Category == CollectionErrorCategory.InvalidField);
        AssertPayloadPreserved(payload, batch.Detail!);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentValidStateField_DoesNotHideRejectedSiblingField(bool power)
    {
        var clock = new ManualTimeProvider(); var c = Context(clock) with { Kind = power ? CollectionKind.Power : CollectionKind.ConnectionNeighbor };
        var payload = PolicyPayload(c, power ? "unknown-power-state" : "undefined-port-state", false);
        payload = power ? payload with { PowerStates = [payload.PowerStates[0] with { SupplyState = (PowerSupplyState)999, SupplyMethod = PowerSupplyMethod.Poe }] }
            : payload with { Ports = [payload.Ports[0] with { OperationalState = PortOperationalState.Up }] };
        var batch = await new BoundedEnrichmentQueryRunner(clock).CollectAsync(c, [new("one", _ => Task.FromResult(payload))], () => false, default);
        Assert.Equal(RunOutcome.Partial, batch.Outcome); Assert.Contains(batch.Errors, e => e.Category == CollectionErrorCategory.InvalidField);
        AssertPayloadPreserved(payload, batch.Detail!);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitZeroCounterOrFalseNegotiation_IsAValidProvidedFact(bool negotiation)
    {
        var clock = new ManualTimeProvider(); var c = Context(clock) with { Kind = CollectionKind.Power };
        var payload = PolicyPayload(c, "unknown-power-state", false); var missing = Evidence(c) with { Availability = FieldAvailability.NotProvided, Validity = FieldValidity.Unknown };
        payload = payload with { PowerStates = [payload.PowerStates[0] with { SupplyEvidence = missing, MethodEvidence = missing,
            ErrorCount = negotiation ? null : 0, ErrorEvidence = negotiation ? missing : Evidence(c),
            LldpNegotiationSupported = negotiation ? false : null, NegotiationEvidence = negotiation ? Evidence(c) : missing }] };
        var batch = await new BoundedEnrichmentQueryRunner(clock).CollectAsync(c, [new("one", _ => Task.FromResult(payload))], () => false, default);
        Assert.Equal(RunOutcome.Succeeded, batch.Outcome); Assert.Empty(batch.Errors);
    }
    private static void AssertPayloadPreserved(EnrichmentPayload expected, EnrichmentPayload actual)
    {
        Assert.Equal(expected.Neighbors.ToArray(), actual.Neighbors.ToArray()); Assert.Equal(expected.Ports.ToArray(), actual.Ports.ToArray());
        Assert.Equal(expected.PowerSamples.ToArray(), actual.PowerSamples.ToArray()); Assert.Equal(expected.PowerStates.ToArray(), actual.PowerStates.ToArray());
    }
    internal static CollectionKind ScenarioKind(string scenario) => scenario == "unknown-power-state" ? CollectionKind.Power : CollectionKind.ConnectionNeighbor;
    internal static EnrichmentPayload PolicyPayload(CollectionContext c, string scenario, bool includeValid)
    {
        var binding = EnrichmentProfileBinding.From(c.Profile, EvidenceSource.Demo); var local = new ConnectionEndpoint("switch", "1", EndpointRole.Switch);
        var remote = new ConnectionEndpoint("ap", "eth0", EndpointRole.AccessPoint); var timing = new SourceTiming(c.StartedAtUtc, c.StartedAtUtc, SourceTimeMeaning.LastObserved, true);
        var row = new ConnectionObservation(Guid.NewGuid(), c.RunId, binding, local, remote, DiscoveryProtocol.Lldp, ObservationDirection.LocalToRemote,
            timing, TimeSpan.FromMinutes(5), Evidence(c), true);
        var neighbor = new NeighborObservation(new(binding, local, remote), scenario == "failed-neighbor" ? RunOutcome.Failed : RunOutcome.Succeeded,
            ObservationCompleteness.Complete, true, scenario == "conflicting-neighbor", timing, TimeSpan.FromMinutes(5), [row]);
        var payload = scenario switch
        {
            "conflicting-neighbor" or "failed-neighbor" => new EnrichmentPayload([neighbor], [], []),
            "undefined-port-state" => new([], [Valid(c).Ports[0] with { AdministrativeState = (PortAdministrativeState)999, OperationalState = (PortOperationalState)999 }], []),
            "unknown-power-state" => new([], [], [], [new(Guid.NewGuid(), c.RunId, binding, local, PowerSupplyState.Unknown, null, PowerSupplyMethod.Unknown, null,
                null, null, Evidence(c), Evidence(c), Evidence(c), Evidence(c))]),
            _ => throw new ArgumentException("test scenario")
        };
        if (!includeValid) return payload;
        return c.Kind == CollectionKind.ConnectionNeighbor ? payload with { Ports = [.. payload.Ports, Valid(c).Ports[0]] }
            : payload with { PowerSamples = [new(Guid.NewGuid(), c.RunId, binding, local, PowerMetric.PortActual, 5m, "W", true,
                new(c.StartedAtUtc, c.StartedAtUtc, SourceTimeMeaning.Measurement, true), Evidence(c))] };
    }
    [Fact]
    public async Task LiveAdapterIsUnverifiedAndReturnsNoObservationsEvenWhenFlagsEnabled()
    {
        var clock = new ManualTimeProvider(); var c = Context(clock); var source = new EnrichmentSource(clock);
        Assert.False(source.Capability(c.Profile, c.Kind).CanCollect);
        var batch = await source.CollectAsync(c, default);
        Assert.Equal(RunOutcome.Failed, batch.Outcome); Assert.Empty(batch.Observations);
        Assert.Contains(batch.Errors, e => e.Category == CollectionErrorCategory.Unverified);
    }
}
