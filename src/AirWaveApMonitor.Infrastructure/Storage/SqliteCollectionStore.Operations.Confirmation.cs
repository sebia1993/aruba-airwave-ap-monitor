using AirWaveApMonitor.Core.Contracts;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    public Task<OperationCommandReceipt> ConfirmReplacementAsync(ReplacementConfirmationEdit edit, CancellationToken ct)
        => WriteOperationAsync(edit.CommandId, "replacement-confirmation", edit, edit.ExpectedRestoreEpoch, (connection, transaction) =>
        {
            if (edit.ReplacementEventId <= 0) throw new ArgumentException("기존 자동 교체 감지 ID가 필요합니다.");
            var source = Optional<OperationEventRecord>(connection, transaction, "SELECT event_json FROM operation_events WHERE event_id=$id AND dataset_id=$dataset",
                ("$id", edit.ReplacementEventId), ("$dataset", location.DatasetId)) ?? throw new KeyNotFoundException("자동 교체 감지 이력이 없습니다.");
            var change = source.AutomaticChange;
            if (source.Kind != OperationHistoryKind.HardwareReplacement || change?.Semantics != ChangeSemantics.ReplacementDetected ||
                change.PreviousDeviceGenerationId != edit.PreviousDeviceGenerationId || change.DeviceGenerationId != edit.DeviceGenerationId)
                throw new ArgumentException("기존 자동 교체 감지와 이전/새 장비 세대가 일치해야 합니다.");
            var work = ReadWork(connection, transaction, edit.WorkId); RequireVersion(work.Version, edit.ExpectedWorkVersion);
            if (work.Content.Status != WorkStatus.Completed ||
                !work.Content.Targets.Any(t => t.ApId == change.ApId && t.DeviceGenerationId == edit.PreviousDeviceGenerationId) ||
                !work.Content.Targets.Any(t => t.ApId == change.ApId && t.DeviceGenerationId == edit.DeviceGenerationId))
                throw new ArgumentException("완료된 작업의 정확한 이전/새 장비 대상이 필요합니다.");
            if (Scalar(connection, transaction, "SELECT confirmation_event_id FROM replacement_confirmations WHERE replacement_event_id=$id", ("$id", edit.ReplacementEventId)) is not null)
                throw new OperationsConflictException("이미 별도 교체 확인 이력이 있습니다. 연결된 작업 정정 이력을 사용하세요.");
            if (edit.ActualWorkAtUtc is { } supplied && work.Content.ActualWorkAtUtc is { } recorded && supplied != recorded)
                throw new ArgumentException("연결 작업의 실제 작업 시각과 다릅니다.");
            var at = timeProvider.GetUtcNow().ToUniversalTime();
            var confirmation = new ConfirmedReplacement(edit.ReplacementEventId, edit.PreviousDeviceGenerationId,
                edit.DeviceGenerationId, edit.WorkId, work.Version, Text(edit.Reason, nameof(edit.Reason), true),
                edit.ActualWorkAtUtc ?? work.Content.ActualWorkAtUtc, at);
            var id = AddEvent(connection, transaction, location.DatasetId, $"command:{edit.CommandId}:replacement-confirmed",
                new(0, OperationHistoryKind.HardwareReplacement, at, EvidenceSource.Manual, "ReplacementConfirmed", "hardwareReplacement",
                    change.BeforeValue, change.AfterValue, confirmation.Reason, confirmation.ActualWorkAtUtc, source.Targets,
                    Work: work, TimeNeedsReview: at < source.RecordedAtUtc || confirmation.ActualWorkAtUtc > at, ReplacementConfirmation: confirmation));
            Execute(connection, transaction, "INSERT INTO replacement_confirmations VALUES($source,$confirmation,$work,$version)",
                ("$source", edit.ReplacementEventId), ("$confirmation", id), ("$work", edit.WorkId), ("$version", work.Version));
            return new OperationCommandReceipt(edit.CommandId, id, false);
        }, value => value with { AlreadyApplied = true }, ct);
}
