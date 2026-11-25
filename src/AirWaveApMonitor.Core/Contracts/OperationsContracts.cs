using System.Collections.Immutable;

namespace AirWaveApMonitor.Core.Contracts;

public enum OperationHistoryKind { Incident, BasicInformation, Connection, HardwareReplacement, FirmwareController, WorkAction }
public enum ChangeSemantics { BaselineRegistered, ValueChanged, ExplicitClear, SourcePreferenceChanged, ReplacementDetected, ReplacementConfirmed }

/// <summary>Only accepted automatic channels belong here. Source timestamps remain in each field's evidence.</summary>
public sealed record ObservationEvidence(Guid DatasetId, Guid InstanceId, Guid ApId, Guid DeviceGenerationId,
    long RunId, long ScopeVersion, DateTimeOffset ObservedAtUtc,
    ImmutableDictionary<string, FieldEvidence> BeforeFields, ImmutableDictionary<string, FieldEvidence> AfterFields,
    Guid? PreviousDeviceGenerationId = null, IdentityChange IdentityChange = IdentityChange.Unchanged,
    bool HasObservationGap = false, bool TimeNeedsReview = false);

/// <summary>Connection category with an upstream_* field is an AP-list reference, not a verified physical/LLDP link.</summary>
public sealed record ChangeEvent(OperationHistoryKind Kind, ChangeSemantics Semantics, string Field,
    string? BeforeValue, string? AfterValue, Guid DatasetId, Guid InstanceId, Guid ApId,
    Guid DeviceGenerationId, Guid? PreviousDeviceGenerationId, long RunId, long ScopeVersion,
    DateTimeOffset ObservedAtUtc, FieldEvidence? BeforeEvidence, FieldEvidence? AfterEvidence,
    bool HasObservationGap, bool TimeNeedsReview, bool NeedsConfirmation = false)
{
    public bool IsChangedValue => Semantics == ChangeSemantics.ValueChanged;
}

/// <summary>Manual descriptions never replace automatic identity, AP state, LLDP or power evidence.</summary>
public enum InventoryField
{
    Name, IpAddress, Model, Firmware, Site, Building, Floor, Zone, Detail, RawLocation, Folder, Group,
    ControllerId, ControllerName, ControllerIp, SwitchName, SwitchIp, PortName, ApInterface, AssetTag, Notes, FollowUp
}

public sealed record ManualInventorySnapshot(long Version, Guid DeviceGenerationId, EvidenceSource Source,
    DateTimeOffset RecordedAtUtc, ImmutableDictionary<InventoryField, string> Values,
    ImmutableHashSet<InventoryField> ManualPreferredFields, PlacementReference? Placement = null);
public sealed record PlacementReference(Guid PlacementId, Guid InstallationPointId, long PointVersion);
public sealed record TrackingPolicySnapshot(long Version, OperationalStatus Status, MonitoringDirective Directive,
    string Reason, DateTimeOffset RecordedAtUtc);

public sealed record InstallationPointVersion(Guid InstallationPointId, long Version, string Name,
    string? InventoryKey, LocationDetails Location, DateTimeOffset RecordedAtUtc, string? CorrectionReason = null);

/// <summary>Recorded dates are registration facts; optional actual dates are operator statements, not inferred observations.</summary>
public sealed record PlacementPeriod(Guid PlacementId, Guid DatasetId, Guid ApId, Guid DeviceGenerationId,
    Guid InstallationPointId, long PointVersion, DateTimeOffset RecordedStartAtUtc, DateTimeOffset? ActualStartAtUtc,
    EvidenceSource Source, Guid? WorkId = null, DateTimeOffset? RecordedEndAtUtc = null,
    DateTimeOffset? ActualEndAtUtc = null, string? EndReason = null, bool TimeNeedsReview = false);
public sealed record PlacementRequest(Guid NewPlacementId, Guid DatasetId, Guid ApId, Guid DeviceGenerationId,
    Guid? InstallationPointId, long? PointVersion, DateTimeOffset RecordedAtUtc, DateTimeOffset? ActualWorkAtUtc,
    EvidenceSource Source, string Reason, Guid? WorkId = null);
public sealed record PlacementDecision(PlacementPeriod? Closed, PlacementPeriod? Open, bool Changed);

/// <summary>Current registrations require an exact current generation; historical work targets deliberately do not.</summary>
public sealed record CurrentRegistration(Guid DatasetId, Guid ApId, Guid CurrentDeviceGenerationId,
    ManualInventorySnapshot? Inventory, TrackingPolicySnapshot? TrackingPolicy, PlacementPeriod? Placement,
    long? LastAppliedRunId, bool PendingNextCollection);
public sealed record InventoryEdit(Guid CommandId, Guid ApId, Guid ExpectedCurrentGenerationId, long ExpectedVersion,
    ImmutableDictionary<InventoryField, string> SetValues, ImmutableHashSet<InventoryField> ClearFields,
    ImmutableHashSet<InventoryField>? ManualPreferredFields = null, string? ClearReason = null, long ExpectedRestoreEpoch = 0);

