using AirWaveApMonitor.Core.Contracts;
namespace AirWaveApMonitor.App.Collection;
/// <summary>The runner checks shouldYield between HTTP requests and commits acquired results before returning.</summary>
public interface IEnrichmentRunner
{
    bool Supports(CollectionKind kind);
    Task ExecuteAsync(ProfileSnapshot profile, CollectionKind kind, Func<bool> shouldYield, Action<long> started, CancellationToken ct);
}
