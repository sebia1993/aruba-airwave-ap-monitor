using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Enrichment;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    private sealed record StoredNeighbor(NeighborAssessment Assessment, ImmutableArray<Guid> ObservedIds,
        ImmutableArray<Guid> RetainedIds, DateTimeOffset? LastAcceptedSourceAtUtc = null);
    private sealed record StoredPort(PortStateObservation Accepted, PortStateAssessment Assessment);
    private sealed record StoredPowerState(PowerStateObservation Accepted, PowerStateAssessment Assessment);
    private sealed record EnrichmentRecord(long Id, long RunId, int Type, string Key, string Raw, string State, bool Gap);

    public async Task<EnrichmentCommitReceipt> ApplyEnrichmentBatchAsync(CollectionContext context, EnrichmentBatch batch, CancellationToken ct)
    {
        await writeGate.WaitAsync(ct);
        try
        {
            using var connection = Open(); Initialize(connection, ct);
            // A large power batch otherwise repeatedly spills dirty index pages from
            // SQLite's default small cache. This connection-local 16-MiB page-cache
            // target is not a process memory cap; pooling is disabled and durability
            // (WAL, synchronous FULL and normal cache spilling) stays unchanged.
            if (context.Kind == CollectionKind.Power) Execute(connection, null, "PRAGMA cache_size=-16384");
            using var transaction = connection.BeginTransaction();
            var saved = CheckedEnrichmentContext(connection, transaction, context);
            var existing = Optional<EnrichmentCommitReceipt>(connection, transaction, "SELECT receipt_json FROM run_results WHERE run_id=$run", ("$run", saved.RunId));
            if (existing is not null)
            {
                if ((string?)Scalar(connection, transaction, "SELECT payload_hash FROM enrichment_run_stats WHERE run_id=$run", ("$run", saved.RunId)) != EnrichmentHash(batch))
                    throw new InvalidDataException("이미 확정한 부가 수집 회차에 다른 결과를 반영할 수 없습니다.");
                return existing with { AlreadyApplied = true };
            }
            var receipt = FinalizeEnrichment(connection, transaction, saved, batch, RunLifecycle.Finalized, ct);
            ct.ThrowIfCancellationRequested(); transactionObserver?.Invoke(StorageTransactionBoundary.BeforeFinalCommit);
            transaction.Commit(); transactionObserver?.Invoke(StorageTransactionBoundary.AfterFinalCommit); return receipt;
        }
        finally { writeGate.Release(); }
    }
    public async Task<EnrichmentCommitReceipt> InterruptEnrichmentRunAsync(CollectionContext context, CancellationToken ct)
    {
        await writeGate.WaitAsync(ct);
        try
        {
            using var connection = Open(); Initialize(connection, ct); using var transaction = connection.BeginTransaction();
            var saved = CheckedEnrichmentContext(connection, transaction, context);
            var existing = Optional<EnrichmentCommitReceipt>(connection, transaction, "SELECT receipt_json FROM run_results WHERE run_id=$run", ("$run", saved.RunId));
            if (existing is not null) return existing with { AlreadyApplied = true };
            var receipt = FinalizeEnrichment(connection, transaction, saved, InterruptedEnrichment(saved, timeProvider.GetUtcNow()), RunLifecycle.Interrupted, ct);
            ct.ThrowIfCancellationRequested(); transactionObserver?.Invoke(StorageTransactionBoundary.BeforeFinalCommit);
            transaction.Commit(); transactionObserver?.Invoke(StorageTransactionBoundary.AfterFinalCommit); return receipt;
        }
        finally { writeGate.Release(); }
    }
    private static EnrichmentBatch InterruptedEnrichment(CollectionContext context, DateTimeOffset at)
        => new(context.Kind, RunOutcome.Failed, [], [], ObservationCompleteness.Unknown, at,
            [new(CollectionErrorCategory.Interrupted, 1, "부가 수집 실행이 최종 저장 전에 중단되었습니다.")]);

    private EnrichmentCommitReceipt FinalizeEnrichment(SqliteConnection connection, SqliteTransaction transaction,
        CollectionContext context, EnrichmentBatch batch, RunLifecycle lifecycle, CancellationToken ct)
    {
        if (!IsEnrichmentKind(context.Kind) || batch.Kind != context.Kind || !Enum.IsDefined(batch.Outcome) || !Enum.IsDefined(batch.Completeness))
            throw new ArgumentException("부가 수집 종류·결과가 시작 당시 계약과 다릅니다.", nameof(batch));
        var profile = EnrichmentProfile(context.Profile); var profileKey = EnrichmentHash(profile);
        var targets = ReadEnrichmentTargets(connection, transaction, context.RunId);
        var detail = batch.Detail ?? new EnrichmentPayload([], [], []);
        if (detail.Neighbors.IsDefault || detail.Ports.IsDefault || detail.PowerSamples.IsDefault) throw new ArgumentException("부가 관측 목록이 초기화되지 않았습니다.", nameof(batch));
        var targetSet = targets.ToHashSet();
        bool ExactTarget(EnrichmentTargetBinding? target) => target is null || targetSet.Contains(target);
        var bindingValid = (context.Kind == CollectionKind.ConnectionNeighbor ? detail.PowerSamples.Length == 0 && detail.PowerStates.Length == 0 : detail.Neighbors.Length == 0 && detail.Ports.Length == 0) &&
            detail.Neighbors.All(n => n.Scope.Profile == profile && n.Neighbors.All(r => r.RunId == context.RunId && r.Profile == profile && ExactTarget(r.Target))) &&
            detail.Ports.All(p => p.RunId == context.RunId && p.Profile == profile) &&
            detail.PowerSamples.All(p => p.RunId == context.RunId && p.Profile == profile && ExactTarget(p.DirectTarget)) &&
            detail.PowerStates.All(p => p.RunId == context.RunId && p.Profile == profile);
        var contextCurrent = IsCurrentEnrichmentBinding(connection, transaction, context, targets);
        var disposition = !bindingValid ? EnrichmentApplicationDisposition.RejectedBinding : contextCurrent
            ? EnrichmentApplicationDisposition.Current : EnrichmentApplicationDisposition.HistoricalOnly;
        var current = disposition == EnrichmentApplicationDisposition.Current;
        var updateFacts = current && batch.Outcome != RunOutcome.Failed && lifecycle == RunLifecycle.Finalized;
        var frozenConnections = ReadFrozenConnections(connection, transaction, context.RunId);
        var frozenByEndpoint = IndexConnections(frozenConnections);
        var targetBounds = new StoredTargetBoundsCache(connection, transaction);
        var valid = 0; var quality = batch.Outcome != RunOutcome.Succeeded || batch.Completeness != ObservationCompleteness.Complete || batch.Errors.Length != 0 || batch.ExcludedRecords.Length != 0 || !bindingValid;
        var errors = batch.Errors.ToBuilder();
        // Invalid bindings are retained in the receipt/evidence count, never admitted into target-linked facts.
        // Raw source fields with a valid run binding are always retained, even when their value is invalid.
        if (bindingValid)
        {
            foreach (var neighbor in detail.Neighbors)
            {
                ct.ThrowIfCancellationRequested(); var accepted = StoreNeighbor(connection, transaction, context, profileKey, neighbor, targets, updateFacts);
                valid += accepted.Valid ? 1 : 0; quality |= accepted.Gap;
            }
            var connectionFacts = frozenConnections.Concat(detail.Neighbors.SelectMany(n => n.Neighbors)).DistinctBy(c => c.Id).ToArray();
            var connectionsByEndpoint = detail.Ports.IsEmpty ? frozenByEndpoint : IndexConnections(connectionFacts);
            foreach (var port in detail.Ports)
            {
                ct.ThrowIfCancellationRequested(); var accepted = StorePort(connection, transaction, context, profileKey, port, EndpointConnections(connectionsByEndpoint, port.Endpoint), targetBounds, updateFacts);
                valid += accepted.Valid ? 1 : 0; quality |= accepted.Gap;
            }
            using (var statements = new PowerStatements(connection, transaction))
            {
                for (var i = 0; i < detail.PowerSamples.Length; i++)
                {
                    ct.ThrowIfCancellationRequested(); var accepted = StorePower(connection, transaction, context, profileKey, detail.PowerSamples[i], i,
                        detail.PowerSamples[i].Metric == PowerMetric.ApConsumption ? [] : EndpointConnections(frozenByEndpoint, detail.PowerSamples[i].Endpoint), targetBounds, statements, updateFacts);
                    valid += accepted ? 1 : 0; quality |= !accepted;
                }
            }
            foreach (var state in detail.PowerStates)
            {
                ct.ThrowIfCancellationRequested(); var accepted = StorePowerState(connection, transaction, context, profileKey, state, EndpointConnections(frozenByEndpoint, state.Endpoint), targetBounds, updateFacts);
                valid += accepted.Valid ? 1 : 0; quality |= accepted.Gap;
            }
        }
        if (!bindingValid) errors.Add(new(CollectionErrorCategory.InvalidField, 1, "시작 당시 고정한 부가 수집 대상·프로필과 일치하지 않는 결과입니다."));
        if (valid == 0 && lifecycle != RunLifecycle.Interrupted) errors.Add(new(CollectionErrorCategory.Unverified, 1, "유효한 부가 관측이 없습니다. 이전 사실과 AP 수집 결과를 유지합니다."));
        var outcome = lifecycle == RunLifecycle.Interrupted || valid == 0 || batch.Outcome == RunOutcome.Failed ? RunOutcome.Failed : quality ? RunOutcome.Partial : RunOutcome.Succeeded;
        var completeness = outcome == RunOutcome.Failed ? ObservationCompleteness.Unknown : outcome == RunOutcome.Partial ? ObservationCompleteness.Partial : ObservationCompleteness.Complete;
        var finalized = timeProvider.GetUtcNow().ToUniversalTime();
        var receipt = new EnrichmentCommitReceipt(location.DatasetId, context.Kind, context.RunId, false, bindingValid, outcome, completeness, errors.ToImmutable()) { Disposition = disposition };
        Execute(connection, transaction, "INSERT INTO enrichment_run_stats VALUES($run,$profile,$hash,$disposition,$neighbors,$ports,$power,$states,$valid,$gap)",
            ("$run", context.RunId), ("$profile", profileKey), ("$hash", EnrichmentHash(batch)), ("$disposition", disposition),
            ("$neighbors", detail.Neighbors.Length), ("$ports", detail.Ports.Length), ("$power", detail.PowerSamples.Length), ("$states", detail.PowerStates.Length), ("$valid", valid), ("$gap", outcome == RunOutcome.Succeeded ? 0 : 1));
        Execute(connection, transaction, """
            INSERT INTO run_results(run_id,dataset_id,kind,finalized_at_utc,outcome,receipt_json,completeness,errors_json,source_evidence_json)
            VALUES($run,$dataset,$kind,$at,$outcome,$receipt,$complete,$errors,'null')
            """, ("$run", context.RunId), ("$dataset", location.DatasetId), ("$kind", context.Kind), ("$at", finalized), ("$outcome", outcome),
            ("$receipt", Encode(receipt)), ("$complete", completeness), ("$errors", Encode(receipt.Errors)));
        if (Execute(connection, transaction, "UPDATE runs SET lifecycle=$life WHERE run_id=$run AND lifecycle=0", ("$life", lifecycle), ("$run", context.RunId)) != 1)
            throw new InvalidDataException("이미 확정한 부가 수집 회차입니다.");
        Execute(connection, transaction, "INSERT INTO collection_commits(run_id) VALUES($run)", ("$run", context.RunId));
        if (contextCurrent) UpdateEnrichmentGap(connection, transaction, context, profileKey, outcome, batch.CollectedAtUtc, errors.FirstOrDefault()?.Category ?? CollectionErrorCategory.Unverified);
        var elapsed = finalized - context.StartedAtUtc;
        Execute(connection, transaction, "INSERT INTO run_execution_details VALUES($run,$at,$elapsed,$received,$accepted,$excluded,$errors)",
            ("$run", context.RunId), ("$at", finalized), ("$elapsed", elapsed < TimeSpan.Zero ? null : (long?)elapsed.TotalMilliseconds),
            ("$received", detail.Neighbors.Length + detail.Ports.Length + detail.PowerSamples.Length + detail.PowerStates.Length), ("$accepted", valid),
            ("$excluded", batch.ExcludedRecords.Length), ("$errors", Encode(receipt.Errors)));
        return receipt;
    }
    private bool IsCurrentEnrichmentBinding(SqliteConnection connection, SqliteTransaction transaction, CollectionContext context, ImmutableArray<EnrichmentTargetBinding> targets)
    {
        if (Convert.ToInt64(Scalar(connection, transaction, "SELECT restore_epoch FROM enrichment_run_bindings WHERE run_id=$run", ("$run", context.RunId))) != CurrentRestoreEpoch(connection, transaction)) return false;
        var latest = Scalar(connection, transaction, "SELECT MAX(run_id) FROM runs WHERE dataset_id=$dataset", ("$dataset", location.DatasetId));
        if (latest is not null && EnrichmentProfile(ReadContext(connection, transaction, Convert.ToInt64(latest)).Profile) != EnrichmentProfile(context.Profile)) return false;
        foreach (var target in targets)
        {
            using var command = Command(connection, transaction, "SELECT generation_id,interval_id,tracking_json FROM ap_current WHERE ap_id=$ap", ("$ap", target.ApId));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != target.DeviceGenerationId.ToString("D") || reader.GetString(1) != target.MonitoringIntervalId.ToString("D") ||
                Decode<ApTrackingSnapshot>(reader.GetString(2)).MonitoringStatus != MonitoringStatus.Active) return false;
        }
        return true;
    }
    private static List<ConnectionObservation> ReadFrozenConnections(SqliteConnection connection, SqliteTransaction transaction, long run)
    {
        var ids = Decode<long[]>((string)Scalar(connection, transaction, "SELECT connection_record_ids_json FROM enrichment_run_bindings WHERE run_id=$run", ("$run", run))!);
        return ids.SelectMany(id => ReadConnections(connection, transaction, Decode<StoredNeighbor>(ReadEnrichmentRecord(connection, transaction, id).State).RetainedIds)).DistinctBy(c => c.Id).ToList();
    }
    private static EnrichmentRecord ReadEnrichmentRecord(SqliteConnection connection, SqliteTransaction transaction, long id)
    {
        using var command = Command(connection, transaction, "SELECT record_id,run_id,record_type,entity_key,raw_json,state_json,has_gap FROM enrichment_records WHERE record_id=$id", ("$id", id));
        using var reader = command.ExecuteReader(); if (!reader.Read()) throw new InvalidDataException("고정한 부가 관측이 없습니다.");
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6) != 0);
    }
    private static ImmutableArray<ConnectionObservation> ReadConnections(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<Guid> ids)
        => ids.Select(id => Optional<ConnectionObservation>(connection, transaction, "SELECT observation_json FROM enrichment_connections WHERE connection_id=$id", ("$id", id))
            ?? throw new InvalidDataException("고정한 연결 근거가 없습니다.")).ToImmutableArray();
    private static EnrichmentRecord? CurrentEnrichmentRecord(SqliteConnection connection, SqliteTransaction transaction, string profile, int type, string key)
        => Scalar(connection, transaction, "SELECT record_id FROM enrichment_current WHERE profile_key=$profile AND record_type=$type AND entity_key=$key", ("$profile", profile), ("$type", type), ("$key", key)) is { } id
            ? ReadEnrichmentRecord(connection, transaction, Convert.ToInt64(id)) : null;
    private static long InsertEnrichmentRecord(SqliteConnection connection, SqliteTransaction transaction, CollectionContext context, string profile,
        int type, string key, Guid? sourceId, DateTimeOffset at, object raw, object state, bool gap, bool current, IEnumerable<EnrichmentTargetBinding> targets)
    {
        Execute(connection, transaction, "INSERT INTO enrichment_records(run_id,record_type,profile_key,entity_key,source_id,observed_at_utc,raw_json,state_json,has_gap) VALUES($run,$type,$profile,$key,$source,$at,$raw,$state,$gap)",
            ("$run", context.RunId), ("$type", type), ("$profile", profile), ("$key", key), ("$source", sourceId), ("$at", at), ("$raw", Encode(raw)), ("$state", Encode(state)), ("$gap", gap ? 1 : 0));
        var id = Convert.ToInt64(Scalar(connection, transaction, "SELECT last_insert_rowid()")); var ordinal = 0;
        foreach (var target in targets.DistinctBy(t => (t.ApId, t.DeviceGenerationId, t.MonitoringIntervalId)))
            Execute(connection, transaction, "INSERT INTO enrichment_record_targets VALUES($id,$ordinal,$ap,$gen,$interval,$point)", ("$id", id), ("$ordinal", ordinal++), ("$ap", target.ApId), ("$gen", target.DeviceGenerationId), ("$interval", target.MonitoringIntervalId), ("$point", target.Placement?.InstallationPointId));
        if (current)
        {
            Execute(connection, transaction, "INSERT INTO enrichment_current VALUES($profile,$type,$key,$id) ON CONFLICT(profile_key,record_type,entity_key) DO UPDATE SET record_id=excluded.record_id", ("$profile", profile), ("$type", type), ("$key", key), ("$id", id));
            Execute(connection, transaction, "INSERT INTO enrichment_accepted_records VALUES($id,$profile,$type,$key,$run)", ("$id", id), ("$profile", profile), ("$type", type), ("$key", key), ("$run", context.RunId));
        }
        return id;
    }
}
