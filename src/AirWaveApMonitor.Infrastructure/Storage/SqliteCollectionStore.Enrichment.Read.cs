using System.Collections.Immutable;
using System.Globalization;
using AirWaveApMonitor.Core.Contracts;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    public Task<EnrichmentRunPage> GetEnrichmentRunsAsync(EnrichmentRunQuery query, CancellationToken ct)
    {
        ValidateDataset(query.DatasetId); ValidatePage(query.Page, query.PageSize); ValidateRange(query.FromUtc, query.ToUtc);
        if (query.ScopeVersion is <= 0 || query.ThroughRunId is <= 0 || query.Kind is { } kind && !IsEnrichmentKind(kind))
            throw new ArgumentOutOfRangeException(nameof(query));
        return ReadOperationsAsync((connection, transaction) =>
        {
            var through = query.ThroughRunId ?? Scalar(connection, transaction, "SELECT MAX(run_id) FROM runs WHERE dataset_id=$dataset", ("$dataset", query.DatasetId)) as long?;
            if (query.ThroughRunId is { } specified) ValidateDataset(ReadContext(connection, transaction, specified).Profile.DatasetId);
            var rows = ReadEnrichmentItems(connection, transaction, query.DatasetId, through, query.ScopeVersion, query.Kind, ct)
                .Where(row => (query.FromUtc is null || row.StartedAtUtc >= query.FromUtc) && (query.ToUtc is null || row.StartedAtUtc <= query.ToUtc)).ToArray();
            return new EnrichmentRunPage(Page(rows, query.Page, query.PageSize), rows.Length, through, query.Page, query.PageSize);
        }, ct);
    }

    public Task<EnrichmentRunDetail> GetEnrichmentRunAsync(Guid datasetId, long runId, CancellationToken ct)
    {
        ValidateDataset(datasetId); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runId);
        return ReadOperationsAsync((connection, transaction) =>
        {
            var run = ReadEnrichmentItems(connection, transaction, datasetId, runId, null, null, ct, exact: true).SingleOrDefault()
                ?? throw new KeyNotFoundException("선택한 부가 수집 회차가 없습니다.");
            return new EnrichmentRunDetail(run, ReadEnrichmentRunEvidence(connection, transaction, runId, ct));
        }, ct);
    }

    public Task<PowerSamplePage> GetPowerSamplesAsync(PowerSampleQuery query, CancellationToken ct)
    {
        ValidateDataset(query.DatasetId); ValidatePage(query.Page, query.PageSize); ValidateRange(query.FromUtc, query.ToUtc);
        if (query.ScopeVersion <= 0 || query.ThroughRunId is <= 0 || query.ThroughCommitSequence is < 0 ||
            query.ApId == Guid.Empty || query.DeviceGenerationId == Guid.Empty || query.InstallationPointId == Guid.Empty ||
            query.Metric is { } metric && !Enum.IsDefined(metric) || query.EndpointRole is { } role && !Enum.IsDefined(role) ||
            InvalidSourceFilter(query.SourceDeviceId) || InvalidSourceFilter(query.SourceInterfaceId))
            throw new ArgumentOutOfRangeException(nameof(query));
        return ReadOperationsAsync((connection, transaction) =>
        {
            ValidatePowerReadTargets(connection, transaction, query);
            var maximum = Convert.ToInt64(Scalar(connection, transaction,
                "SELECT COALESCE(MAX(c.commit_sequence),0) FROM collection_commits c JOIN runs r USING(run_id) WHERE r.dataset_id=$dataset", ("$dataset", query.DatasetId)));
            var boundary = maximum;
            if (query.ThroughRunId is { } runId)
            {
                var context = ReadContext(connection, transaction, runId); ValidateDataset(context.Profile.DatasetId);
                if (context.Profile.ScopeVersion != query.ScopeVersion) throw new KeyNotFoundException("선택한 범위의 회차가 없습니다.");
                boundary = Scalar(connection, transaction, "SELECT commit_sequence FROM collection_commits WHERE run_id=$run", ("$run", runId)) as long?
                    ?? throw new KeyNotFoundException("확정된 회차만 과거 표본 조회 기준으로 사용할 수 있습니다.");
            }
            if (query.ThroughCommitSequence > boundary) throw new ArgumentOutOfRangeException(nameof(query.ThroughCommitSequence));
            var throughCommit = query.ThroughCommitSequence ?? boundary;
            var parameters = new (string, object?)[]
            {
                ("$dataset", query.DatasetId), ("$scope", query.ScopeVersion), ("$run", query.ThroughRunId), ("$commit", throughCommit),
                ("$ap", query.ApId), ("$gen", query.DeviceGenerationId), ("$point", query.InstallationPointId), ("$metric", query.Metric),
                ("$device", query.SourceDeviceId), ("$interface", query.SourceInterfaceId), ("$role", query.EndpointRole),
                ("$from", query.FromUtc), ("$to", query.ToUtc), ("$limit", query.PageSize), ("$offset", (query.Page - 1L) * query.PageSize)
            };
            const string from = """
                FROM power_samples p JOIN runs r ON r.run_id=p.run_id JOIN collection_commits c ON c.run_id=p.run_id
                WHERE r.dataset_id=$dataset AND p.scope_version=$scope AND r.scope_version=$scope
                AND ($run IS NULL OR p.run_id<=$run) AND c.commit_sequence<=$commit
                AND ($ap IS NULL OR p.ap_id=$ap) AND ($gen IS NULL OR p.generation_id=$gen) AND ($point IS NULL OR p.point_id=$point)
                AND ($metric IS NULL OR p.metric=$metric) AND ($device IS NULL OR p.source_device_id=$device)
                AND ($interface IS NULL OR p.source_interface_id=$interface) AND ($role IS NULL OR p.endpoint_role=$role)
                AND ($from IS NULL OR p.read_at_utc>=$from) AND ($to IS NULL OR p.read_at_utc<=$to)
                """;
            var count = Convert.ToInt32(Scalar(connection, transaction, "SELECT COUNT(*) " + from, parameters));
            var rows = ImmutableArray.CreateBuilder<PowerEvidenceView>();
            using (var command = Command(connection, transaction, """
                SELECT p.sample_sequence,p.run_id,p.sample_json,p.normalized_json,p.attribution_json,p.ap_id,p.generation_id,p.interval_id,p.point_id
                """ + " " + from + " ORDER BY p.read_at_utc DESC,p.sample_sequence DESC LIMIT $limit OFFSET $offset", parameters))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    var sample = ReadStored<PowerSample>(reader.GetString(2)); var attribution = ReadStored<PowerAttribution>(reader.GetString(4));
                    if (sample.RunId != reader.GetInt64(1) || sample.Profile.DatasetId != query.DatasetId || sample.Profile.ScopeVersion != query.ScopeVersion ||
                        attribution.SampleId != sample.Id || GuidAt(reader, 5) != attribution.Target?.ApId || GuidAt(reader, 6) != attribution.Target?.DeviceGenerationId ||
                        GuidAt(reader, 7) != attribution.Target?.MonitoringIntervalId || GuidAt(reader, 8) != attribution.Target?.Placement?.InstallationPointId)
                        throw new InvalidDataException("전력 표본의 고정 귀속 참조가 일치하지 않습니다.");
                    rows.Add(new(reader.GetInt64(0), sample, ReadStored<NormalizedPower>(reader.GetString(3)), attribution));
                }
            }
            var expiry = Scalar(connection, transaction, "SELECT cutoff_utc FROM enrichment_retention_state WHERE key='power'") as string;
            var cutoff = expiry is null ? (DateTimeOffset?)null : DateTimeOffset.ParseExact(expiry, "O", CultureInfo.InvariantCulture).ToUniversalTime();
            return new PowerSamplePage(rows.ToImmutable(), count, query.ThroughRunId, throughCommit, query.Page, query.PageSize,
                cutoff, cutoff is not null && (query.FromUtc is null || query.FromUtc < cutoff));
        }, ct);
    }

    private List<EnrichmentRunItem> ReadEnrichmentItems(SqliteConnection connection, SqliteTransaction transaction, Guid datasetId,
        long? through, long? scope, CollectionKind? kind, CancellationToken ct, bool exact = false)
    {
        using var command = Command(connection, transaction, """
            SELECT r.run_id,r.scope_version,r.kind,r.lifecycle,r.context_json,b.profile_json,f.finalized_at_utc,f.receipt_json,
                s.disposition,s.connection_count,s.port_count,s.power_count,s.state_count,s.has_gap,f.outcome,f.completeness
            FROM runs r LEFT JOIN enrichment_run_bindings b ON b.run_id=r.run_id LEFT JOIN run_results f ON f.run_id=r.run_id
            LEFT JOIN enrichment_run_stats s ON s.run_id=r.run_id
            WHERE r.dataset_id=$dataset AND r.kind IN (1,2) AND ($through IS NULL OR r.run_id<=$through)
                AND ($exact=0 OR r.run_id=$through) AND ($scope IS NULL OR r.scope_version=$scope) AND ($kind IS NULL OR r.kind=$kind)
            ORDER BY r.run_id DESC
            """, ("$dataset", datasetId), ("$through", through), ("$scope", scope), ("$kind", kind), ("$exact", exact ? 1 : 0));
        using var reader = command.ExecuteReader(); var rows = new List<EnrichmentRunItem>();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested(); var context = ReadStored<CollectionContext>(reader.GetString(4));
            var lifecycle = (RunLifecycle)reader.GetInt32(3);
            if (reader.IsDBNull(5)) throw new InvalidDataException("부가 수집 시작 바인딩이 없습니다.");
            var profile = ReadStored<EnrichmentProfileBinding>(reader.GetString(5));
            var receipt = reader.IsDBNull(7) ? null : ReadStored<EnrichmentCommitReceipt>(reader.GetString(7));
            if (context.RunId != reader.GetInt64(0) || context.Profile.DatasetId != datasetId || context.Profile.ScopeVersion != reader.GetInt64(1) ||
                (int)context.Kind != reader.GetInt32(2) || profile != EnrichmentProfile(context.Profile) || !Enum.IsDefined(lifecycle) ||
                lifecycle == RunLifecycle.Running && receipt is not null || lifecycle != RunLifecycle.Running && receipt is null)
                throw new InvalidDataException("부가 수집 회차 바인딩이 일치하지 않습니다.");
            if (receipt is not null && (receipt.RunId != context.RunId || receipt.Kind != context.Kind || receipt.DatasetId != datasetId ||
                receipt.Disposition is null || !Enum.IsDefined(receipt.Disposition.Value) || reader.IsDBNull(8) ||
                (int)receipt.Disposition != reader.GetInt32(8) || !Enum.IsDefined(receipt.Outcome) || (int)receipt.Outcome != reader.GetInt32(14) ||
                !Enum.IsDefined(receipt.Completeness) || (int)receipt.Completeness != reader.GetInt32(15)))
                throw new InvalidDataException("부가 수집 확정 결과가 일치하지 않습니다.");
            var counts = new[] { 9, 10, 11, 12 }.Select(i => reader.IsDBNull(i) ? 0 : reader.GetInt32(i)).ToArray();
            if (counts.Any(value => value < 0)) throw new InvalidDataException("부가 수집 건수가 올바르지 않습니다.");
            rows.Add(new(context.RunId, context.Profile.ScopeVersion, context.Profile.ConfigurationVersion, context.Kind, context.Trigger,
                profile.Source, context.StartedAtUtc, Time(reader, 6), lifecycle, receipt?.Outcome, receipt?.Completeness, receipt?.Disposition,
                receipt?.Errors ?? [], counts[0], counts[1], counts[2], counts[3], !reader.IsDBNull(13) && reader.GetInt32(13) != 0));
        }
        return rows;
    }

    private void ValidatePowerReadTargets(SqliteConnection connection, SqliteTransaction transaction, PowerSampleQuery query)
    {
        if (Scalar(connection, transaction, "SELECT 1 FROM scopes WHERE dataset_id=$dataset AND scope_version=$scope", ("$dataset", query.DatasetId), ("$scope", query.ScopeVersion)) is null)
            throw new KeyNotFoundException("선택한 수집 범위가 없습니다.");
        if (query.ApId is { } ap && Scalar(connection, transaction, "SELECT 1 FROM aps WHERE ap_id=$ap AND dataset_id=$dataset", ("$ap", ap), ("$dataset", query.DatasetId)) is null)
            throw new KeyNotFoundException("선택한 AP가 없습니다.");
        if (query.DeviceGenerationId is { } generation && Scalar(connection, transaction, """
            SELECT 1 FROM generations g JOIN aps a USING(ap_id)
            WHERE g.generation_id=$gen AND a.dataset_id=$dataset AND ($ap IS NULL OR a.ap_id=$ap)
            """, ("$gen", generation), ("$dataset", query.DatasetId), ("$ap", query.ApId)) is null)
            throw new KeyNotFoundException("선택한 AP의 장비 세대가 없습니다.");
        if (query.InstallationPointId is { } point && Scalar(connection, transaction,
            "SELECT 1 FROM installation_points WHERE point_id=$point AND dataset_id=$dataset", ("$point", point), ("$dataset", query.DatasetId)) is null)
            throw new KeyNotFoundException("선택한 설치 지점이 없습니다.");
    }
    private static bool InvalidSourceFilter(string? value) => value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > 4096);
    private static Guid? GuidAt(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
}
