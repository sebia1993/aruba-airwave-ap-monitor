using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AirWaveApMonitor.Core.Contracts;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    private sealed record FrozenTargetIdentity(Guid ApId, string SourceApId, Guid DeviceGenerationId,
        Guid MonitoringIntervalId, PlacementEvidenceSnapshot? Placement, EvidenceInterval? IdentityWindow);
    private EvidenceSource EnrichmentSource => location.Mode == AppMode.Demo ? EvidenceSource.Demo : EvidenceSource.AirWave;
    private EnrichmentProfileBinding EnrichmentProfile(ProfileSnapshot profile) => EnrichmentProfileBinding.From(profile, EnrichmentSource);
    private static string EnrichmentHash(object value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(JsonNode.Parse(Encode(value))).ToJsonString())));
    private string EnrichmentProfileKey(ProfileSnapshot profile) => EnrichmentHash(EnrichmentProfile(profile));
    private static bool IsEnrichmentKind(CollectionKind kind) => kind is CollectionKind.ConnectionNeighbor or CollectionKind.Power;
    private CollectionContext CheckedEnrichmentContext(SqliteConnection connection, SqliteTransaction transaction, CollectionContext context)
    {
        ValidateDataset(context.Profile.DatasetId);
        if (!IsEnrichmentKind(context.Kind)) throw new ArgumentException("부가 수집 종류가 필요합니다.", nameof(context));
        var saved = ReadContext(connection, transaction, context.RunId);
        if (!JsonNode.DeepEquals(JsonNode.Parse(Encode(saved)), JsonNode.Parse(Encode(context))))
            throw new ArgumentException("시작 당시 고정한 부가 수집 컨텍스트와 다릅니다.", nameof(context));
        return saved;
    }
    private void FreezeEnrichmentBindings(SqliteConnection connection, SqliteTransaction transaction, CollectionContext context)
    {
        var profile = EnrichmentProfile(context.Profile); var key = EnrichmentHash(profile);
        var connectionIds = new List<long>();
        using (var command = Command(connection, transaction, "SELECT record_id FROM enrichment_current WHERE profile_key=$profile AND record_type=0 ORDER BY record_id", ("$profile", key)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) connectionIds.Add(reader.GetInt64(0));
        Execute(connection, transaction, "INSERT INTO enrichment_run_bindings VALUES($run,$key,$profile,$epoch,$connections)",
            ("$run", context.RunId), ("$key", key), ("$profile", Encode(profile)), ("$epoch", CurrentRestoreEpoch(connection, transaction)), ("$connections", Encode(connectionIds)));
        foreach (var target in context.Profile.ExpectedTargets)
        {
            var apId = Scalar(connection, transaction, "SELECT a.ap_id FROM aps a JOIN ap_current c USING(ap_id) WHERE a.dataset_id=$dataset AND a.instance_id=$instance AND a.source_ap_id=$source", ("$dataset", location.DatasetId), ("$instance", context.Profile.InstanceId), ("$source", target.SourceApId)) as string;
            if (apId is null) continue;
            var ap = ReadOperationAp(connection, transaction, Guid.Parse(apId)); var view = ap.Stored.View;
            var manual = ReadFrozenBinding(connection, transaction, context.RunId, view.ApId)!;
            if (ap.Stored.Tracking.MonitoringStatus != MonitoringStatus.Active || manual.Policy?.Directive is MonitoringDirective.EndTracking or MonitoringDirective.TemporarilyExclude) continue;
            PlacementEvidenceSnapshot? placement = manual.Placement is not { } period ? null : new(view.DeviceGenerationId,
                manual.PlacementVersion, period.InstallationPointId, manual.PointVersion, period.ActualStartAtUtc ?? period.RecordedStartAtUtc,
                period.ActualEndAtUtc ?? period.RecordedEndAtUtc, period.RecordedStartAtUtc);
            // These are authored Demo registration boundaries only. Polling does not establish
            // verified physical continuity or an exact replacement time on Live equipment.
            EvidenceInterval? identity = location.Mode != AppMode.Demo ? null : DemoIdentityWindow(connection, transaction, view);
            var frozen = new FrozenTargetIdentity(view.ApId, view.SourceApId, view.DeviceGenerationId, view.MonitoringIntervalId, placement, identity);
            Execute(connection, transaction, "INSERT INTO enrichment_target_bindings VALUES($run,$ap,$gen,$interval,$json)",
                ("$run", context.RunId), ("$ap", view.ApId), ("$gen", view.DeviceGenerationId), ("$interval", view.MonitoringIntervalId), ("$json", Encode(frozen)));
        }
    }
    private static EvidenceInterval? DemoIdentityWindow(SqliteConnection connection, SqliteTransaction transaction, CommittedDeviceView view)
    {
        using var command = Command(connection, transaction, "SELECT g.started_run_id,g.ended_run_id,i.started_run_id,i.ended_run_id FROM generations g JOIN monitoring_intervals i ON i.generation_id=g.generation_id WHERE g.generation_id=$gen AND i.interval_id=$interval", ("$gen", view.DeviceGenerationId), ("$interval", view.MonitoringIntervalId));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        var startRuns = new[] { reader.GetInt64(0), reader.GetInt64(2) };
        var endRuns = new[] { reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1), reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3) };
        reader.Close();
        DateTimeOffset Boundary(long run) => ReadContext(connection, transaction, run).StartedAtUtc;
        var from = startRuns.Select(Boundary).Max();
        var until = endRuns.Where(r => r is not null).Select(r => (DateTimeOffset?)Boundary(r!.Value)).Min();
        return until is { } end && end <= from ? null : new(from, until, true);
    }
    private static ImmutableArray<EnrichmentTargetBinding> ReadEnrichmentTargets(SqliteConnection connection, SqliteTransaction transaction, long runId)
    {
        var profile = Optional<EnrichmentProfileBinding>(connection, transaction, "SELECT profile_json FROM enrichment_run_bindings WHERE run_id=$run", ("$run", runId))
            ?? throw new InvalidDataException("저장된 부가 수집 시작 바인딩이 없습니다.");
        return ReadList<FrozenTargetIdentity>(connection, transaction, "SELECT target_json FROM enrichment_target_bindings WHERE run_id=$run ORDER BY ap_id", ("$run", runId))
            .Select(t => new EnrichmentTargetBinding(profile, t.ApId, t.SourceApId, t.DeviceGenerationId, t.MonitoringIntervalId, t.Placement, t.IdentityWindow)).ToImmutableArray();
    }
    public Task<ImmutableArray<EnrichmentTargetBinding>> GetFrozenEnrichmentTargetsAsync(CollectionContext context, CancellationToken ct)
        => ReadOperationsAsync((connection, transaction) => ReadEnrichmentTargets(connection, transaction,
            CheckedEnrichmentContext(connection, transaction, context).RunId), ct);
}
