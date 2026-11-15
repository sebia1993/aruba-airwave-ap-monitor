using AirWaveApMonitor.App.Runtime;

namespace AirWaveApMonitor.App.Maintenance;

public sealed class MaintenanceWorker(MaintenanceCoordinator coordinator, TimeProvider clock, IHostApplicationLifetime host, BackgroundFailurePolicy? failurePolicy = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = host.ApplicationStarted.Register(() => started.TrySetResult());
        try
        {
            await started.Task.WaitAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await coordinator.TickAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch
        {
            // Known maintenance failures are reported by the coordinator without stopping.
            // Service faults must exit before WindowsServiceLifetime reports a clean Stop.
            if (failurePolicy is not null) await failurePolicy.HandleAsync();
            else Environment.ExitCode = 1;
            throw;
        }
    }
}
