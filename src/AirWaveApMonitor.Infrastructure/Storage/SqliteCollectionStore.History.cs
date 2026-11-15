using System.Text.Json.Nodes;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    // Fields whose accepted values DeviceMetadataMerger/IdentityResolver can refresh.
    // Other evidence (e.g. device_category) is retained by the merger, so its initial
    // time stays literal. This controls reuse only; no field/value is discarded.
    private static readonly HashSet<string> HistoryRefreshedFields = new([
        "is_up", "serial_number", "lan_mac", "name", "lan_ip", "model", "firmware", "snmp_uptime", "last_reboot",
        "site", "building", "floor", "zone", "detail", "installation_point_id", "syslocation", "folder", "group",
        "controller_id", "controller_name", "controller_ip", "planned_maintenance_mode", "operating_mode",
        "upstream_device_id", "upstream_port_index", "upstream_port_name", "ap_interface", "last_contacted"
    ], StringComparer.Ordinal);
    private sealed record HistoryMember(Guid ApId, long VersionId);
    private sealed record HistoryCapture(long RunId, DateTimeOffset ObservedAtUtc, long CommitSequence)
    {
        public List<HistoryMember> Members { get; } = [];
    }

    private static void CaptureNormalHistory(SqliteConnection connection, SqliteTransaction transaction,
        HistoryCapture capture, CommittedDeviceView view, long metadataId, RowFacts facts)
    {
        var template = JsonNode.Parse(Encode(facts))!.AsObject();
        TransformHistoryTokens(template, capture, encode: true);
        var hash = EnrichmentHash(template); var json = template.ToJsonString();
        Execute(connection, transaction, """
            INSERT OR IGNORE INTO ap_history_versions(ap_id,generation_id,interval_id,metadata_version_id,facts_hash,facts_template_json)
            VALUES($ap,$gen,$interval,$metadata,$hash,$json)
            """, ("$ap", view.ApId), ("$gen", view.DeviceGenerationId), ("$interval", view.MonitoringIntervalId),
            ("$metadata", metadataId), ("$hash", hash), ("$json", json));
        using var command = Command(connection, transaction, """
            SELECT version_id,facts_template_json FROM ap_history_versions WHERE ap_id=$ap AND generation_id=$gen
            AND interval_id=$interval AND metadata_version_id=$metadata AND facts_hash=$hash
            """, ("$ap", view.ApId), ("$gen", view.DeviceGenerationId), ("$interval", view.MonitoringIntervalId), ("$metadata", metadataId), ("$hash", hash));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || !JsonNode.DeepEquals(template, JsonNode.Parse(reader.GetString(1))))
            throw new InvalidDataException("과거 표시 버전이 일치하지 않습니다.");
        capture.Members.Add(new(view.ApId, reader.GetInt64(0)));
    }

    private static void PersistHistoryFrame(SqliteConnection connection, SqliteTransaction transaction,
        CollectionContext context, HistoryCapture capture)
    {
        var members = capture.Members.OrderBy(m => m.ApId).ToArray();
        if (members.Select(m => m.ApId).Distinct().Count() != members.Length)
            throw new InvalidDataException("과거 목록의 AP가 중복되었습니다.");
        var hash = EnrichmentHash(members);
        var id = Scalar(connection, transaction, "SELECT set_id FROM ap_history_sets WHERE dataset_id=$dataset AND scope_version=$scope AND members_hash=$hash",
            ("$dataset", context.Profile.DatasetId), ("$scope", context.Profile.ScopeVersion), ("$hash", hash));
        long setId;
        if (id is null)
        {
            Execute(connection, transaction, "INSERT INTO ap_history_sets(dataset_id,scope_version,members_hash,member_count) VALUES($dataset,$scope,$hash,$count)",
                ("$dataset", context.Profile.DatasetId), ("$scope", context.Profile.ScopeVersion), ("$hash", hash), ("$count", members.Length));
            setId = Convert.ToInt64(Scalar(connection, transaction, "SELECT last_insert_rowid()"));
            foreach (var member in members)
                Execute(connection, transaction, "INSERT INTO ap_history_members VALUES($set,$ap,$version)", ("$set", setId), ("$ap", member.ApId), ("$version", member.VersionId));
        }
        else
        {
            setId = Convert.ToInt64(id); var saved = new List<HistoryMember>();
            using var command = Command(connection, transaction, "SELECT ap_id,version_id FROM ap_history_members WHERE set_id=$set", ("$set", setId));
            using var reader = command.ExecuteReader();
            while (reader.Read()) saved.Add(new(Guid.Parse(reader.GetString(0)), reader.GetInt64(1)));
            if (!members.SequenceEqual(saved.OrderBy(m => m.ApId))) throw new InvalidDataException("과거 목록 버전이 일치하지 않습니다.");
        }
        Execute(connection, transaction, "INSERT INTO run_history_frames VALUES($run,$set,$at,$commit)",
            ("$run", context.RunId), ("$set", setId), ("$at", capture.ObservedAtUtc), ("$commit", capture.CommitSequence));
    }

    private static RowFacts ReadHistoryFacts(string json, long runId, string? observedAt, long? commit)
    {
        if (observedAt is null && commit is null) return ReadStored<RowFacts>(json);
        if (observedAt is null || commit is null) throw new InvalidDataException("과거 회차의 관측 컨텍스트가 없습니다.");
        try
        {
            var template = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("과거 사실 버전이 없습니다.");
            TransformHistoryTokens(template, new(runId, DateTimeOffset.Parse(observedAt), commit.Value), encode: false);
            return ReadStored<RowFacts>(template.ToJsonString());
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        { throw new InvalidDataException("저장된 과거 관측의 형식이 올바르지 않습니다.", error); }
    }

    private static RowFacts ReadHistoricalFacts(SqliteConnection connection, SqliteTransaction transaction, long runId, Guid apId)
    {
        using var command = Command(connection, transaction, "SELECT facts_json,history_observed_at_utc,history_commit_sequence FROM historical_ap_rows WHERE run_id=$run AND ap_id=$ap",
            ("$run", runId), ("$ap", apId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("과거 관측 사실이 없습니다.");
        return ReadHistoryFacts(reader.GetString(0), runId, reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    // Only typed fields can contain tokens. Raw source strings are never parsed or transformed.
    // StorageDatabase normalizes DateTimeOffset to UTC; compare that exact stored representation.
    private static void TransformHistoryTokens(JsonObject facts, HistoryCapture context, bool encode)
    {
        void Bind(JsonObject target, string property, string token, JsonNode value)
        {
            if (encode)
            {
                if (JsonNode.DeepEquals(target[property], value)) target[property] = new JsonObject { ["$history"] = token };
            }
            else if (target[property] is JsonObject marker)
            {
                if (marker.Count != 1 || marker["$history"]?.GetValue<string>() != token)
                    throw new InvalidDataException("과거 사실의 관측 참조가 올바르지 않습니다.");
                target[property] = value.DeepClone();
            }
        }
        var at = JsonNode.Parse(Encode(context.ObservedAtUtc))!;
        foreach (var property in new[] { "LastValidObservedAtUtc", "SourceAtUtc", "LastContactedAtUtc", "EnrichmentCapturedAtUtc" })
            Bind(facts, property, "observed", at);
        if (facts["Fields"] is JsonObject fields)
            foreach (var (name, node) in fields)
            {
                if (node is not JsonObject field) continue;
                // Absent/invalid fields keep their first known evidence time in the merger.
                // Encoding that first time relative to its run would clone the version on
                // the next run when the same retained time becomes an absolute value.
                var refreshedValue = HistoryRefreshedFields.Contains(name) && field["Availability"]?.GetValue<int>() == (int)FieldAvailability.Provided &&
                    field["Validity"]?.GetValue<int>() == (int)FieldValidity.Valid;
                var rawValue = facts["Measurements"]?["RawSourceValues"] is JsonObject raw && raw.ContainsKey(name) &&
                    field["Availability"]?.GetValue<int>() != (int)FieldAvailability.NotProvided &&
                    field["Validity"]?.GetValue<int>() != (int)FieldValidity.Invalid;
                if (encode && !refreshedValue && !rawValue) continue;
                Bind(field, "CollectedAtUtc", "observed", at);
                Bind(field, "SourceAtUtc", "observed", at);
            }
        if (facts["Measurements"] is JsonObject measurements) Bind(measurements, "RebootedAtUtc", "observed", at);
        Bind(facts, "LastValidRunId", "run", JsonValue.Create(context.RunId)!);
        Bind(facts, "EnrichmentCommitSequence", "commit", JsonValue.Create(context.CommitSequence)!);
    }
}
