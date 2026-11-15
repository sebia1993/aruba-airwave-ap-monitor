using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
namespace AirWaveApMonitor.App.Collection;

/// <summary>Bounded local diagnostics share the maintenance append/rotation/expiry gate.</summary>
public sealed class CollectorDiagnostics(StorageLocation location, TimeProvider clock)
{
    private readonly OperationalLog log = new(location, clock);
    public bool Record(CollectionErrorCategory category, long? runId) => log.Record(category, runId);
}
