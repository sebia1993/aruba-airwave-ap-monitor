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
        if (context.Kind != CollectionKind.ApStatus) throw new ArgumentException("AP 상태 회차가 필요합니다.", nameof(context));
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
            !Enum.IsDefined(query.Filter) || !Enum.IsDefined(query.Sort) ||
            query.RunId is <= 0 || query.MinimumOpenElapsedSeconds is <= 0 ||
            (query.ManagementStatus is { } management && !Enum.IsDefined(management)))
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
            context = ReadStored<CollectionContext>(reader.GetString(1));
            lifecycle = (RunLifecycle)reader.GetInt32(2);
            finalized = Time(reader, 3)!.Value;
            receipt = ReadStored<CommitReceipt>(reader.GetString(4));
            completeness = (ObservationCompleteness)reader.GetInt32(5);
            errors = ReadStored<ImmutableArray<CollectionError>>(reader.GetString(6));
            sourceEvidence = ReadStored<ApRunSourceEvidence>(reader.GetString(7));
            if (context.RunId != runId || receipt.RunId != runId || context.Profile.DatasetId != query.DatasetId ||
                context.Profile.ScopeVersion != query.ScopeVersion || context.Kind != CollectionKind.ApStatus)
            {
                throw new InvalidDataException("과거 현황의 회차 참조가 일치하지 않습니다.");
            }
        }
        var rows = ReadSnapshotRows(connection, transaction, runId, ct);
        IEnumerable<ApSnapshotRow> filtered = rows.Where(row => MatchesFilter(row, query.Filter));
        if (!string.IsNullOrWhiteSpace(query.Search))
            filtered = filtered.Where(row => MatchesSearch(row, query.Search));
        if (query.ManagementStatus is { } managementStatus)
            filtered = filtered.Where(row => row.ManualManagement?.Status == managementStatus);
        filtered = filtered.Where(row => MeetsOpenElapsedMinimum(row.FirstDownAtUtc, row.TimeNeedsReview,
            row.OpenIncidentId is not null, finalized, query.MinimumOpenElapsedSeconds));
        var selected = filtered.ToList();
        var hidden = query.HidePlanned ? selected.Count(row => row.ManualManagement?.Status == OperationalStatus.PlannedWork) : 0;
        if (query.HidePlanned)
            selected.RemoveAll(row => row.ManualManagement?.Status == OperationalStatus.PlannedWork);
        IOrderedEnumerable<ApSnapshotRow> ordered = query.Sort switch
        {
            DashboardSort.IpAddress => query.Descending ? selected.OrderByDescending(row => DisplayValue(row, InventoryField.IpAddress), StringComparer.OrdinalIgnoreCase)
                : selected.OrderBy(row => DisplayValue(row, InventoryField.IpAddress), StringComparer.OrdinalIgnoreCase),
            DashboardSort.LastObservedAt => query.Descending ? selected.OrderByDescending(row => row.LastValidObservedAtUtc)
                : selected.OrderBy(row => row.LastValidObservedAtUtc),
            DashboardSort.FirstDownAt => query.Descending ? selected.OrderByDescending(row => row.FirstDownAtUtc)
                : selected.OrderBy(row => row.FirstDownAtUtc),
            _ => query.Descending ? selected.OrderByDescending(row => DisplayValue(row, InventoryField.Name), StringComparer.OrdinalIgnoreCase)
                : selected.OrderBy(row => DisplayValue(row, InventoryField.Name), StringComparer.OrdinalIgnoreCase)
        };
        var offset = ((long)query.Page - 1) * query.PageSize;
        var page = offset > int.MaxValue ? ImmutableArray<ApSnapshotRow>.Empty
            : ordered.ThenBy(row => row.SourceApId, StringComparer.Ordinal).Skip((int)offset).Take(query.PageSize).ToImmutableArray();
        return new(query.DatasetId, query.ScopeVersion, runId, context.StartedAtUtc, finalized, lifecycle,
            receipt.Outcome, receipt.Summary, page, selected.Count, completeness, errors, sourceEvidence, hidden);
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

    private static List<ApSnapshotRow> ReadSnapshotRows(SqliteConnection connection, SqliteTransaction transaction,
        long runId, CancellationToken ct)
    {
        var rows = new List<ApSnapshotRow>();
        using (var command = Command(connection, transaction, """
            SELECT r.ap_id,a.source_ap_id,r.generation_id,r.interval_id,m.metadata_json,r.facts_json,r.history_observed_at_utc,r.history_commit_sequence
            FROM historical_ap_rows r JOIN aps a ON a.ap_id=r.ap_id JOIN metadata_versions m ON m.metadata_version_id=r.metadata_version_id
            WHERE r.run_id=$run
            """, ("$run", runId)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var facts = ReadHistoryFacts(reader.GetString(5), runId, reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetInt64(7));
                var metadata = RestoreMeasurements(ReadStored<DeviceMetadata>(reader.GetString(4)), facts.Measurements);
                var upstream = metadata.Upstream is { } reference && facts.Fields.TryGetValue("upstream_device_id", out var upstreamEvidence)
                    ? new SourceUpstreamReference(reference.SourceSwitchId, reference.PortIndex, reference.PortName, reference.ApInterface, upstreamEvidence)
                    : null;
                rows.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), Guid.Parse(reader.GetString(2)),
                    Guid.Parse(reader.GetString(3)), metadata.Identity, metadata.Device, metadata.Location, metadata.Controller,
                    facts.CurrentState, facts.LastValidState, facts.LastValidObservedAtUtc, facts.FirstDownAtUtc,
                    facts.OpenIncidentId, facts.Classification, facts.Fields, metadata.SourceManagement, metadata.ManualManagement,
                    upstream, facts.UnknownReason, facts.TimeNeedsReview, facts.LastValidRunId, facts.SourceAtUtc, facts.LastContactedAtUtc, metadata.RawSourceValues, metadata.ManualInventory)
                { EnrichmentDetail = facts.EnrichmentReferenceId is { } enrichmentId
                    ? ReadReferencedEnrichment(connection, transaction, enrichmentId, runId, facts.EnrichmentCommitSequence, facts.EnrichmentCapturedAtUtc!.Value, ct) : null });
            }
        }
        return rows;
    }

    private static string? DisplayValue(ApSnapshotRow row, InventoryField field)
        => row.ManualInventory?.ManualPreferredFields.Contains(field) == true && row.ManualInventory.Values.TryGetValue(field, out var preferred)
            ? preferred : field == InventoryField.Name ? row.Device.Name : row.Device.IpAddress;

    private static bool MatchesSearch(ApSnapshotRow row, string search) => new[]
    {
        row.SourceApId, row.Device.Name, row.Device.IpAddress, row.Identity.SerialNumber, row.Identity.WiredMac,
        row.Location.Site, row.Location.Building, row.Location.Floor, row.Location.Zone, row.Location.Detail,
        row.Location.AirWaveRawLocation, row.Location.Folder, row.Location.Group,
        row.Controller.SourceId, row.Controller.Name, row.Controller.IpAddress,
        row.Upstream?.SourceSwitchId, row.Upstream?.PortIndex, row.Upstream?.PortName, row.Upstream?.ApInterface
    }.Concat(row.ManualInventory?.Values.Values ?? []).Any(value => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);

    private static bool MatchesFilter(ApSnapshotRow row, DashboardFilter filter) => filter switch
    {
        DashboardFilter.All => true,
        DashboardFilter.OpenIncidents => row.OpenIncidentId is not null,
        DashboardFilter.Up => row.CurrentState == ApState.Up,
        DashboardFilter.Down => row.CurrentState == ApState.Down,
        DashboardFilter.NewDown => row.Classification == ObservationClassification.NewDown,
        DashboardFilter.InitialDown => row.Classification == ObservationClassification.InitialDown,
        DashboardFilter.ExistingDown => row.Classification == ObservationClassification.ExistingDown,
        DashboardFilter.Recovered => row.Classification == ObservationClassification.Recovered,
        DashboardFilter.Unknown => row.Classification == ObservationClassification.Unknown,
        _ => false
    };
}
