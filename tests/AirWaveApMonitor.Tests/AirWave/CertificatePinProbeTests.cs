using System.Net;
using System.Net.Sockets;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;

namespace AirWaveApMonitor.Tests.AirWave;

public sealed partial class TemporaryCertificatePinTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeReadsCurrentLeafWithoutLoggingInOrSendingAnHttpRequest(bool previouslyRegistered)
    {
        var now = DateTimeOffset.UtcNow;
        await using var server = await TestServer.StartAsync(now.AddDays(-2), now.AddDays(-1));
        var profile = server.CreateProfile(new string('0', 64), now.AddHours(1));
        if (!previouslyRegistered) profile = profile with { TemporaryCertificatePin = null };

        var fingerprint = await CertificatePinProbe.ProbeAsync(profile, default);

        Assert.Equal(server.Fingerprint, fingerprint);
        Assert.Empty(server.Paths);
        if (previouslyRegistered) Assert.NotEqual(fingerprint, profile.TemporaryCertificatePin!.Sha256);
        else Assert.Null(profile.TemporaryCertificatePin);
    }

    [Fact]
    public async Task ProbeDoesNotDiscloseOrTrustAnInvalidHttpsProxyCertificate()
    {
        var now = DateTimeOffset.UtcNow;
        await using var proxy = await TestServer.StartAsync(now.AddDays(-1), now.AddDays(1));
        var origin = new Uri("https://192.0.2.10/");
        var profile = proxy.CreateProfile(proxy.Fingerprint, now.AddDays(1), origin)
            with { Proxy = new(ProxyMode.Fixed, proxy.Origin) };

        var error = await Assert.ThrowsAsync<CertificatePinProbeException>(
            () => CertificatePinProbe.ProbeAsync(profile, default));

        Assert.Contains("프록시", error.Message);
        Assert.DoesNotContain(proxy.Fingerprint, error.Message);
        Assert.DoesNotContain(proxy.Origin.Host, error.Message);
        Assert.Empty(proxy.Paths);
    }

    [Fact]
    public async Task ProbeUsesConfiguredHttpTunnelAndOnlyObservesTheOriginLeaf()
    {
        var now = DateTimeOffset.UtcNow;
        await using var server = await TestServer.StartAsync(now.AddDays(-1), now.AddDays(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxy = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        var origin = new Uri("https://192.0.2.10/");
        var profile = server.CreateProfile(server.Fingerprint, now.AddDays(1), origin)
            with { Proxy = new(ProxyMode.Fixed, proxy), TemporaryCertificatePin = null };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var relay = ForwardConnectAsync(listener, server.Origin.Port, timeout.Token);

        Assert.Equal(server.Fingerprint, await CertificatePinProbe.ProbeAsync(profile, timeout.Token));
        try
        {
            await relay;
        }
        catch (IOException error) when (OperatingSystem.IsWindows() &&
            error.GetBaseException() is SocketException { SocketErrorCode: SocketError.ConnectionReset })
        {
            // After observing the leaf, the probe aborts TLS. Windows may reset the CONNECT socket.
        }
        Assert.Empty(server.Paths);
    }

    [Fact]
    public async Task ProbeRejectsHostnameBeforeConnecting()
    {
        var profile = new ProfileSnapshot(Guid.NewGuid(), Guid.NewGuid(), new Uri("https://localhost/"),
            1, 1, [], BaselineStatus.Unverified, new()) { Proxy = new(ProxyMode.Direct) };

        await Assert.ThrowsAsync<ArgumentException>(() => CertificatePinProbe.ProbeAsync(profile, default));
    }
}
