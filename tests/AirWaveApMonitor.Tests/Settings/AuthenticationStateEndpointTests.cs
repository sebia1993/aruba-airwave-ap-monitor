using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Security;
using AirWaveApMonitor.Tests.Security;
using AirWaveApMonitor.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AirWaveApMonitor.Tests.Settings;

[Collection("Host environment")]
public sealed class AuthenticationStateEndpointTests
{
    [Fact]
    public async Task LiveSettings_RestoresStopBeforeAnyDiagnosticAndRetryClearsItDurably()
    {
        using var directory = new TemporaryDirectory();
        var profile = (await AuthenticationPersistenceTests.Profiles(directory.Path)).Current!;
        var location = StoragePaths.Open(AppMode.Live, directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(location, gate);
        await store.SaveStopAsync(new(AuthenticationBinding.From(profile), AuthenticationStopReason.LoginRejected,
            new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.Zero)), default);
        var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "live", "--data-dir", directory.Path, "--port", port.ToString()]);
        await app.StartAsync();
        Assert.IsType<SqliteAuthenticationStateStore>(app.Services.GetRequiredService<IAuthenticationStateStore>());
        using var client = SettingsEndpointTests.Client(port);
        var settings = JsonDocument.Parse(await client.GetStringAsync("/api/settings")).RootElement;
        Assert.True(settings.GetProperty("authenticationBlocked").GetBoolean());
        Assert.False(settings.GetProperty("credentialRegistrationPending").GetBoolean());
        await SettingsEndpointTests.Token(client);
        var retry = await client.PostAsync("/api/authentication/retry", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.False(JsonDocument.Parse(await retry.Content.ReadAsStringAsync()).RootElement.GetProperty("authenticationBlocked").GetBoolean());
        Assert.Null((await new SqliteAuthenticationStateStore(location, gate).ReadAsync(AuthenticationBinding.From(profile), default)).Stop);
    }

    [Fact]
    public async Task RetryClearFailure_ReturnsSafe503AndDoesNotClaimUnblocked()
    {
        using var directory = new TemporaryDirectory();
        var profile = (await AuthenticationPersistenceTests.Profiles(directory.Path)).Current!;
        var location = StoragePaths.Open(AppMode.Live, directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteAuthenticationStateStore(location, gate);
        await store.SaveStopAsync(new(AuthenticationBinding.From(profile), AuthenticationStopReason.LoginRejected, DateTimeOffset.UtcNow), default);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory.Path, "monitor.db"), Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER refuse_auth_clear BEFORE UPDATE OF blocked_reason ON authentication_state BEGIN SELECT RAISE(ABORT, 'synthetic-database-secret'); END;";
            command.ExecuteNonQuery();
        }
        var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "live", "--data-dir", directory.Path, "--port", port.ToString()]);
        await app.StartAsync();
        using var client = SettingsEndpointTests.Client(port);
        await SettingsEndpointTests.Token(client);
        var retry = await client.PostAsync("/api/authentication/retry", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, retry.StatusCode);
        var text = await retry.Content.ReadAsStringAsync();
        Assert.DoesNotContain("synthetic-database-secret", text);
        Assert.DoesNotContain(directory.Path, text);
        Assert.DoesNotContain("authenticationBlocked", text);
        Assert.True(JsonDocument.Parse(await client.GetStringAsync("/api/settings")).RootElement.GetProperty("authenticationBlocked").GetBoolean());
    }

    [Fact]
    public async Task DatabaseBecomesUnreadableAfterStartup_SettingsAndRetryReturnSafe503()
    {
        using var directory = new TemporaryDirectory();
        await AuthenticationPersistenceTests.Profiles(directory.Path);
        var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "live", "--data-dir", directory.Path, "--port", port.ToString()]);
        await app.StartAsync();
        using var client = SettingsEndpointTests.Client(port);
        await SettingsEndpointTests.Token(client);
        // Startup now validates the database before serving. Exercise a runtime storage
        // failure after successful initialization; startup corruption has a separate test.
        File.WriteAllText(Path.Combine(directory.Path, "monitor.db"), "synthetic-database-secret");
        var get = await client.GetAsync("/api/settings");
        var retry = await client.PostAsync("/api/authentication/retry", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, get.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, retry.StatusCode);
        Assert.DoesNotContain("synthetic-database-secret", await get.Content.ReadAsStringAsync());
        Assert.DoesNotContain(directory.Path, await retry.Content.ReadAsStringAsync());
    }

    [UnsupportedDpapiFact]
    public async Task UnsupportedDpapiRegistration_DoesNotPersistPendingIntentOrPreventRetry()
    {
        using var directory = new TemporaryDirectory();
        var profile = (await AuthenticationPersistenceTests.Profiles(directory.Path)).Current!;
        var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "live", "--data-dir", directory.Path, "--port", port.ToString()]);
        await app.StartAsync();
        using var client = SettingsEndpointTests.Client(port);
        await SettingsEndpointTests.Token(client);
        var response = await client.PostAsJsonAsync("/api/credentials", new { username = "synthetic-user", password = "synthetic-secret" });
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        var savedState = await app.Services.GetRequiredService<IAuthenticationStateStore>()
            .ReadAsync(AuthenticationBinding.From(profile), default);
        Assert.False(savedState.CredentialRegistrationPending);
        Assert.Null(savedState.Stop);
        var settings = JsonDocument.Parse(await client.GetStringAsync("/api/settings")).RootElement;
        Assert.False(settings.GetProperty("credentialRegistrationPending").GetBoolean());
        Assert.Equal("unsupported-platform", settings.GetProperty("credentialStatus").GetString());
        Assert.False(settings.GetProperty("authenticationBlocked").GetBoolean());
        Assert.Equal(profile.CredentialsVersion, settings.GetProperty("profile").GetProperty("credentialsVersion").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/authentication/retry", null)).StatusCode);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.dpapi"));
    }

    private sealed class UnsupportedDpapiFactAttribute : FactAttribute
    {
        public UnsupportedDpapiFactAttribute()
        {
            if (OperatingSystem.IsWindows()) Skip = "지원되지 않는 DPAPI 플랫폼에서만 실행하는 사전 확인 검사입니다.";
        }
    }

    [Fact]
    public async Task DemoAuthenticationState_IsExplicitlyEphemeralAndCannotOpenLiveDatabase()
    {
        using var directory = new TemporaryDirectory();
        var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", directory.Path, "--port", port.ToString()]);
        await app.StartAsync();
        Assert.IsType<MemoryAuthenticationStateStore>(app.Services.GetRequiredService<IAuthenticationStateStore>());
        using var client = SettingsEndpointTests.Client(port);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/settings")).StatusCode);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory.Path, "monitor.db"), Pooling = false, Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM authentication_state";
        Assert.Equal(0L, command.ExecuteScalar());
    }
}
