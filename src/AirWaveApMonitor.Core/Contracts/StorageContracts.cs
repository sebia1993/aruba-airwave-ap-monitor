using System.Collections.Immutable;

namespace AirWaveApMonitor.Core.Contracts;

public interface ICollectionStore
{
    Task<CollectionContext> BeginRunAsync(ProfileSnapshot profile, CollectionKind kind, RunTrigger trigger, CancellationToken ct);
    Task<CommitReceipt> ApplyApBatchAsync(CollectionContext context, ApCollectionBatch batch, CancellationToken ct);
    Task<CommitReceipt> InterruptRunAsync(CollectionContext context, CancellationToken ct);
    Task<EnrichmentCommitReceipt> ApplyEnrichmentBatchAsync(CollectionContext context, EnrichmentBatch batch, CancellationToken ct);
    Task<DashboardSnapshot> GetDashboardAsync(DashboardQuery query, CancellationToken ct);
    Task<CollectionSuccessTimes> GetSuccessTimesAsync(Guid datasetId, long scopeVersion, CancellationToken ct);
}

/// <summary>For total failure, current Up/Down and change counts are null. Open incidents retain saved fact.</summary>
public sealed record RunSummary(int? ConfirmedUp, int? ConfirmedDown, int? Unknown, int? NewDown,
    int? InitialDown, int? ExistingDown, int? Recovered, int OpenIncidents, int? TargetCount,
    int ExcludedCount, BaselineStatus BaselineStatus);
public sealed record CommitReceipt(long RunId, bool AlreadyApplied, RunOutcome Outcome, RunSummary Summary)
{
    public int SchemaVersion => 1;
}
public sealed record EnrichmentCommitReceipt(Guid DatasetId, CollectionKind Kind, long RunId,
    bool AlreadyApplied, bool Applied, RunOutcome Outcome, ObservationCompleteness Completeness,
    ImmutableArray<CollectionError> Errors)
{
    public int SchemaVersion => 1;
}

public enum DashboardFilter { All, Down, NewDown, InitialDown, ExistingDown, Recovered, Unknown }
public enum DashboardSort { Name, IpAddress, LastObservedAt, FirstDownAt }
public enum ObservationClassification { Up, NewDown, InitialDown, ExistingDown, Recovered, Unknown }
public sealed record DashboardQuery(Guid DatasetId, long ScopeVersion, long? RunId = null, string? Search = null,
    DashboardFilter Filter = DashboardFilter.All, DashboardSort Sort = DashboardSort.Name,
    bool Descending = false, int Page = 1, int PageSize = 50);
public sealed record ApSnapshotRow(Guid ApId, string SourceApId, Guid DeviceGenerationId, Guid MonitoringIntervalId,
    DeviceIdentity Identity, DeviceDetails Device, LocationDetails Location, ControllerDetails Controller,
    ApState? CurrentState, ApState? LastValidState, DateTimeOffset? LastValidObservedAtUtc,
    DateTimeOffset? FirstDownAtUtc, Guid? OpenIncidentId, ObservationClassification Classification,
    ImmutableDictionary<string, FieldEvidence> Fields, SourceManagementDetails? SourceManagement = null,
    ManualManagementDetails? ManualManagement = null, SourceUpstreamReference? Upstream = null,
    MissingObservationReason? UnknownReason = null, bool TimeNeedsReview = false, long? LastValidRunId = null,
    DateTimeOffset? SourceAtUtc = null, DateTimeOffset? LastContactedAtUtc = null,
    ImmutableDictionary<string, string>? RawSourceValues = null);
public sealed record DashboardSnapshot(Guid DatasetId, long ScopeVersion, long RunId,
    DateTimeOffset StartedAtUtc, DateTimeOffset FinalizedAtUtc, RunLifecycle Lifecycle, RunOutcome? Outcome,
    RunSummary Summary, ImmutableArray<ApSnapshotRow> Rows, int TotalRows, ObservationCompleteness Completeness,
    ImmutableArray<CollectionError> Errors, ApRunSourceEvidence? SourceEvidence = null)
{
    public int SchemaVersion => 1;
}
