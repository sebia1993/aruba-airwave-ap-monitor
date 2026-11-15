using AirWaveApMonitor.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace AirWaveApMonitor.App.Runtime;

/// <summary>Validate and recover storage before the HTTP server accepts requests.</summary>
public sealed class StorageInitialization(SqliteCollectionStore store) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await store.InitializeAsync(cancellationToken);
        }
        catch (SqliteException error)
        {
            throw new InvalidOperationException("저장 DB를 초기화하거나 복원하지 못했습니다. 기존 파일을 보존합니다.", error);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
