using AirWaveApMonitor.Infrastructure.Storage;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace AirWaveApMonitor.App.Runtime;

/// <summary>Validate and recover storage before the HTTP server accepts requests.</summary>
public sealed class StorageInitialization(SqliteCollectionStore store, InstanceLock ownership, ProfileStore profiles) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Resolve the lifetime owner before any startup recovery can touch a Running run.
            _ = ownership;
            await store.InitializeAsync(cancellationToken);
            await profiles.OperationGate.WaitAsync(cancellationToken);
            try { await profiles.EnsureInitializedAsync(cancellationToken); }
            finally { profiles.OperationGate.Release(); }
        }
        catch (SqliteException error)
        {
            throw new InvalidOperationException("저장 DB를 초기화하거나 복원하지 못했습니다. 기존 파일을 보존합니다.", error);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
