using AirWaveApMonitor.Core.Contracts;
namespace AirWaveApMonitor.App.Maintenance;

public sealed class MaintenanceHealth
{
    private readonly object gate = new();
    private MaintenanceSnapshot state = new();
    public MaintenanceSnapshot GetSnapshot() { lock (gate) return state; }
    internal void Update(Func<MaintenanceSnapshot, MaintenanceSnapshot> update) { lock (gate) state = update(state); }
}
