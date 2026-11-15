using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Operations;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    public Task<ImportPreview> PreviewImportAsync(ReadOnlyMemory<byte> csv, ProfileSnapshot currentProfile, CancellationToken ct)
    {
        ValidateDataset(currentProfile.DatasetId);
        if (csv.Length > new ImportLimits().MaxBytes) throw new ArgumentException("CSV 최대 크기는 4MiB입니다.");
        return ReadOperationsAsync((connection, transaction) =>
        {
            var mapping = BuildImportMapping(connection, transaction, currentProfile, Guid.NewGuid());
            var preview = ValidateStoredPreview(connection, transaction, new ManualImportValidator().Preview(csv, mapping), mapping);
            var placementVersions = mapping.Inventory.ToDictionary(entry => entry.ApId, entry => PlacementVersion(connection, transaction, entry.DeviceGenerationId));
            Execute(connection, transaction, "INSERT INTO import_previews VALUES($id,$dataset,$json,$placements,$at)",
                ("$id", preview.PreviewId), ("$dataset", location.DatasetId), ("$json", Encode(preview)), ("$placements", Encode(placementVersions)), ("$at", preview.CreatedAtUtc));
            return preview;
        }, ct);
    }

    public Task<ImportApplyReceipt> ApplyImportAsync(ImportApply apply, ProfileSnapshot currentProfile, CancellationToken ct)
    {
        ValidateDataset(currentProfile.DatasetId);
        return WriteOperationAsync(apply.CommandId, "import", apply, null, (connection, transaction) =>
        {
            RequireId(apply.PreviewId, nameof(apply.PreviewId));
            var preview = Optional<ImportPreview>(connection, transaction, "SELECT preview_json FROM import_previews WHERE preview_id=$id AND dataset_id=$dataset", ("$id", apply.PreviewId), ("$dataset", location.DatasetId))
                ?? throw new KeyNotFoundException("서버에 저장된 미리보기가 없습니다.");
            if (apply.ExpectedVersion != preview.Version) throw new OperationsConflictException("미리보기 버전이 다릅니다.");
            var placementVersions = Optional<Dictionary<Guid, long>>(connection, transaction, "SELECT placement_versions_json FROM import_previews WHERE preview_id=$id", ("$id", apply.PreviewId))!;
            var mapping = BuildImportMapping(connection, transaction, currentProfile, preview.PreviewId);
            var errors = new ManualImportValidator().Revalidate(preview, apply.RowNumbers, mapping);
            if (!errors.IsEmpty) throw new OperationsConflictException(string.Join(" ", errors.Select(e => $"{e.RowNumber}: {e.Message}")));
            var results = ImmutableArray.CreateBuilder<ImportRowResult>();
            foreach (var number in apply.RowNumbers)
            {
                ct.ThrowIfCancellationRequested(); var row = preview.Rows.Single(r => r.RowNumber == number);
                var target = row.Target!; var ap = ReadOperationAp(connection, transaction, target.ApId, target.DeviceGenerationId);
                if (!placementVersions.TryGetValue(target.ApId, out var frozenPlacementVersion) ||
                    PlacementVersion(connection, transaction, target.DeviceGenerationId) != frozenPlacementVersion)
                    throw new OperationsConflictException("배치 등록 이력이 변경되었습니다. 다시 미리보기 하십시오.");
                var mutation = row.Mutation; var manualVersion = target.ManualVersion;
                var origin = $"{apply.CommandId}:{number}";
                if (row.HasChanges)
                {
                    if (!mutation.SetValues.IsEmpty || !mutation.ClearFields.IsEmpty || mutation.ManualPreferredFields is not null)
                        manualVersion = SaveInventory(connection, transaction, ap, mutation, EvidenceSource.Import, origin).Version;
                    if (mutation.ManagementStatus is { } status)
                        SavePolicy(connection, transaction, ap, status, mutation.ManagementReason!, null, EvidenceSource.Import, origin);
                    if (mutation.ChangesPlacement)
                        MovePlacement(connection, transaction, ap, mutation.InstallationPointId, target.RequestedPointVersion,
                            mutation.ClearReason ?? mutation.ManagementReason ?? "CSV 가져오기에서 설치 지점 등록", null, null, EvidenceSource.Import, origin);
                }
                results.Add(new(number, target.ApId, target.DeviceGenerationId, manualVersion, row.HasChanges));
            }
            return new ImportApplyReceipt(apply.CommandId, apply.PreviewId, false, results.ToImmutable());
        }, value => value with { AlreadyApplied = true }, ct);
    }

    private ImportPreview ValidateStoredPreview(SqliteConnection connection, SqliteTransaction transaction, ImportPreview preview, ImportMapping mapping)
    {
        var entries = mapping.Inventory.ToDictionary(entry => entry.ApId);
        return preview with { Rows = preview.Rows.Select(row =>
        {
            if (!row.IsValid) return row;
            var inventory = entries[row.Target!.ApId].Inventory;
            var values = ManualImportValidator.ApplyValues(inventory, row.Mutation);
            var preferred = row.Mutation.ManualPreferredFields ?? inventory?.ManualPreferredFields ?? [];
            if (preferred.Any(field => !values.ContainsKey(field)))
                row = row with { Errors = row.Errors.Add(new("missing_preferred_value", "수동 우선값이 없습니다. 값을 등록하거나 manual_preferred_fields를 none으로 지정하세요.", row.RowNumber, "manual_preferred_fields")) };
            if (row.Mutation.ManagementStatus == OperationalStatus.MonitoringExcluded &&
                ReadOperationAp(connection, transaction, row.Target.ApId).Stored.Tracking.MonitoringStatus == MonitoringStatus.Ended)
                row = row with { Errors = row.Errors.Add(new("ended_tracking", "종료된 감시 구간은 운영 재개 후에 일시 제외할 수 있습니다.", row.RowNumber, "management_status")) };
            return row;
        }).ToImmutableArray() };
    }

    private ImportMapping BuildImportMapping(SqliteConnection connection, SqliteTransaction transaction, ProfileSnapshot profile, Guid preview)
    {
        var ids = new List<Guid>();
        using (var command = Command(connection, transaction, "SELECT a.ap_id FROM aps a JOIN ap_current c ON c.ap_id=a.ap_id WHERE a.dataset_id=$dataset AND a.instance_id=$instance ORDER BY a.ap_id", ("$dataset", location.DatasetId), ("$instance", profile.InstanceId)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(Guid.Parse(reader.GetString(0)));
        var inventory = ids.Select(id =>
        {
            var ap = ReadOperationAp(connection, transaction, id); var generation = ap.Stored.View.DeviceGenerationId;
            return new ImportInventoryEntry(location.DatasetId, ap.InstanceId, id, generation, ap.Stored.View.Metadata.Identity,
                ap.Stored.View.Fields, ReadInventory(connection, transaction, generation), ReadPolicy(connection, transaction, id), ReadPlacement(connection, transaction, generation));
        }).ToImmutableArray();
        var points = ReadList<InstallationPointVersion>(connection, transaction, "SELECT v.version_json FROM installation_points p JOIN installation_point_versions v ON v.point_id=p.point_id AND v.version=p.current_version WHERE p.dataset_id=$dataset", ("$dataset", location.DatasetId))
            .Select(p => new ImportPointEntry(p.InstallationPointId, p.Version, p.Name)).ToImmutableArray();
        var epoch = CurrentRestoreEpoch(connection, transaction);
        return new(preview, new(location.DatasetId, profile.InstanceId, profile.ScopeVersion, profile.ConfigurationVersion, epoch),
            timeProvider.GetUtcNow().ToUniversalTime(), inventory, points);
    }
}
