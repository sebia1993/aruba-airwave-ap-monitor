using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Infrastructure.Storage;
using AirWaveApMonitor.Tests.Settings;
using AirWaveApMonitor.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AirWaveApMonitor.Tests.Collection;

[Collection("Host environment")]
public sealed class CollectorStartupTests
{
    [Fact]
    public async Task DemoInitialProfile_IsDurableBeforeCollectionWithoutSettingsSave()
    {
        using var directory = new TemporaryDirectory();
        var location = StoragePaths.Open(AppMode.Demo, directory.Path);
        var first = new ProfileStore(location);
        await first.EnsureInitializedAsync(default);
        var restarted = new ProfileStore(location);
        Assert.Equal(first.Current!.InstanceId, restarted.Current!.InstanceId);
        Assert.Equal(first.Current.DatasetId, restarted.Current.DatasetId);
        Assert.Equal(first.Current.ConfigurationVersion, restarted.Current.ConfigurationVersion);
        Assert.Equal(first.Current.ScopeVersion, restarted.Current.ScopeVersion);
    }

    [Fact]
    public async Task SecondHostSameFolder_IsRejectedBeforeItCanInterruptActiveRun()
    {
        using var directory = new TemporaryDirectory();
        await using var first = AppBootstrap.Build(["--mode", "demo", "--data-dir", directory.Path,
            "--port", SettingsEndpointTests.Port().ToString()]);
        await first.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await first.StartAsync();
        var profiles = first.Services.GetRequiredService<ProfileStore>();
        var store = first.Services.GetRequiredService<SqliteCollectionStore>();
        var running = await store.BeginRunAsync(profiles.Current!, CollectionKind.ApStatus, RunTrigger.Manual, default);
        await using var second = AppBootstrap.Build(["--mode", "demo", "--data-dir", directory.Path,
            "--port", SettingsEndpointTests.Port().ToString()]);
        await Assert.ThrowsAnyAsync<IOException>(() => second.StartAsync());
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory.Path, "monitor.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT lifecycle FROM runs WHERE run_id=$run";
        command.Parameters.AddWithValue("$run", running.RunId);
        Assert.Equal(0L, command.ExecuteScalar());
    }
}
