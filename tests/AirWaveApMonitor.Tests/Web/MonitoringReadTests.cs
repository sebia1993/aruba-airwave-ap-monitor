using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Tests.Web;

public sealed class MonitoringReadTests
{
    [Fact]
    public async Task StatusHistory_CountsAllRecentTransitionsBeforePagination_AndPreservesHistoricalCutoff()
    {
        using var f = new ReadFixture();
        await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Up)]);
        var firstDown = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        await f.Apply([], outcome: RunOutcome.Failed);
        var recovery = await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Up)]);
        var secondDown = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        var ap = (await f.Dashboard()).Rows.Single(r => r.SourceApId == "100");
        var current = await f.Store.GetApDetailAsync(new(f.Location.DatasetId, 1, secondDown.RunId, ap.ApId, 1), default);
        Assert.True(current.MoreIncidents);
        var history = Assert.IsType<ApStatusHistorySummary>(current.StatusHistory);
        Assert.Equal(2, history.NewDownCount24Hours);
        Assert.Equal(secondDown.StartedAtUtc, history.RecentDownAtUtc);
        Assert.Equal(recovery.StartedAtUtc, history.LastUpObservedAtUtc);
        Assert.Equal(recovery.StartedAtUtc, history.LastRecoveryAtUtc);
        var past = await f.Store.GetApDetailAsync(new(f.Location.DatasetId, 1, firstDown.RunId, ap.ApId, 1), default);
        Assert.Equal(1, past.StatusHistory!.NewDownCount24Hours);
        Assert.Equal(firstDown.StartedAtUtc, past.StatusHistory.RecentDownAtUtc);
        Assert.NotEqual(recovery.StartedAtUtc, past.StatusHistory.LastRecoveryAtUtc);
        f.AdvanceTime(TimeSpan.FromHours(25));
        var later = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        var aged = await f.Store.GetApDetailAsync(new(f.Location.DatasetId, 1, later.RunId, ap.ApId, 1), default);
        Assert.Equal(0, aged.StatusHistory!.NewDownCount24Hours);
        Assert.Equal(secondDown.StartedAtUtc, aged.StatusHistory.RecentDownAtUtc);
    }

    [Fact]
    public async Task RepeatDashboard_IncludesRecoveredAp_CountsBeforePage_AndPreservesSelectedGenerationAndTime()
    {
        using var f = new ReadFixture();
        await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Down)]);
        var before = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Down)]);
        for (var i = 0; i < 2; i++)
        {
            await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Down)]);
            await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Down)]);
        }
        var last = await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Down)]);
        var query = new DashboardQuery(f.Location.DatasetId, 1, last.RunId, Filter: DashboardFilter.RepeatSuspected,
            Sort: DashboardSort.RecentDownCount, Descending: true, PageSize: 1);
        var view = await f.Store.GetDashboardAsync(query, default);
        Assert.Equal(1, view.TotalRows);
        Assert.Equal(1, view.RepeatSuspectedCount);
        Assert.Equal(0, view.RepeatPriorityCount);
        var row = Assert.Single(view.Rows);
        Assert.Equal("100", row.SourceApId); Assert.Equal(ApState.Up, row.CurrentState);
        Assert.Equal(3, row.StatusHistory!.NewDownCount24Hours);
        Assert.Equal(last.StartedAtUtc, row.StatusHistory.LastUpObservedAtUtc);
        Assert.Equal(1, (await f.Store.GetDashboardAsync(query with { Page = 2 }, default)).TotalRows);
        Assert.Empty((await f.Store.GetDashboardAsync(query with { Page = 2 }, default)).Rows);
        var all = await f.Store.GetDashboardAsync(query with { RepeatThreshold = 1, PriorityThreshold = 3 }, default);
        Assert.Equal(2, all.TotalRows); Assert.Equal(2, all.RepeatSuspectedCount); Assert.Equal(1, all.RepeatPriorityCount);
        var priority = await f.Store.GetDashboardAsync(query with { Filter = DashboardFilter.RepeatPriority, RepeatThreshold = 1, PriorityThreshold = 3 }, default);
        Assert.Equal("100", Assert.Single(priority.Rows).SourceApId);
        var earlier = await f.Store.GetDashboardAsync(query with { RunId = before.RunId }, default);
        Assert.Equal(0, earlier.TotalRows);
        var gap = await f.Apply([], outcome: RunOutcome.Failed);
        var unknown = await f.Store.GetDashboardAsync(query with { RunId = gap.RunId }, default);
        Assert.Null(Assert.Single(unknown.Rows).CurrentState);
        Assert.Equal(3, unknown.Rows[0].StatusHistory!.NewDownCount24Hours);
        var replacement = await f.Apply([f.Ap("100", ApState.Down, serial:"REPLACEMENT"), f.Ap("200", ApState.Down)]);
        Assert.Equal(0, (await f.Store.GetDashboardAsync(query with { RunId = replacement.RunId }, default)).TotalRows);
        Assert.Equal(1, (await f.Store.GetDashboardAsync(query, default)).TotalRows);
    }

    [Fact]
    public async Task UnknownAp_RemainsInOpenIncidentView()
    {
        using var f = new ReadFixture();
        await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        await f.Apply([], outcome: RunOutcome.Failed);
        var view = await f.Dashboard(filter: DashboardFilter.OpenIncidents);
        Assert.Equal("100", Assert.Single(view.Rows).SourceApId);
        Assert.Null(view.Rows[0].CurrentState);
        Assert.Null(view.Summary.ConfirmedDown);
        Assert.Equal(2, (await f.Dashboard(filter: DashboardFilter.Unknown)).TotalRows);
    }

    [Theory]
    [InlineData("site-100")]
    [InlineData("building-100")]
    [InlineData("floor-100")]
    [InlineData("zone-100")]
    [InlineData("detail-100")]
    [InlineData("controller-100")]
    [InlineData("198.51.100.1")]
    [InlineData("switch-100")]
    [InlineData("port-index-100")]
    [InlineData("port-name-100")]
    [InlineData("ap-interface-100")]
    public async Task StructuredLocationControllerAndUpstreamSearch_UsesSelectedMetadata(string search)
    {
        using var f = new ReadFixture();
        var first = await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Up)]);
        var view = await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1, first.RunId, search), default);
        Assert.Equal("100", Assert.Single(view.Rows).SourceApId);
    }

    [Fact]
    public async Task PlannedHide_CountsOnlyMatchingManualWork_WithoutChangingSummary()
    {
        using var f = new ReadFixture();
        await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Up)]);
        f.SetManualStatus("100", OperationalStatus.PlannedWork);
        var run = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Down)]);
        var view = await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1, run.RunId,
            Filter: DashboardFilter.Down, HidePlanned: true), default);
        Assert.Equal(1, view.HiddenPlannedCount);
        Assert.Equal("200", Assert.Single(view.Rows).SourceApId); // source maintenance alone is not manual planned work.
        Assert.Equal(2, view.Summary.ConfirmedDown);
        var empty = await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1, run.RunId, "AP-100", HidePlanned: true), default);
        Assert.Empty(empty.Rows); Assert.Equal(1, empty.HiddenPlannedCount);
        var management = await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1, run.RunId,
            ManagementStatus: OperationalStatus.PlannedWork), default);
        Assert.Equal("100", Assert.Single(management.Rows).SourceApId);
    }

    [Fact]
    public async Task PastDetail_DoesNotLeakFutureRecoveryOrGapClosure()
    {
        using var f = new ReadFixture();
        var down = await f.Apply([f.Ap("100", ApState.Down, "original"), f.Ap("200", ApState.Up)]);
        var ap = (await f.Dashboard()).Rows.Single(r => r.SourceApId == "100");
        var gap = await f.Apply([], outcome: RunOutcome.Failed);
        var up = await f.Apply([f.Ap("100", ApState.Up, "renamed"), f.Ap("200", ApState.Up)]);
        var old = await f.Store.GetApDetailAsync(new(f.Location.DatasetId, 1, down.RunId, ap.ApId), default);
        Assert.Equal("original", old.Row.Device.Name);
        var incident = Assert.Single(old.Incidents);
        Assert.Equal(IncidentStatus.Open, incident.Status); Assert.Null(incident.Recovery);
        Assert.False(incident.Tracking.HasObservationGap); Assert.Empty(old.ObservationGaps);
        var during = await f.Store.GetApDetailAsync(new(f.Location.DatasetId, 1, gap.RunId, ap.ApId), default);
        Assert.Null(Assert.Single(during.ObservationGaps).ResumedRunId);
        Assert.Null(Assert.Single(during.Incidents).Tracking.RecoveredRunId);
        var recovered = await f.Store.GetApDetailAsync(new(f.Location.DatasetId, 1, up.RunId, ap.ApId), default);
        Assert.Equal(up.RunId, Assert.Single(recovered.ObservationGaps).ResumedRunId);
        Assert.Equal(IncidentStatus.Recovered, Assert.Single(recovered.Incidents).Status);
        Assert.Equal(600, recovered.Incidents[0].ObservedElapsedSeconds);
        await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Up)]);
        Assert.Equal(600, (await f.Store.GetIncidentsAsync(new(f.Location.DatasetId, 1, up.RunId), default)).Rows[0].ObservedElapsedSeconds);
    }

    [Fact]
    public async Task PastDetail_DoesNotLeakFutureReplacementOrTrackingEnd()
    {
        using var f = new ReadFixture();
        var oldRun = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        var oldRow = (await f.Dashboard()).Rows.Single(r => r.SourceApId == "100");
        var newer = await f.Apply([f.Ap("100", ApState.Up, serial: "REPLACEMENT"), f.Ap("200", ApState.Up)]);
        var detail = await f.Store.GetApDetailAsync(new(f.Location.DatasetId, 1, oldRun.RunId, oldRow.ApId), default);
        Assert.Equal(oldRow.DeviceGenerationId, detail.Row.DeviceGenerationId);
        Assert.Null(Assert.Single(detail.Incidents).Tracking.TrackingEndedRunId);
        Assert.Equal(IncidentStatus.Open, detail.Incidents[0].Status);
        var current = await f.Store.GetApDetailAsync(new(f.Location.DatasetId, 1, newer.RunId, oldRow.ApId), default);
        Assert.NotEqual(oldRow.DeviceGenerationId, current.Row.DeviceGenerationId);
        Assert.Empty(current.Incidents);
        var history = await f.Store.GetIncidentsAsync(new(f.Location.DatasetId, 1, newer.RunId), default);
        Assert.Equal(IncidentStatus.TrackingEnded, Assert.Single(history.Rows).Status);
    }

    [Fact]
    public async Task RunPages_IncludeFailedInterruptedAndRunning_KeepWatermarkAndScopes()
    {
        using var f = new ReadFixture();
        var first = await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Up)]);
        var second = await f.Apply([], scope: 2, outcome: RunOutcome.Failed);
        var interrupted = await f.Store.BeginRunAsync(f.Profile(2), CollectionKind.ApStatus, RunTrigger.Manual, default);
        await f.Store.InterruptRunAsync(interrupted, default);
        var page = await f.Store.GetRunsAsync(new(f.Location.DatasetId, PageSize: 2), default);
        Assert.Equal(new[] { interrupted.RunId, second.RunId }, page.Rows.Select(r => r.RunId));
        Assert.Equal(RunLifecycle.Interrupted, page.Rows[0].Lifecycle); Assert.Null(page.Rows[0].Summary!.ConfirmedDown);
        Assert.Equal(new long[] { 2, 1 }, page.AvailableScopes);
        var running = await f.Store.BeginRunAsync(f.Profile(2), CollectionKind.ApStatus, RunTrigger.Manual, default);
        var oldPage = await f.Store.GetRunsAsync(new(f.Location.DatasetId, Page: 2, PageSize: 2, ThroughRunId: page.ThroughRunId), default);
        Assert.Equal(first.RunId, Assert.Single(oldPage.Rows).RunId);
        var current = await f.Store.GetRunAsync(f.Location.DatasetId, running.RunId, default);
        Assert.Equal(RunLifecycle.Running, current.Lifecycle); Assert.Null(current.Summary); Assert.Null(current.Outcome);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Store.GetApDetailAsync(new(f.Location.DatasetId, 1, second.RunId, Guid.NewGuid()), default));
    }
}
