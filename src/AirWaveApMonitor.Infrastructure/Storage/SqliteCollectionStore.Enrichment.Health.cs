using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    private void UpdateEnrichmentGap(SqliteConnection connection, SqliteTransaction transaction, CollectionContext context,
        string profile, RunOutcome outcome, DateTimeOffset at, CollectionErrorCategory reason)
    {
        var old = Optional<EnrichmentGapView>(connection, transaction, "SELECT v.view_json FROM enrichment_gaps g JOIN enrichment_gap_versions v ON v.gap_id=g.gap_id AND v.version=g.current_version WHERE g.profile_key=$profile AND g.kind=$kind AND g.is_open=1", ("$profile", profile), ("$kind", context.Kind));
        if (outcome == RunOutcome.Succeeded && old is null) return;
        if (outcome != RunOutcome.Succeeded && old is not null) return;
        if (old is null)
        {
            var id = Guid.NewGuid(); var view = new EnrichmentGapView(id, context.Kind, context.RunId, at, null, null, reason);
            Execute(connection, transaction, "INSERT INTO enrichment_gaps VALUES($id,$profile,$kind,$run,$at,$reason,1,1,$detail)",
                ("$id", id), ("$profile", profile), ("$kind", context.Kind), ("$run", context.RunId), ("$at", at), ("$reason", reason), ("$detail", timeProvider.GetUtcNow()));
            Execute(connection, transaction, "INSERT INTO enrichment_gap_versions VALUES($id,1,$at,$json)", ("$id", id), ("$at", timeProvider.GetUtcNow()), ("$json", Encode(view)));
        }
        else
        {
            var version = Convert.ToInt64(Scalar(connection, transaction, "SELECT current_version FROM enrichment_gaps WHERE gap_id=$id", ("$id", old.GapId))) + 1;
            Execute(connection, transaction, "INSERT INTO enrichment_gap_versions VALUES($id,$version,$at,$json)", ("$id", old.GapId), ("$version", version), ("$at", timeProvider.GetUtcNow()), ("$json", Encode(old with { ResumedRunId = context.RunId, ResumedAtUtc = at })));
            Execute(connection, transaction, "UPDATE enrichment_gaps SET current_version=$version,is_open=0,last_detail_at_utc=$at WHERE gap_id=$id", ("$version", version), ("$at", timeProvider.GetUtcNow()), ("$id", old.GapId));
        }
    }
    public Task<EnrichmentKindHealthSnapshot> GetEnrichmentHealthAsync(ProfileSnapshot profile, CollectionKind kind, CancellationToken ct)
    {
        ValidateDataset(profile.DatasetId); if (!IsEnrichmentKind(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        return ReadOperationsAsync((connection, transaction) =>
        {
            var key = EnrichmentProfileKey(profile);
            var pending = Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM runs WHERE dataset_id=$dataset AND kind=$kind AND lifecycle=0", ("$dataset", profile.DatasetId), ("$kind", kind))) > 0;
            var attempt = Scalar(connection, transaction, "SELECT MAX(r.run_id) FROM runs r JOIN enrichment_run_bindings b USING(run_id) WHERE b.profile_key=$key AND r.kind=$kind", ("$key", key), ("$kind", kind)) as long?;
            DateTimeOffset? attemptAt = attempt is { } attemptRun ? ReadContext(connection, transaction, attemptRun).StartedAtUtc : null;
            using var command = Command(connection, transaction, """
                SELECT r.run_id,f.finalized_at_utc,f.receipt_json,s.valid_count FROM runs r JOIN run_results f USING(run_id)
                JOIN enrichment_run_stats s USING(run_id) JOIN collection_commits c USING(run_id)
                WHERE s.profile_key=$key AND r.kind=$kind AND s.disposition<>1 ORDER BY c.commit_sequence DESC
                """, ("$key", key), ("$kind", kind));
            using var reader = command.ExecuteReader(); long? last = null; EnrichmentCommitReceipt? receipt = null; DateTimeOffset? valid = null, complete = null;
            while (reader.Read())
            {
                var value = Decode<EnrichmentCommitReceipt>(reader.GetString(2)); var finalized = Time(reader, 1);
                if (last is null) { last = reader.GetInt64(0); receipt = value; }
                if (valid is null && reader.GetInt32(3) > 0 && value.Outcome != RunOutcome.Failed) valid = finalized;
                if (complete is null && value.Outcome == RunOutcome.Succeeded) complete = finalized;
            }
            reader.Close();
            var gap = Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM enrichment_gaps WHERE profile_key=$key AND kind=$kind AND is_open=1", ("$key", key), ("$kind", kind))) > 0;
            return new EnrichmentKindHealthSnapshot(kind, LastAttemptAtUtc: attemptAt, LastAttemptRunId: attempt,
                LastValidCommittedAtUtc: valid, LastCompleteCommittedAtUtc: complete, LastFinalRunId: last,
                LastOutcome: receipt?.Outcome, Completeness: receipt?.Completeness, HasQualityGap: gap, StorageRecoveryPending: pending,
                LastError: receipt?.Errors.FirstOrDefault()?.Category);
        }, ct);
    }
}
