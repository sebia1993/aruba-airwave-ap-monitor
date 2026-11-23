using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace AirWaveApMonitor.Tests.Settings;

[Collection("Host environment")]
public sealed class CertificatePinSettingsTests
{
    private const string PathName = "/api/settings/certificate-pin";
    private static readonly string Digest = Convert.ToHexString(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    private static readonly Uri Origin = new("https://192.0.2.30:4343");

    [Theory]
    [InlineData(":")]
    [InlineData("-")]
    [InlineData(" ")]
    [InlineData("")]
    public void AcceptsCompleteSha256BytesAndPreservesIpv6Origin(string separator)
    {
        var value = string.Join(separator, Digest.ToLowerInvariant().Chunk(2).Select(pair => new string(pair)));
        var origin = new Uri("https://[2001:db8::2]:4343");
        var pin = new TemporaryCertificatePin(origin, value, DateTimeOffset.UnixEpoch);
        Assert.Equal(Digest, pin.Sha256);
        Assert.Equal(origin, pin.Origin);
    }

    [Fact]
    public async Task AllProfileWritesPreserveThePinAndMismatchedStoredOriginStopsLoading()
    {
        using var temp = new TemporaryDirectory();
        var location = StoragePaths.Open(AppMode.Live, temp.Path);
        var store = new ProfileStore(location);
        await store.UpdateAsync(Origin, 5, new(ProxyMode.Direct), 0, default);
        Assert.Null(new ProfileStore(location).Current!.TemporaryCertificatePin);
        var now = DateTimeOffset.UnixEpoch;
        await store.UpdateTemporaryCertificatePinAsync(Digest, 1, 1, now, default);
        await store.UpdateAsync(Origin, 8, new(ProxyMode.System), 2, default);
        await store.UpdateEnrichmentAsync(24, false, false, 3, default);
        await store.CredentialsChangedAsync(default);
        await store.UpdateScopeAsync(new(MonitoringScopeMode.SelectedApIds, ["one"]), ["one"], "test", 5, default);
        var saved = new ProfileStore(location).Current!;
        Assert.Equal(new TemporaryCertificatePin(Origin, Digest, now.AddHours(1)), saved.TemporaryCertificatePin);
        Assert.Equal(6, saved.ConfigurationVersion);
        var path = Path.Combine(temp.Path, "profile.json");
        var inconsistent = saved with { TemporaryCertificatePin = new(new Uri("https://192.0.2.30:4344"), Digest, now.AddHours(2)) };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(inconsistent));
        Assert.Throws<InvalidOperationException>(() => new ProfileStore(location));
    }

    [Fact]
    public async Task PinEndpointEnforcesVersionDeadlineExplicitClearAndIpScope()
    {
        using var temp = new TemporaryDirectory();
        var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "live", "--data-dir", temp.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync();
        using var client = SettingsEndpointTests.Client(port);
        await SettingsEndpointTests.Token(client);
        var profiles = app.Services.GetRequiredService<ProfileStore>();
        await profiles.UpdateAsync(Origin, 5, new(ProxyMode.Direct), 0, default);
        var before = DateTimeOffset.UtcNow;
        var response = await client.PostAsJsonAsync(PathName, new { expectedConfigurationVersion = 1, sha256 = Digest, durationHours = 168 });
        response.EnsureSuccessStatusCode();
        var pin = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("profile").GetProperty("temporaryCertificatePin");
        Assert.Equal(Origin, new Uri(pin.GetProperty("origin").GetString()!));
        Assert.Equal(Digest, pin.GetProperty("sha256").GetString());
        Assert.InRange(pin.GetProperty("expiresAtUtc").GetDateTimeOffset(), before.AddHours(168), DateTimeOffset.UtcNow.AddHours(168));
        foreach (var invalid in new[] {
            """{"expectedConfigurationVersion":2}""",
            """{"expectedConfigurationVersion":2,"sha256":null}""",
            """{"expectedConfigurationVersion":2,"sha256":null,"durationHours":1}""",
            JsonSerializer.Serialize(new { expectedConfigurationVersion = 2, sha256 = Digest, durationHours = 169 }),
            JsonSerializer.Serialize(new { expectedConfigurationVersion = 2, sha256 = Digest[..40], durationHours = 1 }) })
        {
            using var body = new StringContent(invalid, Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(PathName, body)).StatusCode);
            Assert.Equal(2, profiles.Current!.ConfigurationVersion);
        }
        var clear = new { expectedConfigurationVersion = 1, sha256 = (string?)null, durationHours = (int?)null };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(PathName, clear)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(PathName, clear with { expectedConfigurationVersion = 2 })).StatusCode);
        Assert.Null(new ProfileStore(StoragePaths.Open(AppMode.Live, temp.Path)).Current!.TemporaryCertificatePin);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("https://airwave.example.invalid")]
    [InlineData("http://192.0.2.1")]
    [InlineData("https://192.0.2.1/foo")]
    [InlineData("https://name@192.0.2.1")]
    public void RejectsInvalidPinTargets(string url)
        => Assert.Throws<ArgumentException>(() => new TemporaryCertificatePin(new Uri(url), Digest, DateTimeOffset.UtcNow));
}
