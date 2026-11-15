using System.Collections.Immutable;

namespace AirWaveApMonitor.Core.Contracts;

public interface IMonitoringReadStore
{
    Task<RunPage> GetRunsAsync(RunListQuery query, CancellationToken ct);
    Task<RunListItem> GetRunAsync(Guid datasetId, long runId, CancellationToken ct);
    Task<ApDetailSnapshot> GetApDetailAsync(ApDetailQuery query, CancellationToken ct);
    Task<IncidentPage> GetIncidentsAsync(IncidentQuery query, CancellationToken ct);
    Task<IncidentReadModel> GetIncidentAsync(IncidentQuery query, Guid incidentId, CancellationToken ct);
}

public sealed record RunSelection(Guid DatasetId, long ScopeVersion, long RunId, DateTimeOffset StartedAtUtc,
    DateTimeOffset FinalizedAtUtc, RunLifecycle Lifecycle, RunOutcome? Outcome);
public sealed record RunListQuery(Guid DatasetId, long? ScopeVersion = null, CollectionKind? Kind = null,
    DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null, int Page = 1, int PageSize = 50,
    long? ThroughRunId = null);
public sealed record RunListItem(long RunId, long ScopeVersion, CollectionKind Kind, RunTrigger Trigger,
    RunLifecycle Lifecycle, DateTimeOffset StartedAtUtc, DateTimeOffset? FinalizedAtUtc, RunOutcome? Outcome,
    RunSummary? Summary, ObservationCompleteness? Completeness, ImmutableArray<CollectionError> Errors,
    ApRunSourceEvidence? SourceEvidence);
public sealed record RunPage(Guid DatasetId, ImmutableArray<RunListItem> Rows, int TotalRows,
    ImmutableArray<long> AvailableScopes, int Page, int PageSize, long? ThroughRunId);

public enum IncidentStatus { Open, Recovered, TrackingEnded }
public sealed record IncidentQuery(Guid DatasetId, long ScopeVersion, long AsOfRunId, Guid? ApId = null,
    IncidentStatus? Status = null, string? Search = null, DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null, int Page = 1, int PageSize = 50, Guid? DeviceGenerationId = null);
public sealed record IncidentReadModel(Guid IncidentId, Guid ApId, string SourceApId, Guid DeviceGenerationId,
    Guid MonitoringIntervalId, IncidentStatus Status, IncidentTrackingSnapshot Tracking,
    IncidentSnapshot Detection, IncidentSnapshot? Recovery, ImmutableArray<ObservationGap> ObservationGaps,
    double? ObservedElapsedSeconds, bool MoreObservationGaps = false);
public sealed record IncidentPage(RunSelection Selection, ImmutableArray<IncidentReadModel> Rows,
    int TotalRows, int DistinctApCount, int Page, int PageSize);
public sealed record ApDetailQuery(Guid DatasetId, long ScopeVersion, long RunId, Guid ApId, int HistoryLimit = 50);
public sealed record ApObservationHistoryItem(long RunId, long ScopeVersion, DateTimeOffset StartedAtUtc,
    DateTimeOffset FinalizedAtUtc, RunLifecycle Lifecycle, RunOutcome Outcome, ApState? CurrentState,
    ObservationClassification Classification, MissingObservationReason? UnknownReason, bool TimeNeedsReview);
public sealed record ApDetailSnapshot(RunSelection Selection, ApSnapshotRow Row,
    ImmutableArray<IncidentReadModel> Incidents, ImmutableArray<ObservationGap> ObservationGaps,
    ImmutableArray<ApObservationHistoryItem> Observations, bool MoreIncidents = false,
    bool MoreObservationGaps = false, bool MoreObservations = false);
