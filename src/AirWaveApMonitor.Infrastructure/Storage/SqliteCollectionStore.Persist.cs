using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Monitoring;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    private void PersistDecision(SqliteConnection connection, SqliteTransaction transaction,
        CollectionContext context, ApTransitionDecision decision, StoredAp? previous, bool includeRow)
    {
        var next = decision.Next;
        if (next.Identity.DeviceGenerationId is null || next.MonitoringIntervalId is null ||
            decision.Incidents.Any(mutation => mutation.Incident.IncidentId is null ||
                mutation.Incident.DeviceGenerationId is null || mutation.Incident.MonitoringIntervalId is null))
        {
            throw new InvalidDataException("저장 전에 생성 ID가 모두 할당되어야 합니다.");
        }
        var apId = previous?.View.ApId ?? Guid.NewGuid();
        var view = DeviceMetadataMerger.Merge(previous?.View, decision, apId);
        Execute(connection, transaction, "INSERT OR IGNORE INTO aps VALUES($ap,$dataset,$instance,$source)",
            ("$ap", apId), ("$dataset", context.Profile.DatasetId), ("$instance", context.Profile.InstanceId), ("$source", decision.SourceApId));
        Execute(connection, transaction, "INSERT OR IGNORE INTO generations VALUES($generation,$ap,$run,NULL,$confirm)",
            ("$generation", view.DeviceGenerationId), ("$ap", apId), ("$run", context.RunId),
            ("$confirm", decision.Identity?.ReplacementNeedsConfirmation == true ? 1 : 0));
        Execute(connection, transaction, "INSERT OR IGNORE INTO monitoring_intervals VALUES($interval,$generation,$run,NULL,NULL)",
            ("$interval", view.MonitoringIntervalId), ("$generation", view.DeviceGenerationId), ("$run", context.RunId));
        if (decision.Segment.EndsPreviousInterval && previous is not null)
        {
            Execute(connection, transaction, "UPDATE monitoring_intervals SET ended_run_id=$run,end_reason=$reason WHERE interval_id=$interval",
                ("$run", context.RunId), ("$reason", decision.Segment.EndReason), ("$interval", previous.View.MonitoringIntervalId));
            if (previous.View.DeviceGenerationId != view.DeviceGenerationId)
            {
                Execute(connection, transaction, "UPDATE generations SET ended_run_id=$run WHERE generation_id=$generation",
                    ("$run", context.RunId), ("$generation", previous.View.DeviceGenerationId));
            }
        }
        view = AttachFrozenRegistration(connection, transaction, context, view);
        RecordAutomaticChanges(connection, transaction, context, previous?.View, view, decision.Identity?.Change ?? IdentityChange.Unchanged,
            next.LastProcessedAtUtc ?? context.StartedAtUtc, previous?.Tracking.OpenGap is not null || decision.ClosedGap is not null, next.TimeNeedsReview);
        var measurements = new MetadataMeasurements(view.Metadata.Device.Uptime, view.Metadata.Device.RebootedAtUtc,
            view.Metadata.RawSourceValues);
        var metadataJson = Encode(view.Metadata with
        {
            Device = view.Metadata.Device with { Uptime = null, RebootedAtUtc = null },
            RawSourceValues = null
        });
        Execute(connection, transaction, "INSERT OR IGNORE INTO metadata_versions(ap_id,generation_id,metadata_json) VALUES($ap,$generation,$json)",
            ("$ap", apId), ("$generation", view.DeviceGenerationId), ("$json", metadataJson));
        var metadataId = Convert.ToInt64(Scalar(connection, transaction,
            "SELECT metadata_version_id FROM metadata_versions WHERE ap_id=$ap AND generation_id=$generation AND metadata_json=$json",
            ("$ap", apId), ("$generation", view.DeviceGenerationId), ("$json", metadataJson)));
        if (previous?.MetadataVersionId != metadataId)
        {
            Execute(connection, transaction, "INSERT INTO metadata_changes VALUES($run,$ap,$before,$after)",
                ("$run", context.RunId), ("$ap", apId), ("$before", previous?.MetadataVersionId), ("$after", metadataId));
        }
        Execute(connection, transaction, """
            INSERT INTO ap_current VALUES($ap,$generation,$interval,$metadata,$run,$tracking,$fields,$sourceAt,$lastContacted,$measurements)
            ON CONFLICT(ap_id) DO UPDATE SET generation_id=excluded.generation_id,interval_id=excluded.interval_id,
                metadata_version_id=excluded.metadata_version_id,last_processed_run_id=excluded.last_processed_run_id,
                tracking_json=excluded.tracking_json,fields_json=excluded.fields_json,source_at_utc=excluded.source_at_utc,
                last_contacted_at_utc=excluded.last_contacted_at_utc,measurements_json=excluded.measurements_json
            """, ("$ap", apId), ("$generation", view.DeviceGenerationId), ("$interval", view.MonitoringIntervalId),
            ("$metadata", metadataId), ("$run", context.RunId), ("$tracking", Encode(next)), ("$fields", Encode(view.Fields)),
            ("$sourceAt", view.SourceAtUtc), ("$lastContacted", view.LastContactedAtUtc), ("$measurements", Encode(measurements)));
        foreach (var mutation in decision.Incidents)
        {
            var incident = mutation.Incident;
            var isOpen = incident.RecoveredRunId is null && incident.TrackingEndedRunId is null ? 1 : 0;
            if (mutation.Kind == IncidentMutationKind.Created)
            {
                Execute(connection, transaction, "INSERT INTO incidents VALUES($id,$ap,$generation,$interval,$open,$json)",
                    ("$id", incident.IncidentId), ("$ap", apId), ("$generation", incident.DeviceGenerationId),
                    ("$interval", incident.MonitoringIntervalId), ("$open", isOpen), ("$json", Encode(incident)));
            }
            else if (Execute(connection, transaction, "UPDATE incidents SET is_open=$open,tracking_json=$json WHERE incident_id=$id AND ap_id=$ap AND generation_id=$generation AND interval_id=$interval AND is_open=1",
                ("$open", isOpen), ("$json", Encode(incident)), ("$id", incident.IncidentId), ("$ap", apId),
                ("$generation", incident.DeviceGenerationId), ("$interval", incident.MonitoringIntervalId)) != 1)
            {
                throw new InvalidDataException("갱신할 열린 사건의 참조가 일치하지 않습니다.");
            }
            Execute(connection, transaction, "INSERT INTO run_transitions VALUES($run,$ap,$incident,$generation,$interval,$kind,$json)",
                ("$run", context.RunId), ("$ap", apId), ("$incident", incident.IncidentId), ("$generation", incident.DeviceGenerationId),
                ("$interval", incident.MonitoringIntervalId), ("$kind", mutation.Kind), ("$json", Encode(incident)));
            var eventView = incident.DeviceGenerationId == view.DeviceGenerationId ? view : previous?.View;
            var source = decision.AcceptedObservation?.Fields.GetValueOrDefault(MonitoringFieldNames.State)?.Source
                ?? ReadFrozenBinding(connection, transaction, context.RunId, apId)?.PolicySource;
            RecordIncidentEvent(connection, transaction, context, apId, mutation, source, eventView?.Metadata.ManualInventory?.Placement?.InstallationPointId);
            if (mutation.Kind is IncidentMutationKind.Created or IncidentMutationKind.Recovered)
            {
                var snapshot = new IncidentSnapshotBuilder().Build(view, context);
                Execute(connection, transaction, "INSERT INTO incident_snapshots VALUES($incident,$kind,$run,$json)",
                    ("$incident", incident.IncidentId), ("$kind", mutation.Kind == IncidentMutationKind.Created ? 0 : 1),
                    ("$run", context.RunId), ("$json", Encode(snapshot)));
            }
        }
        if (decision.ClosedGap is { } closed && previous is not null)
        {
            SaveGap(connection, transaction, apId, previous.View.DeviceGenerationId, previous.View.MonitoringIntervalId, closed);
        }
        if (next.OpenGap is { } openGap)
        {
            SaveGap(connection, transaction, apId, view.DeviceGenerationId, view.MonitoringIntervalId, openGap);
        }
        if (previous is not null && (previous.Tracking.MonitoringStatus != next.MonitoringStatus || decision.Segment.EndsPreviousInterval))
        {
            Execute(connection, transaction, "INSERT INTO monitoring_changes VALUES($run,$ap,$status,$reason)",
                ("$run", context.RunId), ("$ap", apId), ("$status", next.MonitoringStatus),
                ("$reason", decision.Segment.ChangeReason ?? decision.Segment.EndReason ?? "현재 범위 재편입"));
        }
        if (includeRow)
        {
            var firstDown = next.OpenIncident?.FirstDownAtUtc ?? decision.Incidents
                .FirstOrDefault(mutation => mutation.Kind == IncidentMutationKind.Recovered)?.Incident.FirstDownAtUtc;
            var facts = new RowFacts(decision.CurrentState, next.LastValidState, next.LastValidRunId,
                next.LastValidObservedAtUtc, firstDown, next.OpenIncident?.IncidentId, decision.Classification,
                view.Fields, decision.UnknownReason, next.TimeNeedsReview, view.SourceAtUtc, view.LastContactedAtUtc, measurements);
            Execute(connection, transaction, "INSERT INTO run_rows VALUES($run,$ap,$generation,$interval,$metadata,$json)",
                ("$run", context.RunId), ("$ap", apId), ("$generation", view.DeviceGenerationId),
                ("$interval", view.MonitoringIntervalId), ("$metadata", metadataId), ("$json", Encode(facts)));
        }
    }

    private static void SaveGap(SqliteConnection connection, SqliteTransaction transaction, Guid apId,
        Guid generation, Guid interval, ObservationGap gap)
    {
        Execute(connection, transaction, """
            INSERT INTO observation_gaps VALUES($ap,$generation,$interval,$first,$json)
            ON CONFLICT(ap_id,generation_id,interval_id,first_run_id) DO UPDATE SET gap_json=excluded.gap_json
            """, ("$ap", apId), ("$generation", generation), ("$interval", interval), ("$first", gap.FirstRunId), ("$json", Encode(gap)));
    }
}
