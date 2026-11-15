using AirWaveApMonitor.App.Maintenance;
using AirWaveApMonitor.App.Runtime;
using Microsoft.Extensions.Hosting;
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
