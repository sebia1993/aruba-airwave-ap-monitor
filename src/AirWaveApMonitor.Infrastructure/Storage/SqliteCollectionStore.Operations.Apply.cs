using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Operations;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    private void FreezeOperationBindings(SqliteConnection connection, SqliteTransaction transaction, CollectionContext context)
    {
        var current = new List<(Guid Ap, Guid Generation)>();
        using (var command = Command(connection, transaction, "SELECT a.ap_id,c.generation_id FROM aps a JOIN ap_current c ON c.ap_id=a.ap_id WHERE a.dataset_id=$dataset AND a.instance_id=$instance", ("$dataset", context.Profile.DatasetId), ("$instance", context.Profile.InstanceId)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) current.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1))));
        foreach (var (ap, generation) in current)
            Execute(connection, transaction, "INSERT INTO run_manual_bindings VALUES($run,$ap,$gen,$json)",
                ("$run", context.RunId), ("$ap", ap), ("$gen", generation), ("$json", Encode(CurrentBinding(connection, transaction, ap, generation))));
    }
    private static FrozenOperationsBinding? ReadFrozenBinding(SqliteConnection connection, SqliteTransaction transaction, long runId, Guid apId)
        => Optional<FrozenOperationsBinding>(connection, transaction, "SELECT binding_json FROM run_manual_bindings WHERE run_id=$run AND ap_id=$ap", ("$run", runId), ("$ap", apId));

    private static ApObservation PreserveFreshIdentity(StoredAp? previous, ApObservation observation)
    {
        if (previous is null) return observation;
        var identity = observation.Identity; var fields = observation.Fields;
        foreach (var name in new[] { MonitoringFieldNames.SerialNumber, MonitoringFieldNames.WiredMac })
        {
            if (fields.TryGetValue(name, out var current) && previous.Tracking.Identity.Fields.TryGetValue(name, out var old) &&
                current.Availability == FieldAvailability.Provided && current.Validity == FieldValidity.Valid &&
                current.SourceAtUtc is { } currentAt && old.SourceAtUtc is { } oldAt && currentAt < oldAt)
            {
                identity = name == MonitoringFieldNames.SerialNumber ? identity with { SerialNumber = previous.Tracking.Identity.Identity.SerialNumber }
                    : identity with { WiredMac = previous.Tracking.Identity.Identity.WiredMac };
                fields = fields.SetItem(name, old);
            }
        }
        return observation with { Identity = identity, Fields = fields };
    }

    private static CommittedDeviceView AttachFrozenRegistration(SqliteConnection connection, SqliteTransaction transaction,
        CollectionContext context, CommittedDeviceView view)
    {
        var binding = ReadFrozenBinding(connection, transaction, context.RunId, view.ApId);
        if (binding is null) binding = new(view.DeviceGenerationId, null, null, null, 0, null);
        else if (binding.GenerationId != view.DeviceGenerationId)
            binding = new(view.DeviceGenerationId, null, binding.Policy, null, 0, null, binding.PolicySource);
        var metadata = view.Metadata with
        {
            ManualInventory = ManualSnapshot(binding),
            ManualManagement = binding.Policy is { } policy ? new(policy.Status, policy.Reason, policy.RecordedAtUtc) : binding.LegacyManagement
        };
        Execute(connection, transaction, "INSERT INTO operations_applied_bindings VALUES($run,$ap,$gen,$json)",
            ("$run", context.RunId), ("$ap", view.ApId), ("$gen", view.DeviceGenerationId), ("$json", Encode(binding)));
        return view with { Metadata = metadata };
    }

    private static void RecordAutomaticChanges(SqliteConnection connection, SqliteTransaction transaction, CollectionContext context,
        CommittedDeviceView? previous, CommittedDeviceView current, IdentityChange identity, DateTimeOffset observedAt,
        bool gap, bool timeNeedsReview)
    {
        var evidence = new ObservationEvidence(context.Profile.DatasetId, context.Profile.InstanceId, current.ApId, current.DeviceGenerationId,
            context.RunId, context.Profile.ScopeVersion, observedAt, previous?.Fields ?? ImmutableDictionary<string, FieldEvidence>.Empty,
            current.Fields, previous?.DeviceGenerationId, identity, gap, timeNeedsReview);
        foreach (var change in new ChangeDetector().Compare(previous?.Metadata, current.Metadata, evidence))
        {
            var target = new WorkTarget(current.ApId, current.DeviceGenerationId, InstallationPointId: current.Metadata.ManualInventory?.Placement?.InstallationPointId);
            var targets = identity == IdentityChange.Replaced && change.Kind == OperationHistoryKind.HardwareReplacement && previous is not null
                ? ImmutableArray.Create(new WorkTarget(previous.ApId, previous.DeviceGenerationId, InstallationPointId: previous.Metadata.ManualInventory?.Placement?.InstallationPointId), target)
                : ImmutableArray.Create(target);
            AddEvent(connection, transaction, context.Profile.DatasetId, $"run:{context.RunId}:ap:{current.ApId}:change:{change.Semantics}:{change.Field}",
                new(0, change.Kind, observedAt, change.AfterEvidence?.Source, change.Semantics.ToString(), change.Field,
                    change.BeforeValue, change.AfterValue, null, null, targets, context.RunId, change, TimeNeedsReview: change.TimeNeedsReview));
        }
    }
    private static void RecordIncidentEvent(SqliteConnection connection, SqliteTransaction transaction, CollectionContext context,
        Guid apId, IncidentMutation mutation, EvidenceSource? source, Guid? pointId)
    {
        if (mutation.Kind == IncidentMutationKind.Updated) return;
        var incident = mutation.Incident;
        var at = mutation.Kind switch { IncidentMutationKind.Created => incident.FirstDownAtUtc,
            IncidentMutationKind.Recovered => incident.RecoveredAtUtc!.Value, _ => incident.TrackingEndedAtUtc!.Value };
        var after = mutation.Kind switch { IncidentMutationKind.Created => "Open", IncidentMutationKind.Recovered => "Recovered", _ => "TrackingEnded" };
        AddEvent(connection, transaction, context.Profile.DatasetId, $"run:{context.RunId}:incident:{incident.IncidentId}:{mutation.Kind}",
            new(0, OperationHistoryKind.Incident, at, source, mutation.Kind.ToString(), "incident",
                mutation.Kind == IncidentMutationKind.Created ? null : "Open", after, incident.TrackingEndReason, null,
                [new(apId, incident.DeviceGenerationId, incident.IncidentId, pointId)], context.RunId, Incident: incident, TimeNeedsReview: incident.TimeNeedsReview));
    }

    private void InitializeOperations(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        Execute(connection, transaction, "INSERT OR IGNORE INTO operations_restore_epochs(restore_event_id) SELECT restore_event_id FROM restore_events ORDER BY restored_at_utc,restore_event_id");
        if (Scalar(connection, transaction, "SELECT value FROM operations_state WHERE key='legacy_backfill'") is not null) return;
        var changes = new List<(long Run, Guid Ap, long? Before, long After)>();
        using (var command = Command(connection, transaction, "SELECT m.run_id,m.ap_id,m.before_version_id,m.after_version_id FROM metadata_changes m JOIN runs r USING(run_id) WHERE r.lifecycle!=0 ORDER BY m.run_id,m.ap_id"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) changes.Add((reader.GetInt64(0), Guid.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetInt64(3)));
        foreach (var change in changes)
        {
            ct.ThrowIfCancellationRequested(); var context = ReadContext(connection, transaction, change.Run);
            var current = ReadHistoricalView(connection, transaction, change.Ap, change.After, change.Run, false);
            if (current is null) continue; // No row-level evidence was stored for an excluded target; do not invent it.
            var previous = change.Before is { } before ? ReadHistoricalView(connection, transaction, change.Ap, before, change.Run, true) : null;
            var identity = previous is null ? IdentityChange.FirstGeneration : previous.DeviceGenerationId != current.DeviceGenerationId ? IdentityChange.Replaced : IdentityChange.Unchanged;
            var facts = ReadHistoricalFacts(connection, transaction, change.Run, change.Ap);
            var observed = facts.Fields.Values.Select(f => f.CollectedAtUtc).DefaultIfEmpty(context.StartedAtUtc).Max();
            // A schema 1-3 replacement could have accepted stale identity. Keep that historical fact
            // but do not synthesize a new typed event with contradictory evidence.
            try { RecordAutomaticChanges(connection, transaction, context, previous, current, identity, observed, facts.UnknownReason is not null, facts.TimeNeedsReview); }
            catch (ArgumentException) { /* Existing immutable source rows remain available; no guessed typed history. */ }
        }
        var incidents = new List<(long Run, Guid Ap, IncidentMutationKind Kind, IncidentTrackingSnapshot Tracking)>();
        using (var command = Command(connection, transaction, "SELECT run_id,ap_id,mutation_kind,tracking_json FROM run_transitions WHERE mutation_kind!=1 ORDER BY run_id,incident_id,mutation_kind"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) incidents.Add((reader.GetInt64(0), Guid.Parse(reader.GetString(1)), (IncidentMutationKind)reader.GetInt32(2), Decode<IncidentTrackingSnapshot>(reader.GetString(3))));
        foreach (var item in incidents)
        {
            ct.ThrowIfCancellationRequested(); var context = ReadContext(connection, transaction, item.Run);
            var snapshot = Optional<IncidentSnapshot>(connection, transaction, "SELECT snapshot_json FROM incident_snapshots WHERE incident_id=$id AND run_id=$run", ("$id", item.Tracking.IncidentId), ("$run", item.Run));
            var source = snapshot?.Device.Fields.GetValueOrDefault(MonitoringFieldNames.State)?.Source;
            RecordIncidentEvent(connection, transaction, context, item.Ap, new(item.Kind, item.Tracking), source,
                snapshot?.Device.Metadata.ManualInventory?.Placement?.InstallationPointId);
        }
        Execute(connection, transaction, "INSERT INTO operations_state VALUES('legacy_backfill',1)");
    }

    private static CommittedDeviceView? ReadHistoricalView(SqliteConnection connection, SqliteTransaction transaction,
        Guid apId, long metadataId, long runId, bool before)
    {
        using var command = Command(connection, transaction, """
            SELECT a.source_ap_id,r.generation_id,r.interval_id,m.metadata_json,r.facts_json,r.run_id,r.history_observed_at_utc,r.history_commit_sequence
            FROM historical_ap_rows r JOIN aps a USING(ap_id) JOIN metadata_versions m ON m.metadata_version_id=$metadata
            WHERE r.ap_id=$ap AND (($before=0 AND r.run_id=$run) OR ($before=1 AND r.run_id<$run))
            ORDER BY r.run_id DESC LIMIT 1
            """, ("$metadata", metadataId), ("$ap", apId), ("$before", before ? 1 : 0), ("$run", runId));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        var facts = ReadHistoryFacts(reader.GetString(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetInt64(7));
        return new(apId, reader.GetString(0), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
            RestoreMeasurements(Decode<DeviceMetadata>(reader.GetString(3)), facts.Measurements), facts.Fields, facts.SourceAtUtc, facts.LastContactedAtUtc);
    }
}
