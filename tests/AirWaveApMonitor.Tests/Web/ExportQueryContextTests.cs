using System.Text;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using Microsoft.VisualBasic.FileIO;

namespace AirWaveApMonitor.Tests.Web;

public sealed class ExportQueryContextTests
{
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
