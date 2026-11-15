using AirWaveApMonitor.Core.Contracts;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    internal static int PruneEnrichment(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now - TimeSpan.FromDays(30); ct.ThrowIfCancellationRequested();
        var deleted = Execute(connection, transaction, "DELETE FROM power_samples WHERE read_at_utc < $cutoff", ("$cutoff", cutoff));
        // A successful zero-row prune still establishes the real retention boundary. Never move it
        // backwards after a clock correction; immutable incidents have their own full copies.
        Execute(connection, transaction, "INSERT INTO enrichment_retention_state VALUES('power',$cutoff) ON CONFLICT(key) DO UPDATE SET cutoff_utc=MAX(enrichment_retention_state.cutoff_utc,excluded.cutoff_utc)", ("$cutoff", cutoff));
        Execute(connection, transaction, """
            DELETE FROM enrichment_gap_versions AS v WHERE EXISTS
                (SELECT 1 FROM enrichment_gaps g WHERE g.gap_id=v.gap_id AND g.is_open=0 AND g.last_detail_at_utc<$cutoff AND v.version<>g.current_version)
                AND NOT EXISTS(SELECT 1 FROM enrichment_snapshot_refs r, json_each(r.references_json,'$.Gaps') j
                    WHERE json_extract(j.value,'$.Id')=v.gap_id AND json_extract(j.value,'$.Version')=v.version)
            """, ("$cutoff", now - TimeSpan.FromDays(90)));
        return deleted;
    }
    private void ConsumeEnrichmentRestoreEvent(SqliteConnection connection, SqliteTransaction transaction, Guid restoreId,
        DateTimeOffset backupAt, DateTimeOffset restoredAt, CancellationToken ct)
    {
        foreach (var kind in new[] { CollectionKind.ConnectionNeighbor, CollectionKind.Power })
        {
            if (Scalar(connection, transaction, "SELECT consumed_run_id FROM enrichment_restore_events WHERE restore_event_id=$id AND kind=$kind", ("$id", restoreId), ("$kind", kind)) is not null) continue;
            if (Scalar(connection, transaction, "SELECT MAX(run_id) FROM runs WHERE kind=$kind", ("$kind", kind)) is not long last) continue;
            var profile = ReadContext(connection, transaction, last).Profile;
            Execute(connection, transaction, "INSERT INTO runs(dataset_id,scope_version,kind,lifecycle,context_json,target_universe_known) VALUES($dataset,$scope,$kind,0,'',$known)",
                ("$dataset", profile.DatasetId), ("$scope", profile.ScopeVersion), ("$kind", kind), ("$known", profile.ExpectedTargets.Length > 0 ? 1 : 0));
            var run = Convert.ToInt64(Scalar(connection, transaction, "SELECT last_insert_rowid()"));
            var context = new CollectionContext(run, profile, kind, RunTrigger.DatabaseRestore, restoredAt, restoredAt + profile.Settings.RunTimeout);
            Execute(connection, transaction, "UPDATE runs SET context_json=$json WHERE run_id=$run", ("$json", Encode(context)), ("$run", run));
            FreezeOperationBindings(connection, transaction, context); FreezeEnrichmentBindings(connection, transaction, context);
            FinalizeEnrichment(connection, transaction, context, InterruptedEnrichment(context, backupAt), RunLifecycle.Interrupted, ct);
            Execute(connection, transaction, "INSERT INTO enrichment_restore_events VALUES($id,$kind,$run)", ("$id", restoreId), ("$kind", kind), ("$run", run));
        }
    }
}
