namespace AirWaveApMonitor.Core.Contracts;

public enum BackupKind { Daily, PreUpdate }
public sealed record VerifiedBackup(string BackupPath, Guid DatasetId, AppMode Mode, int SchemaVersion,
    BackupKind Kind, DateTimeOffset CreatedAtUtc, string Sha256, long Bytes, string? PruningError = null);
public sealed record RestoreReceipt(string RestoredFrom, Guid DatasetId, int SchemaVersion,
    DateTimeOffset RestoredAtUtc, Guid RestoreEventId, string PreservedDatabasePath, bool RequiresCredentialRegistration);
public sealed record RetentionResult(int ExecutionDetailsDeleted, int ClosedGapDetailsDeleted,
    int OperationalLogsDeleted, int FileLogLinesDeleted, bool PowerSampleRetentionAvailable = false, int PowerSamplesDeleted = 0);
public sealed record MaintenanceSnapshot(string BackupStatus = "initializing", DateTimeOffset? LastBackupAtUtc = null,
    string? LastBackupError = null, DateTimeOffset? LastRetentionAtUtc = null, string? LastRetentionError = null,
    bool Busy = false, DateTimeOffset? LastRestoreAtUtc = null, string? LastBackupPruneError = null, string? LastLogError = null);
