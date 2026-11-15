using System.Collections.Immutable;

namespace AirWaveApMonitor.Core.Contracts;

public enum ApState
{
    Up, Down
}
public enum FieldAvailability
{
    Provided, NotProvided, Unsupported, Unverified
}
public enum FieldValidity
{
    Valid, Invalid, Unknown
}
public enum SourceFreshness
{
    Current, Delayed, Unknown
}
public enum ObservationCompleteness
{
    Complete, Partial, Unknown
}
public enum EvidenceSource
{
    AirWave, Demo, Manual, Import
}

public sealed record FieldEvidence(FieldAvailability Availability, FieldValidity Validity, EvidenceSource Source,
    DateTimeOffset CollectedAtUtc, DateTimeOffset? SourceAtUtc = null, SourceFreshness Freshness = SourceFreshness.Unknown);

public sealed record DeviceIdentity(string? SerialNumber, string? WiredMac);
public sealed record DeviceDetails(string? Name, string? IpAddress, string? Model, string? Firmware,
    TimeSpan? Uptime = null, DateTimeOffset? RebootedAtUtc = null);
public sealed record LocationDetails(string? Site = null, string? Building = null, string? Floor = null,
    string? Zone = null, string? Detail = null, Guid? InstallationPointId = null,
    string? AirWaveRawLocation = null, string? Folder = null, string? Group = null);
public sealed record ControllerDetails(string? SourceId = null, string? Name = null, string? IpAddress = null);
public sealed record SourceManagementDetails(bool? MaintenanceMode = null, string? ManagementStatus = null);
/// <summary>AP-list upstream reference is not proof of a current physical link, LLDP or power state.</summary>
public sealed record SourceUpstreamReference(string? SourceSwitchId, string? PortIndex, string? PortName,
    string? ApInterface, FieldEvidence Evidence);
public enum OperationalStatus
{
    Operating, PlannedWork, Decommissioned, MonitoringExcluded
}
public sealed record ManualManagementDetails(OperationalStatus Status, string Reason, DateTimeOffset RecordedAtUtc);

/// <summary>Identity conflicts exclude the entire source record; null state cannot drive a transition.</summary>
public sealed record ApObservation(string SourceApId, ApState? State, DeviceIdentity Identity, DeviceDetails Device,
    LocationDetails Location, ControllerDetails Controller, DateTimeOffset CollectedAtUtc,
    DateTimeOffset? SourceAtUtc, DateTimeOffset? LastContactedAtUtc, ImmutableDictionary<string, FieldEvidence> Fields,
    SourceManagementDetails? SourceManagement = null, SourceUpstreamReference? Upstream = null, ImmutableDictionary<string, string>? RawSourceValues = null)
{
    public int SchemaVersion => 1;
}

// Detailed connection/power types are extended in Task 9. Every value already carries its own evidence.
public sealed record ObservedValue(string? Text, decimal? Number, string? Unit, FieldEvidence Evidence);
public sealed record EnrichmentObservation(string SourceEntityId, string? SourceApId, Guid? DeviceGenerationId,
    Guid? MonitoringIntervalId, string? SourceInterfaceId, DateTimeOffset CollectedAtUtc,
    DateTimeOffset? SourceAtUtc, ImmutableDictionary<string, ObservedValue> Values);
public sealed record EnrichmentBatch(CollectionKind Kind, RunOutcome Outcome,
    ImmutableArray<EnrichmentObservation> Observations, ImmutableArray<ExcludedRecord> ExcludedRecords,
    ObservationCompleteness Completeness, DateTimeOffset CollectedAtUtc, ImmutableArray<CollectionError> Errors)
{
    public int SchemaVersion => 1;
    public EnrichmentPayload? Detail { get; init; }
}
