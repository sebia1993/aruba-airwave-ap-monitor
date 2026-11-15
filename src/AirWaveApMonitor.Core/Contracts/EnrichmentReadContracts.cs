using System.Collections.Immutable;

namespace AirWaveApMonitor.Core.Contracts;

public sealed record ConnectionEvidenceView(long RunId, NeighborObservation Observation, NeighborAssessment Assessment,
    ImmutableArray<ConnectionObservation> RetainedConnections, bool HasQualityGap);
public sealed record PortEvidenceView(long RunId, PortStateObservation Observation, PortStateAssessment Assessment,
    bool HasQualityGap = false);
public sealed record PowerEvidenceView(long Sequence, PowerSample Sample, NormalizedPower Normalized, PowerAttribution Attribution);
public sealed record PowerStateEvidenceView(long RunId, PowerStateObservation Observation, PowerStateAssessment Assessment,
    bool HasQualityGap = false);
public sealed record EnrichmentGapView(Guid GapId, CollectionKind Kind, long FirstRunId, DateTimeOffset FirstMissingAtUtc,
    long? ResumedRunId, DateTimeOffset? ResumedAtUtc, CollectionErrorCategory Reason);

/// <summary>Materialized only from frozen immutable references and a durable commit cutoff.
/// Incident snapshots embed the complete value; AP run detail may report expired numeric samples.</summary>
public sealed record EnrichmentSnapshot(long ThroughRunId, long ThroughCommitSequence, DateTimeOffset CapturedAtUtc,
    ImmutableArray<ConnectionEvidenceView> Connections, ImmutableArray<PortEvidenceView> Ports,
    ImmutableArray<PowerEvidenceView> PowerSamples, ImmutableArray<PowerStateEvidenceView> PowerStates,
    ImmutableArray<EnrichmentGapView> Gaps, bool PowerSamplesExpired = false);

public sealed record EnrichmentRunQuery(Guid DatasetId, long? ScopeVersion = null, CollectionKind? Kind = null,
    long? ThroughRunId = null, DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null, int Page = 1, int PageSize = 50);
public sealed record EnrichmentRunItem(long RunId, long ScopeVersion, long ConfigurationVersion, CollectionKind Kind,
    RunTrigger Trigger, EvidenceSource Source, DateTimeOffset StartedAtUtc, DateTimeOffset? FinalizedAtUtc,
    RunLifecycle Lifecycle, RunOutcome? Outcome, ObservationCompleteness? Completeness,
    EnrichmentApplicationDisposition? Disposition, ImmutableArray<CollectionError> Errors,
    int ConnectionQueryCount, int PortCount, int PowerSampleCount, int PowerStateCount, bool HasQualityGap);
public sealed record EnrichmentRunPage(ImmutableArray<EnrichmentRunItem> Rows, int TotalRows, long? ThroughRunId,
    int Page, int PageSize);
public sealed record EnrichmentRunDetail(EnrichmentRunItem Run, EnrichmentSnapshot Evidence);

/// <summary>ThroughRunId selects an immutable historical run's visibility boundary. For current global
/// paging leave it null and reuse ThroughCommitSequence; a later commit with an earlier RunId cannot leak in.</summary>
public sealed record PowerSampleQuery(Guid DatasetId, long ScopeVersion, long? ThroughRunId = null,
    Guid? ApId = null, Guid? DeviceGenerationId = null, Guid? InstallationPointId = null, PowerMetric? Metric = null,
    string? SourceDeviceId = null, string? SourceInterfaceId = null, EndpointRole? EndpointRole = null,
    DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null, int Page = 1, int PageSize = 50,
    long? ThroughCommitSequence = null);
/// <summary>ExpiredBeforeUtc is the durable successful retention cutoff, not evidence of zero consumption.
/// MayIncludeExpiredSamples means requested time bounds overlap expired detail; retained snapshots remain intact.</summary>
public sealed record PowerSamplePage(ImmutableArray<PowerEvidenceView> Rows, int TotalRows, long? ThroughRunId,
    long ThroughCommitSequence, int Page, int PageSize, DateTimeOffset? ExpiredBeforeUtc,
    bool MayIncludeExpiredSamples, int RetentionDays = 30);

public interface IEnrichmentReadStore
{
    Task<EnrichmentRunPage> GetEnrichmentRunsAsync(EnrichmentRunQuery query, CancellationToken ct);
    Task<EnrichmentRunDetail> GetEnrichmentRunAsync(Guid datasetId, long runId, CancellationToken ct);
    Task<PowerSamplePage> GetPowerSamplesAsync(PowerSampleQuery query, CancellationToken ct);
}
