using AirWaveApMonitor.App.Maintenance;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using static AirWaveApMonitor.Tests.Maintenance.BackupServiceTests;

namespace AirWaveApMonitor.Tests.Maintenance;

public sealed class MaintenanceCoordinatorTests
{
    [Fact]
    public async Task DailyBackup_RestartDoesNotRepeatWithin24Hours()
    {
        using var f = await Fixture.Create();
        await f.Observe(ApState.Down);
        var health = new MaintenanceHealth();
        var retention = new RetentionService(f.Location, f.Gate, f.Store);
        var coordinator = new MaintenanceCoordinator(f.Backup, retention, f.Store, health, f.Clock);
        await coordinator.TickAsync(default);
        var first = health.GetSnapshot().LastBackupAtUtc;
        Assert.Equal(f.Clock.GetUtcNow(), first);
        f.Clock.Advance(TimeSpan.FromHours(23));
        await new MaintenanceCoordinator(f.Backup, retention, f.Store, new(), f.Clock).TickAsync(default);
        Assert.Single(await f.Backup.ListVerifiedAsync(default));
        f.Clock.Advance(TimeSpan.FromHours(1));
        await coordinator.TickAsync(default);
        Assert.Equal(2, (await f.Backup.ListVerifiedAsync(default)).Count);
        Assert.True(health.GetSnapshot().LastBackupAtUtc > first);
        Assert.Null(health.GetSnapshot().LastBackupError);
    }

    [Fact]
    public async Task DiskWriteFailure_DoesNotAdvanceSuccess()
    {
        using var f = await Fixture.Create();
        await f.Observe(ApState.Down);
        var fail = true;
        var backup = new BackupService(f.Location, f.Gate, f.Clock, phase => { if (fail && phase == BackupBoundary.BeforePublish) throw new IOException("private path must never reach health"); });
        var health = new MaintenanceHealth();
        var coordinator = new MaintenanceCoordinator(backup, new(f.Location, f.Gate, f.Store), f.Store, health, f.Clock);
        await coordinator.TickAsync(default);
        Assert.Null(health.GetSnapshot().LastBackupAtUtc);
        Assert.Equal("failed", health.GetSnapshot().BackupStatus);
        Assert.Equal("backup-unavailable", health.GetSnapshot().LastBackupError);
        Assert.Empty(await backup.ListVerifiedAsync(default));
        fail = false; f.Clock.Advance(TimeSpan.FromHours(1));
        await coordinator.TickAsync(default);
        Assert.NotNull(health.GetSnapshot().LastBackupAtUtc);
        Assert.Null(health.GetSnapshot().LastBackupError);
    }

    [Fact]
    public async Task FileRetentionFailure_DoesNotUndoVerifiedBackupSuccess()
    {
        using var f = await Fixture.Create();
        await f.Observe(ApState.Down);
        await File.WriteAllTextAsync(Path.Combine(f.Directory.Path, "collector-diagnostics.log"), new string('x', 1024 * 1024 + 1));
        var health = new MaintenanceHealth();
        await new MaintenanceCoordinator(f.Backup, new(f.Location, f.Gate, f.Store), f.Store, health, f.Clock).TickAsync(default);
        Assert.NotNull(health.GetSnapshot().LastBackupAtUtc);
        Assert.Null(health.GetSnapshot().LastBackupError);
        Assert.Equal("retention-unavailable", health.GetSnapshot().LastRetentionError);
        Assert.Null(health.GetSnapshot().LastRetentionAtUtc);
        Assert.False(health.GetSnapshot().Busy);
    }

    [Fact]
    public async Task MaintenanceLogFailure_IsSeparateFromBackupSuccess()
    {
        using var f = await Fixture.Create();
        await f.Observe(ApState.Down);
        using (var c = f.Open()) { using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TRIGGER refuse_operational_log BEFORE INSERT ON operational_logs BEGIN SELECT RAISE(ABORT,'private detail'); END"; cmd.ExecuteNonQuery(); }
        var health = new MaintenanceHealth();
        await new MaintenanceCoordinator(f.Backup, new(f.Location, f.Gate, f.Store), f.Store, health, f.Clock).TickAsync(default);
        Assert.NotNull(health.GetSnapshot().LastBackupAtUtc);
        Assert.Null(health.GetSnapshot().LastBackupError);
        Assert.Equal("maintenance-log-unavailable", health.GetSnapshot().LastLogError);
    }
}
