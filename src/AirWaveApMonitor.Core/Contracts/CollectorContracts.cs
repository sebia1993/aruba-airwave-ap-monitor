namespace AirWaveApMonitor.Core.Contracts;

public sealed record SourceCheckpoint(string Source, int Position);
public interface ISourceCheckpointStore
{
    Task<SourceCheckpoint?> GetSourceCheckpointAsync(Guid datasetId, string source, CancellationToken ct);
}

public enum CollectorPhase { Initializing, Idle, Preparing, Collecting, Finalizing, StorageRecovery, Stopping, Stopped }
public sealed record CollectorHealthSnapshot(DateTimeOffset RecordedAtUtc, CollectorPhase Phase,
    CollectionKind? ActiveKind = null, long? ActiveRunId = null, Guid? PendingApQueueId = null,
    bool Paused = false, bool Configured = false, bool AuthenticationBlocked = false,
    AuthenticationStop? AuthenticationStop = null, string? BlockedReason = null,
    bool StorageRecoveryPending = false, long SkippedApTicks = 0, long SkippedEnrichmentTicks = 0,
    DateTimeOffset? LastAttemptAtUtc = null, long? LastAttemptRunId = null,
    DateTimeOffset? LastValidCommittedAtUtc = null, DateTimeOffset? LastCompleteCommittedAtUtc = null,
    DateTimeOffset? NextApAtUtc = null, DateTimeOffset? NextEnrichmentAtUtc = null,
    bool UpdateDelayed = false, bool ClockNeedsReview = false, string? LastStorageError = null,
    string? LastDiagnosticFileError = null, string BackupStatus = "not-implemented",
    long? LastFinalRunId = null, RunOutcome? LastOutcome = null, CollectionErrorCategory? LastCollectionError = null);
