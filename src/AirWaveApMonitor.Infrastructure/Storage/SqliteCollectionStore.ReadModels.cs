using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore : IMonitoringReadStore
{
    public async Task<RunPage> GetRunsAsync(RunListQuery query, CancellationToken ct)
    {
        ValidateDataset(query.DatasetId); ValidatePage(query.Page, query.PageSize); ValidateRange(query.FromUtc, query.ToUtc);
        if (query.ScopeVersion is <= 0 || query.ThroughRunId is <= 0 || (query.Kind is { } kind && !Enum.IsDefined(kind)))
            throw new ArgumentOutOfRangeException(nameof(query));
        await InitializeAsync(ct);
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: true);
        var watermark = query.ThroughRunId ?? Scalar(connection, transaction,
            "SELECT MAX(run_id) FROM runs WHERE dataset_id=$dataset", ("$dataset", query.DatasetId)) as long?;
        var rows = ReadRuns(connection, transaction, query.DatasetId, watermark, ct);
        var scopes = rows.Select(row => row.ScopeVersion).Distinct().OrderDescending().ToImmutableArray();
        var filtered = rows.Where(row => (query.ScopeVersion is null || row.ScopeVersion == query.ScopeVersion) &&
            (query.Kind is null || row.Kind == query.Kind) && (query.FromUtc is null || row.StartedAtUtc >= query.FromUtc) &&
            (query.ToUtc is null || row.StartedAtUtc <= query.ToUtc)).ToList();
        return new(query.DatasetId, Page(filtered, query.Page, query.PageSize), filtered.Count, scopes, query.Page, query.PageSize, watermark);
    }

    public async Task<RunListItem> GetRunAsync(Guid datasetId, long runId, CancellationToken ct)
    {
        ValidateDataset(datasetId); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runId);
        await InitializeAsync(ct);
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: true);
        return ReadRuns(connection, transaction, datasetId, runId, ct, exact: true).SingleOrDefault()
            ?? throw new KeyNotFoundException("선택한 수집 회차가 없습니다.");
    }

    public async Task<ApDetailSnapshot> GetApDetailAsync(ApDetailQuery query, CancellationToken ct)
    {
        if (query.ApId == Guid.Empty || query.HistoryLimit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(query));
        var snapshot = await GetDashboardAsync(new(query.DatasetId, query.ScopeVersion, query.RunId, PageSize: 1), ct);
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: true);
        var row = ReadSnapshotRows(connection, transaction, query.RunId, ct).SingleOrDefault(row => row.ApId == query.ApId)
            ?? throw new KeyNotFoundException("선택한 회차에 AP가 없습니다.");
        var selection = Select(snapshot);
        var incidents = ReadIncidents(connection, transaction, selection, new(query.DatasetId, query.ScopeVersion, query.RunId,
            query.ApId, PageSize: query.HistoryLimit, DeviceGenerationId: row.DeviceGenerationId), ct);
        var gaps = ReadGaps(connection, transaction, query.ApId, row.DeviceGenerationId, query.RunId, null, ct);
        var observations = new List<ApObservationHistoryItem>();
        using var command = Command(connection, transaction, """
            SELECT r.run_id,r.context_json,r.lifecycle,f.finalized_at_utc,f.receipt_json,x.facts_json
            FROM run_rows x JOIN runs r ON r.run_id=x.run_id JOIN run_results f ON f.run_id=r.run_id
            WHERE x.ap_id=$ap AND x.generation_id=$generation AND x.run_id<=$run
            ORDER BY x.run_id DESC LIMIT $limit
            """, ("$ap", query.ApId), ("$generation", row.DeviceGenerationId), ("$run", query.RunId), ("$limit", query.HistoryLimit + 1));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            var context = ReadStored<CollectionContext>(reader.GetString(1)); var facts = ReadStored<RowFacts>(reader.GetString(5));
            observations.Add(new(reader.GetInt64(0), context.Profile.ScopeVersion, context.StartedAtUtc, Time(reader, 3)!.Value,
                (RunLifecycle)reader.GetInt32(2), ReadStored<CommitReceipt>(reader.GetString(4)).Outcome,
                facts.CurrentState, facts.Classification, facts.UnknownReason, facts.TimeNeedsReview));
        }
        return new(selection, row, incidents.Rows, gaps.Take(query.HistoryLimit).ToImmutableArray(),
            observations.Take(query.HistoryLimit).ToImmutableArray(), incidents.TotalRows > query.HistoryLimit,
            gaps.Count > query.HistoryLimit, observations.Count > query.HistoryLimit);
    }

    public async Task<IncidentPage> GetIncidentsAsync(IncidentQuery query, CancellationToken ct)
    {
        ValidateIncidentQuery(query);
        var snapshot = await GetDashboardAsync(new(query.DatasetId, query.ScopeVersion, query.AsOfRunId, PageSize: 1), ct);
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: true);
        return ReadIncidents(connection, transaction, Select(snapshot), query, ct);
    }

    public async Task<IncidentReadModel> GetIncidentAsync(IncidentQuery query, Guid incidentId, CancellationToken ct)
    {
        ValidateIncidentQuery(query);
        if (incidentId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(incidentId));
        var snapshot = await GetDashboardAsync(new(query.DatasetId, query.ScopeVersion, query.AsOfRunId, PageSize: 1), ct);
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: true);
        var seed = ReadIncidentSeeds(connection, transaction, query, ct).SingleOrDefault(seed => seed.IncidentId == incidentId)
            ?? throw new KeyNotFoundException("선택한 시점과 대상에 사건이 없습니다.");
        return BuildIncident(connection, transaction, Select(snapshot), seed, ct);
    }

    private static RunSelection Select(DashboardSnapshot snapshot) => new(snapshot.DatasetId, snapshot.ScopeVersion,
        snapshot.RunId, snapshot.StartedAtUtc, snapshot.FinalizedAtUtc, snapshot.Lifecycle, snapshot.Outcome);

    private static List<RunListItem> ReadRuns(SqliteConnection connection, SqliteTransaction transaction,
        Guid datasetId, long? through, CancellationToken ct, bool exact = false)
    {
        using var command = Command(connection, transaction, """
            SELECT r.run_id,r.context_json,r.lifecycle,f.finalized_at_utc,f.receipt_json,f.completeness,f.errors_json,f.source_evidence_json
            FROM runs r LEFT JOIN run_results f ON f.run_id=r.run_id
            WHERE r.dataset_id=$dataset AND ($through IS NULL OR r.run_id<=$through) AND ($exact=0 OR r.run_id=$through)
            ORDER BY r.run_id DESC
            """, ("$dataset", datasetId), ("$through", through), ("$exact", exact ? 1 : 0));
        using var reader = command.ExecuteReader(); var rows = new List<RunListItem>();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            var context = ReadStored<CollectionContext>(reader.GetString(1));
            var receipt = reader.IsDBNull(4) || context.Kind != CollectionKind.ApStatus ? null : ReadStored<CommitReceipt>(reader.GetString(4));
            var enrichment = reader.IsDBNull(4) || context.Kind == CollectionKind.ApStatus ? null : ReadStored<EnrichmentCommitReceipt>(reader.GetString(4));
            if (context.RunId != reader.GetInt64(0) || context.Profile.DatasetId != datasetId ||
                (receipt is not null && receipt.RunId != context.RunId)) throw new InvalidDataException("수집 회차 참조가 일치하지 않습니다.");
            rows.Add(new(context.RunId, context.Profile.ScopeVersion, context.Kind, context.Trigger, (RunLifecycle)reader.GetInt32(2),
                context.StartedAtUtc, Time(reader, 3), receipt?.Outcome ?? enrichment?.Outcome, receipt?.Summary,
                reader.IsDBNull(5) ? null : (ObservationCompleteness)reader.GetInt32(5),
                reader.IsDBNull(6) ? [] : ReadStored<ImmutableArray<CollectionError>>(reader.GetString(6)),
                reader.IsDBNull(7) || context.Kind != CollectionKind.ApStatus ? null : ReadStored<ApRunSourceEvidence>(reader.GetString(7))));
        }
        return rows;
    }

    private static IncidentPage ReadIncidents(SqliteConnection connection, SqliteTransaction transaction,
        RunSelection selection, IncidentQuery query, CancellationToken ct)
    {
        var seeds = ReadIncidentSeeds(connection, transaction, query, ct);
        var rows = Page(seeds, query.Page, query.PageSize).Select(seed => BuildIncident(connection, transaction, selection, seed, ct)).ToImmutableArray();
        return new(selection, rows, seeds.Count, seeds.Select(seed => seed.ApId).Distinct().Count(), query.Page, query.PageSize);
    }

    private static List<IncidentSeed> ReadIncidentSeeds(SqliteConnection connection, SqliteTransaction transaction,
        IncidentQuery query, CancellationToken ct)
    {
        var targets = ReadSnapshotRows(connection, transaction, query.AsOfRunId, ct).ToDictionary(row => row.ApId);
        using var command = Command(connection, transaction, """
            WITH latest AS (
                SELECT incident_id,MAX(run_id) AS run_id
                FROM run_transitions WHERE run_id<=$run
                GROUP BY incident_id
            )
            SELECT t.ap_id,a.source_ap_id,t.generation_id,t.interval_id,t.incident_id,t.tracking_json
            FROM latest l JOIN run_transitions t ON t.run_id=l.run_id AND t.incident_id=l.incident_id
                JOIN aps a ON a.ap_id=t.ap_id
            WHERE a.dataset_id=$dataset
                AND EXISTS (SELECT 1 FROM run_rows r WHERE r.run_id=$run AND r.ap_id=t.ap_id)
                AND ($ap IS NULL OR t.ap_id=$ap) AND ($generation IS NULL OR t.generation_id=$generation)
            """, ("$dataset", query.DatasetId), ("$run", query.AsOfRunId), ("$ap", query.ApId), ("$generation", query.DeviceGenerationId));
        using var reader = command.ExecuteReader(); var seeds = new List<IncidentSeed>();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            var tracking = ReadStored<IncidentTrackingSnapshot>(reader.GetString(5));
            var seed = new IncidentSeed(Guid.Parse(reader.GetString(0)), reader.GetString(1), Guid.Parse(reader.GetString(2)),
                Guid.Parse(reader.GetString(3)), Guid.Parse(reader.GetString(4)), tracking);
            if (tracking.IncidentId != seed.IncidentId || tracking.DeviceGenerationId != seed.GenerationId ||
                tracking.MonitoringIntervalId != seed.IntervalId || tracking.FirstDownRunId > query.AsOfRunId ||
                tracking.LastDownRunId > query.AsOfRunId || tracking.RecoveredRunId > query.AsOfRunId || tracking.TrackingEndedRunId > query.AsOfRunId)
                throw new InvalidDataException("과거 사건의 참조 또는 시점이 일치하지 않습니다.");
            if ((query.Status is null || Status(tracking) == query.Status) &&
                (query.FromUtc is null || tracking.FirstDownAtUtc >= query.FromUtc) &&
                (query.ToUtc is null || tracking.FirstDownAtUtc <= query.ToUtc) &&
                (string.IsNullOrWhiteSpace(query.Search) || MatchesSearch(targets[seed.ApId], query.Search) ||
                    seed.IncidentId.ToString("D").Contains(query.Search, StringComparison.OrdinalIgnoreCase))) seeds.Add(seed);
        }
        return seeds.OrderByDescending(seed => seed.Tracking.FirstDownRunId).ThenBy(seed => seed.IncidentId).ToList();
    }

    private static IncidentReadModel BuildIncident(SqliteConnection connection, SqliteTransaction transaction,
        RunSelection selection, IncidentSeed seed, CancellationToken ct)
    {
        IncidentSnapshot? detection = null, recovery = null;
        using (var command = Command(connection, transaction,
            "SELECT snapshot_kind,snapshot_json FROM incident_snapshots WHERE incident_id=$id AND run_id<=$run",
            ("$id", seed.IncidentId), ("$run", selection.RunId)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var value = ReadStored<IncidentSnapshot>(reader.GetString(1));
                if (value.DatasetId != selection.DatasetId || value.RunId > selection.RunId || value.Device.ApId != seed.ApId ||
                    value.Device.DeviceGenerationId != seed.GenerationId || value.Device.MonitoringIntervalId != seed.IntervalId)
                    throw new InvalidDataException("사건 스냅샷의 참조가 일치하지 않습니다.");
                if (reader.GetInt32(0) == 0) detection = value; else recovery = value;
            }
        }
        if (detection is null || ((seed.Tracking.RecoveredRunId is not null) != (recovery is not null)))
            throw new InvalidDataException("사건 감지/복구 스냅샷을 확인할 수 없습니다.");
        var gaps = ReadGaps(connection, transaction, seed.ApId, seed.GenerationId, selection.RunId, seed.IncidentId, ct);
        var end = seed.Tracking.RecoveredAtUtc ?? seed.Tracking.TrackingEndedAtUtc ?? selection.FinalizedAtUtc;
        double? elapsed = seed.Tracking.TimeNeedsReview || end < seed.Tracking.FirstDownAtUtc ? null : (end - seed.Tracking.FirstDownAtUtc).TotalSeconds;
        return new(seed.IncidentId, seed.ApId, seed.SourceApId, seed.GenerationId, seed.IntervalId, Status(seed.Tracking),
            seed.Tracking, detection, recovery, gaps.Take(50).ToImmutableArray(), elapsed, gaps.Count > 50);
    }

    private static List<ObservationGap> ReadGaps(SqliteConnection connection, SqliteTransaction transaction,
        Guid apId, Guid generationId, long cutoff, Guid? incidentId, CancellationToken ct)
    {
        using var command = Command(connection, transaction,
            "SELECT first_run_id,gap_json FROM observation_gaps WHERE ap_id=$ap AND generation_id=$generation AND first_run_id<=$run ORDER BY first_run_id DESC",
            ("$ap", apId), ("$generation", generationId), ("$run", cutoff));
        using var reader = command.ExecuteReader(); var gaps = new List<ObservationGap>();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested(); var gap = ReadStored<ObservationGap>(reader.GetString(1));
            if (gap.FirstRunId != reader.GetInt64(0)) throw new InvalidDataException("관측 공백의 시작 회차가 일치하지 않습니다.");
            if (incidentId is not null && gap.IncidentId != incidentId) continue;
            // Completion fields mutate, and their RunIds define when the facts became visible.
            if (gap.ResumedRunId > cutoff) gap = gap with { ResumedRunId = null, ResumedAtUtc = null };
            if (gap.TrackingEndedRunId > cutoff) gap = gap with { TrackingEndedRunId = null, TrackingEndedAtUtc = null, TrackingEndReason = null };
            gaps.Add(gap);
        }
        return gaps;
    }

    // Deserialized records validate constructor arguments too; invalid persisted values are storage errors,
    // distinct from the caller's query validation, which must remain ArgumentException/HTTP 400.
    private static T ReadStored<T>(string json)
    {
        try { return Decode<T>(json); }
        catch (ArgumentException error)
        { throw new InvalidDataException("저장된 자료의 값이 올바르지 않습니다.", error); }
    }

    private static IncidentStatus Status(IncidentTrackingSnapshot value) => value.RecoveredRunId is not null
        ? IncidentStatus.Recovered : value.TrackingEndedRunId is not null ? IncidentStatus.TrackingEnded : IncidentStatus.Open;
    private static void ValidateIncidentQuery(IncidentQuery query)
    {
        ValidatePage(query.Page, query.PageSize); ValidateRange(query.FromUtc, query.ToUtc);
        if (query.AsOfRunId <= 0 || query.ScopeVersion <= 0 || query.ApId == Guid.Empty || query.DeviceGenerationId == Guid.Empty ||
            (query.Status is { } status && !Enum.IsDefined(status))) throw new ArgumentOutOfRangeException(nameof(query));
    }
    private static void ValidatePage(int page, int pageSize)
    {
        if (page <= 0 || pageSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(page));
    }
    private static void ValidateRange(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from > to) throw new ArgumentException("시각 범위가 올바르지 않습니다.");
    }
    private static ImmutableArray<T> Page<T>(IReadOnlyList<T> values, int page, int size)
    {
        var offset = ((long)page - 1) * size;
        return offset > int.MaxValue ? [] : values.Skip((int)offset).Take(size).ToImmutableArray();
    }
    private sealed record IncidentSeed(Guid ApId, string SourceApId, Guid GenerationId, Guid IntervalId,
        Guid IncidentId, IncidentTrackingSnapshot Tracking);
}
