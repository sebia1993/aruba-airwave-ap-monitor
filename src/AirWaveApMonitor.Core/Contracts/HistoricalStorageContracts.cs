using System.Collections.Immutable;

namespace AirWaveApMonitor.Core.Contracts;

/// <summary>Values change only when source metadata changes; observation clocks live separately.</summary>
public sealed record DeviceMetadata(DeviceIdentity Identity, DeviceDetails Device, LocationDetails Location,
    ControllerDetails Controller, SourceManagementDetails? SourceManagement = null,
    ManualManagementDetails? ManualManagement = null, UpstreamMetadata? Upstream = null,
    ImmutableDictionary<string, string>? RawSourceValues = null);

public sealed record UpstreamMetadata(string? SourceSwitchId, string? PortIndex, string? PortName, string? ApInterface);

/// <summary>The device values already accepted inside the AP transaction, with original evidence times.</summary>
public sealed record CommittedDeviceView(Guid ApId, string SourceApId, Guid DeviceGenerationId,
    Guid MonitoringIntervalId, DeviceMetadata Metadata, ImmutableDictionary<string, FieldEvidence> Fields,
    DateTimeOffset? SourceAtUtc = null, DateTimeOffset? LastContactedAtUtc = null,
    ImmutableArray<EnrichmentObservation> Enrichment = default);

/// <summary>Immutable detection or recovery information, never rebuilt from current AP tables.</summary>
public sealed record IncidentSnapshot(Guid DatasetId, Guid InstanceId, long ScopeVersion, long RunId,
    DateTimeOffset RecordedAtUtc, CommittedDeviceView Device)
{
    public int SchemaVersion => 1;
}

public sealed record CollectionSuccessTimes(long? LastValidRunId, DateTimeOffset? LastValidCommittedAtUtc,
    long? LastCompleteRunId, DateTimeOffset? LastCompleteCommittedAtUtc);

public sealed record ApRunSourceEvidence(int ParsedApCount, int ValidStateCount, int AppliedStateCount,
    ImmutableArray<ExcludedRecord> ExcludedRecords, IdentityBatchConflicts IdentityConflicts);
