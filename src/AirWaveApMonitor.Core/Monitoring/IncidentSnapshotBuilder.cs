using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Core.Monitoring;

public sealed class IncidentSnapshotBuilder
{
    public IncidentSnapshot Build(CommittedDeviceView view, CollectionContext context)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(context);
        if (view.ApId == Guid.Empty || view.DeviceGenerationId == Guid.Empty ||
            view.MonitoringIntervalId == Guid.Empty || string.IsNullOrWhiteSpace(view.SourceApId) ||
            context.Kind != CollectionKind.ApStatus || context.RunId <= 0)
        {
            throw new ArgumentException("확정된 AP·세대·감시 구간과 AP 회차가 필요합니다.");
        }
        return new(context.Profile.DatasetId, context.Profile.InstanceId, context.Profile.ScopeVersion,
            context.RunId, view.Fields.GetValueOrDefault(MonitoringFieldNames.State)?.CollectedAtUtc ?? context.StartedAtUtc,
            view with { Enrichment = view.Enrichment.IsDefault ? ImmutableArray<EnrichmentObservation>.Empty : view.Enrichment });
    }
}
