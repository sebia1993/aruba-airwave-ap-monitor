using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
namespace AirWaveApMonitor.App.Maintenance;

/// <summary>Daily verified backup and expiry have separate success/error clocks.</summary>
public sealed class MaintenanceCoordinator(BackupService backup, RetentionService retention, SqliteCollectionStore store,
    MaintenanceHealth health, TimeProvider clock)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool initialized;
    private DateTimeOffset? nextAttempt;
    private DateTimeOffset? previousTick;

    public async Task TickAsync(CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) return;
        try
        {
            var now = clock.GetUtcNow().ToUniversalTime();
            if (previousTick is { } previous && now < previous) nextAttempt = now;
            previousTick = now;
            if (nextAttempt > now) return;
            health.Update(s => s with { Busy = true });
            if (!initialized)
            {
                var previousBackup = (await backup.ListVerifiedAsync(ct)).FirstOrDefault(b => b.Kind == BackupKind.Daily);
                var restoredAt = await store.GetLastRestoreAtAsync(ct);
                health.Update(s => s with { LastBackupAtUtc = previousBackup?.CreatedAtUtc, BackupStatus = previousBackup is null ? "not-yet-created" : "ready", LastRestoreAtUtc = restoredAt });
                initialized = true;
            }
            var last = health.GetSnapshot().LastBackupAtUtc;
            if (last is null || now - last >= TimeSpan.FromDays(1) || last > now)
            {
                try
                {
                    health.Update(s => s with { BackupStatus = "running" });
                    var created = await backup.CreateAsync(ct);
                    health.Update(s => s with { BackupStatus = "ready", LastBackupAtUtc = created.CreatedAtUtc, LastBackupError = null, LastBackupPruneError = created.PruningError });
                    await RecordAsync("BackupSucceeded", ct);
                }
                catch (Exception error) when (Recoverable(error))
                {
                    health.Update(s => s with { BackupStatus = "failed", LastBackupError = "backup-unavailable" });
                    await RecordAsync("BackupFailed", ct);
                }
            }
            if (health.GetSnapshot().LastRetentionAtUtc is not { } lastRetention || now - lastRetention >= TimeSpan.FromDays(1) || lastRetention > now)
            {
                try
                {
                    await retention.PruneAsync(now, ct);
                    health.Update(s => s with { LastRetentionAtUtc = now, LastRetentionError = null });
                    await RecordAsync("RetentionSucceeded", ct);
                }
                catch (Exception error) when (Recoverable(error))
                {
                    health.Update(s => s with { LastRetentionError = "retention-unavailable" });
                    await RecordAsync("RetentionFailed", ct);
                }
            }
            nextAttempt = now + TimeSpan.FromHours(1); // bounded retry, no catch-up loop
        }
        catch (Exception error) when (Recoverable(error))
        {
            health.Update(s => s with { BackupStatus = "failed", LastBackupError = "backup-unavailable" });
            nextAttempt = clock.GetUtcNow() + TimeSpan.FromHours(1);
        }
        finally { health.Update(s => s with { Busy = false }); gate.Release(); }
    }
    private async Task RecordAsync(string category, CancellationToken ct)
    {
        try { await store.RecordOperationalLogAsync(category, ct); health.Update(s => s with { LastLogError = null }); }
        catch (Exception error) when (Recoverable(error)) { health.Update(s => s with { LastLogError = "maintenance-log-unavailable" }); }
    }
    private static bool Recoverable(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or SqliteException;
}
