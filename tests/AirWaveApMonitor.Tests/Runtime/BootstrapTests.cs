using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace AirWaveApMonitor.Tests.Runtime;

[Collection("Host environment")]
public sealed class BootstrapTests
{
    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Startup_RejectsMissingModeOrInvalidOptions(string[] args)
        => Assert.Throws<ArgumentException>(() => RuntimeOptions.Parse(args));

    public static TheoryData<string[]> InvalidOptions => new()
    {
        Array.Empty<string>(),
        new[] { "--mode", "Demo" },
        new[] { "--mode", "demo", "--urls", "http://0.0.0.0:8765" },
        new[] { "--mode", "demo", "--port", "0" },
        new[] { "--mode", "demo", "--port", "65536" },
        new[] { "--mode", "demo", "--mode", "live" }
    };

    [Theory]
    [InlineData("demo")]
    [InlineData("live")]
    public async Task HttpHealth_ReportsInitializedStorageWithoutFakeCollection(string mode)
    {
        using var temp = new TemporaryDirectory();
        var port = FindAvailablePort();
        await using var app = AppBootstrap.Build(["--mode", mode, "--data-dir", temp.Path, "--port", port.ToString()]);
        // These tests isolate configuration/diagnostics from independent automatic runs.
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var health = JsonDocument.Parse(await client.GetStringAsync("/api/health")).RootElement;
        Assert.Equal(mode, health.GetProperty("mode").GetString());
        Assert.Equal("automatic", health.GetProperty("collection").GetString());
        Assert.True(health.GetProperty("collector").GetProperty("paused").GetBoolean());
        Assert.Equal("sqlite-ready", health.GetProperty("persistence").GetString());
        Assert.NotNull(app.Services.GetService<ICollectionStore>());
        var databaseBytes = await File.ReadAllBytesAsync(Path.Combine(temp.Path, "monitor.db"));
        Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(databaseBytes, 0, 16));
        Assert.False(health.GetProperty("liveConfigured").GetBoolean());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/dashboard")).StatusCode);
        Assert.Contains("AirWave AP Monitor", await client.GetStringAsync("/"));
        if (mode == "live")
        {
            Assert.IsType<AirWaveClient>(app.Services.GetService<IApSource>());
            Assert.Equal(JsonValueKind.Null, health.GetProperty("fixtureApCount").ValueKind);
        }
        else
        {
            Assert.NotNull(app.Services.GetService<IApSource>());
            Assert.Equal(2048, health.GetProperty("fixtureApCount").GetInt32());
        }
    }

    [Fact]
    public async Task Startup_WithUnreadableDatabase_PreservesFileAndRejectsServing()
    {
        using var temp = new TemporaryDirectory();
        var location = StoragePaths.Open(AppMode.Demo, temp.Path);
        var databasePath = Path.Combine(location.DataDirectory, "monitor.db");
        var original = System.Text.Encoding.UTF8.GetBytes("Existing unreadable database must not be replaced.");
        await File.WriteAllBytesAsync(databasePath, original);
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", temp.Path,
            "--port", FindAvailablePort().ToString()]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.Contains("기존 파일을 보존", error.Message);
        Assert.Equal(original, await File.ReadAllBytesAsync(databasePath));
        Assert.Empty(app.Urls);
    }

    // Removing explicit binding or accepting Kestrel environment endpoints exposes the app externally.
    [Fact]
    public async Task EnvironmentUrlOverrides_CannotAddNonLoopbackListeners()
    {
        using var temp = new TemporaryDirectory();
        var port = FindAvailablePort();
        using var environment = new EnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_URLS"] = "http://0.0.0.0:18443",
            ["Kestrel__Endpoints__External__Url"] = "http://0.0.0.0:18444"
        });
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", temp.Path, "--port", port.ToString()]);
        // These tests isolate configuration/diagnostics from independent automatic runs.
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync();
        Assert.Equal([$"http://127.0.0.1:{port}"], app.Urls);
    }

    [Theory]
    [InlineData("Host", "attacker.invalid", HttpStatusCode.BadRequest)]
    [InlineData("Origin", "https://attacker.invalid", HttpStatusCode.Forbidden)]
    public async Task HttpBoundary_RejectsForeignHostOrOrigin(string header, string value, HttpStatusCode expected)
    {
        using var temp = new TemporaryDirectory();
        var port = FindAvailablePort();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", temp.Path, "--port", port.ToString()]);
        // These tests isolate configuration/diagnostics from independent automatic runs.
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/health");
        request.Headers.TryAddWithoutValidation(header, value);
        Assert.Equal(expected, (await client.SendAsync(request)).StatusCode);
    }

    [UnixFact]
    public async Task WindowsOperations_AreExplicitlyUnsupportedOnMacOrLinux()
    {
        using var temp = new TemporaryDirectory();
        var port = FindAvailablePort();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", temp.Path, "--port", port.ToString()]);
        // These tests isolate configuration/diagnostics from independent automatic runs.
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync();
        using var client = new HttpClient();
        var capabilities = JsonDocument.Parse(await client.GetStringAsync($"http://127.0.0.1:{port}/api/capabilities/windows")).RootElement;
        Assert.False(capabilities.GetProperty("supported").GetBoolean());
        Assert.Equal("unsupported-platform", capabilities.GetProperty("status").GetString());
    }

    private static int FindAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> original;
        public EnvironmentScope(Dictionary<string, string?> variables)
        {
            original = variables.ToDictionary(pair => pair.Key, pair => Environment.GetEnvironmentVariable(pair.Key));
            foreach (var variable in variables)
                Environment.SetEnvironmentVariable(variable.Key, variable.Value);
        }
        public void Dispose()
        {
            foreach (var variable in original)
                Environment.SetEnvironmentVariable(variable.Key, variable.Value);
        }
    }
}

[CollectionDefinition("Host environment", DisableParallelization = true)]
public sealed class HostEnvironmentCollection;
