using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Core.Enrichment;

public static class ConnectionEvidencePolicy
{
    public static NeighborAssessment Evaluate(NeighborObservation observation, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var freshness = SourceFreshnessPolicy.Evaluate(observation.Timing, atUtc, observation.AllowedSourceAge).Freshness;
        var rows = observation.Neighbors.IsDefault ? [] : observation.Neighbors;
        NeighborAssessment Unknown(NeighborAssessmentReason reason) => new(NeighborPresence.Unknown, false,
            false, freshness, reason, rows);
        if (observation.Outcome == RunOutcome.Failed)
            return Unknown(NeighborAssessmentReason.Failed);
        if (observation.Neighbors.IsDefault)
            return Unknown(NeighborAssessmentReason.Incomplete);
        if (observation.HasExclusionsOrConflicts || rows.Select(row => row.Id).Distinct().Count() != rows.Length ||
            !EnrichmentGuard.ValidProfile(observation.Scope.Profile) || !EnrichmentGuard.ValidEndpoint(observation.Scope.Local, true) ||
            rows.Select(row => (row.Local, row.Remote, row.Protocol, row.Direction, row.Timing.SourceAtUtc)).Distinct().Count() != rows.Length ||
            rows.Any(row => row.Id == Guid.Empty || row.RunId <= 0 || row.Profile != observation.Scope.Profile || row.Local != observation.Scope.Local ||
                !EnrichmentGuard.ValidEndpoint(row.Remote) || !Enum.IsDefined(row.Protocol) || !Enum.IsDefined(row.Direction) ||
                !EnrichmentGuard.ValidField(row.Evidence, row.Profile)))
            return Unknown(NeighborAssessmentReason.ConflictingEvidence);
        var expected = observation.Scope.ExpectedRemote;
        if (expected?.SourceInterfaceId is not null && rows.Any(row =>
            row.Remote.SourceDeviceId == expected.SourceDeviceId && row.Remote.Role == expected.Role &&
            string.IsNullOrWhiteSpace(row.Remote.SourceInterfaceId)))
            return Unknown(NeighborAssessmentReason.Incomplete);
        var matching = rows.Where(row => expected is null ||
            (row.Remote.SourceDeviceId == expected.SourceDeviceId && row.Remote.Role == expected.Role &&
             (expected.SourceInterfaceId is null || row.Remote.SourceInterfaceId == expected.SourceInterfaceId))).ToArray();
        if (matching.Length != 0)
        {
            var rowFreshness = matching.Select(row => SourceFreshnessPolicy.Evaluate(row.Timing, atUtc, row.AllowedSourceAge).Freshness).ToArray();
            var currentLldp = freshness == SourceFreshness.Current && matching.Any(row =>
                row.Protocol == DiscoveryProtocol.Lldp &&
                SourceFreshnessPolicy.Evaluate(row.Timing, atUtc, row.AllowedSourceAge).Freshness == SourceFreshness.Current);
            var effectiveFreshness = freshness == SourceFreshness.Unknown || rowFreshness.Contains(SourceFreshness.Unknown)
                ? SourceFreshness.Unknown : freshness == SourceFreshness.Delayed || rowFreshness.Contains(SourceFreshness.Delayed)
                    ? SourceFreshness.Delayed : SourceFreshness.Current;
            return new(NeighborPresence.Observed, currentLldp, false, effectiveFreshness,
                NeighborAssessmentReason.ValidObservation, rows);
        }
        if (observation.Outcome != RunOutcome.Succeeded || observation.Completeness != ObservationCompleteness.Complete ||
            !observation.ScopeCovered || !EnrichmentGuard.ValidEndpoint(expected) ||
            string.IsNullOrWhiteSpace(observation.Scope.Local.SourceDeviceId) ||
            string.IsNullOrWhiteSpace(observation.Scope.Local.SourceInterfaceId))
            return Unknown(NeighborAssessmentReason.Incomplete);
        if (freshness != SourceFreshness.Current)
            return Unknown(NeighborAssessmentReason.UnverifiedSource);
        return new(NeighborPresence.ExpectedNeighborNotObserved, false, true, freshness,
            NeighborAssessmentReason.CurrentCompleteAbsence, rows);
    }
}
