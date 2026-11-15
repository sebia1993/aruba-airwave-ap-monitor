using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using AirWaveApMonitor.App.Collection;
using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Demo;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Infrastructure.Storage;
using AirWaveApMonitor.Tests.Settings;
using AirWaveApMonitor.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using static AirWaveApMonitor.Tests.Collection.CollectionCoordinatorTests;

namespace AirWaveApMonitor.Tests.Collection;

[Collection("Host environment")]
public sealed class CollectorHostedTests
{
    [Fact]
    public async Task MissingPauseFlag_DoesNotSilentlyResumeCollection()
    {
        using var directory = new TemporaryDirectory();
        var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", directory.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync();
        using var client = SettingsEndpointTests.Client(port);
        await SettingsEndpointTests.Token(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/collector/pause", new { })).StatusCode);
        Assert.True(app.Services.GetRequiredService<CollectorHealth>().GetSnapshot().Paused);
    }

    [Fact]
    public async Task HostedDemo_CollectsWithoutBrowserAndResumesCommittedFrame()
    {
        using var directory = new TemporaryDirectory();
        Guid instance;
        long firstRun;
        await using (var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", directory.Path, "--port", SettingsEndpointTests.Port().ToString()]))
        {
            await app.StartAsync();
            var health = Assert.IsType<CollectorHealth>(app.Services.GetService<CollectorHealth>());
            await Eventually(() => health.GetSnapshot().LastFinalRunId is not null && health.GetSnapshot().Phase == CollectorPhase.Idle);
            firstRun = health.GetSnapshot().LastFinalRunId!.Value;
            instance = app.Services.GetRequiredService<ProfileStore>().Current!.InstanceId;
            Assert.Equal(0, (await app.Services.GetRequiredService<SqliteCollectionStore>().GetSourceCheckpointAsync(
                app.Services.GetRequiredService<StorageLocation>().DatasetId, ScenarioApSource.CheckpointSource, default))!.Position);
            await app.StopAsync();
        }
        await using (var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", directory.Path, "--port", SettingsEndpointTests.Port().ToString()]))
        {
            await app.StartAsync();
            var health = app.Services.GetRequiredService<CollectorHealth>();
            await Eventually(() => health.GetSnapshot().LastFinalRunId > firstRun && health.GetSnapshot().Phase == CollectorPhase.Idle);
            var profile = app.Services.GetRequiredService<ProfileStore>().Current!;
            Assert.Equal(instance, profile.InstanceId);
            var view = await app.Services.GetRequiredService<ICollectionStore>().GetDashboardAsync(new(profile.DatasetId, 1), default);
            Assert.Equal(7, view.Summary.ConfirmedDown);
            Assert.Equal(7, view.Summary.NewDown);
            Assert.Equal(0, view.Summary.InitialDown);
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task CollectorRoutes_UseStringEnumsAndPreserveMutationGuard()
    {
        using var directory = new TemporaryDirectory();
        var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", directory.Path, "--port", port.ToString()]);
        if (app.Services.GetService<ICollectorControl>() is { } collector) await collector.SetPausedAsync(true, default);
        await app.StartAsync();
        using var client = SettingsEndpointTests.Client(port);
        var noToken = await client.PostAsJsonAsync("/api/collector/pause", new { paused = true });
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        await SettingsEndpointTests.Token(client);
        var pause = await client.PostAsJsonAsync("/api/collector/pause", new { paused = true });
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        var body = JsonDocument.Parse(await pause.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("paused").GetBoolean());
        Assert.Equal(JsonValueKind.String, body.GetProperty("phase").ValueKind);
        var refused = await client.PostAsJsonAsync("/api/collector/requests", new { kind = "ApStatus", trigger = "Manual" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("Paused", JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());
        var health = JsonDocument.Parse(await client.GetStringAsync("/api/health")).RootElement;
        Assert.Equal("automatic", health.GetProperty("collection").GetString());
        Assert.True(health.GetProperty("collector").GetProperty("paused").GetBoolean());
        client.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/collector/pause", new { paused = false })).StatusCode);
    }

    [Fact]
    public async Task OccupiedPort_DoesNotCollectAndReleasesLifetimeOwnershipOnDisposal()
    {
        using var directory = new TemporaryDirectory();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using (var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", directory.Path, "--port", port.ToString()]))
            await Assert.ThrowsAnyAsync<IOException>(() => app.StartAsync());
        using var owner = new InstanceLock(StoragePaths.Open(AppMode.Demo, directory.Path));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory.Path, "monitor.db"),
            Pooling = false,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM runs";
        Assert.Equal(0L, command.ExecuteScalar());
    }
}
