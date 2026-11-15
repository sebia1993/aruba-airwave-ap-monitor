using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Operations;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore : IOperationsStore
{
    public Task<OperationsVersionSnapshot> GetVersionAsync(CancellationToken ct)
        => ReadOperationsAsync((connection, transaction) => new OperationsVersionSnapshot(CurrentRestoreEpoch(connection, transaction)), ct);

    private static long CurrentRestoreEpoch(SqliteConnection connection, SqliteTransaction transaction)
        => Convert.ToInt64(Scalar(connection, transaction, "SELECT COALESCE(MAX(epoch),0) FROM operations_restore_epochs"));

    private sealed record OperationAp(Guid InstanceId, string SourceId, StoredAp Stored);
    private sealed record FrozenOperationsBinding(Guid GenerationId, ManualInventorySnapshot? Inventory,
        TrackingPolicySnapshot? Policy, PlacementPeriod? Placement, long PlacementVersion, long? PointVersion,
        EvidenceSource? PolicySource = null, ManualManagementDetails? LegacyManagement = null);

    private async Task<T> ReadOperationsAsync<T>(Func<SqliteConnection, SqliteTransaction, T> read, CancellationToken ct)
    {
        await writeGate.WaitAsync(ct);
        try
        {
            using var connection = Open(); Initialize(connection, ct);
            using var transaction = connection.BeginTransaction();
            var result = read(connection, transaction); ct.ThrowIfCancellationRequested(); transaction.Commit(); return result;
        }
        finally { writeGate.Release(); }
    }

    private async Task<T> WriteOperationAsync<T>(Guid commandId, string kind, object payload, long? expectedRestoreEpoch,
        Func<SqliteConnection, SqliteTransaction, T> write, Func<T, T> replay, CancellationToken ct)
    {
        var freshCommit = false;
        var result = await ReadOperationsAsync((connection, transaction) =>
        {
            RequireId(commandId, nameof(commandId));
            var payloadNode = JsonNode.Parse(Encode(payload));
            // Epoch zero is the original wire contract; preserve hashes of pre-epoch receipts.
            if (payloadNode is JsonObject properties && properties["ExpectedRestoreEpoch"]?.GetValue<long>() == 0)
                properties.Remove("ExpectedRestoreEpoch");
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(payloadNode).ToJsonString())));
            using (var command = Command(connection, transaction,
                "SELECT dataset_id,kind,payload_hash,result_json FROM operation_commands WHERE command_id=$id", ("$id", commandId)))
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (reader.GetString(0) != location.DatasetId.ToString("D") || reader.GetString(1) != kind || reader.GetString(2) != fingerprint)
                        throw new OperationsConflictException("같은 명령 ID의 내용을 변경할 수 없습니다.");
                    return replay(Decode<T>(reader.GetString(3)));
                }
            }
            // A restored successful receipt may replay above, but a new write must use a draft
            // opened in this restore epoch. Per-entity versions can legitimately repeat after restore.
            if (expectedRestoreEpoch is { } epoch && (epoch < 0 || epoch != CurrentRestoreEpoch(connection, transaction)))
                throw new OperationsConflictException("백업 복원 후 등록 기준이 변경되었습니다. 편집 내용을 다시 확인하고 새로 여세요.");
            var result = write(connection, transaction);
            Execute(connection, transaction, "INSERT INTO operation_commands VALUES($id,$dataset,$kind,$hash,$result,$at)",
                ("$id", commandId), ("$dataset", location.DatasetId), ("$kind", kind), ("$hash", fingerprint),
                ("$result", Encode(result)), ("$at", timeProvider.GetUtcNow()));
            ct.ThrowIfCancellationRequested(); transactionObserver?.Invoke(StorageTransactionBoundary.BeforeFinalCommit);
            freshCommit = true; return result;
        }, ct);
        // A replay returns its committed receipt without performing another mutation or fault seam.
        if (freshCommit) transactionObserver?.Invoke(StorageTransactionBoundary.AfterFinalCommit);
        return result;
    }

    private static JsonNode Canonical(JsonNode? node) => node switch
    {
        JsonObject value => new JsonObject(value.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value is null ? null : Canonical(p.Value)))),
        JsonArray value => new JsonArray(value.Select(p => p is null ? null : Canonical(p)).ToArray()),
        _ => node?.DeepClone() ?? JsonValue.Create((string?)null)!
    };
    private static void RequireId(Guid id, string name) { if (id == Guid.Empty) throw new ArgumentException("명시적인 내부 ID가 필요합니다.", name); }
    private static void RequireVersion(long actual, long expected) { if (expected < 0 || actual != expected) throw new OperationsConflictException("등록 버전이 변경되었습니다. 새로 읽은 뒤 다시 적용하세요."); }
    private static string Text(string? value, string name, bool required = false, int maximum = 4096)
    {
        var result = value?.Trim() ?? "";
        if ((required && result.Length == 0) || result.Length > maximum || result.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new ArgumentException("필수 내용·길이·문자 형식을 확인하세요.", name);
        return result;
    }
    private static T? Optional<T>(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string, object?)[] args)
        where T : class => Scalar(connection, transaction, sql, args) is string json ? Decode<T>(json) : null;

    private OperationAp ReadOperationAp(SqliteConnection connection, SqliteTransaction transaction, Guid apId, Guid? expectedGeneration = null)
    {
        RequireId(apId, nameof(apId));
        using var command = Command(connection, transaction, """
            SELECT a.instance_id,a.source_ap_id,c.generation_id,c.interval_id,c.metadata_version_id,c.tracking_json,c.fields_json,
                c.source_at_utc,c.last_contacted_at_utc,m.metadata_json,c.measurements_json
            FROM aps a JOIN ap_current c ON c.ap_id=a.ap_id JOIN metadata_versions m ON m.metadata_version_id=c.metadata_version_id
            WHERE a.ap_id=$ap AND a.dataset_id=$dataset
            """, ("$ap", apId), ("$dataset", location.DatasetId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("등록된 AP가 없습니다.");
        var generation = Guid.Parse(reader.GetString(2));
        if (expectedGeneration is { } expected && generation != expected) throw new OperationsConflictException("장비 세대가 변경되었습니다. 현재 장비를 다시 확인하세요.");
        var metadata = Decode<DeviceMetadata>(reader.GetString(9));
        var measurements = Decode<MetadataMeasurements>(reader.GetString(10));
        metadata = metadata with { Device = metadata.Device with { Uptime = measurements.Uptime, RebootedAtUtc = measurements.RebootedAtUtc }, RawSourceValues = measurements.RawSourceValues };
        return new(Guid.Parse(reader.GetString(0)), reader.GetString(1), new(Decode<ApTrackingSnapshot>(reader.GetString(5)),
            new(apId, reader.GetString(1), generation, Guid.Parse(reader.GetString(3)), metadata,
                Decode<ImmutableDictionary<string, FieldEvidence>>(reader.GetString(6)), Time(reader, 7), Time(reader, 8)), reader.GetInt64(4)));
    }

    private static ManualInventorySnapshot? ReadInventory(SqliteConnection connection, SqliteTransaction transaction, Guid generation)
        => Optional<ManualInventorySnapshot>(connection, transaction, "SELECT v.version_json FROM manual_inventory_current c JOIN manual_inventory_versions v USING(generation_id,version) WHERE c.generation_id=$id", ("$id", generation));
    private static TrackingPolicySnapshot? ReadPolicy(SqliteConnection connection, SqliteTransaction transaction, Guid apId)
        => Optional<TrackingPolicySnapshot>(connection, transaction, "SELECT v.version_json FROM tracking_policy_current c JOIN tracking_policy_versions v USING(ap_id,version) WHERE c.ap_id=$id", ("$id", apId));
    private static long PlacementVersion(SqliteConnection connection, SqliteTransaction transaction, Guid generation)
        => Convert.ToInt64(Scalar(connection, transaction, "SELECT version FROM placement_current WHERE generation_id=$id", ("$id", generation)) ?? 0L);
    private static PlacementPeriod? ReadPlacement(SqliteConnection connection, SqliteTransaction transaction, Guid generation)
        => Optional<PlacementPeriod>(connection, transaction, "SELECT v.version_json FROM placement_current c JOIN placements p USING(placement_id) JOIN placement_versions v ON v.placement_id=p.placement_id AND v.version=p.current_version WHERE c.generation_id=$id", ("$id", generation));
    private InstallationPointVersion ReadPoint(SqliteConnection connection, SqliteTransaction transaction, Guid pointId)
    {
        RequireId(pointId, nameof(pointId));
        return Optional<InstallationPointVersion>(connection, transaction, "SELECT v.version_json FROM installation_points p JOIN installation_point_versions v ON v.point_id=p.point_id AND v.version=p.current_version WHERE p.point_id=$id AND p.dataset_id=$dataset", ("$id", pointId), ("$dataset", location.DatasetId))
            ?? throw new KeyNotFoundException("등록된 설치 지점이 없습니다.");
    }
    private FrozenOperationsBinding CurrentBinding(SqliteConnection connection, SqliteTransaction transaction, Guid apId, Guid generation)
    {
        var placement = ReadPlacement(connection, transaction, generation);
        var policySource = Scalar(connection, transaction, "SELECT v.source FROM tracking_policy_current c JOIN tracking_policy_versions v USING(ap_id,version) WHERE c.ap_id=$id", ("$id", apId));
        var legacy = policySource is null ? ReadOperationAp(connection, transaction, apId).Stored.View.Metadata.ManualManagement : null;
        return new(generation, ReadInventory(connection, transaction, generation), ReadPolicy(connection, transaction, apId),
            placement, PlacementVersion(connection, transaction, generation), placement is null ? null : ReadPoint(connection, transaction, placement.InstallationPointId).Version,
            policySource is null ? null : (EvidenceSource)Convert.ToInt32(policySource), legacy);
    }
    private static ManualInventorySnapshot? ManualSnapshot(FrozenOperationsBinding binding)
    {
        if (binding.Placement is null) return binding.Inventory;
        var reference = new PlacementReference(binding.Placement.PlacementId, binding.Placement.InstallationPointId, binding.PointVersion!.Value);
        return binding.Inventory is { } inventory ? inventory with { Placement = reference }
            : new(0, binding.GenerationId, binding.Placement.Source, binding.Placement.RecordedStartAtUtc, ImmutableDictionary<InventoryField, string>.Empty, [], reference);
    }

    public Task<RegistrationSnapshot> GetRegistrationAsync(Guid apId, CancellationToken ct)
        => ReadOperationsAsync((connection, transaction) =>
        {
            var ap = ReadOperationAp(connection, transaction, apId); var generation = ap.Stored.View.DeviceGenerationId;
            var binding = CurrentBinding(connection, transaction, apId, generation);
            var applied = Optional<FrozenOperationsBinding>(connection, transaction, "SELECT binding_json FROM operations_applied_bindings WHERE ap_id=$id AND generation_id=$generation ORDER BY run_id DESC LIMIT 1", ("$id", apId), ("$generation", generation));
            var run = Scalar(connection, transaction, "SELECT MAX(run_id) FROM operations_applied_bindings WHERE ap_id=$id AND generation_id=$generation", ("$id", apId), ("$generation", generation));
            var pending = Encode(applied ?? new(generation, null, null, null, 0, null)) != Encode(binding);
            return new RegistrationSnapshot(new(location.DatasetId, apId, generation, ManualSnapshot(binding), binding.Policy, binding.Placement,
                run is null ? null : Convert.ToInt64(run), pending), binding.PlacementVersion,
                ap.Stored.View.Metadata, ap.Stored.View.Fields, ap.Stored.Tracking.LastProcessedRunId!.Value,
                ap.Stored.Tracking.LastProcessedAtUtc!.Value);
        }, ct);

    private static ImmutableArray<WorkTarget> ApTargets(Guid apId, Guid generation, Guid? point = null)
        => [new(apId, generation, InstallationPointId: point)];
    private static long AddEvent(SqliteConnection connection, SqliteTransaction transaction, Guid dataset, string origin, OperationEventRecord value)
    {
        if (Scalar(connection, transaction, "SELECT event_id FROM operation_events WHERE origin_key=$origin", ("$origin", origin)) is { } old) return Convert.ToInt64(old);
        // ID is materialized while reading; never update an append-only event payload after insertion.
        Execute(connection, transaction, "INSERT INTO operation_events(dataset_id,kind,recorded_at_utc,origin_key,run_id,event_json) VALUES($dataset,$kind,$at,$origin,$run,$json)",
            ("$dataset", dataset), ("$kind", value.Kind), ("$at", value.RecordedAtUtc), ("$origin", origin), ("$run", value.RunId), ("$json", Encode(value)));
        var id = Convert.ToInt64(Scalar(connection, transaction, "SELECT last_insert_rowid()"));
        for (var i = 0; i < value.Targets.Length; i++)
        {
            var t = value.Targets[i];
            Execute(connection, transaction, "INSERT INTO operation_event_targets VALUES($event,$i,$ap,$gen,$incident,$point)",
                ("$event", id), ("$i", i), ("$ap", t.ApId), ("$gen", t.DeviceGenerationId), ("$incident", t.IncidentId), ("$point", t.InstallationPointId));
        }
        return id;
    }
}
