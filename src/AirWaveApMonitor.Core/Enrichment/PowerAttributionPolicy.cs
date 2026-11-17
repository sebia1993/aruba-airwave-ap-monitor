using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Core.Enrichment;

public static class PowerAttributionPolicy
{
    public static PowerAttribution Resolve(PowerSample sample, IReadOnlyList<ConnectionObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(observations);
        var measuredAt = sample.Timing.SourceAtUtc;
        DateTimeOffset? verifiedAt = null;
        PowerAttribution Unassigned(PowerAttributionStatus status, PowerAttributionReason reason)
            => new(sample.Id, status, reason, verifiedAt, null, null);
        if (sample.Id == Guid.Empty || sample.RunId <= 0 || PowerValuePolicy.Normalize(sample).Watts is null)
            return Unassigned(PowerAttributionStatus.Unknown, PowerAttributionReason.InvalidSample);
        if (measuredAt is not { } at || !sample.Timing.MeaningVerified ||
            sample.Timing.Meaning != SourceTimeMeaning.Measurement || at > sample.Timing.ReadAtUtc)
            return Unassigned(PowerAttributionStatus.Unknown, PowerAttributionReason.UnknownMeasurementTime);
        verifiedAt = at;
        if (string.IsNullOrWhiteSpace(sample.Endpoint.SourceDeviceId))
            return Unassigned(PowerAttributionStatus.Unknown, PowerAttributionReason.MissingIdentity);

        if (sample.Metric == PowerMetric.ApConsumption)
        {
            if (sample.Endpoint.Role != EndpointRole.AccessPoint || sample.DirectTarget is not { } direct ||
                !ValidTarget(direct, sample.Profile, sample.Endpoint.SourceDeviceId))
                return Unassigned(PowerAttributionStatus.OutsideBinding, PowerAttributionReason.BindingMismatch);
            if (direct.IdentityWindow is not { ContinuityVerified: true } identityWindow)
                return Unassigned(PowerAttributionStatus.Unknown, PowerAttributionReason.UnknownIdentityWindow);
            if (!Contains(identityWindow, at))
                return Unassigned(PowerAttributionStatus.OutsideBinding, PowerAttributionReason.OutsideIdentityWindow);
            if (!ValidPlacement(direct, at, sample.Timing.ReadAtUtc))
                return Unassigned(PowerAttributionStatus.OutsideBinding, PowerAttributionReason.InvalidPlacement);
            return new(sample.Id, PowerAttributionStatus.Attributed, PowerAttributionReason.DirectApMeasurement, at, direct, null);
        }
        if (sample.Endpoint.Role != EndpointRole.Switch || string.IsNullOrWhiteSpace(sample.Endpoint.SourceInterfaceId))
            return Unassigned(PowerAttributionStatus.Unknown, PowerAttributionReason.MissingIdentity);

        var candidates = new List<ConnectionObservation>();
        var stale = false;
        var outside = false;
        foreach (var row in observations)
        {
            var remote = row.Local == sample.Endpoint ? row.Remote : row.Remote == sample.Endpoint ? row.Local : null;
            if (remote is null)
                continue;
            if (row.Profile != sample.Profile || row.Target is not { } target || remote.Role != EndpointRole.AccessPoint ||
                !ValidTarget(target, sample.Profile, remote.SourceDeviceId))
            {
                outside = true;
                continue;
            }
            // The connection's verified mapping/window can supply historical identity evidence, but
            // a separately recorded target window must not contradict it.
            if (target.IdentityWindow is { } targetWindow && (!targetWindow.ContinuityVerified || !Contains(targetWindow, at)))
            {
                outside = true;
                continue;
            }
            if (row.Id == Guid.Empty || row.RunId <= 0 || !row.IdentityMappingVerified || row.Protocol is not (DiscoveryProtocol.Lldp or DiscoveryProtocol.Cdp or DiscoveryProtocol.OtherVerified) ||
                row.Evidence.Availability != FieldAvailability.Provided || row.Evidence.Validity != FieldValidity.Valid ||
                row.Evidence.Source != row.Profile.Source || row.Timing.ReadAtUtc > sample.Timing.ReadAtUtc ||
                row.Window is not { ContinuityVerified: true } window || window.FromUtc > at ||
                (window.UntilUtc is { } until && (until <= window.FromUtc || at >= until)))
                continue;
            if (!ValidPlacement(target, at, sample.Timing.ReadAtUtc))
            {
                outside = true;
                continue;
            }
            var freshness = SourceFreshnessPolicy.Evaluate(row.Timing, at, row.AllowedSourceAge).Freshness;
            if (freshness != SourceFreshness.Current)
            {
                stale |= freshness == SourceFreshness.Delayed;
                continue;
            }
            candidates.Add(row);
        }
        if (candidates.Select(row => row.Target).Distinct().Skip(1).Any() ||
            candidates.GroupBy(row => row.Id).Any(group => group.Distinct().Skip(1).Any()))
            return Unassigned(PowerAttributionStatus.Ambiguous, PowerAttributionReason.AmbiguousConnections);
        var selected = candidates.OrderByDescending(row => row.Timing.SourceAtUtc).ThenBy(row => row.Id).FirstOrDefault();
        if (selected is not null)
            return new(sample.Id, PowerAttributionStatus.Attributed, PowerAttributionReason.HistoricalConnection,
                at, selected.Target, selected);
        if (outside)
            return Unassigned(PowerAttributionStatus.OutsideBinding, PowerAttributionReason.BindingMismatch);
        return stale ? Unassigned(PowerAttributionStatus.Stale, PowerAttributionReason.StaleConnection)
            : Unassigned(PowerAttributionStatus.Unknown, PowerAttributionReason.NoConnection);
    }

    private static bool ValidTarget(EnrichmentTargetBinding target, EnrichmentProfileBinding profile, string sourceApId)
        => target.Profile == profile && target.ApId != Guid.Empty && target.DeviceGenerationId != Guid.Empty &&
            target.MonitoringIntervalId != Guid.Empty && !string.IsNullOrWhiteSpace(sourceApId) &&
            string.Equals(target.SourceApId, sourceApId, StringComparison.Ordinal);

    private static bool Contains(EvidenceInterval window, DateTimeOffset at)
        => window.FromUtc <= at && (window.UntilUtc is not { } until || (until > window.FromUtc && at < until));

    private static bool ValidPlacement(EnrichmentTargetBinding target, DateTimeOffset at, DateTimeOffset readAt)
        => target.Placement is not { } placement ||
            (placement.DeviceGenerationId == target.DeviceGenerationId && placement.PlacementVersion > 0 &&
             (placement.InstallationPointId is { } point ? point != Guid.Empty && placement.PointVersion > 0 : placement.PointVersion is null) &&
             placement.EffectiveFromUtc <= at && placement.RecordedAtUtc <= readAt &&
             (placement.EffectiveUntilUtc is null || placement.EffectiveUntilUtc > at));
}
