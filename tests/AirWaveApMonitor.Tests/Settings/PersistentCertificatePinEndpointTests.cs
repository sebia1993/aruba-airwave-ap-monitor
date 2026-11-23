using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using TestServer = AirWaveApMonitor.Tests.AirWave.TemporaryCertificatePinTests.TestServer;

namespace AirWaveApMonitor.Tests.Settings;

[Collection("Host environment")]
public sealed class PersistentCertificatePinEndpointTests
{
    private const string Probe = "/api/settings/certificate-pin/probe";
    private const string Apply = "/api/settings/certificate-pin/persistent";

    [Theory]
    [InlineData("demo", Probe)]
    [InlineData("demo", Apply)]
    [InlineData("live", Probe)]
    [InlineData("live", Apply)]
    public async Task NoSavedLiveIpCannotStartAnInspectionOrInstallAPin(string mode, string path)
    {
        using var temp = new TemporaryDirectory();
        var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", mode, "--data-dir", temp.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync();
        using var client = SettingsEndpointTests.Client(port);
        await SettingsEndpointTests.Token(client);
        var profiles = app.Services.GetRequiredService<ProfileStore>();
        var before = profiles.Current;
        var version = before?.ConfigurationVersion ?? 0;
        var response = path == Probe
            ? await client.PostAsJsonAsync(path, new { expectedConfigurationVersion = version })
            : await client.PostAsJsonAsync(path, new { expectedConfigurationVersion = version,
                observedSha256 = new string('A', 64), independentlyVerifiedSha256 = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, profiles.Current);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("unregistered")]
    [InlineData("matched")]
    [InlineData("expired")]
    [InlineData("changed")]
    public async Task RealInspectionAndApplyRespectExistingTrustAndSurviveRestart(string initialStatus)
    {
        var now = DateTimeOffset.UtcNow;
        await using var target = await TestServer.StartAsync(now.AddDays(-2), now.AddDays(-1));
        var other = new string('A', 64);
        using var temp = new TemporaryDirectory();
        var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "live", "--data-dir", temp.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync();
        using var client = SettingsEndpointTests.Client(port);
        await SettingsEndpointTests.Token(client);
        var profiles = app.Services.GetRequiredService<ProfileStore>();
        await profiles.UpdateAsync(target.Origin, 5, new(ProxyMode.Direct), 0, default);
        if (initialStatus != "unregistered")
            await profiles.UpdateTemporaryCertificatePinAsync(initialStatus == "changed" ? other : target.Fingerprint,
                1, 1, initialStatus == "expired" ? now.AddDays(-3) : now, default);
        var before = profiles.Current!;
        var version = before.ConfigurationVersion;

        var observed = await client.PostAsJsonAsync(Probe, new { expectedConfigurationVersion = version });
        observed.EnsureSuccessStatusCode();
        Assert.True(observed.Headers.CacheControl?.NoStore);
        var body = await observed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(initialStatus, body.GetProperty("status").GetString());
        Assert.Equal(target.Fingerprint, body.GetProperty("sha256").GetString());
        Assert.Equal(target.Origin, new Uri(body.GetProperty("origin").GetString()!));
        Assert.Equal(version, body.GetProperty("configurationVersion").GetInt64());
        Assert.Equal(before, profiles.Current);

        Task<HttpResponseMessage> Install(long expectedConfigurationVersion, string observedSha256, string? independentlyVerifiedSha256)
            => client.PostAsJsonAsync(Apply, new { expectedConfigurationVersion, observedSha256, independentlyVerifiedSha256 });
        Assert.Equal(HttpStatusCode.Conflict, (await Install(version - 1, target.Fingerprint, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Install(version, other, null)).StatusCode);
        if (initialStatus is "unregistered" or "expired")
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await Install(version, target.Fingerprint, null)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Install(version, target.Fingerprint, other)).StatusCode);
        }
        Assert.Equal(before, profiles.Current);
        var result = await Install(version, target.Fingerprint, initialStatus == "matched" ? null : target.Fingerprint);
        if (initialStatus == "changed")
        {
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
            Assert.Equal(before, profiles.Current);
        }
        else
        {
            result.EnsureSuccessStatusCode();
            var saved = (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("profile");
            var pin = saved.GetProperty("temporaryCertificatePin");
            Assert.Equal(JsonValueKind.Null, pin.GetProperty("expiresAtUtc").ValueKind);
            Assert.Equal(target.Fingerprint, pin.GetProperty("sha256").GetString());
            Assert.Equal(version + 1, saved.GetProperty("configurationVersion").GetInt64());
            var next = await client.PostAsJsonAsync(Probe, new { expectedConfigurationVersion = version + 1 });
            next.EnsureSuccessStatusCode();
            Assert.Equal("persistent", (await next.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
            Assert.Equal(target.Fingerprint, new ProfileStore(app.Services.GetRequiredService<StorageLocation>())
                .Current!.TemporaryCertificatePin!.Sha256);
        }
        Assert.Empty(target.Paths);
        await app.StopAsync();
    }

    [Fact]
    public async Task ApplyRequiresFreshLiveCertificateEvenAfterSuccessfulInspection()
    {
        using var temp = new TemporaryDirectory();
        var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "live", "--data-dir", temp.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync();
        using var client = SettingsEndpointTests.Client(port);
        await SettingsEndpointTests.Token(client);
        var profiles = app.Services.GetRequiredService<ProfileStore>();
        string sha256;
        int targetPort;
        var now = DateTimeOffset.UtcNow;
        await using (var target = await TestServer.StartAsync(now.AddDays(-2), now.AddDays(-1)))
        {
            await profiles.UpdateAsync(target.Origin, 5, new(ProxyMode.Direct), 0, default);
            var observed = await client.PostAsJsonAsync(Probe, new { expectedConfigurationVersion = 1 });
            observed.EnsureSuccessStatusCode();
            sha256 = (await observed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sha256").GetString()!;
            targetPort = target.Origin.Port;
        }
        var apply = await client.PostAsJsonAsync(Apply, new { expectedConfigurationVersion = 1, observedSha256 = sha256,
            independentlyVerifiedSha256 = sha256 });
        Assert.Equal(HttpStatusCode.BadGateway, apply.StatusCode);
        await using var replacement = await TestServer.StartAsync(now.AddDays(-1), now.AddDays(1), port: targetPort);
        Assert.NotEqual(sha256, replacement.Fingerprint);
        var rotated = await client.PostAsJsonAsync(Apply, new { expectedConfigurationVersion = 1, observedSha256 = sha256,
            independentlyVerifiedSha256 = sha256 });
        Assert.Equal(HttpStatusCode.Conflict, rotated.StatusCode);
        Assert.Empty(replacement.Paths);
        Assert.Null(profiles.Current!.TemporaryCertificatePin);
        Assert.Equal(1, profiles.Current.ConfigurationVersion);
        await app.StopAsync();
    }
}
