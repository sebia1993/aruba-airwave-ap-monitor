namespace AirWaveApMonitor.App.Collection;

/// <summary>Web listeners must have started successfully before any operational network request.</summary>
public sealed class CollectionWorker(CollectionCoordinator coordinator, TimeProvider clock, IHostApplicationLifetime host) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = host.ApplicationStarted.Register(() => started.TrySetResult());
        try
        {
            await started.Task.WaitAsync(stoppingToken);
            await coordinator.StartAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                // A delayed wake produces one Tick; missed periods are coalesced by the coordinator.
                await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
                await coordinator.TickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await coordinator.StopAsync(CancellationToken.None); }
    }
}
