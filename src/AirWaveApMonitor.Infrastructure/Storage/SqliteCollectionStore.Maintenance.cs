using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    private static DateTimeOffset? PendingRestoreBoundary(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction, "SELECT MIN(backup_created_at_utc) FROM restore_events WHERE consumed=0");
        using var reader = command.ExecuteReader();
        return reader.Read() ? Time(reader, 0) : null;
    }

    private void ConsumeRestoreEvents(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        var events = new List<(Guid Id, DateTimeOffset BackupAt, DateTimeOffset RestoredAt)>();
        using (var command = Command(connection, transaction, "SELECT restore_event_id,backup_created_at_utc,restored_at_utc FROM restore_events WHERE consumed=0 ORDER BY restored_at_utc,restore_event_id"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) events.Add((Guid.Parse(reader.GetString(0)), Time(reader, 1)!.Value, Time(reader, 2)!.Value));
        foreach (var item in events)
        {
            ct.ThrowIfCancellationRequested();
            ConsumeEnrichmentRestoreEvent(connection, transaction, item.Id, item.BackupAt, item.RestoredAt, ct);
            long? restorationRun = null;
            var latest = Scalar(connection, transaction, "SELECT MAX(run_id) FROM runs WHERE kind=0") as long?;
            if (latest is { } last)
            {
                var original = ReadContext(connection, transaction, last).Profile;
                var targets = ImmutableArray.CreateBuilder<ExpectedTarget>();
                using (var command = Command(connection, transaction, """
                    SELECT t.source_ap_id,c.generation_id,c.interval_id FROM expected_targets t
                    LEFT JOIN aps a ON a.dataset_id=t.dataset_id AND a.instance_id=$instance AND a.source_ap_id=t.source_ap_id
                    LEFT JOIN ap_current c ON c.ap_id=a.ap_id
                    WHERE t.dataset_id=$dataset AND t.scope_version=$scope ORDER BY t.source_ap_id
                    """, ("$dataset", location.DatasetId), ("$scope", original.ScopeVersion), ("$instance", original.InstanceId)))
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) targets.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2))));
                var profile = new ProfileSnapshot(original.InstanceId, original.DatasetId, original.Origin,
                    original.ConfigurationVersion, original.ScopeVersion, targets.ToImmutable(), original.BaselineStatus, original.Settings)
                { Proxy = original.Proxy, CredentialsVersion = original.CredentialsVersion,
                    MonitoringScope = original.MonitoringScope, ScopeRevisions = original.ScopeRevisions,
                    BaselineConfirmedSourceApIds = original.BaselineConfirmedSourceApIds };
                Execute(connection, transaction, "INSERT INTO runs(dataset_id,scope_version,kind,lifecycle,context_json,target_universe_known) VALUES($dataset,$scope,0,0,'',$known)",
                    ("$dataset", location.DatasetId), ("$scope", profile.ScopeVersion), ("$known", targets.Count > 0 ? 1 : 0));
                restorationRun = Convert.ToInt64(Scalar(connection, transaction, "SELECT last_insert_rowid()"));
                var context = new CollectionContext(restorationRun.Value, profile, CollectionKind.ApStatus, RunTrigger.DatabaseRestore,
                    item.RestoredAt, item.RestoredAt + profile.Settings.RunTimeout);
                Execute(connection, transaction, "UPDATE runs SET context_json=$json WHERE run_id=$run", ("$json", Encode(context)), ("$run", context.RunId));
                FreezeOperationBindings(connection, transaction, context);
                FinalizeAp(connection, transaction, context,
                    new(RunOutcome.Failed, [], [], item.BackupAt, ObservationCompleteness.Unknown,
                        [new(CollectionErrorCategory.Interrupted, 1, "DB 백업 복원으로 보존되지 않은 관측 구간입니다. 다음 유효 관측까지 공백을 유지합니다.")]),
                    RunLifecycle.Interrupted, ct);
            }
            Execute(connection, transaction, "UPDATE restore_events SET consumed=1,consumed_run_id=$run WHERE restore_event_id=$id AND consumed=0",
                ("$run", restorationRun), ("$id", item.Id));
            Execute(connection, transaction, "INSERT INTO operational_logs(recorded_at_utc,category,related_run_id) VALUES($at,'RestoreApplied',$run)",
                ("$at", timeProvider.GetUtcNow()), ("$run", restorationRun));
        }
    }

    private static void PersistExecutionDetails(SqliteConnection connection, SqliteTransaction transaction,
        CollectionContext context, ApCollectionBatch batch, DateTimeOffset finalized, int accepted)
    {
        var elapsed = finalized - context.StartedAtUtc;
        Execute(connection, transaction, "INSERT INTO run_execution_details VALUES($run,$at,$elapsed,$received,$accepted,$excluded,$errors)",
            ("$run", context.RunId), ("$at", finalized), ("$elapsed", elapsed < TimeSpan.Zero ? null : (long?)elapsed.TotalMilliseconds),
            ("$received", batch.Observations.Length), ("$accepted", accepted), ("$excluded", batch.ExcludedRecords.Length),
            ("$errors", Encode(batch.Errors.GroupBy(e => e.Category).Select(group => new { Category = group.Key, Count = group.Sum(e => e.Count) }).ToArray())));
    }

    public async Task RecordOperationalLogAsync(string category, CancellationToken ct)
    {
        if (category is not ("BackupSucceeded" or "BackupFailed" or "RetentionSucceeded" or "RetentionFailed"))
            throw new ArgumentException("허용되지 않은 운영 기록 종류입니다.", nameof(category));
        await writeGate.WaitAsync(ct);
        try
        {
            using var connection = Open(); Initialize(connection, ct);
            Execute(connection, null, "INSERT INTO operational_logs(recorded_at_utc,category) VALUES($at,$category)",
                ("$at", timeProvider.GetUtcNow()), ("$category", category));
        }
        finally { writeGate.Release(); }
    }

    public async Task<DateTimeOffset?> GetLastRestoreAtAsync(CancellationToken ct)
    {
        await writeGate.WaitAsync(ct);
        try
        {
            using var connection = Open(); Initialize(connection, ct);
            using var command = Command(connection, null, "SELECT restored_at_utc FROM restore_events ORDER BY restored_at_utc DESC LIMIT 1");
            using var reader = command.ExecuteReader(); return reader.Read() ? Time(reader, 0) : null;
        }
        finally { writeGate.Release(); }
    }
}
