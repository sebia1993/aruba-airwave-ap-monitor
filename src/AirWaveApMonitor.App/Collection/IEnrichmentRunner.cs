using AirWaveApMonitor.Core.Contracts;
namespace AirWaveApMonitor.App.Collection;
/// <summary>Runs in the coordinator's sole slot, including durable finalization and response-loss recovery.</summary>
public interface IEnrichmentRunner
{
    bool Supports(CollectionKind kind);
    bool Supports(ProfileSnapshot profile, CollectionKind kind) => Supports(kind);
    bool RecoveryPending => false;
    Task<bool> RecoverAsync(CancellationToken ct) => Task.FromResult(true);
    Task RefreshHealthAsync(ProfileSnapshot profile, CancellationToken ct) => Task.CompletedTask;
    void UpdateSchedule(ProfileSnapshot? profile, DateTimeOffset nextAt) { }
    Task ExecuteAsync(ProfileSnapshot profile, CollectionKind kind, Func<bool> shouldYield, Action<long> started, CancellationToken ct);
    Task ExecuteAsync(ProfileSnapshot profile, CollectionKind kind, RunTrigger trigger, Func<bool> shouldYield, Action<long> started, CancellationToken ct)
        => ExecuteAsync(profile, kind, shouldYield, started, ct);
}
