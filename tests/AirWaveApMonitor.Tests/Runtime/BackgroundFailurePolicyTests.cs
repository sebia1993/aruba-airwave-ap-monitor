using AirWaveApMonitor.App.Runtime;

namespace AirWaveApMonitor.Tests.Runtime;

public sealed class BackgroundFailurePolicyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FatalFailure_FinalizesBeforeServiceTermination(bool service)
    {
        var calls = new List<string>();
        var policy = new BackgroundFailurePolicy(_ => { calls.Add("finalize"); return Task.CompletedTask; }, service,
            code => calls.Add($"code:{code}"), code => calls.Add($"exit:{code}"));
        await policy.HandleAsync();
        Assert.Equal(service ? new[] { "code:1", "finalize", "exit:1" } : ["code:1", "finalize"], calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedOrStalledCleanup_CannotPreventServiceTermination(bool throws)
    {
        var exit = 0;
        var policy = new BackgroundFailurePolicy(_ => throws ? Task.FromException(new IOException("fault")) : new TaskCompletionSource().Task,
            true, _ => { }, code => exit = code, TimeSpan.FromMilliseconds(20));
        await policy.HandleAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task ConcurrentFatalWorkers_FinalizeAndTerminateOnlyOnce()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0; var exits = 0;
        var policy = new BackgroundFailurePolicy(async _ => { Interlocked.Increment(ref count); await barrier.Task; },
            true, _ => { }, _ => Interlocked.Increment(ref exits));
        var first = policy.HandleAsync(); var second = policy.HandleAsync();
        barrier.SetResult(); await Task.WhenAll(first, second);
        Assert.Equal(1, count); Assert.Equal(1, exits);
    }

    [Fact]
    public async Task SynchronouslyBlockedCancellationCleanup_CannotDelayServiceExitBudget()
    {
        using var release = new ManualResetEventSlim();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new BackgroundFailurePolicy(_ => { release.Wait(); return Task.CompletedTask; },
            true, _ => { }, code => exited.TrySetResult(code), TimeSpan.FromMilliseconds(20));
        var running = Task.Run(policy.HandleAsync);
        try { Assert.Equal(1, await exited.Task.WaitAsync(TimeSpan.FromSeconds(2))); }
        finally { release.Set(); await running.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public void NamedServiceOption_IsPassedWithoutChangingModeDataOrPort()
    {
        var options = RuntimeOptions.Parse(["--mode", "demo", "--data-dir", "data path", "--port", "18769", "--service-name", "AirWave_Test-7"]);
        Assert.Equal("AirWave_Test-7", options.ServiceName);
        Assert.Equal("data path", options.DataDirectory); Assert.Equal(18769, options.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad service")]
    [InlineData("../service")]
    public void NamedServiceOption_RejectsUnsafeNames(string name)
        => Assert.Throws<ArgumentException>(() => RuntimeOptions.Parse(["--mode", "demo", "--service-name", name]));
}
