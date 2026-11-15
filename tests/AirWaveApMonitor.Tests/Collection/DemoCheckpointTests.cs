using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Demo;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Security;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Infrastructure.Storage;
using AirWaveApMonitor.Tests.Support;
using Microsoft.Data.Sqlite;

namespace AirWaveApMonitor.Tests.Collection;

public sealed class DemoCheckpointTests
{
    [Fact]
    public async Task Restart_ResumesCommittedFrame_InterruptedRunDoesNotAdvance()
    {
        using var directory = new TemporaryDirectory();
        var location = StoragePaths.Open(AppMode.Demo, directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var clock = new ManualTimeProvider();
        var store = new SqliteCollectionStore(location, gate, clock);
        var profile = new ProfileStore(location).Current!;
        var first = await store.BeginRunAsync(profile, CollectionKind.ApStatus, RunTrigger.Startup, default);
        var firstBatch = await new ScenarioApSource(clock, store).CollectAsync(first, default);
        await store.ApplyApBatchAsync(first, firstBatch, default);
        Assert.Equal(new SourceCheckpoint("demo-ap-v1", 0), await store.GetSourceCheckpointAsync(location.DatasetId, "demo-ap-v1", default));
        var next = await store.BeginRunAsync(profile, CollectionKind.ApStatus, RunTrigger.Manual, default);
        var uncommitted = await new ScenarioApSource(clock, store).CollectAsync(next, default);
        Assert.Equal(ApState.Down, uncommitted.Observations.Single(x => x.SourceApId == "9").State);
        await store.InterruptRunAsync(next, default);
        var reopened = new SqliteCollectionStore(location, gate, clock);
        var third = await reopened.BeginRunAsync(profile, CollectionKind.ApStatus, RunTrigger.Startup, default);
        var repeated = await new ScenarioApSource(clock, reopened).CollectAsync(third, default);
        Assert.Equal(1, repeated.Checkpoint!.Position);
        await reopened.ApplyApBatchAsync(third, repeated, default);
        Assert.Equal(1, (await reopened.GetSourceCheckpointAsync(location.DatasetId, "demo-ap-v1", default))!.Position);
    }

    [Fact]
    public async Task CommitResponseLost_AdvancesCheckpointExactlyOnceIncludingFailedFrame()
    {
        using var directory = new TemporaryDirectory();
        var location = StoragePaths.Open(AppMode.Demo, directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var clock = new ManualTimeProvider();
        var profile = new ProfileStore(location).Current!;
        var store = new SqliteCollectionStore(location, gate, clock, boundary =>
        {
            if (boundary == StorageTransactionBoundary.AfterFinalCommit) throw new IOException("lost receipt");
        });
        var context = await store.BeginRunAsync(profile, CollectionKind.ApStatus, RunTrigger.Manual, default);
        var failed = new ApCollectionBatch(RunOutcome.Failed, [], [], clock.GetUtcNow(), ObservationCompleteness.Unknown, [])
        { Checkpoint = new("demo-ap-v1", 5) };
        await Assert.ThrowsAsync<IOException>(() => store.ApplyApBatchAsync(context, failed, default));
        var receipt = await store.InterruptRunAsync(context, default);
        Assert.True(receipt.AlreadyApplied);
        Assert.Equal(RunOutcome.Failed, receipt.Outcome);
        var restarted = new SqliteCollectionStore(location, gate, clock);
        var checkpoint = Assert.IsType<SourceCheckpoint>(await restarted.GetSourceCheckpointAsync(location.DatasetId, "demo-ap-v1", default));
        Assert.Equal(5, checkpoint.Position);
        var next = await restarted.BeginRunAsync(profile, CollectionKind.ApStatus, RunTrigger.Startup, default);
        Assert.Equal(6, (await new ScenarioApSource(clock, restarted).CollectAsync(next, default)).Checkpoint!.Position);
    }

    [Fact]
    public async Task VersionOneDatabase_UpgradesWithoutDiscardingAuthenticationState()
    {
        using var directory = new TemporaryDirectory();
        var location = StoragePaths.Open(AppMode.Demo, directory.Path);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory.Path, "monitor.db"), Pooling = false }.ToString()))
        {
            connection.Open();
            using var resource = typeof(SchemaMigrator).Assembly.GetManifestResourceStream("AirWaveApMonitor.Infrastructure.Storage.Migrations.001_core.sql")!;
            using var reader = new StreamReader(resource);
            using var command = connection.CreateCommand();
            command.CommandText = reader.ReadToEnd(); command.ExecuteNonQuery();
            SqliteAuthenticationStateStore.EnsureSchema(connection);
            command.CommandText = "INSERT INTO dataset VALUES($dataset,0,1); INSERT INTO schema_migrations VALUES(1,'2026-11-16T00:00:00Z'); PRAGMA user_version=1;";
            command.Parameters.AddWithValue("$dataset", location.DatasetId.ToString("D")); command.ExecuteNonQuery();
        }
        using var gate = new SemaphoreSlim(1, 1);
        var auth = new SqliteAuthenticationStateStore(location, gate);
        var binding = new AuthenticationBinding(location.DatasetId, ScenarioApSource.DemoOrigin, 1);
        await auth.SaveStopAsync(new(binding, AuthenticationStopReason.LoginRejected, DateTimeOffset.UtcNow), default);
        await new SqliteCollectionStore(location, gate).InitializeAsync(default);
        Assert.NotNull((await auth.ReadAsync(binding, default)).Stop);
        using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory.Path, "monitor.db"), Pooling = false }.ToString());
        verify.Open(); using var check = verify.CreateCommand(); check.CommandText = "PRAGMA user_version";
        Assert.Equal(2L, check.ExecuteScalar());
    }
}
