using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Operations;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    public Task<OperationCommandReceipt> SaveInventoryAsync(InventoryEdit edit, CancellationToken ct)
        => WriteOperationAsync(edit.CommandId, "inventory", edit, edit.ExpectedRestoreEpoch, (connection, transaction) =>
        {
            var ap = ReadOperationAp(connection, transaction, edit.ApId, edit.ExpectedCurrentGenerationId);
            var current = ReadInventory(connection, transaction, edit.ExpectedCurrentGenerationId);
            RequireVersion(current?.Version ?? 0, edit.ExpectedVersion);
            var mutation = new ImportMutation(edit.SetValues, edit.ClearFields, edit.ManualPreferredFields, null, null, false, null, edit.ClearReason);
            ValidateMutation(mutation);
            var saved = SaveInventory(connection, transaction, ap, mutation, EvidenceSource.Manual, edit.CommandId.ToString("D"));
            return new OperationCommandReceipt(edit.CommandId, saved.Version, false);
        }, value => value with { AlreadyApplied = true }, ct);

    private static void ValidateMutation(ImportMutation mutation)
    {
        if (mutation.SetValues is null || mutation.ClearFields is null) throw new ArgumentException("등록 값과 해제 항목이 필요합니다.");
        var errors = ManualImportValidator.ValidateMutation(mutation);
        if (!errors.IsEmpty) throw new ArgumentException(string.Join(" ", errors.Select(e => e.Message)));
    }
    private ManualInventorySnapshot SaveInventory(SqliteConnection connection, SqliteTransaction transaction, OperationAp ap,
        ImportMutation mutation, EvidenceSource source, string origin)
    {
        var generation = ap.Stored.View.DeviceGenerationId; var apId = ap.Stored.View.ApId;
        var before = ReadInventory(connection, transaction, generation);
        var values = ManualImportValidator.ApplyValues(before, mutation);
        var preferred = mutation.ManualPreferredFields ?? before?.ManualPreferredFields ?? [];
        if (preferred.Any(field => !values.ContainsKey(field))) throw new ArgumentException("수동 우선으로 선택할 등록 값이 없습니다.");
        var at = timeProvider.GetUtcNow().ToUniversalTime();
        var after = new ManualInventorySnapshot((before?.Version ?? 0) + 1, generation, source, at, values, preferred);
        var timeNeedsReview = at < before?.RecordedAtUtc || at < ap.Stored.Tracking.LastProcessedAtUtc;
        Execute(connection, transaction, "INSERT INTO manual_inventory_versions VALUES($gen,$version,$ap,$json)",
            ("$gen", generation), ("$version", after.Version), ("$ap", apId), ("$json", Encode(after)));
        Execute(connection, transaction, "INSERT INTO manual_inventory_current VALUES($gen,$version) ON CONFLICT(generation_id) DO UPDATE SET version=excluded.version", ("$gen", generation), ("$version", after.Version));
        var targets = ApTargets(apId, generation, ReadPlacement(connection, transaction, generation)?.InstallationPointId);
        foreach (var field in (before?.Values.Keys ?? []).Union(values.Keys).Order())
        {
            var oldValue = before?.Values.GetValueOrDefault(field); var newValue = values.GetValueOrDefault(field);
            if (oldValue == newValue) continue;
            var kind = field is InventoryField.Firmware or InventoryField.ControllerId or InventoryField.ControllerName or InventoryField.ControllerIp
                ? OperationHistoryKind.FirmwareController : field is InventoryField.SwitchName or InventoryField.SwitchIp or InventoryField.PortName or InventoryField.ApInterface
                    ? OperationHistoryKind.Connection : OperationHistoryKind.BasicInformation;
            AddEvent(connection, transaction, location.DatasetId, $"command:{origin}:inventory:{field}",
                new(0, kind, at, source, newValue is null ? "ExplicitClear" : oldValue is null ? "BaselineRegistered" : "ValueChanged",
                    field.ToString(), oldValue, newValue, mutation.ClearReason, null, targets, TimeNeedsReview: timeNeedsReview));
        }
        if (!(before?.ManualPreferredFields ?? []).SetEquals(preferred))
            AddEvent(connection, transaction, location.DatasetId, $"command:{origin}:preference",
                new(0, OperationHistoryKind.BasicInformation, at, source, "SourcePreferenceChanged", "manualPreferredFields",
                    string.Join(",", (before?.ManualPreferredFields ?? []).Order()), string.Join(",", preferred.Order()), null, null, targets, TimeNeedsReview: timeNeedsReview));
        return after;
    }

    public Task<OperationCommandReceipt> SaveTrackingPolicyAsync(TrackingPolicyEdit edit, CancellationToken ct)
        => WriteOperationAsync(edit.CommandId, "tracking", edit, edit.ExpectedRestoreEpoch, (connection, transaction) =>
        {
            var ap = ReadOperationAp(connection, transaction, edit.ApId, edit.ExpectedCurrentGenerationId);
            RequireVersion(ReadPolicy(connection, transaction, edit.ApId)?.Version ?? 0, edit.ExpectedVersion);
            var after = SavePolicy(connection, transaction, ap, edit.Status, edit.Reason, edit.Directive, EvidenceSource.Manual, edit.CommandId.ToString("D"));
            return new OperationCommandReceipt(edit.CommandId, after.Version, false);
        }, value => value with { AlreadyApplied = true }, ct);

    private TrackingPolicySnapshot SavePolicy(SqliteConnection connection, SqliteTransaction transaction, OperationAp ap,
        OperationalStatus status, string reason, MonitoringDirective? explicitDirective, EvidenceSource source, string origin)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentException("지원하지 않는 관리 상태입니다.");
        reason = Text(reason, nameof(reason), true);
        var expected = status switch { OperationalStatus.Decommissioned => MonitoringDirective.EndTracking,
            OperationalStatus.MonitoringExcluded => MonitoringDirective.TemporarilyExclude, _ => MonitoringDirective.Observe };
        var directive = explicitDirective ?? expected;
        if (directive != expected && !(status == OperationalStatus.MonitoringExcluded && directive == MonitoringDirective.EndTracking))
            throw new ArgumentException("관리 상태와 추적 지시가 일치하지 않습니다.");
        if (directive == MonitoringDirective.TemporarilyExclude && ap.Stored.Tracking.MonitoringStatus == MonitoringStatus.Ended)
            throw new OperationsConflictException("종료된 감시 구간은 운영 재개 후에 일시 제외할 수 있습니다.");
        var apId = ap.Stored.View.ApId; var before = ReadPolicy(connection, transaction, apId);
        var after = new TrackingPolicySnapshot((before?.Version ?? 0) + 1, status, directive, reason, timeProvider.GetUtcNow().ToUniversalTime());
        Execute(connection, transaction, "INSERT INTO tracking_policy_versions VALUES($ap,$version,$json,$source)", ("$ap", apId), ("$version", after.Version), ("$json", Encode(after)), ("$source", source));
        Execute(connection, transaction, "INSERT INTO tracking_policy_current VALUES($ap,$version) ON CONFLICT(ap_id) DO UPDATE SET version=excluded.version", ("$ap", apId), ("$version", after.Version));
        AddEvent(connection, transaction, location.DatasetId, $"command:{origin}:policy",
            new(0, OperationHistoryKind.WorkAction, after.RecordedAtUtc, source, "TrackingPolicyRegistered", "managementStatus",
                before is null ? null : $"{before.Status}/{before.Directive}", $"{after.Status}/{after.Directive}", reason, null,
                ApTargets(apId, ap.Stored.View.DeviceGenerationId, ReadPlacement(connection, transaction, ap.Stored.View.DeviceGenerationId)?.InstallationPointId),
                TimeNeedsReview: after.RecordedAtUtc < before?.RecordedAtUtc || after.RecordedAtUtc < ap.Stored.Tracking.LastProcessedAtUtc));
        return after;
    }

    public Task<OperationCommandReceipt> SavePointAsync(PointEdit edit, CancellationToken ct)
        => WriteOperationAsync(edit.CommandId, "point", edit, edit.ExpectedRestoreEpoch, (connection, transaction) =>
        {
            RequireId(edit.InstallationPointId, nameof(edit.InstallationPointId));
            var version = Convert.ToInt64(Scalar(connection, transaction, "SELECT current_version FROM installation_points WHERE point_id=$id AND dataset_id=$dataset", ("$id", edit.InstallationPointId), ("$dataset", location.DatasetId)) ?? 0L);
            RequireVersion(version, edit.ExpectedVersion);
            var name = Text(edit.Name, nameof(edit.Name), true, 256); var key = Text(edit.InventoryKey, nameof(edit.InventoryKey), maximum: 256);
            var reason = Text(edit.CorrectionReason, nameof(edit.CorrectionReason), required: version > 0);
            ArgumentNullException.ThrowIfNull(edit.Location);
            if (edit.Location.InstallationPointId is { } target && target != edit.InstallationPointId) throw new ArgumentException("설치 지점 ID가 다릅니다.");
            foreach (var value in new[] { edit.Location.Site, edit.Location.Building, edit.Location.Floor, edit.Location.Zone, edit.Location.Detail,
                edit.Location.AirWaveRawLocation, edit.Location.Folder, edit.Location.Group }) Text(value, nameof(edit.Location));
            if (key.Length > 0 && Scalar(connection, transaction, "SELECT point_id FROM installation_points WHERE dataset_id=$dataset AND inventory_key=$key AND point_id!=$id", ("$dataset", location.DatasetId), ("$key", key), ("$id", edit.InstallationPointId)) is not null)
                throw new OperationsConflictException("같은 대장 키의 설치 지점이 이미 있습니다.");
            var before = version == 0 ? null : ReadPoint(connection, transaction, edit.InstallationPointId);
            var after = new InstallationPointVersion(edit.InstallationPointId, version + 1, name, key.Length == 0 ? null : key,
                edit.Location with { InstallationPointId = edit.InstallationPointId }, timeProvider.GetUtcNow().ToUniversalTime(), reason.Length == 0 ? null : reason);
            Execute(connection, transaction, "INSERT INTO installation_points VALUES($id,$dataset,$key,$version) ON CONFLICT(point_id) DO UPDATE SET inventory_key=excluded.inventory_key,current_version=excluded.current_version", ("$id", edit.InstallationPointId), ("$dataset", location.DatasetId), ("$key", after.InventoryKey), ("$version", after.Version));
            Execute(connection, transaction, "INSERT INTO installation_point_versions VALUES($id,$version,$json)", ("$id", edit.InstallationPointId), ("$version", after.Version), ("$json", Encode(after)));
            AddEvent(connection, transaction, location.DatasetId, $"command:{edit.CommandId}:point",
                new(0, OperationHistoryKind.BasicInformation, after.RecordedAtUtc, EvidenceSource.Manual, version == 0 ? "PointRegistered" : "PointCorrected",
                    "installationPoint", before is null ? null : Encode(before), Encode(after), after.CorrectionReason, null, [new(InstallationPointId: edit.InstallationPointId)], Point: after, TimeNeedsReview: after.RecordedAtUtc < before?.RecordedAtUtc));
            return new OperationCommandReceipt(edit.CommandId, after.Version, false);
        }, value => value with { AlreadyApplied = true }, ct);

    public Task<OperationCommandReceipt> MovePlacementAsync(PlacementEdit edit, CancellationToken ct)
        => WriteOperationAsync(edit.CommandId, "placement", edit, edit.ExpectedRestoreEpoch, (connection, transaction) =>
        {
            var ap = ReadOperationAp(connection, transaction, edit.ApId, edit.ExpectedCurrentGenerationId);
            RequireVersion(PlacementVersion(connection, transaction, edit.ExpectedCurrentGenerationId), edit.ExpectedVersion);
            var version = MovePlacement(connection, transaction, ap, edit.InstallationPointId, edit.ExpectedPointVersion, edit.Reason,
                edit.ActualWorkAtUtc, edit.WorkId, EvidenceSource.Manual, edit.CommandId.ToString("D"));
            return new OperationCommandReceipt(edit.CommandId, version, false);
        }, value => value with { AlreadyApplied = true }, ct);

    private long MovePlacement(SqliteConnection connection, SqliteTransaction transaction, OperationAp ap, Guid? pointId,
        long? expectedPointVersion, string reason, DateTimeOffset? actualWork, Guid? workId, EvidenceSource source, string origin)
    {
        reason = Text(reason, nameof(reason), true);
        InstallationPointVersion? point = pointId is { } id ? ReadPoint(connection, transaction, id) : null;
        if (point is not null) RequireVersion(point.Version, expectedPointVersion ?? -1);
        else if (expectedPointVersion is not null) throw new ArgumentException("배치 해제에는 지점 버전이 없습니다.");
        var generation = ap.Stored.View.DeviceGenerationId; var apId = ap.Stored.View.ApId;
        if (workId is { } work) ValidatePlacementWork(connection, transaction, work, apId, generation);
        var before = ReadPlacement(connection, transaction, generation); var version = PlacementVersion(connection, transaction, generation);
        var at = timeProvider.GetUtcNow().ToUniversalTime();
        var decision = new PlacementPolicy().Move(before, new(Guid.NewGuid(), location.DatasetId, apId, generation,
            pointId, point?.Version, at, actualWork, source, reason, workId));
        if (!decision.Changed) return version;
        if (decision.Closed is { } closed)
        {
            var nextVersion = Convert.ToInt64(Scalar(connection, transaction, "SELECT current_version FROM placements WHERE placement_id=$id", ("$id", closed.PlacementId))) + 1;
            Execute(connection, transaction, "INSERT INTO placement_versions VALUES($id,$version,$json)", ("$id", closed.PlacementId), ("$version", nextVersion), ("$json", Encode(closed)));
            Execute(connection, transaction, "UPDATE placements SET current_version=$version WHERE placement_id=$id", ("$version", nextVersion), ("$id", closed.PlacementId));
        }
        if (decision.Open is { } opened)
        {
            Execute(connection, transaction, "INSERT INTO placements VALUES($id,$ap,$gen,$point,1)", ("$id", opened.PlacementId), ("$ap", apId), ("$gen", generation), ("$point", opened.InstallationPointId));
            Execute(connection, transaction, "INSERT INTO placement_versions VALUES($id,1,$json)", ("$id", opened.PlacementId), ("$json", Encode(opened)));
        }
        Execute(connection, transaction, "INSERT INTO placement_current VALUES($gen,$version,$id) ON CONFLICT(generation_id) DO UPDATE SET version=excluded.version,placement_id=excluded.placement_id",
            ("$gen", generation), ("$version", version + 1), ("$id", decision.Open?.PlacementId));
        var targets = new[] { before?.InstallationPointId, pointId }.Distinct().Select(p => new WorkTarget(apId, generation, InstallationPointId: p)).ToImmutableArray();
        AddEvent(connection, transaction, location.DatasetId, $"command:{origin}:placement",
            new(0, OperationHistoryKind.BasicInformation, at, source, point is null ? "PlacementEnded" : "PlacementRegistered", "installationPoint",
                before?.InstallationPointId.ToString("D"), pointId?.ToString("D"), reason, actualWork, targets,
                Placement: decision.Open ?? decision.Closed, TimeNeedsReview: (decision.Open ?? decision.Closed)?.TimeNeedsReview ?? false));
        return version + 1;
    }

    private void ValidatePlacementWork(SqliteConnection connection, SqliteTransaction transaction, Guid workId, Guid apId, Guid generation)
    {
        var work = ReadWork(connection, transaction, workId);
        if (!work.Content.Targets.Any(t => t.ApId == apId && (t.DeviceGenerationId is null || t.DeviceGenerationId == generation)))
            throw new ArgumentException("배치 작업이 해당 장비를 대상으로 하지 않습니다.");
        if (work.Content.Status == WorkStatus.Cancelled) throw new ArgumentException("취소된 작업은 배치 근거로 연결할 수 없습니다.");
    }

    public Task<OperationCommandReceipt> SaveWorkAsync(WorkEdit edit, CancellationToken ct)
        => WriteOperationAsync(edit.CommandId, "work", edit, edit.ExpectedRestoreEpoch, (connection, transaction) =>
        {
            RequireId(edit.WorkId, nameof(edit.WorkId));
            var version = Convert.ToInt64(Scalar(connection, transaction, "SELECT current_version FROM work_items WHERE work_id=$id AND dataset_id=$dataset", ("$id", edit.WorkId), ("$dataset", location.DatasetId)) ?? 0L);
            RequireVersion(version, edit.ExpectedVersion);
            var correction = Text(edit.CorrectionReason, nameof(edit.CorrectionReason), required: version > 0);
            var content = ValidateWork(connection, transaction, edit.Content);
            var at = timeProvider.GetUtcNow().ToUniversalTime();
            var before = version == 0 ? null : ReadWork(connection, transaction, edit.WorkId);
            var after = new WorkVersion(edit.WorkId, version + 1, location.DatasetId, at, content, correction.Length == 0 ? null : correction);
            Execute(connection, transaction, "INSERT INTO work_items VALUES($id,$dataset,$version) ON CONFLICT(work_id) DO UPDATE SET current_version=excluded.current_version", ("$id", edit.WorkId), ("$dataset", location.DatasetId), ("$version", after.Version));
            Execute(connection, transaction, "INSERT INTO work_versions VALUES($id,$version,$json)", ("$id", edit.WorkId), ("$version", after.Version), ("$json", Encode(after)));
            for (var i = 0; i < content.Targets.Length; i++)
            {
                var target = content.Targets[i];
                Execute(connection, transaction, "INSERT INTO work_targets VALUES($id,$version,$i,$ap,$gen,$incident,$point)",
                    ("$id", edit.WorkId), ("$version", after.Version), ("$i", i), ("$ap", target.ApId), ("$gen", target.DeviceGenerationId), ("$incident", target.IncidentId), ("$point", target.InstallationPointId));
            }
            AddEvent(connection, transaction, location.DatasetId, $"command:{edit.CommandId}:work",
                new(0, OperationHistoryKind.WorkAction, at, EvidenceSource.Manual, version == 0 ? "WorkRegistered" : content.Status == WorkStatus.Cancelled ? "WorkCancelled" : "WorkCorrected",
                    "work", before?.Content.Content, content.Content, after.CorrectionReason, content.ActualWorkAtUtc, content.Targets,
                    Work: after, TimeNeedsReview: at < before?.RecordedAtUtc || content.ActualWorkAtUtc > at));
            return new OperationCommandReceipt(edit.CommandId, after.Version, false);
        }, value => value with { AlreadyApplied = true }, ct);

    private WorkVersion ReadWork(SqliteConnection connection, SqliteTransaction transaction, Guid workId)
        => Optional<WorkVersion>(connection, transaction, "SELECT v.version_json FROM work_items w JOIN work_versions v ON v.work_id=w.work_id AND v.version=w.current_version WHERE w.work_id=$id AND w.dataset_id=$dataset", ("$id", workId), ("$dataset", location.DatasetId)) ?? throw new KeyNotFoundException("등록된 작업이 없습니다.");
    private WorkContent ValidateWork(SqliteConnection connection, SqliteTransaction transaction, WorkContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!Enum.IsDefined(content.Status) || !Enum.IsDefined(content.CauseStatus) || content.Targets.IsDefaultOrEmpty || content.Targets.Length > 100 || content.Targets.Distinct().Count() != content.Targets.Length)
            throw new ArgumentException("작업 상태와 중복 없는 대상 1~100개를 확인하세요.");
        if (content.PlannedStartAtUtc is { } start && content.PlannedEndAtUtc is { } end && end < start)
            throw new ArgumentException("예정 종료가 예정 시작보다 빠릅니다.");
        foreach (var target in content.Targets) ValidateTarget(connection, transaction, target);
        return content with { WorkType = Text(content.WorkType, nameof(content.WorkType), true, 128),
            Content = Text(content.Content, nameof(content.Content), true), Result = Text(content.Result, nameof(content.Result)),
            Notes = Text(content.Notes, nameof(content.Notes)), Worker = Text(content.Worker, nameof(content.Worker), maximum: 256),
            CauseDetail = Text(content.CauseDetail, nameof(content.CauseDetail), required: content.CauseStatus == CauseStatus.Confirmed) };
    }
    private void ValidateTarget(SqliteConnection connection, SqliteTransaction transaction, WorkTarget target)
    {
        if (target == new WorkTarget()) throw new ArgumentException("작업 대상을 지정하세요.");
        if (target.DeviceGenerationId is not null && target.ApId is null || target.IncidentId is not null && (target.ApId is null || target.DeviceGenerationId is null))
            throw new ArgumentException("세대와 사건에는 정확한 AP·세대 참조가 필요합니다.");
        if (target.ApId is { } apId)
        {
            RequireId(apId, nameof(target.ApId));
            if (Scalar(connection, transaction, "SELECT ap_id FROM aps WHERE ap_id=$id AND dataset_id=$dataset", ("$id", apId), ("$dataset", location.DatasetId)) is null) throw new KeyNotFoundException("대상 AP가 없습니다.");
        }
        if (target.DeviceGenerationId is { } generation && Scalar(connection, transaction, "SELECT generation_id FROM generations WHERE generation_id=$gen AND ap_id=$ap", ("$gen", generation), ("$ap", target.ApId)) is null)
            throw new ArgumentException("대상 장비 세대와 AP의 관계가 다릅니다.");
        if (target.IncidentId is { } incident && Scalar(connection, transaction, "SELECT incident_id FROM incidents WHERE incident_id=$id AND ap_id=$ap AND generation_id=$gen", ("$id", incident), ("$ap", target.ApId), ("$gen", target.DeviceGenerationId)) is null)
            throw new ArgumentException("대상 사건·장비 세대·AP의 관계가 다릅니다.");
        if (target.InstallationPointId is { } point) _ = ReadPoint(connection, transaction, point);
    }
}
