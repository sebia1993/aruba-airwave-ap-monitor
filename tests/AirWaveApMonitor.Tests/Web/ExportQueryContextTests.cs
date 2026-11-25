using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using Microsoft.VisualBasic.FileIO;

namespace AirWaveApMonitor.Tests.Web;

public sealed class ExportQueryContextTests
{
    [Fact]
    public async Task CsvRepeatAndFollowUp_AgreeWithPinnedDashboard_AndDoNotLeakFutureClear()
    {
        using var f = new ReadFixture();
        await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Down)]);
        var ap = (await f.Dashboard()).Rows.Single(r => r.SourceApId == "100");
        await f.Store.SaveInventoryAsync(new(Guid.NewGuid(), ap.ApId, ap.DeviceGenerationId, 0,
            ImmutableDictionary<InventoryField,string>.Empty.Add(InventoryField.FollowUp,"true"), []), default);
        await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Down)]);
        var recovered = await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Down)]);
        await f.Store.SaveInventoryAsync(new(Guid.NewGuid(), ap.ApId, ap.DeviceGenerationId, 1,
            ImmutableDictionary<InventoryField,string>.Empty, [InventoryField.FollowUp], ClearReason:"checked"), default);
        await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Down)]);
        var expected = Assert.Single((await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1, recovered.RunId,
            Filter: DashboardFilter.RepeatSuspected, RepeatThreshold: 1, PriorityThreshold: 2), default)).Rows);
        await using var host = await ReadApiHost.Start(f);
        foreach (var filter in new[] {"RepeatSuspected", "FollowUp"})
        {
            using var response = await host.Client.GetAsync($"/api/export.csv?runId={recovered.RunId}&scopeVersion=1&filter={filter}&repeatThreshold=1&priorityThreshold=2&page=99&pageSize=1");
            response.EnsureSuccessStatusCode();
            Assert.Equal("1", response.Headers.GetValues("X-AirWave-Row-Count").Single());
            using var parser = new TextFieldParser(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
            parser.SetDelimiters(","); parser.HasFieldsEnclosedInQuotes = true;
            var headers = parser.ReadFields()!;
            var context = headers.Zip(parser.ReadFields()!).ToDictionary(p => p.First, p => p.Second);
            var row = headers.Zip(parser.ReadFields()!).ToDictionary(p => p.First, p => p.Second);
            using var json = JsonDocument.Parse(context["QueryConditions"]);
            Assert.Equal(filter, json.RootElement.GetProperty("filter").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("repeatThreshold").GetInt32());
            Assert.Equal(2, json.RootElement.GetProperty("priorityThreshold").GetInt32());
            Assert.Equal("100", row["원천 AP ID"]);
            Assert.Equal("Up", row["이번 상태"]);
            Assert.Equal("지정", row["사용자 관리 대상"]);
            Assert.Equal("1", row["최근 24시간 신규 Down 횟수"]);
            Assert.Equal(expected.StatusHistory!.RecentDownAtUtc, DateTimeOffset.Parse(row["최근 Down 감지 UTC"]));
            Assert.Equal(expected.StatusHistory.LastUpObservedAtUtc, DateTimeOffset.Parse(row["마지막 Up 관측 UTC"]));
            Assert.Equal(expected.StatusHistory.LastRecoveryAtUtc, DateTimeOffset.Parse(row["최근 Up 복구 확인 UTC"]));
            Assert.True(parser.EndOfData);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CsvPreservesQueryAndPinnedObservationEvenWhenNoApMatches(bool empty)
    {
        using var f = new ReadFixture();
        const string name = "=name,\"quoted\"\n한글";
        await f.Apply([f.Ap("100", ApState.Down, name), f.Ap("200", ApState.Up)]);
        f.SetManualStatus("100", OperationalStatus.Operating);
        f.AdvanceTime(TimeSpan.FromMinutes(55));
        var run = await f.Apply([f.Ap("100", ApState.Down, name), f.Ap("200", ApState.Down)]);
        var search = empty ? "=no-match,\"quoted\"\n한글" : name;
        await using var host = await ReadApiHost.Start(f);
        var url = $"/api/export.csv?runId={run.RunId}&scopeVersion=1&filter=OpenIncidents&sort=FirstDownAt&descending=false" +
            $"&managementStatus=Operating&hidePlanned=true&minimumOpenElapsedSeconds=3600&search={Uri.EscapeDataString(search)}&page=42&pageSize=1";
        var response = await host.Client.GetAsync(url); response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        Assert.Equal(empty ? "0" : "1", response.Headers.GetValues("X-AirWave-Row-Count").Single());
        using var parser = new TextFieldParser(new StringReader(Encoding.UTF8.GetString(bytes.AsSpan(3))))
            { TrimWhiteSpace = false, HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(","); var headers = parser.ReadFields()!;
        Assert.Contains("RecordType", headers); Assert.Contains("QueryConditions", headers);
        var context = parser.ReadFields()!;
        Assert.Equal(headers.Length, context.Length);
        string Field(string key) => context[Array.IndexOf(headers, key)];
        Assert.Equal("QueryContext", Field("RecordType"));
        Assert.Equal("", Field("원천 AP ID")); Assert.Equal("", Field("AP 이름"));
        Assert.Equal(run.RunId.ToString(), Field("수집 회차")); Assert.Equal("1", Field("범위 버전"));
        Assert.Equal(run.StartedAtUtc, DateTimeOffset.Parse(Field("확정 UTC")));
        var query = JsonDocument.Parse(Field("QueryConditions")).RootElement;
        Assert.Equal(f.Location.DatasetId, query.GetProperty("datasetId").GetGuid());
        Assert.Equal(1, query.GetProperty("scopeVersion").GetInt64());
        Assert.Equal(run.RunId, query.GetProperty("runId").GetInt64());
        Assert.Equal(run.StartedAtUtc, query.GetProperty("finalizedAtUtc").GetDateTimeOffset());
        Assert.Equal("AllMatchingRows", query.GetProperty("exportExtent").GetString());
        Assert.Equal("OpenIncidents", query.GetProperty("filter").GetString());
        Assert.Equal(search, query.GetProperty("search").GetString());
        Assert.Equal("FirstDownAt", query.GetProperty("sort").GetString());
        Assert.False(query.GetProperty("descending").GetBoolean());
        Assert.Equal("Operating", query.GetProperty("managementStatus").GetString());
        Assert.True(query.GetProperty("hidePlanned").GetBoolean());
        Assert.Equal(3600, query.GetProperty("minimumOpenElapsedSeconds").GetInt64());
        Assert.Equal(empty ? 0 : 1, query.GetProperty("totalApRows").GetInt32());
        Assert.False(query.TryGetProperty("page", out _));
        if (!empty)
        {
            var ap = parser.ReadFields()!;
            Assert.Equal(headers.Length, ap.Length);
            Assert.Equal("Ap", ap[Array.IndexOf(headers, "RecordType")]);
            Assert.Equal("", ap[Array.IndexOf(headers, "QueryConditions")]);
            Assert.Equal("100", ap[Array.IndexOf(headers, "원천 AP ID")]);
            Assert.Equal("'" + name, ap[Array.IndexOf(headers, "AP 이름")]);
        }
        Assert.True(parser.EndOfData);
    }
}
