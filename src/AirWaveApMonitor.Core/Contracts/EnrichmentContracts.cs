using System.Collections.Immutable;

namespace AirWaveApMonitor.Core.Contracts;

/// <summary>Frozen collection identity; Demo and Live evidence cannot share a binding.</summary>
public sealed record EnrichmentProfileBinding(Guid DatasetId, Guid InstanceId, Uri Origin,
    long ConfigurationVersion, long ScopeVersion, long CredentialsVersion, EvidenceSource Source)
{
    public static EnrichmentProfileBinding From(ProfileSnapshot profile, EvidenceSource source)
        => new(profile.DatasetId, profile.InstanceId, profile.Origin, profile.ConfigurationVersion,
            profile.ScopeVersion, profile.CredentialsVersion, source);
}

public enum CapabilityAvailability { Unverified, AvailableConfirmed, UnsupportedConfirmed }
public enum CapabilityVerification { None, OfficialContract, ProfileResponse, ExplicitUnsupported, DemoFixture }
public enum CapabilityDecisionReason { Unverified, BindingChanged, NotImplemented, Disabled, Available, Unsupported }
public sealed record CapabilityKey(EnrichmentProfileBinding Profile, string DeviceFamily, string Field, string ContractRevision);
public sealed record CapabilityEvidence(CapabilityKey Key, CapabilityAvailability Availability, bool Implemented,
    bool ContractVerified, CapabilityVerification Verification, DateTimeOffset? VerifiedAtUtc, string? SafeReason);
public sealed record CapabilityDecision(CapabilityAvailability Availability, bool CanCollect, CapabilityDecisionReason Reason);

public enum SourceTimeMeaning { Unknown, LastObserved, Measurement }
public enum FreshnessReason { Current, Delayed, UnverifiedTime, UnknownAllowedAge, FutureTime, BackwardsTime }
public sealed record SourceTiming(DateTimeOffset ReadAtUtc, DateTimeOffset? SourceAtUtc,
    SourceTimeMeaning Meaning, bool MeaningVerified);
public sealed record FreshnessAssessment(SourceFreshness Freshness, FreshnessReason Reason, DateTimeOffset? SourceAtUtc);

public enum EndpointRole { AccessPoint, Switch, Controller, Other }
public enum DiscoveryProtocol { Unknown, Lldp, Cdp, OtherVerified, ApListReference }
public enum ObservationDirection { Unknown, LocalToRemote, RemoteToLocal }
public sealed record ConnectionEndpoint(string SourceDeviceId, string? SourceInterfaceId, EndpointRole Role);
/// <summary>An explicit evidence window, not a physical-continuity inference from repeated reads.</summary>
public sealed record EvidenceInterval(DateTimeOffset FromUtc, DateTimeOffset? UntilUtc, bool ContinuityVerified);
public sealed record PlacementEvidenceSnapshot(Guid DeviceGenerationId, long PlacementVersion,
    Guid? InstallationPointId, long? PointVersion, DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveUntilUtc, DateTimeOffset RecordedAtUtc);
/// <summary>IdentityWindow is the verified intersection of this generation and monitoring interval's known bounds.
/// Null is unknown, not unbounded validity; boundaries must not estimate the physical replacement time.</summary>
public sealed record EnrichmentTargetBinding(EnrichmentProfileBinding Profile, Guid ApId, string SourceApId,
    Guid DeviceGenerationId, Guid MonitoringIntervalId, PlacementEvidenceSnapshot? Placement = null,
    EvidenceInterval? IdentityWindow = null);
/// <summary>IdentityMappingVerified attests source-to-internal-AP/generation/interval attribution.
/// It is separate from observing LLDP on a raw source switch/port and never upgrades that observation to AP identity proof.</summary>
public sealed record ConnectionObservation(Guid Id, long RunId, EnrichmentProfileBinding Profile,
    ConnectionEndpoint Local, ConnectionEndpoint Remote, DiscoveryProtocol Protocol, ObservationDirection Direction,
    SourceTiming Timing, TimeSpan? AllowedSourceAge, FieldEvidence Evidence, bool IdentityMappingVerified,
    EvidenceInterval? Window = null, EnrichmentTargetBinding? Target = null);

public sealed record NeighborQueryScope(EnrichmentProfileBinding Profile, ConnectionEndpoint Local,
    ConnectionEndpoint? ExpectedRemote = null);
public sealed record NeighborObservation(NeighborQueryScope Scope, RunOutcome Outcome,
    ObservationCompleteness Completeness, bool ScopeCovered, bool HasExclusionsOrConflicts,
    SourceTiming Timing, TimeSpan? AllowedSourceAge, ImmutableArray<ConnectionObservation> Neighbors);
public enum NeighborPresence { Unknown, Observed, ExpectedNeighborNotObserved }
public enum NeighborAssessmentReason { ValidObservation, CurrentCompleteAbsence, Incomplete, Failed, UnverifiedSource, ConflictingEvidence }
public sealed record NeighborAssessment(NeighborPresence Presence, bool HasCurrentLldpObservation,
    bool MayClosePreviousConnection, SourceFreshness Freshness, NeighborAssessmentReason Reason,
    ImmutableArray<ConnectionObservation> Observations);