public enum WorkStatus { Planned, Completed, Cancelled }
public enum CauseStatus { Unknown, Investigating, Confirmed }
/// <summary>Store validates exact dataset/AP/generation/incident relationships; never retarget old work to current hardware.</summary>
public sealed record WorkTarget(Guid? ApId = null, Guid? DeviceGenerationId = null, Guid? IncidentId = null,
    Guid? InstallationPointId = null);
public sealed record WorkContent(string WorkType, WorkStatus Status, string Content, string? Result, string? Notes,
    CauseStatus CauseStatus, string? CauseDetail, DateTimeOffset? PlannedStartAtUtc, DateTimeOffset? PlannedEndAtUtc,
    DateTimeOffset? ActualWorkAtUtc, string? Worker, ImmutableArray<WorkTarget> Targets);
public sealed record WorkVersion(Guid WorkId, long Version, Guid DatasetId, DateTimeOffset RecordedAtUtc,
    WorkContent Content, string? CorrectionReason);
public sealed record WorkEdit(Guid CommandId, Guid WorkId, long ExpectedVersion, WorkContent Content, string? CorrectionReason,
    long ExpectedRestoreEpoch = 0);
public sealed record OperationCommandReceipt(Guid CommandId, long Version, bool AlreadyApplied);

/// <summary>Scope/configuration and restore epoch are checked again atomically at apply, alongside all entity versions.</summary>
public sealed record ImportVersionStamp(Guid DatasetId, Guid InstanceId, long ScopeVersion, long ConfigVersion, long RestoreEpoch);
public sealed record ImportInventoryEntry(Guid DatasetId, Guid InstanceId, Guid ApId, Guid DeviceGenerationId,
    DeviceIdentity Identity, ImmutableDictionary<string, FieldEvidence> IdentityEvidence,
    ManualInventorySnapshot? Inventory = null, TrackingPolicySnapshot? TrackingPolicy = null, PlacementPeriod? Placement = null);
public sealed record ImportPointEntry(Guid InstallationPointId, long Version, string Name);
public sealed record ImportLimits(int MaxBytes = 4 * 1024 * 1024, int MaxRows = 5000, int MaxCellCharacters = 4096);
public sealed record ImportMapping(Guid PreviewId, ImportVersionStamp Version, DateTimeOffset CreatedAtUtc,
    ImmutableArray<ImportInventoryEntry> Inventory, ImmutableArray<ImportPointEntry> InstallationPoints,
    ImportLimits? Limits = null);
public sealed record ImportSelector(Guid? ApId, string? SerialNumber, string? WiredMac);
public sealed record ImportTargetBinding(Guid ApId, Guid DeviceGenerationId, long ManualVersion, long TrackingVersion,
    Guid? PlacementId, Guid? CurrentPointId, long? CurrentPointVersion, Guid? RequestedPointId, long? RequestedPointVersion);
public sealed record ImportMutation(ImmutableDictionary<InventoryField, string> SetValues,
    ImmutableHashSet<InventoryField> ClearFields, ImmutableHashSet<InventoryField>? ManualPreferredFields,
    OperationalStatus? ManagementStatus, string? ManagementReason, bool ChangesPlacement, Guid? InstallationPointId,
    string? ClearReason);
public sealed record ImportDifference(string Field, string? BeforeValue, string? AfterValue, bool IsExplicitClear = false);
public sealed record ImportProblem(string Code, string Message, int? RowNumber = null, string? Column = null);
public sealed record ImportPreviewRow(int RowNumber, ImportSelector Selector, ImportTargetBinding? Target,
    ImportMutation Mutation, ImmutableArray<ImportDifference> Differences, ImmutableArray<ImportProblem> Errors)
{
    public bool IsValid => Target is not null && Errors.IsEmpty;
    public bool HasChanges => !Differences.IsEmpty;
}
/// <summary>Server must retain this exact preview; apply accepts row IDs, never client replacement values.</summary>
public sealed record ImportPreview(Guid PreviewId, ImportVersionStamp Version, DateTimeOffset CreatedAtUtc,
    ImmutableArray<ImportPreviewRow> Rows, ImmutableArray<ImportProblem> Errors)
{
    public bool IsValidDocument => Errors.IsEmpty;
}
public sealed record ImportApply(Guid CommandId, Guid PreviewId, ImportVersionStamp ExpectedVersion, ImmutableArray<int> RowNumbers);
public sealed record ImportRowResult(int RowNumber, Guid ApId, Guid DeviceGenerationId, long ManualVersion, bool Applied);
public sealed record ImportApplyReceipt(Guid CommandId, Guid PreviewId, bool AlreadyApplied, ImmutableArray<ImportRowResult> Rows);
