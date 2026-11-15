using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Enrichment;
namespace AirWaveApMonitor.Infrastructure.AirWave;
public interface IBoundedEnrichmentSource : IEnrichmentSource
{
    CapabilityDecision Capability(ProfileSnapshot profile, CollectionKind kind);
    ImmutableArray<CapabilityEvidence> DescribeCapabilities(ProfileSnapshot profile, CollectionKind kind) => [];
    Task<EnrichmentBatch> CollectAsync(CollectionContext context, ImmutableArray<EnrichmentTargetBinding> targets, Func<bool> shouldYield, CancellationToken ct);
}
internal static class EnrichmentCapabilityCatalog
{
    internal static ImmutableArray<string> Fields(CollectionKind kind) => kind switch
    {
        CollectionKind.ConnectionNeighbor => ["neighbor", "port.admin", "port.oper"],
        CollectionKind.Power => ["power.ApConsumption", "power.PortActual", "power.Requested", "power.Allocated", "power.Reserved", "power.Maximum",
            "power.supplyState", "power.supplyMethod", "power.errorCount", "power.lldpNegotiationSupported"],
        _ => []
    };
}

public sealed record EnrichmentQuery(string Key, Func<CancellationToken, Task<EnrichmentPayload>> ReadAsync);
/// <summary>A frozen, deduplicated request plan; no request starts after yielding to the AP collector.</summary>
public sealed class BoundedEnrichmentQueryRunner(TimeProvider clock)
{
    public async Task<EnrichmentBatch> CollectAsync(CollectionContext context, IReadOnlyList<EnrichmentQuery> queries, Func<bool> shouldYield, CancellationToken ct)
    {
        var neighbors = ImmutableArray.CreateBuilder<NeighborObservation>();
        var ports = ImmutableArray.CreateBuilder<PortStateObservation>();
        var samples = ImmutableArray.CreateBuilder<PowerSample>();
        var states = ImmutableArray.CreateBuilder<PowerStateObservation>();
        var errors = ImmutableArray.CreateBuilder<CollectionError>();
        var remaining = context.DeadlineUtc - clock.GetUtcNow();
        using var totalBudget = new CancellationTokenSource(TimeSpan.FromTicks(Math.Clamp(remaining.Ticks, 0, context.Profile.Settings.RunTimeout.Ticks)), clock);
        if (remaining <= TimeSpan.Zero) totalBudget.Cancel();
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct, totalBudget.Token);
        var valid = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var query in queries)
        {
            ct.ThrowIfCancellationRequested();
            if (shouldYield()) { Error(CollectionErrorCategory.Interrupted); break; }
            if (total.IsCancellationRequested) { Error(CollectionErrorCategory.Timeout); break; }
            if (string.IsNullOrWhiteSpace(query.Key)) { Error(CollectionErrorCategory.Configuration); break; }
            if (!seen.Add(query.Key)) continue;
            try
            {
                using var requestBudget = new CancellationTokenSource(context.Profile.Settings.RequestTimeout, clock);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(total.Token, requestBudget.Token);
                var detail = await query.ReadAsync(request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (context.Kind == CollectionKind.ConnectionNeighbor && (detail.PowerSamples.Length > 0 || detail.PowerStates.Length > 0) ||
                    context.Kind == CollectionKind.Power && (detail.Neighbors.Length > 0 || detail.Ports.Length > 0))
                { Error(CollectionErrorCategory.InvalidResponse); break; }
                neighbors.AddRange(detail.Neighbors); ports.AddRange(detail.Ports);
                samples.AddRange(detail.PowerSamples); states.AddRange(detail.PowerStates);
                // A Provided/Valid marker alone does not establish a usable observation.
                // Reuse the pure policies, retaining rejected raw facts for their quality evidence.
                var assessed = detail.Ports.Select(Assess).Concat(detail.PowerStates.Select(Assess))
                    .Concat(detail.PowerSamples.Select(sample => new FactAssessment(PowerValuePolicy.Normalize(sample).Watts is not null, false)))
                    .Concat(detail.Neighbors.Select(neighbor => Assess(neighbor, clock.GetUtcNow()))).ToArray();
                valid += assessed.Count(fact => fact.Usable);
                if (assessed.Any(fact => !fact.Usable || fact.HasGap))
                    Error(CollectionErrorCategory.InvalidField);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Error(CollectionErrorCategory.Timeout); break; }
            catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
            { Error(error is IOException or HttpRequestException ? CollectionErrorCategory.Transport : CollectionErrorCategory.InvalidResponse); break; }
            // Yield also after the final request: AP priority is a truthful partial-unit reason.
            if (shouldYield()) { Error(CollectionErrorCategory.Interrupted); break; }
        }
        ct.ThrowIfCancellationRequested();
        if (valid == 0 && errors.Count == 0) Error(CollectionErrorCategory.MissingRecord);
        var outcome = valid == 0 ? RunOutcome.Failed : errors.Count == 0 ? RunOutcome.Succeeded : RunOutcome.Partial;
        return new(context.Kind, outcome, [], [], outcome == RunOutcome.Succeeded ? ObservationCompleteness.Complete : ObservationCompleteness.Partial,
            clock.GetUtcNow(), errors.ToImmutable()) { Detail = new(neighbors.ToImmutable(), ports.ToImmutable(), samples.ToImmutable(), states.ToImmutable()) };
        void Error(CollectionErrorCategory category) => errors.Add(new(category, 1, SafeMessages.For(category)));
    }
    private readonly record struct FactAssessment(bool Usable, bool HasGap);

    private static FactAssessment Assess(NeighborObservation observation, DateTimeOffset atUtc)
    {
        var result = ConnectionEvidencePolicy.Evaluate(observation, atUtc);
        return new(result.Presence != NeighborPresence.Unknown,
            observation.Outcome != RunOutcome.Succeeded || observation.Completeness != ObservationCompleteness.Complete || !observation.ScopeCovered);
    }
    private static FactAssessment Assess(PortStateObservation observation)
    {
        var result = PortStatePolicy.Evaluate(observation);
        var hasAdmin = result.AdministrativeState != PortAdministrativeState.Unknown;
        var hasOper = result.OperationalState != PortOperationalState.Unknown;
        return new(hasAdmin || hasOper,
            !hasAdmin && (Reported(observation.AdministrativeEvidence) || observation.AdministrativeState != PortAdministrativeState.Unknown) ||
            !hasOper && (Reported(observation.OperationalEvidence) || observation.OperationalState != PortOperationalState.Unknown));
    }
    private static FactAssessment Assess(PowerStateObservation observation)
    {
        var result = PowerStatePolicy.Evaluate(observation);
        var hasSupply = result.SupplyState != PowerSupplyState.Unknown;
        var hasMethod = result.SupplyMethod != PowerSupplyMethod.Unknown;
        var hasErrors = result.ErrorCount is not null;
        var hasNegotiation = result.LldpNegotiationSupported is not null;
        return new(hasSupply || hasMethod || hasErrors || hasNegotiation,
            !hasSupply && (Reported(observation.SupplyEvidence) || observation.SupplyState != PowerSupplyState.Unknown) ||
            !hasMethod && (Reported(observation.MethodEvidence) || observation.SupplyMethod != PowerSupplyMethod.Unknown) ||
            !hasErrors && (Reported(observation.ErrorEvidence) || observation.ErrorCount is not null) ||
            !hasNegotiation && (Reported(observation.NegotiationEvidence) || observation.LldpNegotiationSupported is not null));
    }
    // Truly absent optional fields are not rejected siblings. A supplied/invalid field or
    // a raw value rejected by normalization must still leave a quality gap beside valid fields.
    private static bool Reported(FieldEvidence evidence)
        => evidence.Availability == FieldAvailability.Provided || evidence.Validity == FieldValidity.Invalid;
}

