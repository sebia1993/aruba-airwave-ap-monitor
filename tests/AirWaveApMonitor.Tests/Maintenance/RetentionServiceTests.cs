using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Tests.Maintenance.BackupServiceTests;

namespace AirWaveApMonitor.Tests.Maintenance;

public sealed class RetentionServiceTests
{
    [Fact]
    public async Task PruneAfter90Days_PreservesHistoricalRunsAndOpenIncidentGapDetails()
    {
        using var f = await Fixture.Create();
        var oldRun = await f.Observe(ApState.Down);
        var failing = await f.Store.BeginRunAsync(f.Profile, CollectionKind.ApStatus, RunTrigger.Manual, default);
        await f.Store.ApplyApBatchAsync(failing, new(RunOutcome.Failed, [], [], f.Clock.GetUtcNow(), ObservationCompleteness.Unknown, []), default);
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        await f.Observe(ApState.Down); // closes the gap while its incident remains open
        await f.Store.RecordOperationalLogAsync("BackupSucceeded", default);
        var historic = JsonSerializer.Serialize(await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1, oldRun), default));
        f.Clock.Advance(TimeSpan.FromDays(91));
        var retention = new RetentionService(f.Location, f.Gate, f.Store);
        var first = await retention.PruneAsync(f.Clock.GetUtcNow(), default);
        Assert.Equal(3, first.ExecutionDetailsDeleted);
        Assert.Equal(1, first.OperationalLogsDeleted);
        Assert.Equal(0, first.ClosedGapDetailsDeleted);
        Assert.Equal(1, Fixture.Count(f.DatabasePath, "observation_gaps"));
        Assert.Equal(3, Fixture.Count(f.DatabasePath, "runs"));
        Assert.Equal(historic, JsonSerializer.Serialize(await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1, oldRun), default)));
        await f.Observe(ApState.Up);
        var second = await retention.PruneAsync(f.Clock.GetUtcNow(), default);
        Assert.Equal(1, second.ClosedGapDetailsDeleted);
        Assert.Equal(1, Fixture.Count(f.DatabasePath, "run_execution_details"));
        Assert.Equal(2, Fixture.Count(f.DatabasePath, "incident_snapshots"));
        using var c = f.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT tracking_json FROM incidents";
        Assert.True(JsonDocument.Parse((string)cmd.ExecuteScalar()!).RootElement.GetProperty("HasObservationGap").GetBoolean());
    }

    [Fact]
    public async Task UnclosedGapAndRunningExecutionSurviveExpiry()
    {
        using var f = await Fixture.Create();
        await f.Observe(ApState.Down);
        var failure = await f.Store.BeginRunAsync(f.Profile, CollectionKind.ApStatus, RunTrigger.Manual, default);
        await f.Store.InterruptRunAsync(failure, default);
        var running = await f.Store.BeginRunAsync(f.Profile, CollectionKind.ApStatus, RunTrigger.Manual, default);
        f.Clock.Advance(TimeSpan.FromDays(100));
        await new RetentionService(f.Location, f.Gate, f.Store).PruneAsync(f.Clock.GetUtcNow(), default);
        Assert.Equal(1, Fixture.Count(f.DatabasePath, "observation_gaps"));
        using var c = f.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT lifecycle FROM runs WHERE run_id=$run"; cmd.Parameters.AddWithValue("$run", running.RunId);
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    [Fact]
    public async Task OperationalFileLogs_ExpireAfter30DaysAndRemainBounded()
    {
        using var f = await Fixture.Create();
        var first = new OperationalLog(f.Location, f.Clock);
        Assert.True(first.Record(CollectionErrorCategory.Storage, 1));
        f.Clock.Advance(TimeSpan.FromDays(31));
        var second = new OperationalLog(f.Location, f.Clock);
        Assert.True(second.Record(CollectionErrorCategory.Timeout, 2));
        Assert.Equal(1, second.Prune(f.Clock.GetUtcNow() - TimeSpan.FromDays(30)));
        var path = Path.Combine(f.Directory.Path, "collector-diagnostics.log");
        var text = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("Storage", text);
        Assert.Contains("Timeout", text);
        for (var i = 0; i < 2000; i++) Assert.True(first.Record(CollectionErrorCategory.Storage, i + 1));
        Assert.True(new FileInfo(path).Length < 66 * 1024);
        Assert.True(new FileInfo(path + ".previous").Length < 66 * 1024);
    }
}