public enum PortAdministrativeState { Unknown, Up, Down, Testing }
public enum PortOperationalState { Unknown, Up, Down, Testing, Dormant, NotPresent, LowerLayerDown }
public sealed record PortStateObservation(Guid Id, long RunId, EnrichmentProfileBinding Profile,
    ConnectionEndpoint Endpoint, PortAdministrativeState AdministrativeState,
    PortOperationalState OperationalState, FieldEvidence AdministrativeEvidence, FieldEvidence OperationalEvidence,
    string? RawAdministrativeState = null, string? RawOperationalState = null);
public sealed record PortStateAssessment(PortAdministrativeState AdministrativeState, PortOperationalState OperationalState);

public enum PowerSupplyState { Unknown, On, Off, Fault, Denied, Searching }
public enum PowerSupplyMethod { Unknown, Poe, Local, Other }
/// <summary>Supply and negotiation capability are not measured watts or observed LLDP neighbors.</summary>
public sealed record PowerStateObservation(Guid Id, long RunId, EnrichmentProfileBinding Profile,
    ConnectionEndpoint Endpoint, PowerSupplyState SupplyState, string? RawSupplyState,
    PowerSupplyMethod SupplyMethod, string? RawSupplyMethod, long? ErrorCount, bool? LldpNegotiationSupported,
    FieldEvidence SupplyEvidence, FieldEvidence MethodEvidence, FieldEvidence ErrorEvidence, FieldEvidence NegotiationEvidence);
public sealed record PowerStateAssessment(PowerSupplyState SupplyState, PowerSupplyMethod SupplyMethod,
    long? ErrorCount, bool? LldpNegotiationSupported);

public enum PowerMetric { ApConsumption, PortActual, Requested, Allocated, Reserved, Maximum }
public sealed record SourceSampleIdentity(string Id, bool UniquenessVerified);
public sealed record PowerSample(Guid Id, long RunId, EnrichmentProfileBinding Profile,
    ConnectionEndpoint Endpoint, PowerMetric Metric, decimal? Value, string? Unit, bool UnitVerified,
    SourceTiming Timing, FieldEvidence Evidence, EnrichmentTargetBinding? DirectTarget = null,
    SourceSampleIdentity? SourceIdentity = null);
public enum PowerValueReason { Valid, Missing, Invalid, UnverifiedUnit, UnsupportedUnit, DifferentQuantity }
public sealed record NormalizedPower(decimal? Watts, PowerValueReason Reason);
public enum PowerAttributionStatus { Attributed, Unknown, Ambiguous, Stale, OutsideBinding }
public enum PowerAttributionReason
{
    DirectApMeasurement, HistoricalConnection, InvalidSample, UnknownMeasurementTime,
    MissingIdentity, NoConnection, StaleConnection, AmbiguousConnections, BindingMismatch, InvalidPlacement,
    UnknownIdentityWindow, OutsideIdentityWindow
}
public sealed record PowerAttribution(Guid SampleId, PowerAttributionStatus Status, PowerAttributionReason Reason,
    DateTimeOffset? MeasuredAtUtc, EnrichmentTargetBinding? Target, ConnectionObservation? Connection);
public sealed record PowerSampleKey(EnrichmentProfileBinding Profile, ConnectionEndpoint Endpoint,
    PowerMetric Metric, string SourceSampleId);

public sealed record EnrichmentPayload(ImmutableArray<NeighborObservation> Neighbors,
    ImmutableArray<PortStateObservation> Ports, ImmutableArray<PowerSample> PowerSamples,
    ImmutableArray<PowerStateObservation> PowerStates = default)
{
    public ImmutableArray<PowerStateObservation> PowerStates { get; init; } = PowerStates.IsDefault ? [] : PowerStates;
}
public enum EnrichmentApplicationDisposition { Current, HistoricalOnly, RejectedBinding }

public sealed record EnrichmentKindHealthSnapshot(CollectionKind Kind, bool Enabled = false, bool CanCollect = false,
    DateTimeOffset? LastAttemptAtUtc = null, long? LastAttemptRunId = null,
    DateTimeOffset? LastValidCommittedAtUtc = null, DateTimeOffset? LastCompleteCommittedAtUtc = null,
    DateTimeOffset? NextAtUtc = null, long? LastFinalRunId = null, RunOutcome? LastOutcome = null,
    ObservationCompleteness? Completeness = null, bool HasQualityGap = false, bool StorageRecoveryPending = false,
    CollectionErrorCategory? LastError = null)
{
    /// <summary>Safe adapter-controlled display reason; never a raw response or exception message.</summary>
    public string? CapabilityReason { get; init; }
}

public interface IEnrichmentSource
{
    Task<EnrichmentBatch> CollectAsync(CollectionContext context, CancellationToken ct);
}
public interface IEnrichmentInterruptionStore
{
    Task<EnrichmentCommitReceipt> InterruptEnrichmentRunAsync(CollectionContext context, CancellationToken ct);
}

public interface IEnrichmentTargetStore
{
    /// <summary>Returns only the immutable targets persisted at this run's Begin; never current inventory as fallback.</summary>
    Task<ImmutableArray<EnrichmentTargetBinding>> GetFrozenEnrichmentTargetsAsync(CollectionContext context, CancellationToken ct);
}

public interface IEnrichmentHealthStore
{
    Task<EnrichmentKindHealthSnapshot> GetEnrichmentHealthAsync(ProfileSnapshot profile, CollectionKind kind, CancellationToken ct);
}
