using AirWaveApMonitor.Core.Contracts;
namespace AirWaveApMonitor.App.Collection;

public sealed class CollectorHealth(TimeProvider clock)
{
    private readonly object gate = new();
    private CollectorHealthSnapshot state = new(clock.GetUtcNow(), CollectorPhase.Initializing);
    private DateTimeOffset? firstAttempt;
    private TimeSpan interval = TimeSpan.FromMinutes(5);
    internal void SetInterval(TimeSpan value) { lock (gate) interval = value; }
    internal void Update(Func<CollectorHealthSnapshot, CollectorHealthSnapshot> update)
    {
        lock (gate)
        {
            state = update(state) with { RecordedAtUtc = clock.GetUtcNow() };
            firstAttempt ??= state.LastAttemptAtUtc;
        }
    }
    public CollectorHealthSnapshot GetSnapshot()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var reference = state.LastValidCommittedAtUtc ?? firstAttempt;
            return state with { RecordedAtUtc = now, UpdateDelayed = reference is { } at && now - at >= interval * 2 };
        }
    }
}
