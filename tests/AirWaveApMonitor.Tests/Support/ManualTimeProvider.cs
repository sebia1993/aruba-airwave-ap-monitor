namespace AirWaveApMonitor.Tests.Support;
// Controllable clock for application time budgets; no real 45/120-second waits.
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.Parse("2026-11-15T00:00:00Z");
    private readonly List<ManualTimer> timers = [];
    public override DateTimeOffset GetUtcNow() => now;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }
    public void Advance(TimeSpan elapsed)
    {
        now += elapsed;
        foreach (var timer in timers.ToArray())
        if (timer.Due is { } due && due <= now)
        {
            timer.Due = null;
            timer.Callback(timer.State);
        }
    }
    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback; public object? State { get; } = state; public DateTimeOffset? Due
        {
            get; set;
        }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
            return true;
        }
        public void Dispose() => Due = null; public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
