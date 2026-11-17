using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Enrichment;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    private sealed record GapReference(Guid Id, long Version);
    private sealed record EnrichmentReferences(ImmutableArray<long> Records, ImmutableArray<long> Power,
        ImmutableArray<GapReference> Gaps);
    private sealed record CapturedEnrichment(long ReferenceId, long CommitSequence, DateTimeOffset At, EnrichmentSnapshot Snapshot);

    private CapturedEnrichment CaptureEnrichment(SqliteConnection connection, SqliteTransaction transaction,
        CollectionContext context, CommittedDeviceView view, DateTimeOffset at)
    {
        var sequence = Convert.ToInt64(Scalar(connection, transaction, "SELECT COALESCE(MAX(commit_sequence),0) FROM collection_commits"));
        var profile = EnrichmentProfileKey(context.Profile); var records = ImmutableArray.CreateBuilder<long>();
        using (var command = Command(connection, transaction, """
            WITH target_keys AS (
                SELECT DISTINCT h.record_type,h.entity_key FROM enrichment_record_targets t JOIN enrichment_accepted_records h USING(record_id)
                WHERE t.ap_id=$ap AND t.generation_id=$gen AND t.interval_id=$interval AND h.profile_key=$profile
            ), eligible AS (
                SELECT h.record_id,ROW_NUMBER() OVER(PARTITION BY h.record_type,h.entity_key ORDER BY c.commit_sequence DESC,h.record_id DESC) AS position
                FROM target_keys k JOIN enrichment_accepted_records h ON h.profile_key=$profile AND h.record_type=k.record_type AND h.entity_key=k.entity_key
                JOIN collection_commits c USING(run_id) WHERE h.run_id<=$run AND c.commit_sequence<=$commit
            )
            SELECT e.record_id FROM eligible e WHERE e.position=1 AND EXISTS
                (SELECT 1 FROM enrichment_record_targets t WHERE t.record_id=e.record_id AND t.ap_id=$ap AND t.generation_id=$gen AND t.interval_id=$interval)
            ORDER BY e.record_id
            """, ("$profile", profile), ("$ap", view.ApId), ("$gen", view.DeviceGenerationId), ("$interval", view.MonitoringIntervalId), ("$run", context.RunId), ("$commit", sequence)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) records.Add(reader.GetInt64(0));
        var power = ImmutableArray.CreateBuilder<long>();
        using (var command = Command(connection, transaction, """
            WITH target_keys AS (
                SELECT DISTINCT entity_key FROM enrichment_accepted_power WHERE profile_key=$profile AND ap_id=$ap AND generation_id=$gen AND interval_id=$interval
            ), eligible AS (
                SELECT h.*,ROW_NUMBER() OVER(PARTITION BY h.entity_key ORDER BY c.commit_sequence DESC,h.accepted_sequence DESC) AS position
                FROM target_keys k JOIN enrichment_accepted_power h ON h.profile_key=$profile AND h.entity_key=k.entity_key
                JOIN collection_commits c USING(run_id) LEFT JOIN power_samples p USING(sample_sequence)
                WHERE h.run_id<=$run AND c.commit_sequence<=$commit AND (p.run_id IS NULL OR p.run_id<=$run)
            )
            SELECT sample_sequence FROM eligible WHERE position=1 AND ap_id=$ap AND generation_id=$gen AND interval_id=$interval ORDER BY sample_sequence
            """, ("$profile", profile), ("$ap", view.ApId), ("$gen", view.DeviceGenerationId), ("$interval", view.MonitoringIntervalId), ("$run", context.RunId), ("$commit", sequence)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) power.Add(reader.GetInt64(0));
        var gaps = ImmutableArray.CreateBuilder<GapReference>();
        using (var command = Command(connection, transaction, """
            WITH eligible AS (
                SELECT v.gap_id,v.version,v.view_json,ROW_NUMBER() OVER(PARTITION BY v.gap_id ORDER BY v.version DESC) AS position
                FROM enrichment_gap_versions v JOIN enrichment_gaps g USING(gap_id)
                JOIN collection_commits c ON c.run_id=COALESCE(json_extract(v.view_json,'$.ResumedRunId'),g.first_run_id)
                WHERE g.profile_key=$profile AND c.run_id<=$run AND c.commit_sequence<=$commit
            )
            SELECT gap_id,version FROM eligible WHERE position=1 AND json_extract(view_json,'$.ResumedRunId') IS NULL ORDER BY gap_id
            """, ("$profile", profile), ("$run", context.RunId), ("$commit", sequence)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) gaps.Add(new(Guid.Parse(reader.GetString(0)), reader.GetInt64(1)));
        var refs = new EnrichmentReferences(records.ToImmutable(), power.ToImmutable(), gaps.ToImmutable());
        var hash = EnrichmentHash(refs);
        Execute(connection, transaction, "INSERT OR IGNORE INTO enrichment_snapshot_refs(ap_id,generation_id,interval_id,profile_key,refs_hash,references_json) VALUES($ap,$gen,$interval,$profile,$hash,$refs)",
            ("$ap", view.ApId), ("$gen", view.DeviceGenerationId), ("$interval", view.MonitoringIntervalId), ("$profile", profile), ("$hash", hash), ("$refs", Encode(refs)));
        var id = Convert.ToInt64(Scalar(connection, transaction, "SELECT ref_id FROM enrichment_snapshot_refs WHERE ap_id=$ap AND generation_id=$gen AND interval_id=$interval AND profile_key=$profile AND refs_hash=$hash",
            ("$ap", view.ApId), ("$gen", view.DeviceGenerationId), ("$interval", view.MonitoringIntervalId), ("$profile", profile), ("$hash", hash)));
        return new(id, sequence, at, MaterializeEnrichment(connection, transaction, refs, context.RunId, sequence, at, true, false, default));
    }
    private static EnrichmentSnapshot ReadReferencedEnrichment(SqliteConnection connection, SqliteTransaction transaction,
        long referenceId, long runId, long commitSequence, DateTimeOffset at, CancellationToken ct)
    {
        var refs = Optional<EnrichmentReferences>(connection, transaction, "SELECT references_json FROM enrichment_snapshot_refs WHERE ref_id=$id", ("$id", referenceId))
            ?? throw new InvalidDataException("고정한 부가 관측 참조가 없습니다.");
        return MaterializeEnrichment(connection, transaction, refs, runId, commitSequence, at, true, false, ct);
    }
    private static EnrichmentSnapshot MaterializeEnrichment(SqliteConnection connection, SqliteTransaction transaction,
        EnrichmentReferences refs, long runId, long sequence, DateTimeOffset at, bool accepted, bool expired, CancellationToken ct)
    {
        var neighbors = ImmutableArray.CreateBuilder<ConnectionEvidenceView>(); var ports = ImmutableArray.CreateBuilder<PortEvidenceView>();
        var states = ImmutableArray.CreateBuilder<PowerStateEvidenceView>(); var power = ImmutableArray.CreateBuilder<PowerEvidenceView>();
        foreach (var id in refs.Records)
        {
            ct.ThrowIfCancellationRequested(); var record = ReadEnrichmentRecord(connection, transaction, id);
            if (record.Type == 0)
            {
                var state = Decode<StoredNeighbor>(record.State);
                var observation = Decode<NeighborObservation>(record.Raw) with { Neighbors = ReadConnections(connection, transaction, state.ObservedIds) };
                var evaluated = ConnectionEvidencePolicy.Evaluate(observation, accepted ? at : observation.Timing.ReadAtUtc);
                // The saved fail-closed decision (e.g. a source clock regression) is not upgraded by a later read.
                if (state.Assessment.Presence == NeighborPresence.Unknown) evaluated = state.Assessment with { Observations = observation.Neighbors, HasCurrentLldpObservation = false, MayClosePreviousConnection = false };
                neighbors.Add(new(record.RunId, observation, evaluated, ReadConnections(connection, transaction, state.RetainedIds), record.Gap));
            }
            else if (record.Type == 1)
            {
                var state = Decode<StoredPort>(record.State); var raw = Decode<PortStateObservation>(record.Raw);
                ports.Add(new(record.RunId, accepted ? state.Accepted : raw, accepted ? state.Assessment : PortStatePolicy.Evaluate(raw), record.Gap));
            }
            else
            {
                var state = Decode<StoredPowerState>(record.State); var raw = Decode<PowerStateObservation>(record.Raw);
                states.Add(new(record.RunId, accepted ? state.Accepted : raw, accepted ? state.Assessment : PowerStatePolicy.Evaluate(raw), record.Gap));
            }
        }
        foreach (var id in refs.Power)
        {
            ct.ThrowIfCancellationRequested(); var value = ReadPowerEvidence(connection, transaction, id);
            if (value is null) expired = true; else power.Add(value);
        }
        var gaps = refs.Gaps.Select(g => Optional<EnrichmentGapView>(connection, transaction, "SELECT view_json FROM enrichment_gap_versions WHERE gap_id=$id AND version=$version", ("$id", g.Id), ("$version", g.Version))
            ?? throw new InvalidDataException("고정한 부가 공백 버전이 없습니다.")).ToImmutableArray();
        return new(runId, sequence, at, neighbors.ToImmutable(), ports.ToImmutable(), power.ToImmutable(), states.ToImmutable(), gaps, expired);
    }
    private EnrichmentSnapshot ReadEnrichmentRunEvidence(SqliteConnection connection, SqliteTransaction transaction, long runId, CancellationToken ct)
    {
        var context = ReadContext(connection, transaction, runId); var records = ImmutableArray.CreateBuilder<long>(); var power = ImmutableArray.CreateBuilder<long>();
        using (var command = Command(connection, transaction, "SELECT record_id FROM enrichment_records WHERE run_id=$run ORDER BY record_id", ("$run", runId)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) records.Add(reader.GetInt64(0));
        var expired = false;
        using (var command = Command(connection, transaction, "SELECT sample_sequence FROM enrichment_power_reads WHERE run_id=$run ORDER BY ordinal", ("$run", runId)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) { if (reader.IsDBNull(0)) expired = true; else power.Add(reader.GetInt64(0)); }
        var sequence = Convert.ToInt64(Scalar(connection, transaction, "SELECT COALESCE((SELECT commit_sequence FROM collection_commits WHERE run_id=$run),0)", ("$run", runId)));
        var gaps = ImmutableArray.CreateBuilder<GapReference>();
        // Select versions by their actual run commit visibility, not by wall time or later mutable current_version.
        using (var command = Command(connection, transaction, """
            SELECT v.gap_id,MAX(v.version) FROM enrichment_gap_versions v JOIN enrichment_gaps g USING(gap_id)
            JOIN collection_commits c ON c.run_id=COALESCE(json_extract(v.view_json,'$.ResumedRunId'),g.first_run_id)
            WHERE g.profile_key=$profile AND c.run_id<=$run AND c.commit_sequence<=$sequence GROUP BY v.gap_id ORDER BY v.gap_id
            """, ("$profile", EnrichmentProfileKey(context.Profile)), ("$sequence", sequence), ("$run", runId)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) gaps.Add(new(Guid.Parse(reader.GetString(0)), reader.GetInt64(1)));
        var captured = (string?)Scalar(connection, transaction, "SELECT finalized_at_utc FROM run_results WHERE run_id=$run", ("$run", runId));
        return MaterializeEnrichment(connection, transaction, new(records.ToImmutable(), power.ToImmutable(), gaps.ToImmutable()), runId,
            sequence, captured is null ? context.StartedAtUtc : DateTimeOffset.Parse(captured), false, expired, ct);
    }
}
