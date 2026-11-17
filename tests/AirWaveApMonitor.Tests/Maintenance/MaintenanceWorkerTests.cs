using AirWaveApMonitor.App.Maintenance;
using AirWaveApMonitor.App.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using AirWaveApMonitor.Infrastructure.Maintenance;
using static AirWaveApMonitor.Tests.Maintenance.BackupServiceTests;

namespace AirWaveApMonitor.Tests.Maintenance;

[Collection("Host environment")]
public sealed class MaintenanceWorkerTests
{
    [Fact]
    public async Task UnexpectedWorkerFailure_SetsNonzeroInteractiveExit()
    {
        using var f = await Fixture.Create();
        var clock = new BrokenClock();
        var coordinator = new MaintenanceCoordinator(f.Backup, new(f.Location, f.Gate, f.Store), f.Store, new(), clock);
        using var worker = new MaintenanceWorker(coordinator, clock, new StartedHost());
        var previous = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            await worker.StartAsync(default);
            await Assert.ThrowsAsync<NotSupportedException>(() => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, Environment.ExitCode);
            await worker.DisposeAsync();
            await worker.DisposeAsync();
        }
        finally { Environment.ExitCode = previous; }
    }
    [Fact]
    public async Task UnexpectedServiceWorkerFailure_InvokesBoundedExitPolicyBeforeEscaping()
    {
        using var f = await Fixture.Create(); var clock = new BrokenClock();
        var coordinator = new MaintenanceCoordinator(f.Backup, new(f.Location, f.Gate, f.Store), f.Store, new(), clock);
        var calls = new List<string>();
        var policy = new BackgroundFailurePolicy(_ => { calls.Add("finalize"); return Task.CompletedTask; }, true,
            _ => { }, code => calls.Add($"exit:{code}"));
        using var worker = new MaintenanceWorker(coordinator, clock, new StartedHost(), policy);
        await worker.StartAsync(default);
        await Assert.ThrowsAsync<NotSupportedException>(() => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["finalize", "exit:1"], calls);
        await worker.StopAsync(default);
        await worker.DisposeAsync();
    }
    [Fact]
    public async Task AsyncHostDisposal_WaitsForBackupAndKeepsDataDirectoryOwnedUntilItFinishes()
    {
        using var f = await Fixture.Create();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backup = new BackupService(f.Location, f.Gate, f.Clock, boundary: boundary =>
        {
            if (boundary == BackupBoundary.AfterSnapshot) { entered.TrySetResult(); release.Wait(); }
        });
        var coordinator = new MaintenanceCoordinator(backup, new(f.Location, f.Gate, f.Store), f.Store, new(), f.Clock);
        var services = new ServiceCollection();
        services.AddSingleton(_ => new InstanceLock(f.Location));
        services.AddSingleton(_ => new MaintenanceWorker(coordinator, f.Clock, new StartedHost()));
        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<InstanceLock>();
        var worker = provider.GetRequiredService<MaintenanceWorker>();
        Task? disposing = null;
        try
        {
            await worker.StartAsync(default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            disposing = provider.DisposeAsync().AsTask();
            await Task.Delay(50);
            Assert.False(disposing.IsCompleted, "Disposal must not release the data owner while backup is still running.");
            Assert.Throws<IOException>(() => new InstanceLock(f.Location));
        }
        finally
        {
            release.Set();
            if (disposing is not null) await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            else await provider.DisposeAsync();
            if (worker.ExecuteTask is not null) await worker.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Empty(Directory.EnumerateFiles(f.Directory.Path, "*.staging", SearchOption.AllDirectories));
        using var nextOwner = new InstanceLock(f.Location);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncDisposal_BeforeApplicationStarted_IsSafeAndDoesNotRunMaintenance(bool startWorker)
    {
        using var f = await Fixture.Create();
        var coordinator = new MaintenanceCoordinator(f.Backup, new(f.Location, f.Gate, f.Store), f.Store, new(), f.Clock);
        var worker = new MaintenanceWorker(coordinator, f.Clock, new WaitingHost());
        if (startWorker) await worker.StartAsync(default);
        await worker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await worker.DisposeAsync();
        Assert.True(worker.ExecuteTask is null || worker.ExecuteTask.IsCompleted);
        Assert.Empty(await f.Backup.ListVerifiedAsync(default));
    }

    private sealed class WaitingHost : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }
    private sealed class BrokenClock : TimeProvider { public override DateTimeOffset GetUtcNow() => throw new NotSupportedException("synthetic unexpected worker error"); }
    private sealed class StartedHost : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => new(true);
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }
}
