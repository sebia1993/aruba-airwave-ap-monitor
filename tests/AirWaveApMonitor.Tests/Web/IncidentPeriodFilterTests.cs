using System.Net;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Tests.Web;

public sealed class IncidentPeriodFilterTests
{
    [Fact]
    public async Task TransitionCount_ExcludesInitialDownAndCountsWholeFilteredSetBeforePaging()
    {
        using var f = new ReadFixture();
        await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Down)]);
        var firstDown = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Up)]);
        var secondDown = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Up)]);
        var thirdDown = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        await using var host = await ReadApiHost.Start(f);

        var all = await Get(host, thirdDown.RunId, "&pageSize=1&page=2");
        Assert.Equal(4, all.GetProperty("totalRows").GetInt32());
        Assert.Equal(2, all.GetProperty("distinctApCount").GetInt32());
        Assert.Single(all.GetProperty("rows").EnumerateArray());
        Assert.True(all.TryGetProperty("upToDownTransitionCount", out var count));
        Assert.Equal(3, count.GetInt32());
        Assert.Equal(1, all.GetProperty("initialDownCount").GetInt32());

        var oneAp = await Get(host, thirdDown.RunId, "&search=AP-100&pageSize=1");
        Assert.Equal(3, oneAp.GetProperty("totalRows").GetInt32());
        Assert.Equal(1, oneAp.GetProperty("distinctApCount").GetInt32());
        Assert.Equal(3, oneAp.GetProperty("upToDownTransitionCount").GetInt32());
        Assert.Equal(0, oneAp.GetProperty("initialDownCount").GetInt32());

        var period = await Get(host, thirdDown.RunId,
            $"&fromUtc={Time(secondDown.StartedAtUtc)}&toUtc={Time(thirdDown.StartedAtUtc)}&pageSize=1");
        Assert.Equal(2, period.GetProperty("totalRows").GetInt32());
        Assert.Equal(2, period.GetProperty("upToDownTransitionCount").GetInt32());
        var recovered = await Get(host, thirdDown.RunId, "&status=Recovered");
        Assert.Equal(3, recovered.GetProperty("totalRows").GetInt32());
        Assert.Equal(2, recovered.GetProperty("upToDownTransitionCount").GetInt32());
        var past = await Get(host, firstDown.RunId);
        Assert.Equal(2, past.GetProperty("totalRows").GetInt32());
        Assert.Equal(1, past.GetProperty("upToDownTransitionCount").GetInt32());
        Assert.Equal(1, (await f.Dashboard(thirdDown.RunId)).Summary.ConfirmedDown);
    }

    [Fact]
    public async Task MinimumOpenElapsed_UsesPinnedRunTimeAndDoesNotTreatRecoveryAsOpen()
    {
        using var f = new ReadFixture();
        var first = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        f.AdvanceTime(TimeSpan.FromMinutes(55));
        var oneHour = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Down)]);
        await using var host = await ReadApiHost.Start(f);

        var filtered = await Get(host, oneHour.RunId, "&minimumOpenElapsedSeconds=3600");
        Assert.Equal(1, filtered.GetProperty("totalRows").GetInt32());
        var row = Assert.Single(filtered.GetProperty("rows").EnumerateArray());
        Assert.Equal("100", row.GetProperty("sourceApId").GetString());
        Assert.Equal(3600, row.GetProperty("observedElapsedSeconds").GetDouble());
        var id = row.GetProperty("incidentId").GetGuid();
        var tooSoon = await host.Client.GetAsync($"/api/incidents/{id}?asOfRunId={oneHour.RunId}&minimumOpenElapsedSeconds=3601");
        Assert.Equal(HttpStatusCode.NotFound, tooSoon.StatusCode);

        // Advancing the process clock and storing a future recovery cannot age a pinned run.
        f.AdvanceTime(TimeSpan.FromDays(10));
        var recovered = await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Down)]);
        Assert.Equal(0, (await Get(host, first.RunId, "&minimumOpenElapsedSeconds=3600")).GetProperty("totalRows").GetInt32());
        Assert.Equal(0, (await Get(host, oneHour.RunId, "&minimumOpenElapsedSeconds=3601")).GetProperty("totalRows").GetInt32());
        var current = await Get(host, recovered.RunId, "&minimumOpenElapsedSeconds=86400");
        Assert.Equal("200", Assert.Single(current.GetProperty("rows").EnumerateArray()).GetProperty("sourceApId").GetString());
        Assert.Equal(0, (await Get(host, recovered.RunId, "&status=Recovered&minimumOpenElapsedSeconds=3600")).GetProperty("totalRows").GetInt32());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    [InlineData("3600&minimumOpenElapsedSeconds=86400")]
    public async Task InvalidMinimumOpenElapsed_IsRejected(string supplied)
    {
        using var f = new ReadFixture();
        var run = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        await using var host = await ReadApiHost.Start(f);
        var response = await host.Client.GetAsync($"/api/incidents?asOfRunId={run.RunId}&minimumOpenElapsedSeconds={supplied}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PeriodCounters_KeepPastScopeAndApSelectionAfterTargetRemovalAndRename()
    {
        using var f = new ReadFixture();
        await f.Apply([f.Ap("100", ApState.Up, "past-name"), f.Ap("200", ApState.Up)]);
        var down = await f.Apply([f.Ap("100", ApState.Down, "past-name"), f.Ap("200", ApState.Up)]);
        var ap = (await f.Dashboard(down.RunId)).Rows.Single(row => row.SourceApId == "100");
        await f.Apply([f.Ap("100", ApState.Up, "future-name"), f.Ap("200", ApState.Up)]);
        var laterScope = await f.Store.BeginRunAsync(f.Profile(2, "200"), CollectionKind.ApStatus, RunTrigger.Manual, default);
        await f.Store.ApplyApBatchAsync(laterScope, new(RunOutcome.Succeeded, [f.Ap("200", ApState.Up)], [],
            f.Now, ObservationCompleteness.Complete, []), default);
        await using var host = await ReadApiHost.Start(f, currentScope: 2);

        var old = await Get(host, down.RunId,
            $"&scopeVersion=1&apId={ap.ApId}&search=past-name&fromUtc={Time(down.StartedAtUtc)}&toUtc={Time(down.StartedAtUtc)}");
        Assert.Equal(1, old.GetProperty("totalRows").GetInt32());
        Assert.Equal(1, old.GetProperty("upToDownTransitionCount").GetInt32());
        Assert.Equal(0, old.GetProperty("initialDownCount").GetInt32());
        Assert.Equal("Open", old.GetProperty("rows")[0].GetProperty("status").GetString());
        Assert.Equal(0, (await Get(host, laterScope.RunId)).GetProperty("upToDownTransitionCount").GetInt32());
        Assert.Equal(0, (await Get(host, down.RunId, "&search=future-name")).GetProperty("totalRows").GetInt32());
        Assert.Equal(0, (await Get(host, down.RunId, $"&fromUtc={Time(down.StartedAtUtc.AddTicks(1))}")).GetProperty("upToDownTransitionCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/api/incidents?asOfRunId={down.RunId}&scopeVersion=2")).StatusCode);
    }

    [Fact]
    public async Task MinimumOpenElapsed_DoesNotIncludeTrackingEndedIncidentWithLongElapsed()
    {
        using var f = new ReadFixture();
        await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        f.AdvanceTime(TimeSpan.FromMinutes(55));
        var replaced = await f.Apply([f.Ap("100", ApState.Up, serial: "replacement"), f.Ap("200", ApState.Up)]);
        await using var host = await ReadApiHost.Start(f);
        var all = await Get(host, replaced.RunId);
        var ended = Assert.Single(all.GetProperty("rows").EnumerateArray());
        Assert.Equal("TrackingEnded", ended.GetProperty("status").GetString());
        Assert.Equal(3600, ended.GetProperty("observedElapsedSeconds").GetDouble());
        var filtered = await Get(host, replaced.RunId, "&minimumOpenElapsedSeconds=1");
        Assert.Equal(0, filtered.GetProperty("totalRows").GetInt32());
        Assert.Equal(0, filtered.GetProperty("initialDownCount").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidMinimumOpenElapsed_IsRejectedByDirectStoreQueries(long minimum)
    {
        using var f = new ReadFixture();
        var run = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Store.GetIncidentsAsync(
            new(f.Location.DatasetId, 1, run.RunId, MinimumOpenElapsedSeconds: minimum), default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Store.GetDashboardAsync(
            new(f.Location.DatasetId, 1, run.RunId, MinimumOpenElapsedSeconds: minimum), default));
    }

    [Fact]
    public async Task PeriodRange_IsValidatedAndVeryLargeMinimumIsAnEmptyValidSelection()
    {
        using var f = new ReadFixture();
        var run = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        await using var host = await ReadApiHost.Start(f);
        var reverse = $"&fromUtc={Time(run.StartedAtUtc.AddHours(1))}&toUtc={Time(run.StartedAtUtc)}";
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync($"/api/incidents?asOfRunId={run.RunId}{reverse}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync($"/api/runs?scopeVersion=1{reverse}")).StatusCode);
        var empty = await Get(host, run.RunId, "&minimumOpenElapsedSeconds=9223372036854775807");
        Assert.Equal(0, empty.GetProperty("totalRows").GetInt32());
        Assert.Equal(0, empty.GetProperty("upToDownTransitionCount").GetInt32());
    }

    private static string Time(DateTimeOffset time) => Uri.EscapeDataString(time.ToString("O"));
    private static async Task<JsonElement> Get(ReadApiHost host, long run, string query = "")
        => JsonDocument.Parse(await host.Client.GetStringAsync($"/api/incidents?asOfRunId={run}{query}")).RootElement;
}
