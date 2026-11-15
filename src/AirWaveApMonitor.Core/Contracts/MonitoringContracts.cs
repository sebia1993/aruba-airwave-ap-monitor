using System.Collections.Immutable;

namespace AirWaveApMonitor.Core.Contracts;

/// <summary>Canonical field names emitted by the AP-list adapter.</summary>
public static class MonitoringFieldNames
{
    public const string SerialNumber = "serial_number";
    public const string WiredMac = "lan_mac";
    public const string State = "is_up";
}

public enum IdentityChange { Unchanged, Enriched, FirstGeneration, Replaced, Unattributable }
public enum MonitoringStatus { Active, Paused, Ended }
public enum MonitoringDirective { Observe, TemporarilyExclude, EndTracking }
public enum MissingObservationReason { MissingRecord, UnknownState, DuplicateIdentity, CollectionFailed, Interrupted, ScopeExcluded, TrackingEnded }
public enum IncidentMutationKind { Created, Updated, Recovered, TrackingEnded }

/// <summary>Only values with Provided/Valid evidence participate in identity comparison.</summary>
public sealed record ApIdentitySnapshot(Guid InstanceId, Guid DatasetId, string SourceApId,
    Guid? DeviceGenerationId, DeviceIdentity Identity, ImmutableDictionary<string, FieldEvidence> Fields);

public sealed record IdentityDecision(IdentityChange Change, DeviceIdentity Identity,
    ImmutableDictionary<string, FieldEvidence> Fields, bool ReplacementNeedsConfirmation,
    bool WiredMacChanged)
{
    public bool StartsNewGeneration => Change is IdentityChange.FirstGeneration or IdentityChange.Replaced;
}

public sealed record IdentityBatchConflicts(ImmutableHashSet<string> DuplicateApIds,
    ImmutableDictionary<string, ImmutableArray<string>> SerialCollisions);

public sealed record ObservationGap(long FirstRunId, DateTimeOffset StartedAtUtc,
    MissingObservationReason Reason, Guid? IncidentId, long? ResumedRunId = null,
    DateTimeOffset? ResumedAtUtc = null, long? TrackingEndedRunId = null,
    DateTimeOffset? TrackingEndedAtUtc = null, string? TrackingEndReason = null);

/// <summary>Confirmed observation times are not actual outage start/recovery times.</summary>
public sealed record IncidentTrackingSnapshot(Guid? IncidentId, Guid? DeviceGenerationId, Guid? MonitoringIntervalId,
    long FirstDownRunId, DateTimeOffset FirstDownAtUtc, long LastDownRunId, DateTimeOffset LastDownAtUtc,
    DateTimeOffset? LastUpAtUtc, bool IsInitialDown, bool HasObservationGap = false,
    long? RecoveredRunId = null, DateTimeOffset? RecoveredAtUtc = null,
    long? TrackingEndedRunId = null, DateTimeOffset? TrackingEndedAtUtc = null,
    string? TrackingEndReason = null, bool TimeNeedsReview = false)
{
    public bool ActualStartUnknown => true;
    public bool ActualRecoveryUnknown => RecoveredAtUtc is not null;
    public TimeSpan? ConfirmedElapsed => TimeNeedsReview ||
        (RecoveredAtUtc ?? LastDownAtUtc) < FirstDownAtUtc
        ? null
        : (RecoveredAtUtc ?? LastDownAtUtc) - FirstDownAtUtc;
}

/// <summary>
/// Restored saved state. Null IDs in decisions are creation intentions: storage must allocate and
/// bind generation, interval and incident references atomically before persisting a snapshot.
/// </summary>
public sealed record ApTrackingSnapshot(ApIdentitySnapshot Identity, Guid? MonitoringIntervalId,
    MonitoringStatus MonitoringStatus = MonitoringStatus.Active, ApState? LastValidState = null,
    long? LastValidRunId = null, DateTimeOffset? LastValidObservedAtUtc = null,
    DateTimeOffset? LastUpAtUtc = null, IncidentTrackingSnapshot? OpenIncident = null,
    ObservationGap? OpenGap = null, long? LastProcessedRunId = null,
    DateTimeOffset? LastProcessedAtUtc = null, bool TimeNeedsReview = false);

public sealed record MonitoringSegmentDecision(MonitoringStatus Status, Guid? MonitoringIntervalId,
    bool StartsNewInterval, bool EndsPreviousInterval, bool ResetsBaseline, string? EndReason = null,
    string? ChangeReason = null);

/// <summary>Optional IDs allocated by the caller. The pure engine never generates IDs.</summary>
public sealed record TransitionIdentifiers(Guid? NewGenerationId = null,
    Guid? NewMonitoringIntervalId = null, Guid? NewIncidentId = null);

/// <summary>An AP in the caller's current target set, without an attributable valid state observation.</summary>
public sealed record UnobservedAp(string SourceApId, MissingObservationReason Reason, DateTimeOffset RecordedAtUtc);
public sealed record IncidentMutation(IncidentMutationKind Kind, IncidentTrackingSnapshot Incident);

public sealed record ApTransitionDecision(CollectionContext Context, ApTrackingSnapshot Next,
    ObservationClassification Classification, ApState? CurrentState, IdentityDecision? Identity,
    MonitoringSegmentDecision Segment, ImmutableArray<IncidentMutation> Incidents,
    ObservationGap? ClosedGap, MissingObservationReason? UnknownReason,
    ApObservation? AcceptedObservation, bool InvalidatesEnrichment)
{
    public string SourceApId => Next.Identity.SourceApId;
    public bool IsMonitoringTarget => Next.MonitoringStatus == MonitoringStatus.Active;
    public bool AllowsAutomaticMetadataChanges => AcceptedObservation is not null;
}

/// <summary>Excluded count means APs outside current monitoring, never excluded source rows.</summary>
public sealed record RunClassificationContext(BaselineStatus BaselineStatus,
    bool TargetUniverseKnown, int MonitoringExcludedApCount = 0);
