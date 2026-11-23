using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AirWaveApMonitor.Core.Contracts;

/// <summary>Values change only when source metadata changes; observation clocks live separately.</summary>
public sealed record DeviceMetadata(DeviceIdentity Identity, DeviceDetails Device, LocationDetails Location,
    ControllerDetails Controller, SourceManagementDetails? SourceManagement = null,
    ManualManagementDetails? ManualManagement = null, UpstreamMetadata? Upstream = null,
    ImmutableDictionary<string, string>? RawSourceValues = null, ManualInventorySnapshot? ManualInventory = null);

public sealed record UpstreamMetadata(string? SourceSwitchId, string? PortIndex, string? PortName, string? ApInterface);

/// <summary>The device values already accepted inside the AP transaction, with original evidence times.</summary>
public sealed record CommittedDeviceView(Guid ApId, string SourceApId, Guid DeviceGenerationId,
    Guid MonitoringIntervalId, DeviceMetadata Metadata, ImmutableDictionary<string, FieldEvidence> Fields,
    DateTimeOffset? SourceAtUtc = null, DateTimeOffset? LastContactedAtUtc = null,
    ImmutableArray<EnrichmentObservation> Enrichment = default)
{
    public EnrichmentSnapshot? EnrichmentDetail { get; init; }
}

/// <summary>Immutable detection or recovery information, never rebuilt from current AP tables.</summary>
public sealed record IncidentSnapshot(Guid DatasetId, Guid InstanceId, long ScopeVersion, long RunId,
    DateTimeOffset RecordedAtUtc, CommittedDeviceView Device)
{
    public int SchemaVersion => 1;
}

public sealed record CollectionSuccessTimes(long? LastValidRunId, DateTimeOffset? LastValidCommittedAtUtc,
    long? LastCompleteRunId, DateTimeOffset? LastCompleteCommittedAtUtc);

public sealed record ApRunSourceEvidence(int ParsedApCount, int ValidStateCount, int AppliedStateCount,
    ImmutableArray<ExcludedRecord> ExcludedRecords, IdentityBatchConflicts IdentityConflicts)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunOutcome? SourceOutcome { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObservationCompleteness? SourceCompleteness { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ScopeExcludedApCount { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ScopeExcludedRecordCount { get; init; }
    /// <summary>Last committed operation event visible in the AP finalization transaction.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OperationsThroughEventId { get; init; }
}
