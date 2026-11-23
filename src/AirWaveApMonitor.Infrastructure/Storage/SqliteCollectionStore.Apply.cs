using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Monitoring;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    private sealed record StoredAp(ApTrackingSnapshot Tracking, CommittedDeviceView View, long MetadataVersionId);
    private sealed record MetadataMeasurements(TimeSpan? Uptime, DateTimeOffset? RebootedAtUtc,
        ImmutableDictionary<string, string>? RawSourceValues);
    private sealed record RowFacts(ApState? CurrentState, ApState? LastValidState, long? LastValidRunId,
        DateTimeOffset? LastValidObservedAtUtc, DateTimeOffset? FirstDownAtUtc, Guid? OpenIncidentId,
        ObservationClassification Classification, ImmutableDictionary<string, FieldEvidence> Fields,
        MissingObservationReason? UnknownReason, bool TimeNeedsReview, DateTimeOffset? SourceAtUtc,
        DateTimeOffset? LastContactedAtUtc, MetadataMeasurements Measurements)
    {
        public long? EnrichmentReferenceId { get; init; }
        public long EnrichmentCommitSequence { get; init; }
        public DateTimeOffset? EnrichmentCapturedAtUtc { get; init; }
    }

    private CommitReceipt FinalizeAp(SqliteConnection connection, SqliteTransaction transaction,
        CollectionContext context, ApCollectionBatch batch, RunLifecycle lifecycle, CancellationToken ct)
    {
        if (context.Kind != CollectionKind.ApStatus || context.RunId <= 0)
        {
            throw new InvalidDataException("잘못된 AP 회차입니다.");
        }
        var frozen = context.Profile.ExpectedTargets.ToDictionary(target => target.SourceApId, StringComparer.Ordinal);
        var sourceBatch = batch;
        var parsedObservationCount = batch.Observations.Length;
        int? scopeExcludedApCount = null, scopeExcludedRecordCount = null;
        if (context.Profile.MonitoringScope is { Mode: MonitoringScopeMode.SelectedApIds } selection)
        {
            var selectedIds = selection.SourceApIds.ToHashSet(StringComparer.Ordinal);
            if (frozen.Keys.Any(id => !selectedIds.Contains(id)))
                throw new InvalidDataException("고정된 기대 대상과 명시 감시 범위가 일치하지 않습니다.");
            batch = batch with
            {
                Observations = batch.Observations.Where(observation => selectedIds.Contains(observation.SourceApId)).ToImmutableArray(),
                ExcludedRecords = batch.ExcludedRecords.Where(record => record.SourceApId is null ||
                    record.Reason == CollectionErrorCategory.InvalidField || selectedIds.Contains(record.SourceApId)).ToImmutableArray()
            };
            scopeExcludedApCount = sourceBatch.Observations.Length - batch.Observations.Length;
            scopeExcludedRecordCount = sourceBatch.ExcludedRecords.Length - batch.ExcludedRecords.Length;
            // Only complete, attributable record diagnostics may be narrowed to the selected inventory.
            // Unknown documents, failures, and unexplained legacy Partial results retain their quality.
            if (batch.RecordErrorAttributionComplete &&
                (batch.Outcome == RunOutcome.Succeeded && batch.Completeness == ObservationCompleteness.Complete ||
                 batch.Outcome == RunOutcome.Partial && batch.Completeness == ObservationCompleteness.Partial && batch.Errors.Length > 0))
            {
                var scopedErrors = batch.Errors.Where(error => error.SourceApId is null || selectedIds.Contains(error.SourceApId)).ToImmutableArray();
                batch = batch with
                {
                    Errors = scopedErrors,
                    Outcome = scopedErrors.IsEmpty ? RunOutcome.Succeeded : RunOutcome.Partial,
                    Completeness = batch.Observations.IsEmpty ? ObservationCompleteness.Unknown
                        : scopedErrors.IsEmpty ? ObservationCompleteness.Complete : ObservationCompleteness.Partial
                };
            }
        }
        var saved = LoadCurrent(connection, transaction, context);
        foreach (var target in frozen.Values)
        {
            saved.TryGetValue(target.SourceApId, out var current);
            if ((target.DeviceGenerationId is not null && target.DeviceGenerationId != current?.Tracking.Identity.DeviceGenerationId) ||
                (target.MonitoringIntervalId is not null && target.MonitoringIntervalId != current?.Tracking.MonitoringIntervalId))
            {
                throw new InvalidDataException("실행 시작 당시 장비 세대·감시 구간과 저장된 현재값이 다릅니다.");
            }
        }
        var bindings = saved.ToDictionary(pair => pair.Key, pair => ReadFrozenBinding(connection, transaction, context.RunId, pair.Value.View.ApId), StringComparer.Ordinal);
        var policyExcluded = bindings.Where(pair => pair.Value?.Policy?.Directive is MonitoringDirective.TemporarilyExclude or MonitoringDirective.EndTracking)
            .ToDictionary(pair => pair.Key, pair => pair.Value!.Policy!, StringComparer.Ordinal);
        batch = batch with { Observations = batch.Observations.Select(observation => PreserveFreshIdentity(saved.GetValueOrDefault(observation.SourceApId), observation)).ToImmutableArray() };
        var conflicts = new IdentityResolver().FindBatchConflicts(batch.Observations);
        var duplicateIds = conflicts.DuplicateApIds.Union(batch.ExcludedRecords
            .Where(row => row.Reason == CollectionErrorCategory.DuplicateIdentity && row.SourceApId is not null)
            .Select(row => row.SourceApId!)).ToHashSet(StringComparer.Ordinal);
        var accepted = batch.Observations.Where(observation => !string.IsNullOrWhiteSpace(observation.SourceApId) &&
                !duplicateIds.Contains(observation.SourceApId))
            .ToDictionary(observation => observation.SourceApId, StringComparer.Ordinal);
        var validCount = accepted.Values.Count(ValidState);
        var appliedValidCount = accepted.Where(pair => !policyExcluded.ContainsKey(pair.Key)).Count(pair => ValidState(pair.Value));
        var failed = lifecycle == RunLifecycle.Interrupted || batch.Outcome == RunOutcome.Failed || appliedValidCount == 0;
        var targets = frozen.Keys.ToHashSet(StringComparer.Ordinal);
        if (!failed)
        {
            targets.UnionWith(accepted.Keys);
        }
        targets.ExceptWith(policyExcluded.Keys);
        var errors = batch.Errors.ToBuilder();
        // A network/TLS failure has no AP payload. Only a received but unusable AP list
        // warrants the zero-valid-state warning; preserve the original failure reason.
        if (appliedValidCount == 0 && lifecycle != RunLifecycle.Interrupted && batch.Outcome != RunOutcome.Failed)
        {
            errors.Add(new(CollectionErrorCategory.Unverified, 1, "유효한 AP 상태가 0대입니다. 대상·권한·응답 확인이 필요합니다."));
        }
        if (conflicts.SerialCollisions.Count > 0)
        {
            errors.Add(new(CollectionErrorCategory.InvalidField, conflicts.SerialCollisions.Count,
                "서로 다른 AP ID에 같은 시리얼이 있습니다. 장비를 자동 병합하지 않았습니다."));
        }
        if (duplicateIds.Count > 0)
        {
            errors.Add(new(CollectionErrorCategory.DuplicateIdentity, duplicateIds.Count, "중복 AP ID 전체의 자동 변경을 제외했습니다."));
        }
        var missing = targets.Count(id => !accepted.ContainsKey(id));
        if (missing > 0 && !failed)
        {
            errors.Add(new(CollectionErrorCategory.MissingRecord, missing, "시작 당시 기대 대상 중 관측되지 않은 AP가 있습니다."));
        }
        var history = new HistoryCapture(context.RunId, batch.CollectedAtUtc,
            Convert.ToInt64(Scalar(connection, transaction, "SELECT COALESCE(MAX(commit_sequence),0) FROM collection_commits")));
        var decisions = new List<ApTransitionDecision>();
        foreach (var sourceId in targets.Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            saved.TryGetValue(sourceId, out var previous);
            var ids = new TransitionIdentifiers(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            ApTransitionDecision decision;
            if (!failed && accepted.TryGetValue(sourceId, out var observation))
            {
                decision = transitions.Evaluate(previous?.Tracking, observation, context, ids);
            }
            else
            {
                var reason = context.Trigger == RunTrigger.DatabaseRestore ? MissingObservationReason.DatabaseRestored
                    : lifecycle == RunLifecycle.Interrupted ? MissingObservationReason.Interrupted
                    : failed ? MissingObservationReason.CollectionFailed
                    : duplicateIds.Contains(sourceId) ? MissingObservationReason.DuplicateIdentity
                    : MissingObservationReason.MissingRecord;
                decision = transitions.EvaluateUnobserved(previous?.Tracking,
                    new(sourceId, reason, batch.CollectedAtUtc), context, ids);
            }
            PersistDecision(connection, transaction, context, decision, previous, true, history);
            decisions.Add(decision);
            Execute(connection, transaction, "INSERT OR IGNORE INTO expected_targets VALUES($dataset,$scope,$source)",
                ("$dataset", location.DatasetId), ("$scope", context.Profile.ScopeVersion), ("$source", sourceId));
        }
        foreach (var (sourceId, policy) in policyExcluded)
        {
            ct.ThrowIfCancellationRequested(); var previous = saved[sourceId];
            // Explicitly ending a target cannot later become a paused old interval. Only Observe reenters.
            var directive = previous.Tracking.MonitoringStatus == MonitoringStatus.Ended ? MonitoringDirective.EndTracking : policy.Directive;
            var decision = transitions.ChangeMonitoring(previous.Tracking, directive, policy.Reason, batch.CollectedAtUtc, context);
            PersistDecision(connection, transaction, context, decision, previous, false);
        }
        // Scope membership is a configuration fact. Pausing retains the physical generation and open incident.
        var excluded = saved.Where(pair => !targets.Contains(pair.Key) && !policyExcluded.ContainsKey(pair.Key)).ToArray();
        foreach (var (sourceId, previous) in excluded)
        {
            ct.ThrowIfCancellationRequested();
            if (previous.Tracking.MonitoringStatus != MonitoringStatus.Active)
            {
                continue;
            }
            var decision = transitions.ChangeMonitoring(previous.Tracking, MonitoringDirective.TemporarilyExclude,
                "현재 수집 범위에서 제외됨", batch.CollectedAtUtc, context);
            PersistDecision(connection, transaction, context, decision, previous, false);
        }
        PersistHistoryFrame(connection, transaction, context, history);
        var unknown = decisions.Any(decision => decision.CurrentState is null);
        var outcome = failed ? RunOutcome.Failed
            : unknown || batch.Outcome == RunOutcome.Partial || errors.Count > 0 || batch.Completeness != ObservationCompleteness.Complete
                ? RunOutcome.Partial : RunOutcome.Succeeded;
        var baseline = context.Profile.BaselineStatus;
        if (context.Profile.BaselineConfirmedSourceApIds is { } confirmedIds && baseline == BaselineStatus.Confirmed &&
            !targets.SetEquals(confirmedIds))
        {
            baseline = BaselineStatus.Unverified;
            Execute(connection, transaction, "UPDATE scopes SET baseline_status=0 WHERE dataset_id=$dataset AND scope_version=$scope",
                ("$dataset", location.DatasetId), ("$scope", context.Profile.ScopeVersion));
        }
        var summary = classifier.Summarize(decisions, targets, outcome,
            new(baseline, targets.Count > 0 || frozen.Count > 0, excluded.Length + policyExcluded.Count));
        var receipt = new CommitReceipt(context.RunId, false, outcome, summary);
        var finalized = timeProvider.GetUtcNow().ToUniversalTime();
        Execute(connection, transaction, """
            INSERT INTO run_results(run_id,dataset_id,kind,finalized_at_utc,outcome,receipt_json,completeness,errors_json,source_evidence_json)
            VALUES($run,$dataset,$kind,$at,$outcome,$receipt,$complete,$errors,$evidence)
            """, ("$run", context.RunId), ("$dataset", location.DatasetId), ("$kind", context.Kind), ("$at", finalized),
            ("$outcome", outcome), ("$receipt", Encode(receipt)),
            ("$complete", failed ? ObservationCompleteness.Unknown : outcome == RunOutcome.Partial ? ObservationCompleteness.Partial : ObservationCompleteness.Complete),
            ("$errors", Encode(errors.ToImmutable())),
            ("$evidence", Encode(new ApRunSourceEvidence(parsedObservationCount, validCount, failed ? 0 : appliedValidCount,
                batch.ExcludedRecords, conflicts with { DuplicateApIds = duplicateIds.ToImmutableHashSet(StringComparer.Ordinal) })
            {
                SourceOutcome = scopeExcludedApCount is null ? null : sourceBatch.Outcome,
                SourceCompleteness = scopeExcludedApCount is null ? null : sourceBatch.Completeness,
                ScopeExcludedApCount = scopeExcludedApCount,
                ScopeExcludedRecordCount = scopeExcludedRecordCount,
                OperationsThroughEventId = LatestOperationEventId(connection, transaction)
            })));
        if (Execute(connection, transaction, "UPDATE runs SET lifecycle=$lifecycle WHERE run_id=$run AND lifecycle=0",
            ("$lifecycle", lifecycle), ("$run", context.RunId)) != 1)
        {
            throw new InvalidDataException("이미 확정된 회차를 다시 변경할 수 없습니다.");
        }
        if (targets.Count > 0)
        {
            Execute(connection, transaction, "UPDATE scopes SET target_universe_known=1 WHERE dataset_id=$dataset AND scope_version=$scope",
                ("$dataset", location.DatasetId), ("$scope", context.Profile.ScopeVersion));
        }
        if (!failed)
        {
            Execute(connection, transaction, """
                INSERT INTO success_times VALUES($dataset,$scope,0,$run,$at,$completeRun,$completeAt)
                ON CONFLICT(dataset_id,scope_version,kind) DO UPDATE SET
                    last_valid_run_id=excluded.last_valid_run_id,last_valid_committed_at_utc=excluded.last_valid_committed_at_utc,
                    last_complete_run_id=COALESCE(excluded.last_complete_run_id,success_times.last_complete_run_id),
                    last_complete_committed_at_utc=COALESCE(excluded.last_complete_committed_at_utc,success_times.last_complete_committed_at_utc)
                """, ("$dataset", location.DatasetId), ("$scope", context.Profile.ScopeVersion), ("$run", context.RunId),
                ("$at", finalized), ("$completeRun", outcome == RunOutcome.Succeeded ? context.RunId : null),
                ("$completeAt", outcome == RunOutcome.Succeeded ? finalized : null));
        }
        Execute(connection, transaction, "INSERT INTO collection_commits(run_id) VALUES($run)", ("$run", context.RunId));
        PersistCheckpoint(connection, transaction, context, batch, lifecycle);
        PersistExecutionDetails(connection, transaction, context, sourceBatch, finalized, failed ? 0 : appliedValidCount);
        return receipt;
    }

    private static bool ValidState(ApObservation observation)
        => observation.State is ApState.Up or ApState.Down &&
            observation.Fields.TryGetValue(MonitoringFieldNames.State, out var evidence) &&
            evidence.Availability == FieldAvailability.Provided && evidence.Validity == FieldValidity.Valid;

    private static Dictionary<string, StoredAp> LoadCurrent(SqliteConnection connection, SqliteTransaction transaction, CollectionContext context)
    {
        using var command = Command(connection, transaction, """
            SELECT a.ap_id,a.source_ap_id,c.generation_id,c.interval_id,c.metadata_version_id,c.last_processed_run_id,
                c.tracking_json,c.fields_json,c.source_at_utc,c.last_contacted_at_utc,m.metadata_json,c.measurements_json
            FROM aps a JOIN ap_current c ON c.ap_id=a.ap_id JOIN metadata_versions m ON m.metadata_version_id=c.metadata_version_id
            WHERE a.dataset_id=$dataset AND a.instance_id=$instance
            """, ("$dataset", context.Profile.DatasetId), ("$instance", context.Profile.InstanceId));
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, StoredAp>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var tracking = Decode<ApTrackingSnapshot>(reader.GetString(6));
            var sourceId = reader.GetString(1);
            var generation = Guid.Parse(reader.GetString(2));
            var interval = Guid.Parse(reader.GetString(3));
            ValidateTracking(tracking, context, sourceId, generation, interval, reader.GetInt64(5));
            var metadata = RestoreMeasurements(Decode<DeviceMetadata>(reader.GetString(10)),
                Decode<MetadataMeasurements>(reader.GetString(11)));
            if (metadata.Identity != tracking.Identity.Identity)
            {
                throw new InvalidDataException("저장된 장비 식별 값이 현재 상태와 일치하지 않습니다.");
            }
            var view = new CommittedDeviceView(Guid.Parse(reader.GetString(0)), sourceId, generation, interval,
                metadata, Decode<ImmutableDictionary<string, FieldEvidence>>(reader.GetString(7)), Time(reader, 8), Time(reader, 9), []);
            result.Add(sourceId, new(tracking, view, reader.GetInt64(4)));
        }
        reader.Close();
        foreach (var saved in result.Values)
        {
            if (saved.Tracking.OpenIncident is { } incident)
            {
                var persisted = Scalar(connection, transaction,
                    "SELECT tracking_json FROM incidents WHERE incident_id=$id AND generation_id=$generation AND interval_id=$interval AND is_open=1",
                    ("$id", incident.IncidentId), ("$generation", saved.View.DeviceGenerationId), ("$interval", saved.View.MonitoringIntervalId)) as string;
                if (persisted is null || Decode<IncidentTrackingSnapshot>(persisted) != incident)
                {
                    throw new InvalidDataException("현재 상태와 열린 사건의 저장 참조가 일치하지 않습니다.");
                }
            }
        }
        return result;
    }

    private static DeviceMetadata RestoreMeasurements(DeviceMetadata metadata, MetadataMeasurements measurements)
        => metadata with
        {
            Device = metadata.Device with { Uptime = measurements.Uptime, RebootedAtUtc = measurements.RebootedAtUtc },
            RawSourceValues = measurements.RawSourceValues
        };

    private static void ValidateTracking(ApTrackingSnapshot saved, CollectionContext context, string sourceId,
        Guid generation, Guid interval, long lastProcessed)
    {
        if (saved.Identity.DatasetId != context.Profile.DatasetId || saved.Identity.InstanceId != context.Profile.InstanceId ||
            saved.Identity.SourceApId != sourceId || saved.Identity.DeviceGenerationId != generation || saved.MonitoringIntervalId != interval ||
            generation == Guid.Empty || interval == Guid.Empty || saved.LastProcessedRunId != lastProcessed || lastProcessed <= 0 ||
            lastProcessed >= context.RunId || !Enum.IsDefined(saved.MonitoringStatus) ||
            (saved.LastValidState is not null && saved.LastValidState is not (ApState.Up or ApState.Down)) ||
            (saved.LastValidState is null) != (saved.LastValidRunId is null) ||
            (saved.LastValidRunId is null) != (saved.LastValidObservedAtUtc is null) ||
            saved.LastValidRunId <= 0 || saved.LastValidRunId > lastProcessed)
        {
            throw new InvalidDataException("복원한 AP 상태의 식별 또는 실행 순서가 잘못되었습니다.");
        }
        if (saved.OpenIncident is { } incident &&
            (incident.IncidentId is null || incident.IncidentId == Guid.Empty || incident.DeviceGenerationId != generation ||
             incident.MonitoringIntervalId != interval || incident.FirstDownRunId <= 0 || incident.LastDownRunId < incident.FirstDownRunId ||
             incident.LastDownRunId > lastProcessed || incident.LastDownRunId != saved.LastValidRunId ||
             incident.RecoveredRunId is not null || incident.TrackingEndedRunId is not null))
        {
            throw new InvalidDataException("복원한 열린 사건의 실행 참조가 잘못되었습니다.");
        }
        if (saved.OpenGap is { } gap && (gap.FirstRunId <= 0 || gap.FirstRunId > lastProcessed ||
            gap.ResumedRunId is not null || gap.TrackingEndedRunId is not null || gap.IncidentId != saved.OpenIncident?.IncidentId))
        {
            throw new InvalidDataException("복원한 공백의 실행 참조가 잘못되었습니다.");
        }
    }
}
