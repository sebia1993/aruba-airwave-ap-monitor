using Microsoft.Extensions.DependencyInjection;
using AirWaveApMonitor.Core.Contracts;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.Tests.Support;
using Microsoft.Data.Sqlite;
namespace AirWaveApMonitor.Tests.Settings;

[Collection("Host environment")]
public sealed class SettingsEndpointTests
{
    [Fact]
    public async Task DemoDiagnostic_IsARealParserReadWithoutCreatingOperationalRun()
    {
        using var temp = new TemporaryDirectory();
        var port = Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", temp.Path, "--port", port.ToString()]);
        // These tests isolate configuration/diagnostics from independent automatic runs.
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync();
        using var client = Client(port);
        await Token(client);
        var response = await client.PostAsync("/api/diagnostics", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(Guid.TryParse(report.GetProperty("diagnosticId").GetString(), out _));
        Assert.Equal(2048, report.GetProperty("parsedRecords").GetInt32());
        Assert.Equal("not-applicable-demo", report.GetProperty("tlsResult").GetString());
        Assert.False(report.TryGetProperty("tlsCertificateChecks", out _));
        Assert.Equal(0, report.GetProperty("loginRequests").GetInt32());
        Assert.False(report.TryGetProperty("runId", out _));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(temp.Path, "monitor.db"),
            Pooling = false,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM runs";
        Assert.Equal(0L, command.ExecuteScalar());
    }
    [Theory]
    [InlineData("/api/settings")]
    [InlineData("/api/enrichment/settings")]
    [InlineData("/api/settings/certificate-pin")]
    [InlineData("/api/settings/certificate-pin/probe")]
    [InlineData("/api/settings/certificate-pin/persistent")]
    [InlineData("/api/scope")]
    [InlineData("/api/scope/confirm")]
    [InlineData("/api/credentials")]
    [InlineData("/api/authentication/retry")]
    [InlineData("/api/diagnostics")]
    public async Task EveryMutation_RequiresBothSameOriginAndAntiforgery(string path)
    {
        using var temp = new TemporaryDirectory();
        var port = Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", temp.Path, "--port", port.ToString()]);
        // These tests isolate configuration/diagnostics from independent automatic runs.
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync();
        using var client = Client(port);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new
        {
        })).StatusCode);
        await Token(client);
        client.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path, new
        {
        })).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path, new
        {
        })).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        client.DefaultRequestHeaders.Host = "evil.example.invalid:" + port;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new
        {
        })).StatusCode);
    }
    [Fact]
    public async Task LiveSettings_PersistVersionsAndRejectOriginReuseWithoutLeakingCredentials()
    {
        using var temp = new TemporaryDirectory();
        var port = Port();
        await using (var app = AppBootstrap.Build(["--mode", "live", "--data-dir", temp.Path, "--port", port.ToString()]))
        {
            // These tests isolate configuration/diagnostics from independent automatic runs.
            await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
            await app.StartAsync();
            using var client = Client(port);
            await Token(client);
            var saved = await client.PostAsJsonAsync("/api/settings", new
            {
                origin = "https://airwave.example.invalid",
                apIntervalMinutes = 5,
                proxyMode = "Direct",
                proxyAddress = (string?)null,
                expectedConfigurationVersion = 0
            });
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var conflict = await client.PostAsJsonAsync("/api/settings", new
            {
                origin = "https://other.example.invalid",
                apIntervalMinutes = 5,
                proxyMode = "System",
                expectedConfigurationVersion = 1
            });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            await app.StopAsync();
        }
        await using (var app = AppBootstrap.Build(["--mode", "live", "--data-dir", temp.Path, "--port", port.ToString()]))
        {
            // These tests isolate configuration/diagnostics from independent automatic runs.
            await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
            await app.StartAsync();
            using var client = Client(port);
            var body = JsonDocument.Parse(await client.GetStringAsync("/api/settings")).RootElement;
            Assert.Equal("https://airwave.example.invalid/", body.GetProperty("profile").GetProperty("origin").GetString());
            Assert.Equal(1, body.GetProperty("profile").GetProperty("configurationVersion").GetInt64());
        }
    }
    [Fact]
    public async Task DemoCredentialInput_IsRejectedAndNeverWritten()
    {
        using var temp = new TemporaryDirectory();
        var port = Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", temp.Path, "--port", port.ToString()]);
        // These tests isolate configuration/diagnostics from independent automatic runs.
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync();
        using var c = Client(port);
        await Token(c);
        var r = await c.PostAsJsonAsync("/api/credentials", new
        {
            username = "real-user",
            password = "sensitive"
        });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.DoesNotContain("sensitive", await r.Content.ReadAsStringAsync());
        Assert.Empty(Directory.GetFiles(temp.Path, "*.dpapi"));
    }
    internal static int Port()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
    internal static HttpClient Client(int port)
    {
        var c = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        c.DefaultRequestHeaders.Add("Origin", c.BaseAddress.GetLeftPart(UriPartial.Authority));
        return c;
    }
    internal static async Task Token(HttpClient c)
    {
        var r = JsonDocument.Parse(await c.GetStringAsync("/api/antiforgery")).RootElement;
        c.DefaultRequestHeaders.Add("X-CSRF-TOKEN", r.GetProperty("token").GetString());
    }
}
