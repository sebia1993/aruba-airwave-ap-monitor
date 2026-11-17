using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Enrichment;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    private (bool Valid, bool Gap) StoreNeighbor(SqliteConnection connection, SqliteTransaction transaction, CollectionContext context,
        string profile, NeighborObservation observation, ImmutableArray<EnrichmentTargetBinding> frozenTargets, bool current)
    {
        var key = EnrichmentHash(observation.Scope); var previous = current ? CurrentEnrichmentRecord(connection, transaction, profile, 0, key) : null;
        var previousState = previous is null ? null : Decode<StoredNeighbor>(previous.State);
        var prior = previousState is null ? [] : ReadConnections(connection, transaction, previousState.RetainedIds);
        var assessment = ConnectionEvidencePolicy.Evaluate(observation, observation.Timing.ReadAtUtc);
        var acceptedSourceAt = previousState?.LastAcceptedSourceAtUtc;
        var stale = observation.Timing.MeaningVerified && observation.Timing.SourceAtUtc is { } nextAt && acceptedSourceAt is { } priorAt && nextAt < priorAt;
        var valid = assessment.Presence != NeighborPresence.Unknown && !stale;
        var gap = !valid || observation.Outcome != RunOutcome.Succeeded || observation.Completeness != ObservationCompleteness.Complete ||
            !observation.ScopeCovered || observation.HasExclusionsOrConflicts || assessment.Freshness != SourceFreshness.Current;
        foreach (var row in observation.Neighbors)
        {
            var old = (string?)Scalar(connection, transaction, "SELECT observation_json FROM enrichment_connections WHERE connection_id=$id", ("$id", row.Id));
            if (old is not null && old != Encode(row)) throw new InvalidDataException("연결 근거 ID가 다른 관측에 재사용되었습니다.");
            Execute(connection, transaction, "INSERT OR IGNORE INTO enrichment_connections VALUES($id,$run,$profile,$local,$remote,$read,$source,$json)",
                ("$id", row.Id), ("$run", context.RunId), ("$profile", profile), ("$local", EnrichmentHash(row.Local)), ("$remote", EnrichmentHash(row.Remote)),
                ("$read", row.Timing.ReadAtUtc), ("$source", row.Timing.SourceAtUtc), ("$json", Encode(row)));
        }
        var retained = !valid ? prior : assessment.MayClosePreviousConnection ? observation.Neighbors :
            gap ? observation.Neighbors.Concat(prior).DistinctBy(r => (r.Local, r.Remote, r.Protocol, r.Direction)).ToImmutableArray() : observation.Neighbors;
        if (stale) assessment = assessment with { Presence = NeighborPresence.Unknown, HasCurrentLldpObservation = false, MayClosePreviousConnection = false, Reason = NeighborAssessmentReason.UnverifiedSource, Freshness = SourceFreshness.Unknown };
        var targetRows = retained.Concat(observation.Neighbors).Where(c => c.IdentityMappingVerified && c.Target is not null).Select(c => c.Target!).ToList();
        // A complete empty answer can refer to an explicitly frozen expected remote, but never an AP guessed from a name/IP.
        if (observation.Scope.ExpectedRemote is { Role: EndpointRole.AccessPoint } remote)
            targetRows.AddRange(frozenTargets.Where(t => t.SourceApId == remote.SourceDeviceId));
        var state = new StoredNeighbor(assessment with { Observations = [] }, observation.Neighbors.Select(r => r.Id).ToImmutableArray(), retained.Select(r => r.Id).ToImmutableArray(), valid && assessment.Freshness == SourceFreshness.Current ? observation.Timing.SourceAtUtc : acceptedSourceAt);
        var recordId = InsertEnrichmentRecord(connection, transaction, context, profile, 0, key, null, observation.Timing.ReadAtUtc,
            observation with { Neighbors = [] }, state, gap, current, targetRows);
        string Shape(IEnumerable<ConnectionObservation> values) => Encode(values.Select(c => new { c.Local, c.Remote, c.Protocol, c.Direction, c.Target?.ApId, c.Target?.DeviceGenerationId, c.Target?.MonitoringIntervalId }).OrderBy(EnrichmentHash).ToArray());
        if (current && valid && previous is not null && Shape(prior) != Shape(retained))
            RecordEnrichmentChange(connection, transaction, context, recordId, "NeighborChanged", "neighbors", Shape(prior), Shape(retained), targetRows, observation.Timing.ReadAtUtc);
        return (valid, gap);
    }
    private static bool ValidField(FieldEvidence evidence, EvidenceSource source)
        => evidence.Availability == FieldAvailability.Provided && evidence.Validity == FieldValidity.Valid && evidence.Source == source;
    private static bool Regressed(FieldEvidence value, FieldEvidence? previous)
        => value.SourceAtUtc is { } now && previous?.SourceAtUtc is { } before && now < before;
    private static bool ProvidedInvalid(FieldEvidence evidence, bool valid)
        => evidence.Availability == FieldAvailability.Provided && !valid;
    private static ImmutableArray<EnrichmentTargetBinding> EndpointTargets(ConnectionEndpoint endpoint, IEnumerable<ConnectionObservation> connections, DateTimeOffset at)
        => connections.Where(c => c.Target is not null && c.IdentityMappingVerified && (c.Local == endpoint || c.Remote == endpoint) &&
            c.Protocol is DiscoveryProtocol.Lldp or DiscoveryProtocol.Cdp or DiscoveryProtocol.OtherVerified &&
            c.Window is { ContinuityVerified: true } w && w.FromUtc <= at && (w.UntilUtc is null || at < w.UntilUtc) &&
            ValidField(c.Evidence, c.Profile.Source)).Select(c => c.Target!).Distinct().ToImmutableArray();
    private (bool Valid, bool Gap) StorePort(SqliteConnection connection, SqliteTransaction transaction, CollectionContext context,
        string profile, PortStateObservation observation, IEnumerable<ConnectionObservation> connections, StoredTargetBoundsCache targetBounds, bool current)
    {
        var key = EnrichmentHash(observation.Endpoint); var previous = current ? CurrentEnrichmentRecord(connection, transaction, profile, 1, key) : null;
        var old = previous is null ? null : Decode<StoredPort>(previous.State);
        var assessed = PortStatePolicy.Evaluate(observation);
        var adminValid = assessed.AdministrativeState != PortAdministrativeState.Unknown && !Regressed(observation.AdministrativeEvidence, old?.Accepted.AdministrativeEvidence);
        var operValid = assessed.OperationalState != PortOperationalState.Unknown && !Regressed(observation.OperationalEvidence, old?.Accepted.OperationalEvidence);
        var valid = adminValid || operValid;
        var gap = !valid || ProvidedInvalid(observation.AdministrativeEvidence, adminValid) || ProvidedInvalid(observation.OperationalEvidence, operValid);
        var accepted = observation with
        {
            AdministrativeState = adminValid ? assessed.AdministrativeState : old?.Assessment.AdministrativeState ?? PortAdministrativeState.Unknown,
            AdministrativeEvidence = adminValid ? observation.AdministrativeEvidence : old?.Accepted.AdministrativeEvidence ?? observation.AdministrativeEvidence,
            RawAdministrativeState = adminValid ? observation.RawAdministrativeState : old?.Accepted.RawAdministrativeState,
            OperationalState = operValid ? assessed.OperationalState : old?.Assessment.OperationalState ?? PortOperationalState.Unknown,
            OperationalEvidence = operValid ? observation.OperationalEvidence : old?.Accepted.OperationalEvidence ?? observation.OperationalEvidence,
            RawOperationalState = operValid ? observation.RawOperationalState : old?.Accepted.RawOperationalState
        };
        var state = new StoredPort(accepted, new(accepted.AdministrativeState, accepted.OperationalState));
        var targets = EndpointTargets(observation.Endpoint, connections, context.StartedAtUtc)
            .Where(t => targetBounds.Allows(t, context.StartedAtUtc)).ToImmutableArray();
        var at = new[] { observation.AdministrativeEvidence.CollectedAtUtc, observation.OperationalEvidence.CollectedAtUtc }.Max();
        var id = InsertEnrichmentRecord(connection, transaction, context, profile, 1, key, observation.Id, at, observation, state, gap, current, targets);
        if (current && valid && old is not null && state.Assessment != old.Assessment)
            RecordEnrichmentChange(connection, transaction, context, id, "PortStateChanged", "port", Encode(old.Assessment), Encode(state.Assessment), targets, at);
        return (valid, gap);
    }
    private (bool Valid, bool Gap) StorePowerState(SqliteConnection connection, SqliteTransaction transaction, CollectionContext context,
        string profile, PowerStateObservation observation, IEnumerable<ConnectionObservation> connections, StoredTargetBoundsCache targetBounds, bool current)
    {
        var key = EnrichmentHash(observation.Endpoint); var previous = current ? CurrentEnrichmentRecord(connection, transaction, profile, 2, key) : null;
        var old = previous is null ? null : Decode<StoredPowerState>(previous.State);
        var assessed = PowerStatePolicy.Evaluate(observation);
        var supply = assessed.SupplyState != PowerSupplyState.Unknown && !Regressed(observation.SupplyEvidence, old?.Accepted.SupplyEvidence);
        var method = assessed.SupplyMethod != PowerSupplyMethod.Unknown && !Regressed(observation.MethodEvidence, old?.Accepted.MethodEvidence);
        var error = assessed.ErrorCount is not null && !Regressed(observation.ErrorEvidence, old?.Accepted.ErrorEvidence);
        var negotiation = assessed.LldpNegotiationSupported is not null && !Regressed(observation.NegotiationEvidence, old?.Accepted.NegotiationEvidence);
        var valid = supply || method || error || negotiation;
        var gap = !valid || ProvidedInvalid(observation.SupplyEvidence, supply) || ProvidedInvalid(observation.MethodEvidence, method) || ProvidedInvalid(observation.ErrorEvidence, error) || ProvidedInvalid(observation.NegotiationEvidence, negotiation);
        var accepted = observation with
        {
            SupplyState = supply ? assessed.SupplyState : old?.Assessment.SupplyState ?? PowerSupplyState.Unknown,
            SupplyEvidence = supply ? observation.SupplyEvidence : old?.Accepted.SupplyEvidence ?? observation.SupplyEvidence,
            RawSupplyState = supply ? observation.RawSupplyState : old?.Accepted.RawSupplyState,
            SupplyMethod = method ? assessed.SupplyMethod : old?.Assessment.SupplyMethod ?? PowerSupplyMethod.Unknown,
            MethodEvidence = method ? observation.MethodEvidence : old?.Accepted.MethodEvidence ?? observation.MethodEvidence,
            RawSupplyMethod = method ? observation.RawSupplyMethod : old?.Accepted.RawSupplyMethod,
            ErrorCount = error ? assessed.ErrorCount : old?.Assessment.ErrorCount,
            ErrorEvidence = error ? observation.ErrorEvidence : old?.Accepted.ErrorEvidence ?? observation.ErrorEvidence,
            LldpNegotiationSupported = negotiation ? assessed.LldpNegotiationSupported : old?.Assessment.LldpNegotiationSupported,
            NegotiationEvidence = negotiation ? observation.NegotiationEvidence : old?.Accepted.NegotiationEvidence ?? observation.NegotiationEvidence
        };
        var state = new StoredPowerState(accepted, new(accepted.SupplyState, accepted.SupplyMethod, accepted.ErrorCount, accepted.LldpNegotiationSupported));
        var targets = EndpointTargets(observation.Endpoint, connections, context.StartedAtUtc)
            .Where(t => targetBounds.Allows(t, context.StartedAtUtc)).ToImmutableArray();
        var at = new[] { observation.SupplyEvidence.CollectedAtUtc, observation.MethodEvidence.CollectedAtUtc, observation.ErrorEvidence.CollectedAtUtc, observation.NegotiationEvidence.CollectedAtUtc }.Max();
        var id = InsertEnrichmentRecord(connection, transaction, context, profile, 2, key, observation.Id, at, observation, state, gap, current, targets);
        if (current && valid && old is not null && state.Assessment != old.Assessment)
            RecordEnrichmentChange(connection, transaction, context, id, "PowerStateChanged", "power_state", Encode(old.Assessment), Encode(state.Assessment), targets, at);
        return (valid, gap);
    }
    private void RecordEnrichmentChange(SqliteConnection connection, SqliteTransaction transaction, CollectionContext context,
        long id, string action, string field, string before, string after, IEnumerable<EnrichmentTargetBinding> targets, DateTimeOffset observedAt)
    {
        var exactTargets = targets.DistinctBy(t => (t.ApId, t.DeviceGenerationId, t.Placement?.InstallationPointId))
            .Select(t => new WorkTarget(t.ApId, t.DeviceGenerationId, InstallationPointId: t.Placement?.InstallationPointId)).ToImmutableArray();
        AddEvent(connection, transaction, context.Profile.DatasetId, $"enrichment:{id}", new(0, OperationHistoryKind.Connection,
            timeProvider.GetUtcNow(), EnrichmentSource, action, field, before, after, null, null, exactTargets, context.RunId));
    }
}
