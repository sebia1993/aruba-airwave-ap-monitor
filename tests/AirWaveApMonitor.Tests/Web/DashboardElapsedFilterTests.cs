using System.Net;
using System.Text;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using Microsoft.VisualBasic.FileIO;

namespace AirWaveApMonitor.Tests.Web;

public sealed class DashboardElapsedFilterTests
{
    [Fact]
    public async Task ElapsedFilter_DashboardAndEntireCsvUseSamePinnedRowsBeforePaging()
    {
        using var f = new ReadFixture();
        await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        f.AdvanceTime(TimeSpan.FromMinutes(55));
        var run = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Down)]);
        await using var host = await ReadApiHost.Start(f);
        const string filters = "&filter=OpenIncidents&minimumOpenElapsedSeconds=3600&pageSize=1";
        var page = await Json(host, $"/api/dashboard?runId={run.RunId}{filters}");
        Assert.Equal(1, page.GetProperty("totalRows").GetInt32());
        Assert.Equal(2, page.GetProperty("summary").GetProperty("confirmedDown").GetInt32());
        Assert.Equal("100", Assert.Single(page.GetProperty("rows").EnumerateArray()).GetProperty("sourceApId").GetString());
        var emptyPage = await Json(host, $"/api/dashboard?runId={run.RunId}{filters}&page=2");
        Assert.Equal(1, emptyPage.GetProperty("totalRows").GetInt32());
        Assert.Empty(emptyPage.GetProperty("rows").EnumerateArray());

        // A later recovery and process clock cannot rewrite or age this export's selection.
        f.AdvanceTime(TimeSpan.FromDays(2));
        await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Down)]);
        var response = await host.Client.GetAsync($"/api/export.csv?runId={run.RunId}{filters}&page=2");
        response.EnsureSuccessStatusCode();
        Assert.Equal("1", response.Headers.GetValues("X-AirWave-Row-Count").Single());
        Assert.Equal(run.RunId.ToString(), response.Headers.GetValues("X-AirWave-Run-Id").Single());
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        using var parser = new TextFieldParser(new StringReader(Encoding.UTF8.GetString(bytes.AsSpan(3))));
        parser.SetDelimiters(",");
        var headers = parser.ReadFields()!;
        Assert.Equal("QueryContext", parser.ReadFields()![Array.IndexOf(headers, "RecordType")]);
        var row = parser.ReadFields()!; Assert.Equal("Ap", row[Array.IndexOf(headers, "RecordType")]);
        Assert.Equal("100", row[Array.IndexOf(headers, "원천 AP ID")]);
        Assert.Equal("Down", row[Array.IndexOf(headers, "이번 상태")]);
        Assert.Equal(run.RunId.ToString(), row[Array.IndexOf(headers, "수집 회차")]);
        Assert.True(parser.EndOfData);
    }

    [Fact]
    public async Task ElapsedFilter_KeepsUnknownOpenIncidentButRejectsClockRollbackAndRecoveredRows()
    {
        using var f = new ReadFixture();
        await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        f.AdvanceTime(TimeSpan.FromMinutes(55));
        var unknown = await f.Apply([], outcome: RunOutcome.Failed);
        f.AdvanceTime(TimeSpan.FromHours(-2));
        var rollback = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        f.AdvanceTime(TimeSpan.FromDays(2));
        var uncertainElapsed = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        var recovery = await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Up)]);
        await using var host = await ReadApiHost.Start(f);
        var duringGap = await Json(host, $"/api/dashboard?runId={unknown.RunId}&minimumOpenElapsedSeconds=3600");
        Assert.Equal("100", Assert.Single(duringGap.GetProperty("rows").EnumerateArray()).GetProperty("sourceApId").GetString());
        Assert.Equal(JsonValueKind.Null, duringGap.GetProperty("rows")[0].GetProperty("currentState").ValueKind);
        Assert.Equal(JsonValueKind.Null, duringGap.GetProperty("summary").GetProperty("confirmedDown").ValueKind);
        var rawRollback = await Json(host, $"/api/dashboard?runId={rollback.RunId}");
        Assert.True(rawRollback.GetProperty("rows")[0].GetProperty("timeNeedsReview").GetBoolean());
        foreach (var pinned in new[] { rollback.RunId, uncertainElapsed.RunId, recovery.RunId })
        {
            var filtered = await Json(host, $"/api/dashboard?runId={pinned}&minimumOpenElapsedSeconds=1");
            Assert.Equal(0, filtered.GetProperty("totalRows").GetInt32());
            var incidents = await Json(host, $"/api/incidents?asOfRunId={pinned}&minimumOpenElapsedSeconds=1");
            Assert.Equal(0, incidents.GetProperty("totalRows").GetInt32());
        }
    }

    [Theory]
    [InlineData("dashboard", "0")]
    [InlineData("dashboard", "-1")]
    [InlineData("dashboard", "3600&minimumOpenElapsedSeconds=1")]
    [InlineData("export.csv", "0")]
    [InlineData("export.csv", "-1")]
    [InlineData("export.csv", "3600&minimumOpenElapsedSeconds=1")]
    public async Task InvalidElapsedFilter_IsRejectedByDashboardAndCsv(string route, string supplied)
    {
        using var f = new ReadFixture();
        var run = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        await using var host = await ReadApiHost.Start(f);
        var response = await host.Client.GetAsync($"/api/{route}?runId={run.RunId}&minimumOpenElapsedSeconds={supplied}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<JsonElement> Json(ReadApiHost host, string route)
        => JsonDocument.Parse(await host.Client.GetStringAsync(route)).RootElement;
}
