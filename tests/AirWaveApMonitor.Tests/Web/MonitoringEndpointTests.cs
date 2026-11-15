using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirWaveApMonitor.Core.Contracts;
using Microsoft.VisualBasic.FileIO;

namespace AirWaveApMonitor.Tests.Web;

public sealed class MonitoringEndpointTests
{
    private static async Task<JsonElement> Json(HttpClient client, string path)
        => JsonDocument.Parse(await client.GetStringAsync(path)).RootElement;

    [Fact]
    public async Task DashboardAndExport_UseSameRun()
    {
        using var f = new ReadFixture();
        var first = await f.Apply([f.Ap("100", ApState.Down, "당시 이름"), f.Ap("200", ApState.Up)]);
        await using var host = await ReadApiHost.Start(f);
        var dashboard = await Json(host.Client, "/api/dashboard");
        Assert.Equal(first.RunId, dashboard.GetProperty("runId").GetInt64());
        Assert.Equal("InitialDown", dashboard.GetProperty("rows")[0].GetProperty("classification").GetString());
        await f.Apply([f.Ap("100", ApState.Up, "나중 이름"), f.Ap("200", ApState.Up)]);
        var response = await host.Client.GetAsync($"/api/export.csv?runId={first.RunId}");
        response.EnsureSuccessStatusCode();
        Assert.Equal(first.RunId.ToString(), response.Headers.GetValues("X-AirWave-Run-Id").Single());
        var csv = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("당시 이름", csv); Assert.DoesNotContain("나중 이름", csv);
        var row = dashboard.GetProperty("rows")[0];
        var detail = await Json(host.Client, $"/api/aps/{row.GetProperty("apId").GetString()}?runId={first.RunId}");
        Assert.Equal("당시 이름", detail.GetProperty("row").GetProperty("device").GetProperty("name").GetString());
        Assert.Equal("Open", detail.GetProperty("incidents")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Dashboard_UsesLatestFinalApRunIncludingFailure_AndFailedCollectionIsNotZeroDown()
    {
        using var f = new ReadFixture();
        await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        var failure = await f.Apply([], outcome: RunOutcome.Failed);
        await using var host = await ReadApiHost.Start(f);
        var failed = await Json(host.Client, "/api/dashboard");
        Assert.Equal(failure.RunId, failed.GetProperty("runId").GetInt64());
        Assert.Equal("Failed", failed.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("summary").GetProperty("confirmedDown").ValueKind);
        Assert.Equal(1, failed.GetProperty("totalRows").GetInt32());
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("rows")[0].GetProperty("currentState").ValueKind);
        var running = await f.Store.BeginRunAsync(f.Profile(), CollectionKind.ApStatus, RunTrigger.Manual, default);
        Assert.Equal(failure.RunId, (await Json(host.Client, "/api/dashboard")).GetProperty("runId").GetInt64());
        await f.Store.InterruptRunAsync(running, default);
        Assert.Equal("Interrupted", (await Json(host.Client, "/api/dashboard")).GetProperty("lifecycle").GetString());
    }

    [Fact]
    public async Task PastRun_ReturnToCurrent()
    {
        using var f = new ReadFixture();
        var old = await f.Apply([f.Ap("100", ApState.Down, "old"), f.Ap("200", ApState.Up)]);
        var current = await f.Apply([], scope: 2, outcome: RunOutcome.Failed);
        await using var host = await ReadApiHost.Start(f, currentScope: 2);
        var past = await Json(host.Client, $"/api/runs/{old.RunId}");
        Assert.Equal(1, past.GetProperty("scopeVersion").GetInt64());
        Assert.Equal("old", past.GetProperty("rows")[0].GetProperty("device").GetProperty("name").GetString());
        var latest = await Json(host.Client, "/api/dashboard");
        Assert.Equal(current.RunId, latest.GetProperty("runId").GetInt64());
        Assert.Equal("Failed", latest.GetProperty("outcome").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/api/dashboard?runId={old.RunId}&scopeVersion=2")).StatusCode);
        var runs = await Json(host.Client, "/api/runs");
        Assert.Equal(new long[] { 2, 1 }, runs.GetProperty("availableScopes").EnumerateArray().Select(x => x.GetInt64()));
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData(" +SUM(1,2)")]
    [InlineData("\t@cmd")]
    [InlineData("\r\n-2+3")]
    [InlineData("한글,\"인용\"\n다음 줄")]
    public async Task Csv_UsesBomAndEscapesFormulaCells(string name)
    {
        using var f = new ReadFixture();
        var run = await f.Apply([f.Ap("100", ApState.Down, name), f.Ap("200", ApState.Up)]);
        await using var host = await ReadApiHost.Start(f);
        var response = await host.Client.GetAsync($"/api/export.csv?runId={run.RunId}&filter=All");
        response.EnsureSuccessStatusCode(); var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        var text = Encoding.UTF8.GetString(bytes.AsSpan(3)); Assert.Contains("\r\n", text);
        using var parser = new TextFieldParser(new StringReader(text)) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(","); var header = parser.ReadFields()!; var row = parser.ReadFields()!;
        var exported = row[Array.IndexOf(header, "AP 이름")];
        Assert.Equal("한글,\"인용\"\n다음 줄" == name ? name : "'" + name, exported);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Csv_FailedRunPreservesLastEvidenceWithReadableQualityAndOriginalObservationTime()
    {
        using var f = new ReadFixture();
        var observed = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        var failed = await f.Apply([], outcome: RunOutcome.Failed);
        await using var host = await ReadApiHost.Start(f);
        var response = await host.Client.GetAsync($"/api/export.csv?runId={failed.RunId}");
        response.EnsureSuccessStatusCode();
        using var parser = new TextFieldParser(new StringReader(Encoding.UTF8.GetString((await response.Content.ReadAsByteArrayAsync()).AsSpan(3))));
        parser.SetDelimiters(","); var header = parser.ReadFields()!; var row = parser.ReadFields()!;
        Assert.Equal("Unknown", row[Array.IndexOf(header, "이번 상태")]);
        Assert.Equal("Down", row[Array.IndexOf(header, "마지막 유효 상태")]);
        var evidence = JsonDocument.Parse(row[Array.IndexOf(header, "필드별 근거 JSON")]).RootElement.GetProperty("serial_number");
        Assert.Equal("Provided", evidence.GetProperty("availability").GetString());
        Assert.Equal("Valid", evidence.GetProperty("validity").GetString());
        Assert.Equal("Demo", evidence.GetProperty("source").GetString());
        Assert.Equal(observed.StartedAtUtc, evidence.GetProperty("collectedAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task Csv_ExportsWholeFilteredSetWithStableOrdering_NotSelectedPage()
    {
        using var f = new ReadFixture();
        var run = await f.Apply(Enumerable.Range(100, 501).Select(id => f.Ap(id.ToString(), ApState.Up, "same-name")).ToArray());
        await using var host = await ReadApiHost.Start(f);
        var page = await Json(host.Client, $"/api/dashboard?runId={run.RunId}&filter=Up&sort=Name&pageSize=1&page=2");
        Assert.Equal("101", page.GetProperty("rows")[0].GetProperty("sourceApId").GetString());
        var response = await host.Client.GetAsync($"/api/export.csv?runId={run.RunId}&filter=Up&sort=Name&pageSize=1&page=2");
        response.EnsureSuccessStatusCode(); Assert.Equal("501", response.Headers.GetValues("X-AirWave-Row-Count").Single());
        using var parser = new TextFieldParser(new StringReader(Encoding.UTF8.GetString((await response.Content.ReadAsByteArrayAsync()).AsSpan(3))));
        parser.SetDelimiters(","); var header = parser.ReadFields()!; var ids = new List<string>();
        while (!parser.EndOfData) ids.Add(parser.ReadFields()![Array.IndexOf(header, "원천 AP ID")]);
        Assert.Equal(501, ids.Count); Assert.Equal(501, ids.Distinct().Count()); Assert.Equal("100", ids[0]); Assert.Equal("600", ids[^1]);
    }

    [Theory]
    [InlineData("/api/dashboard?filter=99")]
    [InlineData("/api/dashboard?filter=%20")]
    [InlineData("/api/dashboard?filter=-0")]
    [InlineData("/api/dashboard?filter=All,Down")]
    [InlineData("/api/dashboard?page=0")]
    [InlineData("/api/dashboard?page=1&page=2")]
    [InlineData("/api/dashboard?hidePlanned=invalid")]
    [InlineData("/api/runs?fromUtc=tomorrow")]
    [InlineData("/api/incidents")]
    [InlineData("/api/export.csv")]
    public async Task InvalidSelection_Is400RatherThanEmptySuccess(string path)
    {
        using var f = new ReadFixture(); await using var host = await ReadApiHost.Start(f);
        var result = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("invalid_query", JsonDocument.Parse(await result.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task NoFinalRunAndStorageFailure_AreDistinctAndSafe()
    {
        using var f = new ReadFixture(); await using var host = await ReadApiHost.Start(f);
        var none = await host.Client.GetAsync("/api/dashboard"); Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        Assert.Equal("no_final_run", JsonDocument.Parse(await none.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        File.WriteAllText(Path.Combine(f.Location.DataDirectory, "monitor.db"), "synthetic-private-content");
        var failure = await host.Client.GetAsync("/api/dashboard"); Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        var text = await failure.Content.ReadAsStringAsync(); Assert.DoesNotContain(f.Location.DataDirectory, text); Assert.DoesNotContain("synthetic-private", text);
        Assert.Equal("storage_unavailable", JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoredProfileConstructorFailure_IsStorage503WhileValidQueryRemainsValid(bool finalized)
    {
        using var f = new ReadFixture();
        var run = finalized ? await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)])
            : await f.Store.BeginRunAsync(f.Profile(), CollectionKind.ApStatus, RunTrigger.Manual, default);
        await using var host = await ReadApiHost.Start(f);
        using (var connection = f.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT context_json FROM runs WHERE run_id=$run";
            command.Parameters.AddWithValue("$run", run.RunId);
            var json = JsonNode.Parse((string)command.ExecuteScalar()!)!.AsObject();
            json["Profile"]!["ConfigurationVersion"] = -1;
            // Inject semantic corruption into the synthetic DB, not a malformed HTTP query.
            command.CommandText = (finalized ? "DROP TRIGGER finalized_runs_are_immutable; " : "") +
                "UPDATE runs SET context_json=$json WHERE run_id=$run";
            command.Parameters.AddWithValue("$json", json.ToJsonString());
            command.ExecuteNonQuery();
        }
        var response = await host.Client.GetAsync(finalized ? "/api/dashboard" : "/api/runs");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("storage_unavailable", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptStoredSnapshot_IsSafe503RatherThanEmptyOrUnclassifiedFailure(bool corruptTime)
    {
        using var f = new ReadFixture();
        var run = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        await using var host = await ReadApiHost.Start(f);
        using (var connection = f.Open())
        using (var command = connection.CreateCommand())
        {
            // Deliberately damage this isolated test database, bypassing its normal immutable-row guard.
            command.CommandText = corruptTime
                ? "DROP TRIGGER finalized_results_are_immutable; UPDATE run_results SET finalized_at_utc='synthetic-private-invalid-time' WHERE run_id=$run"
                : "DROP TRIGGER run_rows_are_immutable; UPDATE run_rows SET facts_json='synthetic-private-invalid-json' WHERE run_id=$run";
            command.Parameters.AddWithValue("$run", run.RunId);
            command.ExecuteNonQuery();
        }
        var response = await host.Client.GetAsync($"/api/dashboard?runId={run.RunId}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal("storage_unavailable", JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("synthetic-private", text);
        Assert.DoesNotContain(f.Location.DataDirectory, text);
    }

    [Fact]
    public async Task IncidentRoutes_ArePinnedToPastRunAndDoNotDiscloseLaterRecovery()
    {
        using var f = new ReadFixture();
        var first = await f.Apply([f.Ap("100", ApState.Down), f.Ap("200", ApState.Up)]);
        var incident = (await f.Dashboard()).Rows.Single(row => row.SourceApId == "100").OpenIncidentId;
        var recovery = await f.Apply([f.Ap("100", ApState.Up), f.Ap("200", ApState.Up)]);
        await using var host = await ReadApiHost.Start(f);
        var history = await Json(host.Client, $"/api/incidents?asOfRunId={first.RunId}&status=Open");
        Assert.Equal(1, history.GetProperty("totalRows").GetInt32());
        Assert.Equal(JsonValueKind.Null, history.GetProperty("rows")[0].GetProperty("recovery").ValueKind);
        var detail = await Json(host.Client, $"/api/incidents/{incident}?asOfRunId={first.RunId}");
        Assert.Equal("Open", detail.GetProperty("status").GetString());
        var recovered = await Json(host.Client, $"/api/incidents/{incident}?asOfRunId={recovery.RunId}");
        Assert.Equal("Recovered", recovered.GetProperty("status").GetString());
        Assert.Equal(300, recovered.GetProperty("observedElapsedSeconds").GetDouble());
    }

    [Fact]
    public async Task EmptyFilteredExport_HasHeaderOnlyAndPastSearchNeverUsesCurrentName()
    {
        using var f = new ReadFixture();
        var first = await f.Apply([f.Ap("100", ApState.Down, "original-name"), f.Ap("200", ApState.Up)]);
        await f.Apply([f.Ap("100", ApState.Up, "future-name"), f.Ap("200", ApState.Up)]);
        await using var host = await ReadApiHost.Start(f);
        var old = await Json(host.Client, $"/api/dashboard?runId={first.RunId}&search=original-name");
        Assert.Equal(1, old.GetProperty("totalRows").GetInt32());
        var response = await host.Client.GetAsync($"/api/export.csv?runId={first.RunId}&search=future-name");
        response.EnsureSuccessStatusCode(); Assert.Equal("0", response.Headers.GetValues("X-AirWave-Row-Count").Single());
        using var parser = new TextFieldParser(new StringReader(Encoding.UTF8.GetString((await response.Content.ReadAsByteArrayAsync()).AsSpan(3))));
        parser.SetDelimiters(","); Assert.Contains("AP 이름", parser.ReadFields()!); Assert.True(parser.EndOfData);
    }
}
