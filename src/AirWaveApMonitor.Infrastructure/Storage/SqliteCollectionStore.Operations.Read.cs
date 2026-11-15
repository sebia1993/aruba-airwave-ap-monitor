using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    public Task<PointPage> GetPointsAsync(PointListQuery query, CancellationToken ct)
        => ReadOperationsAsync((connection, transaction) =>
        {
            ValidatePage(query.Page, query.PageSize); var search = Text(query.Search, nameof(query.Search), maximum: 512);
            var rows = new List<InstallationPointVersion>();
            using var command = Command(connection, transaction, "SELECT v.version_json FROM installation_points p JOIN installation_point_versions v ON v.point_id=p.point_id AND v.version=p.current_version WHERE p.dataset_id=$dataset ORDER BY p.point_id", ("$dataset", location.DatasetId));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested(); var point = Decode<InstallationPointVersion>(reader.GetString(0));
                if (search.Length == 0 || point.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    point.InventoryKey?.Contains(search, StringComparison.OrdinalIgnoreCase) == true || Encode(point.Location).Contains(search, StringComparison.OrdinalIgnoreCase)) rows.Add(point);
            }
            rows.Sort((left, right) => { var compared = StringComparer.Ordinal.Compare(left.Name, right.Name); return compared != 0 ? compared : left.InstallationPointId.CompareTo(right.InstallationPointId); });
            return new PointPage(rows.Skip((int)Math.Min(int.MaxValue, (query.Page - 1L) * query.PageSize)).Take(query.PageSize).ToImmutableArray(), rows.Count, query.Page, query.PageSize);
        }, ct);

    public Task<PointSnapshot> GetPointAsync(Guid pointId, CancellationToken ct)
        => ReadOperationsAsync((connection, transaction) =>
        {
            var current = ReadPoint(connection, transaction, pointId);
            var versions = ReadList<InstallationPointVersion>(connection, transaction, "SELECT version_json FROM installation_point_versions WHERE point_id=$id ORDER BY version", ("$id", pointId));
            var placements = ReadList<PlacementPeriod>(connection, transaction, "SELECT v.version_json FROM placements p JOIN placement_versions v ON v.placement_id=p.placement_id AND v.version=p.current_version WHERE p.point_id=$id ORDER BY p.rowid", ("$id", pointId));
            var currentGenerations = ImmutableHashSet.CreateBuilder<Guid>();
            using var command = Command(connection, transaction, "SELECT DISTINCT c.generation_id FROM ap_current c JOIN placements p ON p.generation_id=c.generation_id WHERE p.point_id=$id", ("$id", pointId));
            using var reader = command.ExecuteReader();
            while (reader.Read()) currentGenerations.Add(Guid.Parse(reader.GetString(0)));
            return new PointSnapshot(current, versions, placements, currentGenerations.ToImmutable());
        }, ct);

    public Task<WorkSnapshot> GetWorkAsync(Guid workId, CancellationToken ct)
        => ReadOperationsAsync((connection, transaction) => new WorkSnapshot(ReadWork(connection, transaction, workId),
            ReadList<WorkVersion>(connection, transaction, "SELECT version_json FROM work_versions WHERE work_id=$id ORDER BY version", ("$id", workId))), ct);

    private static ImmutableArray<T> ReadList<T>(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string, object?)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters); using var reader = command.ExecuteReader();
        var result = ImmutableArray.CreateBuilder<T>(); while (reader.Read()) result.Add(Decode<T>(reader.GetString(0))); return result.ToImmutable();
    }

    public Task<OperationsTimelinePage> GetTimelineAsync(OperationsTimelineQuery query, CancellationToken ct)
        => ReadOperationsAsync((connection, transaction) =>
        {
            ValidatePage(query.Page, query.PageSize); ValidateRange(query.FromUtc, query.ToUtc);
            if (query.Kind is { } kind && !Enum.IsDefined(kind) || query.ThroughEventId is < 0) throw new ArgumentException("이력 조회 조건을 확인하세요.");
            if (query.ApId is { } ap) _ = ReadOperationAp(connection, transaction, ap);
            if (query.InstallationPointId is { } point) _ = ReadPoint(connection, transaction, point);
            if (query.DeviceGenerationId is { } generation)
            {
                RequireId(generation, nameof(query.DeviceGenerationId));
                if (Scalar(connection, transaction, "SELECT g.generation_id FROM generations g JOIN aps a ON a.ap_id=g.ap_id WHERE g.generation_id=$gen AND a.dataset_id=$dataset AND ($ap IS NULL OR a.ap_id=$ap)", ("$gen", generation), ("$dataset", location.DatasetId), ("$ap", query.ApId)) is null)
                    throw new KeyNotFoundException("대상 세대가 없습니다.");
            }
            var maximum = Convert.ToInt64(Scalar(connection, transaction, "SELECT COALESCE(MAX(event_id),0) FROM operation_events WHERE dataset_id=$dataset", ("$dataset", location.DatasetId)));
            if (query.ThroughEventId > maximum) throw new OperationsConflictException("이력 조회 기준이 현재 저장 이력과 다릅니다. 첫 페이지부터 다시 조회하세요.");
            var through = query.ThroughEventId ?? maximum;
            const string predicate = """
                FROM operation_events e WHERE e.dataset_id=$dataset AND e.event_id<=$through
                AND ($kind IS NULL OR e.kind=$kind) AND ($from IS NULL OR e.recorded_at_utc>=$from) AND ($to IS NULL OR e.recorded_at_utc<=$to)
                AND (($ap IS NULL AND $gen IS NULL AND $point IS NULL) OR EXISTS(
                    SELECT 1 FROM operation_event_targets t WHERE t.event_id=e.event_id
                    AND ($ap IS NULL OR t.ap_id=$ap) AND ($gen IS NULL OR t.generation_id=$gen) AND ($point IS NULL OR t.point_id=$point)))
                """;
            (string, object?)[] parameters = [("$dataset", location.DatasetId), ("$through", through), ("$kind", query.Kind),
                ("$from", query.FromUtc), ("$to", query.ToUtc), ("$ap", query.ApId), ("$gen", query.DeviceGenerationId), ("$point", query.InstallationPointId),
                ("$limit", query.PageSize), ("$offset", (query.Page - 1L) * query.PageSize)];
            var count = Convert.ToInt32(Scalar(connection, transaction, "SELECT COUNT(*) " + predicate, parameters));
            using var command = Command(connection, transaction, "SELECT e.event_id,e.event_json " + predicate + " ORDER BY e.event_id DESC LIMIT $limit OFFSET $offset", parameters);
            using var reader = command.ExecuteReader(); var rows = ImmutableArray.CreateBuilder<OperationEventRecord>();
            while (reader.Read()) { ct.ThrowIfCancellationRequested(); rows.Add(Decode<OperationEventRecord>(reader.GetString(1)) with { EventId = reader.GetInt64(0) }); }
            return new OperationsTimelinePage(rows.ToImmutable(), count, through, query.Page, query.PageSize);
        }, ct);
}
