using AirWaveApMonitor.App.Runtime;

namespace AirWaveApMonitor.App.Collection;

/// <summary>Web listeners must have started successfully before any operational network request.</summary>
public sealed class CollectionWorker(CollectionCoordinator coordinator, TimeProvider clock, IHostApplicationLifetime host, BackgroundFailurePolicy? failurePolicy = null) : BackgroundService
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
        catch
        {
            if (failurePolicy is not null) await failurePolicy.HandleAsync();
            else Environment.ExitCode = 1;
            throw;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await coordinator.StopAsync(cleanup.Token).WaitAsync(cleanup.Token); }
            catch (OperationCanceledException) when (cleanup.IsCancellationRequested) { }
        }
    }
}
