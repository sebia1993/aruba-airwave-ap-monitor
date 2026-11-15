using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace AirWaveApMonitor.Tests.Settings;

[Collection("Host environment")]
public sealed class EnrichmentSettingsEndpointTests
{
    [Fact]
    public async Task ExplicitDemoSettingsAreVersionedAndAnOlderApSettingsFormCannotDiscardThem()
    {
        using var temp = new TemporaryDirectory(); var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", temp.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync(); using var client = SettingsEndpointTests.Client(port); await SettingsEndpointTests.Token(client);
        var response = await client.PostAsJsonAsync("/api/enrichment/settings", new { expectedConfigurationVersion = 1, enrichmentIntervalMinutes = 25, connectionCollectionEnabled = true, powerCollectionEnabled = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("profile");
        Assert.Equal(2, profile.GetProperty("configurationVersion").GetInt64());
        var stale = await client.PostAsJsonAsync("/api/settings", new { origin = profile.GetProperty("origin").GetString(), apIntervalMinutes = 8, proxyMode = "Direct", expectedConfigurationVersion = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var saved = await client.PostAsJsonAsync("/api/settings", new { origin = profile.GetProperty("origin").GetString(), apIntervalMinutes = 8, proxyMode = "Direct", expectedConfigurationVersion = 2 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var reopened = new ProfileStore(StoragePaths.Open(AppMode.Demo, temp.Path)).Current!;
        Assert.True(reopened.Settings.ConnectionCollectionEnabled); Assert.True(reopened.Settings.PowerCollectionEnabled);
        Assert.Equal(25, reopened.Settings.EnrichmentIntervalMinutes); Assert.Equal(8, reopened.Settings.ApIntervalMinutes);
        await app.StopAsync();
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"expectedConfigurationVersion\":1,\"enrichmentIntervalMinutes\":15,\"connectionCollectionEnabled\":true}")]
    [InlineData("{\"expectedConfigurationVersion\":1,\"enrichmentIntervalMinutes\":0,\"connectionCollectionEnabled\":true,\"powerCollectionEnabled\":true}")]
    public async Task IncompleteOrInvalidSelectionsDoNotModifyConfiguration(string json)
    {
        using var temp = new TemporaryDirectory(); var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", temp.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync(); using var client = SettingsEndpointTests.Client(port); await SettingsEndpointTests.Token(client);
        var profiles = app.Services.GetRequiredService<ProfileStore>(); var before = profiles.Current;
        var response = await client.PostAsync("/api/enrichment/settings", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(before, profiles.Current);
        await app.StopAsync();
    }
    [Fact]
    public async Task LiveRequestCannotEnableAnUnverifiedAdapter()
    {
        using var temp = new TemporaryDirectory(); var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "live", "--data-dir", temp.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync(); using var client = SettingsEndpointTests.Client(port); await SettingsEndpointTests.Token(client);
        var profiles = app.Services.GetRequiredService<ProfileStore>();
        await profiles.UpdateAsync(new("https://airwave.example.invalid"), 5, new(ProxyMode.Direct), 0, default);
        var response = await client.PostAsJsonAsync("/api/enrichment/settings", new { expectedConfigurationVersion = 1, enrichmentIntervalMinutes = 15, connectionCollectionEnabled = true, powerCollectionEnabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("미검증", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, profiles.Current!.ConfigurationVersion);
        Assert.False(profiles.Current.Settings.ConnectionCollectionEnabled); Assert.False(profiles.Current.Settings.PowerCollectionEnabled);
        await app.StopAsync();
    }
    [Fact]
    public async Task ActiveCollectionGateRejectsSettingsWithoutWaitingOrMutating()
    {
        using var temp = new TemporaryDirectory(); var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", temp.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync(); using var client = SettingsEndpointTests.Client(port); await SettingsEndpointTests.Token(client);
        var profiles = app.Services.GetRequiredService<ProfileStore>(); await profiles.OperationGate.WaitAsync();
        try
        {
            var response = await client.PostAsJsonAsync("/api/enrichment/settings", new { expectedConfigurationVersion = 1, enrichmentIntervalMinutes = 15, connectionCollectionEnabled = true, powerCollectionEnabled = true });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Equal(1, profiles.Current!.ConfigurationVersion);
        }
        finally { profiles.OperationGate.Release(); }
        await app.StopAsync();
    }
}
