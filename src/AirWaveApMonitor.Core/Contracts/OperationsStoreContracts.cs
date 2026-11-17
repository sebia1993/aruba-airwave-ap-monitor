using System.Collections.Immutable;

namespace AirWaveApMonitor.Core.Contracts;

/// <summary>Restore boundary captured before reading the entity data for a new edit draft.</summary>
public sealed record OperationsVersionSnapshot(long RestoreEpoch);
/// <summary>Current registration is independent from an explicitly selected historical AP run.</summary>
public sealed record RegistrationSnapshot(CurrentRegistration Registration, long PlacementVersion,
    DeviceMetadata Automatic, ImmutableDictionary<string, FieldEvidence> AutomaticFields,
    long AutomaticRunId, DateTimeOffset AutomaticObservedAtUtc);
public sealed record TrackingPolicyEdit(Guid CommandId, Guid ApId, Guid ExpectedCurrentGenerationId,
    long ExpectedVersion, OperationalStatus Status, string Reason, MonitoringDirective? Directive = null, long ExpectedRestoreEpoch = 0);
public sealed record PointEdit(Guid CommandId, Guid InstallationPointId, long ExpectedVersion, string Name,
    string? InventoryKey, LocationDetails Location, string? CorrectionReason = null, long ExpectedRestoreEpoch = 0);
public sealed record PlacementEdit(Guid CommandId, Guid ApId, Guid ExpectedCurrentGenerationId, long ExpectedVersion,
    Guid? InstallationPointId, long? ExpectedPointVersion, string Reason, DateTimeOffset? ActualWorkAtUtc = null,
    Guid? WorkId = null, long ExpectedRestoreEpoch = 0);
public sealed record ReplacementConfirmationEdit(Guid CommandId, long ReplacementEventId, Guid PreviousDeviceGenerationId,
    Guid DeviceGenerationId, Guid WorkId, long ExpectedWorkVersion, string Reason, DateTimeOffset? ActualWorkAtUtc = null,
    long ExpectedRestoreEpoch = 0);
public sealed record ConfirmedReplacement(long ReplacementEventId, Guid PreviousDeviceGenerationId, Guid DeviceGenerationId,
    Guid WorkId, long WorkVersion, string Reason, DateTimeOffset? ActualWorkAtUtc, DateTimeOffset RecordedAtUtc);
public sealed record PointSnapshot(InstallationPointVersion Current, ImmutableArray<InstallationPointVersion> Versions,
    ImmutableArray<PlacementPeriod> Placements, ImmutableHashSet<Guid>? CurrentDeviceGenerationIds = null);
public sealed record WorkSnapshot(WorkVersion Current, ImmutableArray<WorkVersion> Versions);
public sealed record PointListQuery(string? Search = null, int Page = 1, int PageSize = 50);
public sealed record PointPage(ImmutableArray<InstallationPointVersion> Rows, int TotalRows, int Page, int PageSize);
public sealed record OperationsTimelineQuery(Guid? ApId = null, Guid? DeviceGenerationId = null,
    Guid? InstallationPointId = null, OperationHistoryKind? Kind = null, DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null, long? ThroughEventId = null, int Page = 1, int PageSize = 50);
public sealed record OperationEventRecord(long EventId, OperationHistoryKind Kind, DateTimeOffset RecordedAtUtc,
    EvidenceSource? Source, string Action, string Field, string? BeforeValue, string? AfterValue,
    string? Reason, DateTimeOffset? ActualWorkAtUtc, ImmutableArray<WorkTarget> Targets,
    long? RunId = null, ChangeEvent? AutomaticChange = null, WorkVersion? Work = null,
    PlacementPeriod? Placement = null, InstallationPointVersion? Point = null,
    IncidentTrackingSnapshot? Incident = null, bool TimeNeedsReview = false, ConfirmedReplacement? ReplacementConfirmation = null);
public sealed record OperationsTimelinePage(ImmutableArray<OperationEventRecord> Rows, int TotalRows,
    long ThroughEventId, int Page, int PageSize);

public sealed class OperationsConflictException(string message) : InvalidOperationException(message);

public interface IOperationsStore
{
    Task<OperationsVersionSnapshot> GetVersionAsync(CancellationToken ct);
    Task<RegistrationSnapshot> GetRegistrationAsync(Guid apId, CancellationToken ct);
    Task<OperationCommandReceipt> SaveInventoryAsync(InventoryEdit edit, CancellationToken ct);
    Task<OperationCommandReceipt> SaveTrackingPolicyAsync(TrackingPolicyEdit edit, CancellationToken ct);
    Task<OperationCommandReceipt> SavePointAsync(PointEdit edit, CancellationToken ct);
    Task<OperationCommandReceipt> MovePlacementAsync(PlacementEdit edit, CancellationToken ct);
    Task<OperationCommandReceipt> SaveWorkAsync(WorkEdit edit, CancellationToken ct);
    Task<OperationCommandReceipt> ConfirmReplacementAsync(ReplacementConfirmationEdit edit, CancellationToken ct);
    Task<PointPage> GetPointsAsync(PointListQuery query, CancellationToken ct);
    Task<PointSnapshot> GetPointAsync(Guid pointId, CancellationToken ct);
    Task<WorkSnapshot> GetWorkAsync(Guid workId, CancellationToken ct);
    Task<OperationsTimelinePage> GetTimelineAsync(OperationsTimelineQuery query, CancellationToken ct);
    Task<ImportPreview> PreviewImportAsync(ReadOnlyMemory<byte> csv, ProfileSnapshot currentProfile, CancellationToken ct);
    Task<ImportApplyReceipt> ApplyImportAsync(ImportApply apply, ProfileSnapshot currentProfile, CancellationToken ct);
}
