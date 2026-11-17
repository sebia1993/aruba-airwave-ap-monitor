namespace AirWaveApMonitor.App.Runtime;

/// <summary>Unexpected worker faults finalize briefly, then fail the process before SCM sees a clean Stop.</summary>
public sealed class BackgroundFailurePolicy(Func<CancellationToken, Task> finalize, bool isService,
    Action<int> setExitCode, Action<int> terminate, TimeSpan? cleanupBudget = null)
{
    private readonly object gate = new();
    private Task? handling;

    public Task HandleAsync()
    {
        lock (gate) return handling ??= HandleCoreAsync();
    }

    private async Task HandleCoreAsync()
    {
        setExitCode(1);
        using var budget = new CancellationTokenSource(cleanupBudget ?? TimeSpan.FromSeconds(5));
        var cancellation = budget.Token;
        // CancellationTokenSource.Cancel can invoke blocking callbacks before the first await.
        // Schedule the invocation itself so this worker can still enforce the process exit budget.
        try { await Task.Run(() => finalize(cancellation), CancellationToken.None).WaitAsync(cancellation); }
        catch (Exception) { /* Failure or timeout must not suppress the nonzero process outcome. */ }
        // WindowsServiceLifetime.StopAsync reports SERVICE_STOPPED successfully. A later Main return
        // is insufficient for recovery, so exit while SCM still considers this service running.
        if (isService) terminate(1);
    }
}
