using System.Text.Json;
using AirWaveApMonitor.App.Maintenance;
using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Tests.Settings;
using AirWaveApMonitor.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace AirWaveApMonitor.Tests.Maintenance;

[Collection("Host environment")]
public sealed class MaintenanceHostedTests
{
    [Fact]
    public async Task PausedCollector_StillCreatesVerifiedBackupAndReportsSeparateHealth()
    {
        using var directory = new TemporaryDirectory(); var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", directory.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync();
        var health = app.Services.GetService<MaintenanceHealth>();
        Assert.NotNull(health);
        var until = DateTime.UtcNow.AddSeconds(15);
        while (health.GetSnapshot().LastRetentionAtUtc is null && DateTime.UtcNow < until) await Task.Delay(20);
        using var client = SettingsEndpointTests.Client(port);
        using var json = JsonDocument.Parse(await client.GetStringAsync("/api/health"));
        var value = json.RootElement;
        Assert.True(value.GetProperty("collector").GetProperty("paused").GetBoolean());
        Assert.Equal(JsonValueKind.Null, value.GetProperty("collector").GetProperty("lastValidCommittedAtUtc").ValueKind);
        Assert.Equal("ready", value.GetProperty("maintenance").GetProperty("backupStatus").GetString());
        Assert.Equal(JsonValueKind.String, value.GetProperty("maintenance").GetProperty("lastRetentionAtUtc").ValueKind);
        Assert.Single(await app.Services.GetRequiredService<BackupService>().ListVerifiedAsync(default));
        await app.StopAsync();
    }
}
