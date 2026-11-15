using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AirWaveApMonitor.Core.Contracts;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    /// <summary>Call once during process startup before starting the collector. Repeated calls do not interrupt live runs.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await writeGate.WaitAsync(ct);
        try
        {
            using var connection = Open();
            Initialize(connection, ct);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<CommitReceipt> InterruptRunAsync(CollectionContext context, CancellationToken ct)
    {
        ValidateDataset(context.Profile.DatasetId);
        await writeGate.WaitAsync(ct);
        try
        {
            using var connection = Open();
            Initialize(connection, ct);
            using var transaction = connection.BeginTransaction();
            var stored = ReadContext(connection, transaction, context.RunId);
            if (!JsonNode.DeepEquals(JsonNode.Parse(Encode(stored)), JsonNode.Parse(Encode(context))))
            {
                throw new ArgumentException("실행 시작 당시 고정한 컨텍스트와 일치하지 않습니다.", nameof(context));
            }
            var existing = Scalar(connection, transaction, "SELECT receipt_json FROM run_results WHERE run_id=$run", ("$run", context.RunId)) as string;
            if (existing is not null)
            {
                return Decode<CommitReceipt>(existing) with { AlreadyApplied = true };
            }
            var receipt = FinalizeAp(connection, transaction, stored,
                new(RunOutcome.Failed, [], [], timeProvider.GetUtcNow(), ObservationCompleteness.Unknown,
                    [new(CollectionErrorCategory.Interrupted, 1, "수집 실행을 최종 반영 전에 중단했습니다.")]), RunLifecycle.Interrupted, ct);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return receipt;
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<DashboardSnapshot> GetDashboardAsync(DashboardQuery query, CancellationToken ct)
    {
        ValidateDataset(query.DatasetId);
        if (query.ScopeVersion <= 0 || query.Page <= 0 || query.PageSize is < 1 or > 10000 ||
            !Enum.IsDefined(query.Filter) || !Enum.IsDefined(query.Sort))
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }
        await InitializeAsync(ct);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        long runId;
        CollectionContext context;
        RunLifecycle lifecycle;
        DateTimeOffset finalized;
        CommitReceipt receipt;
        ObservationCompleteness completeness;
        ImmutableArray<CollectionError> errors;
        ApRunSourceEvidence sourceEvidence;
        using (var command = Command(connection, transaction, """
            SELECT r.run_id,r.context_json,r.lifecycle,f.finalized_at_utc,f.receipt_json,f.completeness,f.errors_json,f.source_evidence_json
            FROM runs r JOIN run_results f ON f.run_id=r.run_id
            WHERE r.dataset_id=$dataset AND r.scope_version=$scope AND r.kind=0 AND r.lifecycle IN (1,2)
                AND ($run IS NULL OR r.run_id=$run) ORDER BY r.run_id DESC LIMIT 1
            """, ("$dataset", query.DatasetId), ("$scope", query.ScopeVersion), ("$run", query.RunId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                throw new KeyNotFoundException("선택한 범위에 최종 확정한 AP 회차가 없습니다.");
            }
            runId = reader.GetInt64(0);
            context = Decode<CollectionContext>(reader.GetString(1));
            lifecycle = (RunLifecycle)reader.GetInt32(2);
            finalized = Time(reader, 3)!.Value;
            receipt = Decode<CommitReceipt>(reader.GetString(4));
            completeness = (ObservationCompleteness)reader.GetInt32(5);
            errors = Decode<ImmutableArray<CollectionError>>(reader.GetString(6));
            sourceEvidence = Decode<ApRunSourceEvidence>(reader.GetString(7));
            if (context.RunId != runId || receipt.RunId != runId || context.Profile.DatasetId != query.DatasetId ||
                context.Profile.ScopeVersion != query.ScopeVersion || context.Kind != CollectionKind.ApStatus)
            {
                throw new InvalidDataException("과거 현황의 회차 참조가 일치하지 않습니다.");
            }
        }
        var rows = new List<ApSnapshotRow>();
        using (var command = Command(connection, transaction, """
            SELECT r.ap_id,a.source_ap_id,r.generation_id,r.interval_id,m.metadata_json,r.facts_json
            FROM run_rows r JOIN aps a ON a.ap_id=r.ap_id JOIN metadata_versions m ON m.metadata_version_id=r.metadata_version_id
            WHERE r.run_id=$run
            """, ("$run", runId)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var facts = Decode<RowFacts>(reader.GetString(5));
                var metadata = RestoreMeasurements(Decode<DeviceMetadata>(reader.GetString(4)), facts.Measurements);
                var upstream = metadata.Upstream is { } reference && facts.Fields.TryGetValue("upstream_device_id", out var upstreamEvidence)
                    ? new SourceUpstreamReference(reference.SourceSwitchId, reference.PortIndex, reference.PortName, reference.ApInterface, upstreamEvidence)
                    : null;
                rows.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), Guid.Parse(reader.GetString(2)),
                    Guid.Parse(reader.GetString(3)), metadata.Identity, metadata.Device, metadata.Location, metadata.Controller,
                    facts.CurrentState, facts.LastValidState, facts.LastValidObservedAtUtc, facts.FirstDownAtUtc,
                    facts.OpenIncidentId, facts.Classification, facts.Fields, metadata.SourceManagement, metadata.ManualManagement,
                    upstream, facts.UnknownReason, facts.TimeNeedsReview, facts.LastValidRunId, facts.SourceAtUtc, facts.LastContactedAtUtc, metadata.RawSourceValues));
            }
        }
        IEnumerable<ApSnapshotRow> filtered = rows.Where(row => MatchesFilter(row, query.Filter));
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            filtered = filtered.Where(row => new[] { row.SourceApId, row.Device.Name, row.Device.IpAddress,
                row.Identity.SerialNumber, row.Identity.WiredMac, row.Location.AirWaveRawLocation, row.Location.Folder,
                row.Location.Group, row.Controller.Name }.Any(value => value?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) == true));
        }
        var selected = filtered.ToList();
        IOrderedEnumerable<ApSnapshotRow> ordered = query.Sort switch
        {
            DashboardSort.IpAddress => query.Descending ? selected.OrderByDescending(row => row.Device.IpAddress, StringComparer.OrdinalIgnoreCase)
                : selected.OrderBy(row => row.Device.IpAddress, StringComparer.OrdinalIgnoreCase),
            DashboardSort.LastObservedAt => query.Descending ? selected.OrderByDescending(row => row.LastValidObservedAtUtc)
                : selected.OrderBy(row => row.LastValidObservedAtUtc),
            DashboardSort.FirstDownAt => query.Descending ? selected.OrderByDescending(row => row.FirstDownAtUtc)
                : selected.OrderBy(row => row.FirstDownAtUtc),
            _ => query.Descending ? selected.OrderByDescending(row => row.Device.Name, StringComparer.OrdinalIgnoreCase)
                : selected.OrderBy(row => row.Device.Name, StringComparer.OrdinalIgnoreCase)
        };
        var offset = ((long)query.Page - 1) * query.PageSize;
        var page = offset > int.MaxValue ? ImmutableArray<ApSnapshotRow>.Empty
            : ordered.ThenBy(row => row.SourceApId, StringComparer.Ordinal).Skip((int)offset).Take(query.PageSize).ToImmutableArray();
        return new(query.DatasetId, query.ScopeVersion, runId, context.StartedAtUtc, finalized, lifecycle,
            receipt.Outcome, receipt.Summary, page, selected.Count, completeness, errors, sourceEvidence);
    }

    public async Task<CollectionSuccessTimes> GetSuccessTimesAsync(Guid datasetId, long scopeVersion, CancellationToken ct)
    {
        ValidateDataset(datasetId);
        await InitializeAsync(ct);
        using var connection = Open();
        using var command = Command(connection, null, """
            SELECT last_valid_run_id,last_valid_committed_at_utc,last_complete_run_id,last_complete_committed_at_utc
            FROM success_times WHERE dataset_id=$dataset AND scope_version=$scope AND kind=0
            """, ("$dataset", datasetId), ("$scope", scopeVersion));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.IsDBNull(0) ? null : reader.GetInt64(0), Time(reader, 1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2), Time(reader, 3)) : new(null, null, null, null);
    }

    private static bool MatchesFilter(ApSnapshotRow row, DashboardFilter filter) => filter switch
    {
        DashboardFilter.All => true,
        DashboardFilter.Down => row.CurrentState == ApState.Down,
        DashboardFilter.NewDown => row.Classification == ObservationClassification.NewDown,
        DashboardFilter.InitialDown => row.Classification == ObservationClassification.InitialDown,
        DashboardFilter.ExistingDown => row.Classification == ObservationClassification.ExistingDown,
        DashboardFilter.Recovered => row.Classification == ObservationClassification.Recovered,
        DashboardFilter.Unknown => row.Classification == ObservationClassification.Unknown,
        _ => false
    };
}
