using System.Text;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
namespace AirWaveApMonitor.App.Collection;

/// <summary>Bounded local diagnostics contain only controlled category, UTC and run identity.</summary>
public sealed class CollectorDiagnostics(StorageLocation location, TimeProvider clock)
{
    private readonly object gate = new();
    public bool Record(CollectionErrorCategory category, long? runId)
    {
        lock (gate)
        {
            try
            {
                var path = Path.Combine(location.DataDirectory, "collector-diagnostics.log");
                if (File.Exists(path) && new FileInfo(path).Length >= 64 * 1024)
                    File.Move(path, path + ".previous", true);
                var line = JsonSerializer.Serialize(new { utc = clock.GetUtcNow(), category = category.ToString(), runId });
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
    }
}