/// <summary>Live is deliberately unverified. No transport, credentials, login or topology requests are performed.
/// The separate documented interface parser does not authorize enabling this adapter.</summary>
public sealed class EnrichmentSource(TimeProvider clock) : IBoundedEnrichmentSource
{
    public CapabilityDecision Capability(ProfileSnapshot profile, CollectionKind kind) => new(CapabilityAvailability.Unverified, false, CapabilityDecisionReason.Unverified);
    public ImmutableArray<CapabilityEvidence> DescribeCapabilities(ProfileSnapshot profile, CollectionKind kind)
    {
        if (profile.CredentialsVersion <= 0 || profile.Origin == Demo.ScenarioApSource.DemoOrigin) return [];
        var binding = EnrichmentProfileBinding.From(profile, EvidenceSource.AirWave);
        return EnrichmentCapabilityCatalog.Fields(kind).Select(field => new CapabilityEvidence(
            new(binding, "unverified", field, "live-unverified-v1"), CapabilityAvailability.Unverified, false, false,
            CapabilityVerification.None, null, "live-field-contract-unverified")).ToImmutableArray();
    }
    public Task<EnrichmentBatch> CollectAsync(CollectionContext context, CancellationToken ct) => CollectAsync(context, [], () => false, ct);
    public Task<EnrichmentBatch> CollectAsync(CollectionContext context, ImmutableArray<EnrichmentTargetBinding> targets, Func<bool> shouldYield, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new EnrichmentBatch(context.Kind, RunOutcome.Failed, [], [], ObservationCompleteness.Unknown,
            clock.GetUtcNow(), [new(CollectionErrorCategory.Unverified, 1, SafeMessages.For(CollectionErrorCategory.Unverified))]));
    }
}
