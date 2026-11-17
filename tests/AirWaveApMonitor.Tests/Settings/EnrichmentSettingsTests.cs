using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Demo;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Tests.Support;

namespace AirWaveApMonitor.Tests.Settings;

public sealed class EnrichmentSettingsTests
{
    [Fact]
    public async Task UpdatingApSettingsKeepsExplicitEnrichmentChoicesAndTheirInterval()
    {
        using var temp = new TemporaryDirectory();
        var location = StoragePaths.Open(AppMode.Demo, temp.Path);
        var profile = new ProfileSnapshot(Guid.NewGuid(), location.DatasetId, ScenarioApSource.DemoOrigin,
            4, 2, [], BaselineStatus.Unverified, new(5, 27, true, true));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "profile.json"), JsonSerializer.Serialize(profile));
        var profiles = new ProfileStore(location);
        await profiles.UpdateAsync(profile.Origin, 9, new(ProxyMode.Direct), 4, default);
        var reopened = new ProfileStore(location).Current!;
        Assert.Equal(9, reopened.Settings.ApIntervalMinutes);
        Assert.Equal(27, reopened.Settings.EnrichmentIntervalMinutes);
        Assert.True(reopened.Settings.ConnectionCollectionEnabled);
        Assert.True(reopened.Settings.PowerCollectionEnabled);
        Assert.Equal(profile.InstanceId, reopened.InstanceId);
        Assert.Equal(5, reopened.ConfigurationVersion);
        Assert.Equal(2, reopened.ScopeVersion);
    }
    [Fact]
    public async Task ExplicitDemoSelectionPersistsWithoutChangingApScopeAndStaleSaveCannotReplaceIt()
    {
        using var temp = new TemporaryDirectory();
        var location = StoragePaths.Open(AppMode.Demo, temp.Path);
        var profiles = new ProfileStore(location); var original = profiles.Current!;
        Assert.False(original.Settings.ConnectionCollectionEnabled);
        Assert.False(original.Settings.PowerCollectionEnabled);
        await profiles.UpdateEnrichmentAsync(22, true, false, original.ConfigurationVersion, default);
        var saved = new ProfileStore(location).Current!;
        Assert.Equal(22, saved.Settings.EnrichmentIntervalMinutes);
        Assert.True(saved.Settings.ConnectionCollectionEnabled);
        Assert.False(saved.Settings.PowerCollectionEnabled);
        Assert.Equal(original.ScopeVersion, saved.ScopeVersion);
        Assert.Equal(original.Settings.ApIntervalMinutes, saved.Settings.ApIntervalMinutes);
        Assert.Equal(original.CredentialsVersion, saved.CredentialsVersion);
        await Assert.ThrowsAsync<ProfileConflictException>(() => profiles.UpdateEnrichmentAsync(1, false, true, original.ConfigurationVersion, default));
        Assert.Equal(saved, profiles.Current);
        await profiles.CredentialsChangedAsync(default);
        Assert.Equal(saved.Settings, profiles.Current!.Settings);
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UnverifiedLiveCannotBeEnabledAndRejectedInputDoesNotRewriteProfile(bool connection, bool power)
    {
        using var temp = new TemporaryDirectory();
        var location = StoragePaths.Open(AppMode.Live, temp.Path); var profiles = new ProfileStore(location);
        await profiles.UpdateAsync(new("https://airwave.example.invalid"), 5, new(ProxyMode.Direct), 0, default);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(temp.Path, "profile.json"));
        await Assert.ThrowsAsync<ArgumentException>(() => profiles.UpdateEnrichmentAsync(15, connection, power, 1, default));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(temp.Path, "profile.json")));
        await profiles.UpdateEnrichmentAsync(30, false, false, 1, default);
        Assert.Equal(30, new ProfileStore(location).Current!.Settings.EnrichmentIntervalMinutes);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public async Task InvalidIntervalKeepsExistingProfile(int interval)
    {
        using var temp = new TemporaryDirectory();
        var profiles = new ProfileStore(StoragePaths.Open(AppMode.Demo, temp.Path));
        var original = profiles.Current!;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => profiles.UpdateEnrichmentAsync(interval, true, true, 1, default));
        Assert.Equal(original, profiles.Current);
        Assert.False(File.Exists(Path.Combine(temp.Path, "profile.json")));
    }
}
