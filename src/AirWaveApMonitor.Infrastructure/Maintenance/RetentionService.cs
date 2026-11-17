using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Maintenance;

public sealed class RetentionService(StorageLocation location, SemaphoreSlim writeGate, SqliteCollectionStore store)
{
    public async Task<RetentionResult> PruneAsync(DateTimeOffset now, CancellationToken ct)
    {
        await store.InitializeAsync(ct);
        await writeGate.WaitAsync(ct);
        int details, gaps, logs, power;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(location.DataDirectory, "monitor.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = true, DefaultTimeout = 5 }.ToString());
            connection.Open(); Execute(connection, null, "PRAGMA synchronous=FULL");
            using var transaction = connection.BeginTransaction();
            var detailCutoff = now.ToUniversalTime() - TimeSpan.FromDays(90);
            var logCutoff = now.ToUniversalTime() - TimeSpan.FromDays(30);
            details = Execute(connection, transaction, """
                DELETE FROM run_execution_details WHERE recorded_at_utc < $cutoff
                AND EXISTS(SELECT 1 FROM runs WHERE runs.run_id=run_execution_details.run_id AND lifecycle!=0)
                """, ("$cutoff", detailCutoff));
            // Gap flags and all immutable run/incident snapshots remain. An open gap or any
            // gap still referenced by an open incident/current state is never expired.
            gaps = Execute(connection, transaction, """
                DELETE FROM observation_gaps AS g WHERE
                (json_extract(g.gap_json,'$.ResumedAtUtc') < $cutoff OR json_extract(g.gap_json,'$.TrackingEndedAtUtc') < $cutoff)
                AND NOT EXISTS(SELECT 1 FROM incidents i WHERE i.incident_id=json_extract(g.gap_json,'$.IncidentId') AND i.is_open=1)
                AND NOT EXISTS(SELECT 1 FROM ap_current c WHERE c.ap_id=g.ap_id AND c.generation_id=g.generation_id AND c.interval_id=g.interval_id
                    AND json_extract(c.tracking_json,'$.OpenGap.FirstRunId')=g.first_run_id)
                """, ("$cutoff", detailCutoff));
            logs = Execute(connection, transaction, "DELETE FROM operational_logs WHERE recorded_at_utc < $cutoff", ("$cutoff", logCutoff));
            power = SqliteCollectionStore.PruneEnrichment(connection, transaction, now.ToUniversalTime(), ct);
            ct.ThrowIfCancellationRequested(); transaction.Commit();
        }
        finally { writeGate.Release(); }
        var fileLines = new OperationalLog(location, TimeProvider.System).Prune(now.ToUniversalTime() - TimeSpan.FromDays(30));
        return new(details, gaps, logs, fileLines, true, power);
    }
}
