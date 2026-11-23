using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Tests.Storage;

public sealed partial class SqliteCollectionStoreTests
{
    [Fact]
    public async Task IncidentTimeline_MatchesOneTargetAndDoesNotJoinOtherIncidentsOnTheSameAp()
    {
        var first = await SeedOperations();
        var point = Guid.NewGuid();
        await store.SavePointAsync(new(Guid.NewGuid(), point, 0, "점검 지점", null, new()), default);
        var workId = Guid.NewGuid();
        var target = new WorkTarget(first.ApId, first.DeviceGenerationId, first.OpenIncidentId);
        await store.SaveWorkAsync(new(Guid.NewGuid(), workId, 0, Work(target, new(InstallationPointId: point)), null), default);
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        var second = (await Dashboard()).Rows.Single(r => r.ApId == first.ApId);
        Assert.NotEqual(first.OpenIncidentId, second.OpenIncidentId);
        await store.SaveWorkAsync(new(Guid.NewGuid(), Guid.NewGuid(), 0,
            Work(new WorkTarget(second.ApId, second.DeviceGenerationId, second.OpenIncidentId)), null), default);

        var filter = new OperationsTimelineQuery(ApId: first.ApId, DeviceGenerationId: first.DeviceGenerationId,
            IncidentId: first.OpenIncidentId);
        var records = await store.GetTimelineAsync(filter, default);
        Assert.Equal(3, records.TotalRows);
        Assert.Equal(new[] { "Recovered", "WorkRegistered", "Created" }, records.Rows.Select(r => r.Action));
        Assert.Equal(workId, records.Rows[1].Work!.WorkId);
        Assert.Empty((await store.GetTimelineAsync(filter with { InstallationPointId = point }, default)).Rows);
        Assert.Equal(2, (await store.GetTimelineAsync(filter with { IncidentId = second.OpenIncidentId }, default)).TotalRows);
    }

    [Fact]
    public async Task IncidentTimeline_HistoricalRunFreezesManualAndAutomaticRecordsEvenWithReversedClock()
    {
        var first = await SeedOperations();
        var firstRun = (await Dashboard()).RunId;
        var target = new WorkTarget(first.ApId, first.DeviceGenerationId, first.OpenIncidentId);
        var note = Guid.NewGuid();
        await store.SaveWorkAsync(new(Guid.NewGuid(), note, 0, Work(target), null), default);
        // No new incident/operation event is required for a run to capture its boundary.
        var stillDown = await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        clock.Set(clock.GetUtcNow().AddHours(-2));
        await store.SaveWorkAsync(new(Guid.NewGuid(), note, 1,
            Work(target) with { Content = "늦게 기록한 조치", ActualWorkAtUtc = clock.GetUtcNow().AddDays(-1) }, "정정"), default);
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));

        var filter = new OperationsTimelineQuery(IncidentId: first.OpenIncidentId);
        var original = await store.GetTimelineAsync(filter with { AsOfRunId = firstRun }, default);
        Assert.Equal("Created", Assert.Single(original.Rows).Action);
        var next = await store.GetTimelineAsync(filter with { AsOfRunId = stillDown.RunId }, default);
        Assert.False(next.HistoricalManualBoundaryUnavailable);
        Assert.Equal(new[] { "WorkRegistered", "Created" }, next.Rows.Select(e => e.Action));
        Assert.Equal(1, next.Rows[0].Work!.Version);
        var live = await store.GetTimelineAsync(filter, default);
        Assert.Contains(live.Rows, e => e.Work?.Version == 2);
        Assert.Contains(live.Rows, e => e.Action == "Recovered");
        await Assert.ThrowsAsync<OperationsConflictException>(() =>
            store.GetTimelineAsync(filter with { AsOfRunId = firstRun, ThroughEventId = live.ThroughEventId }, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.GetTimelineAsync(filter with { AsOfRunId = long.MaxValue }, default));
    }

    [Fact]
    public async Task IncidentTimeline_LegacyRunShowsOnlyProvenCollectionEventsWithExplicitManualLimit()
    {
        LoadSchemaFiveHistory();
        var old = (await Dashboard(1)).Rows.Single(r => r.SourceApId == "100");
        var filter = new OperationsTimelineQuery(IncidentId: old.OpenIncidentId);
        var before = await store.GetTimelineAsync(filter with { AsOfRunId = 1 }, default);
        await store.SaveWorkAsync(new(Guid.NewGuid(), Guid.NewGuid(), 0,
            Work(new WorkTarget(old.ApId, old.DeviceGenerationId, old.OpenIncidentId)), null), default);
        var historic = await store.GetTimelineAsync(filter with { AsOfRunId = 1 }, default);
        Assert.Equal(before.ThroughEventId, historic.ThroughEventId);
        Assert.True(historic.HistoricalManualBoundaryUnavailable);
        Assert.Equal("Created", Assert.Single(historic.Rows).Action);
        Assert.Contains((await store.GetTimelineAsync(filter, default)).Rows, e => e.Work is not null);
    }
}
