using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Demo;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    public async Task<SourceCheckpoint?> GetSourceCheckpointAsync(Guid datasetId, string source, CancellationToken ct)
    {
        ValidateDataset(datasetId);
        if (source != ScenarioApSource.CheckpointSource || location.Mode != AppMode.Demo)
            throw new ArgumentException("지원되는 Demo 원천 진행 위치가 아닙니다.");
        await InitializeAsync(ct);
        using var connection = Open();
        using var command = Command(connection, null,
            "SELECT position FROM source_checkpoints WHERE dataset_id=$dataset AND source_name=$source",
            ("$dataset", datasetId), ("$source", source));
        var position = command.ExecuteScalar();
        return position is null ? null : new(source, Convert.ToInt32(position));
    }

    private void PersistCheckpoint(SqliteConnection connection, SqliteTransaction transaction,
        CollectionContext context, ApCollectionBatch batch, RunLifecycle lifecycle)
    {
        if (batch.Checkpoint is not { } checkpoint || lifecycle != RunLifecycle.Finalized) return;
        if (location.Mode != AppMode.Demo || context.Profile.Origin != ScenarioApSource.DemoOrigin ||
            checkpoint.Source != ScenarioApSource.CheckpointSource || checkpoint.Position < 0 || checkpoint.Position >= ScenarioApSource.Frames.Length)
            throw new ArgumentException("유효한 Demo 회차 진행 위치가 아닙니다.");
        Execute(connection, transaction, """
            INSERT INTO source_checkpoints VALUES($dataset,$source,$position,$run)
            ON CONFLICT(dataset_id,source_name) DO UPDATE SET position=excluded.position,run_id=excluded.run_id
            WHERE excluded.run_id > source_checkpoints.run_id AND excluded.position >= source_checkpoints.position
            """, ("$dataset", location.DatasetId), ("$source", checkpoint.Source), ("$position", checkpoint.Position), ("$run", context.RunId));
    }
}
