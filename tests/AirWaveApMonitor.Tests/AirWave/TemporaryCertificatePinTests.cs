using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Infrastructure.Storage;
using AirWaveApMonitor.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AirWaveApMonitor.Tests.AirWave;

public sealed partial class TemporaryCertificatePinTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingPin_AllowsLoginAndApReads_AndRetainsTlsEvidence(bool expiredLeaf)
    {
        var clock = new TestClock();
        await using var server = await TestServer.StartAsync(clock.GetUtcNow().AddDays(-2),
            clock.GetUtcNow().AddDays(expiredLeaf ? -1 : 1));
        var profile = server.CreateProfile(server.Fingerprint, clock.GetUtcNow().AddHours(1));
        using var session = new AirWaveSession(clock: clock);
        var client = new AirWaveClient(session, new Credentials(), clock);

        var first = await client.ReadAsync(profile, default);
        var next = await client.ReadAsync(profile, default);

        Assert.Equal(RunOutcome.Succeeded, first.Batch.Outcome);
        Assert.Equal(RunOutcome.Succeeded, next.Batch.Outcome);
        Assert.Equal(0, next.Report.LoginRequests);
        Assert.Equal(new[] { "/LOGIN", "/ap_list.xml", "/ap_list.xml" }, server.Paths.ToArray());
        Assert.Single(server.Connections.Distinct());
        foreach (var report in new[] { first.Report, next.Report })
        {
            Assert.Equal("pinned-exception", report.TlsResult);
            Assert.Null(report.TlsFailureCode);
            var checks = Assert.IsType<TlsCertificateChecks>(report.TlsCertificateChecks);
            Assert.Equal("mismatch", checks.ServerName);
            Assert.Equal("failed", checks.TrustChain);
            Assert.Contains("untrusted-root", checks.ChainReasons);
            if (expiredLeaf) Assert.Contains("expired", checks.ChainReasons);
        }
    }

    [Theory]
    [InlineData(RunTrigger.Manual, false)]
    [InlineData(RunTrigger.Scheduled, false)]
    [InlineData(RunTrigger.Manual, true)]
    [InlineData(RunTrigger.Scheduled, true)]
    public async Task PinnedDiagnosisAndStoredCollectionRunReadTheSameServer(RunTrigger trigger, bool persistent)
    {
        var clock = new TestClock();
        var now = clock.GetUtcNow();
        await using var server = await TestServer.StartAsync(now.AddDays(-2), now.AddDays(-1));
        using var directory = new TemporaryDirectory();
        var location = StoragePaths.Open(AppMode.Live, directory.Path);
        var profiles = new ProfileStore(location);
        await profiles.UpdateAsync(server.Origin, 5, new(ProxyMode.Direct), 0, default);
        await profiles.UpdateTemporaryCertificatePinAsync(server.Fingerprint, 24, 1, now, default);
        if (persistent)
        {
            await profiles.EnablePersistentCertificatePinAsync(server.Fingerprint, null, 2, now, default);
            clock.Advance(TimeSpan.FromDays(60));
        }
        var profile = new ProfileStore(location).Current!;
        using var session = new AirWaveSession(clock: clock);
        var client = new AirWaveClient(session, new Credentials(), clock);
        var diagnostics = new ConnectionDiagnostics(AppMode.Live, client, clock);

        var report = await diagnostics.RunAsync(profile, default);
        Assert.Equal("pinned-exception", report.TlsResult);
        Assert.Equal(200, report.HttpStatus);
        Assert.Equal((1, 1), (report.LoginRequests, report.ApRequests));

        using var writeGate = new SemaphoreSlim(1, 1);
        var store = new SqliteCollectionStore(location, writeGate, clock);
        var run = await store.BeginRunAsync(profile, CollectionKind.ApStatus, trigger, default);
        var batch = await client.CollectAsync(run, default);
        await store.ApplyApBatchAsync(run, batch, default);
        var recorded = await store.GetRunAsync(location.DatasetId, run.RunId, default);

        Assert.Empty(batch.Errors);
        Assert.Equal(RunOutcome.Succeeded, recorded.Outcome);
        Assert.Equal(profile.TemporaryCertificatePin, run.Profile.TemporaryCertificatePin);
        Assert.Equal(profile.ConfigurationVersion, run.Profile.ConfigurationVersion);
        Assert.Equal(trigger, recorded.Trigger);
        Assert.Equal(new[] { "/LOGIN", "/ap_list.xml", "/ap_list.xml" }, server.Paths.ToArray());
    }

    [Theory]
    [InlineData("mismatch", "pin-mismatch", false)]
    [InlineData("mismatch", "pin-mismatch", true)]
    [InlineData("expired", "pin-expired", false)]
    [InlineData("future-leaf", "pin-disallowed", false)]
    [InlineData("future-leaf", "pin-disallowed", true)]
    public async Task RejectedPin_DoesNotSendCredentialsOrReadAps(string scenario, string expected, bool persistent)
    {
        var clock = new TestClock();
        var now = clock.GetUtcNow();
        await using var server = await TestServer.StartAsync(now.AddDays(scenario == "future-leaf" ? 1 : -1), now.AddDays(3));
        var fingerprint = scenario == "mismatch" ? new string('0', 64) : server.Fingerprint;
        var profile = server.CreateProfile(fingerprint, persistent ? null : now.AddMinutes(2));
        if (scenario == "expired") clock.Advance(TimeSpan.FromMinutes(2));
        using var session = new AirWaveSession(clock: clock);
        var result = await new AirWaveClient(session, new Credentials(), clock).ReadAsync(profile, default);

        Assert.Equal(RunOutcome.Failed, result.Batch.Outcome);
        Assert.Equal(expected, result.Report.TlsFailureCode);
        Assert.Null(result.Report.HttpStatus);
        Assert.Empty(server.Paths);
    }

    [Fact]
    public async Task PinExpiresOnPooledConnection_BlocksNextReadBeforeSendingAnApRequest()
    {
        var clock = new TestClock();
        var now = clock.GetUtcNow();
        await using var server = await TestServer.StartAsync(now.AddDays(-1), now.AddDays(1));
        var profile = server.CreateProfile(server.Fingerprint, now.AddMinutes(1));
        using var session = new AirWaveSession(clock: clock);
        var client = new AirWaveClient(session, new Credentials(), clock);

        Assert.Equal(RunOutcome.Succeeded, (await client.ReadAsync(profile, default)).Batch.Outcome);
        clock.Advance(TimeSpan.FromMinutes(1));
        var result = await client.ReadAsync(profile, default);

        Assert.Equal("pin-expired", result.Report.TlsFailureCode);
        Assert.Null(result.Report.HttpStatus);
        Assert.Equal(new[] { "/LOGIN", "/ap_list.xml" }, server.Paths.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovingPinDropsAuthorizedConnection_AndOlderSnapshotCannotRestoreIt(bool persistent)
    {
        var clock = new TestClock();
        var now = clock.GetUtcNow();
        await using var server = await TestServer.StartAsync(now.AddDays(-1), now.AddDays(1));
        var pinned = server.CreateProfile(server.Fingerprint, persistent ? null : now.AddMinutes(10));
        var cleared = new ProfileSnapshot(pinned.InstanceId, pinned.DatasetId, pinned.Origin,
            pinned.ConfigurationVersion + 1, pinned.ScopeVersion, [], pinned.BaselineStatus, pinned.Settings)
            { Proxy = pinned.Proxy };
        using var session = new AirWaveSession(clock: clock);
        var client = new AirWaveClient(session, new Credentials(), clock);

        Assert.Equal(RunOutcome.Succeeded, (await client.ReadAsync(pinned, default)).Batch.Outcome);
        var normal = await client.ReadAsync(cleared, default);
        Assert.Equal("certificate-name-mismatch", normal.Report.TlsFailureCode);
        var stale = await client.ReadAsync(pinned, default);
        Assert.Equal("pin-disallowed", stale.Report.TlsFailureCode);
        Assert.Equal(new[] { "/LOGIN", "/ap_list.xml" }, server.Paths.ToArray());
    }

    [Fact]
    public async Task PinExpiresAfterLogin_BlocksApRequestWithinTheSameRun()
    {
        var clock = new TestClock();
        var now = clock.GetUtcNow();
        await using var server = await TestServer.StartAsync(now.AddDays(-1), now.AddDays(1),
            onLogin: () => clock.Advance(TimeSpan.FromSeconds(5)));
        var profile = server.CreateProfile(server.Fingerprint, now.AddSeconds(5));
        using var session = new AirWaveSession(clock: clock);
        var result = await new AirWaveClient(session, new Credentials(), clock).ReadAsync(profile, default);

        Assert.Equal("pin-expired", result.Report.TlsFailureCode);
        Assert.Null(result.Report.HttpStatus);
        Assert.Equal("/LOGIN", Assert.Single(server.Paths));
    }

    [Fact]
    public async Task HttpsProxyCannotUseTheRegisteredAirWaveCertificateException()
    {
        var clock = new TestClock();
        var now = clock.GetUtcNow();
        await using var proxy = await TestServer.StartAsync(now.AddDays(-1), now.AddDays(1));
        var origin = new Uri("https://192.0.2.10/");
        var profile = proxy.CreateProfile(proxy.Fingerprint, now.AddMinutes(10), origin)
            with { Proxy = new(ProxyMode.Fixed, proxy.Origin) };
        using var session = new AirWaveSession(clock: clock);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await new AirWaveClient(session, new Credentials(), clock).ReadAsync(profile, timeout.Token);

        Assert.Equal("failed", result.Report.TlsResult);
        Assert.Equal("certificate-name-mismatch", result.Report.TlsFailureCode);
        Assert.Equal("proxy", result.Report.TlsCertificateChecks?.Peer);
        Assert.Empty(proxy.Paths);
    }

    [Fact]
    public async Task HttpConnectTunnelChecksTheTargetCertificateAtTheConfiguredIp()
    {
        var clock = new TestClock();
        var now = clock.GetUtcNow();
        await using var server = await TestServer.StartAsync(now.AddDays(-1), now.AddDays(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxy = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        var profile = server.CreateProfile(server.Fingerprint, now.AddMinutes(10), new Uri("https://192.0.2.10/"))
            with { Proxy = new(ProxyMode.Fixed, proxy) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var relay = ForwardConnectAsync(listener, server.Origin.Port, timeout.Token);
        using (var session = new AirWaveSession(clock: clock))
        {
            var result = await new AirWaveClient(session, new Credentials(), clock).ReadAsync(profile, timeout.Token);
            Assert.Equal("pinned-exception", result.Report.TlsResult);
            Assert.Equal(RunOutcome.Succeeded, result.Batch.Outcome);
            Assert.Equal("server", result.Report.TlsCertificateChecks?.Peer);
        }
        await relay;
        Assert.Equal(new[] { "/LOGIN", "/ap_list.xml" }, server.Paths.ToArray());
    }

    private static async Task ForwardConnectAsync(TcpListener listener, int serverPort, CancellationToken ct)
    {
        using var incoming = await listener.AcceptTcpClientAsync(ct);
        using var downstream = incoming.GetStream();
        using var headers = new StreamReader(downstream, Encoding.ASCII, leaveOpen: true);
        Assert.Equal("CONNECT 192.0.2.10:443 HTTP/1.1", await headers.ReadLineAsync(ct));
        while (!string.IsNullOrEmpty(await headers.ReadLineAsync(ct))) { }
        using var outgoing = new TcpClient();
        await outgoing.ConnectAsync(IPAddress.Loopback, serverPort, ct);
        using var upstream = outgoing.GetStream();
        await downstream.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray(), ct);
        var upstreamToClient = upstream.CopyToAsync(downstream, ct);
        await downstream.CopyToAsync(upstream, ct);
        outgoing.Client.Shutdown(SocketShutdown.Send);
        await upstreamToClient;
    }

    private sealed class TestClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }

    private sealed class Credentials : ICredentialStore
    {
        public Task<AirWaveCredentials?> ReadAsync(Guid datasetId, Uri origin, CancellationToken ct) =>
            Task.FromResult<AirWaveCredentials?>(new("test-reader", "test-password"));
        public Task SaveAsync(Guid datasetId, Uri origin, AirWaveCredentials credentials, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task ClearAsync(Guid datasetId, Uri origin, CancellationToken ct) => throw new NotSupportedException();
    }

    internal sealed class TestServer(WebApplication app, X509Certificate2 certificate) : IAsyncDisposable
    {
        public ConcurrentQueue<string> Paths { get; } = new();
        public ConcurrentQueue<string> Connections { get; } = new();
        public string Fingerprint => certificate.GetCertHashString(HashAlgorithmName.SHA256);
        public Uri Origin => new(app.Urls.Single());
        public ProfileSnapshot CreateProfile(string pin, DateTimeOffset? expiry, Uri? origin = null)
        {
            origin ??= Origin;
            return new ProfileSnapshot(Guid.NewGuid(), Guid.NewGuid(), origin, 1, 1, [], BaselineStatus.Unverified, new())
            {
                Proxy = new(ProxyMode.Direct),
                TemporaryCertificatePin = new(origin, pin, expiry)
            };
        }

        public static async Task<TestServer> StartAsync(DateTimeOffset notBefore, DateTimeOffset notAfter, Action? onLogin = null, int port = 0)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=synthetic-airwave.example.invalid", key,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("synthetic-airwave.example.invalid");
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(notBefore, notAfter);
            var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
                X509KeyStorageFlags.DefaultKeySet);
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port,
                listen => listen.UseHttps(certificate)));
            var app = builder.Build();
            var server = new TestServer(app, certificate);
            app.Run(async context =>
            {
                var path = context.Request.Path.Value!;
                server.Paths.Enqueue(path);
                server.Connections.Enqueue(context.Connection.Id);
                if (path == "/LOGIN")
                {
                    var form = await context.Request.ReadFormAsync();
                    Assert.Equal("test-reader", form["credential_0"].ToString());
                    Assert.Equal("test-password", form["credential_1"].ToString());
                    context.Response.Headers["X-BISCOTTI"] = "test-token";
                    context.Response.Headers.SetCookie = "session=test-cookie; Path=/; Secure";
                    onLogin?.Invoke();
                    await context.Response.WriteAsync("login");
                    return;
                }
                Assert.Equal("/ap_list.xml", path);
                Assert.Equal("test-token", context.Request.Headers["X-BISCOTTI"].ToString());
                Assert.Equal("test-cookie", context.Request.Cookies["session"]);
                await context.Response.WriteAsync(ApListParserTests.Xml(ApListParserTests.Ap()));
            });
            try
            {
                await app.StartAsync();
                return server;
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            certificate.Dispose();
        }
    }
}
