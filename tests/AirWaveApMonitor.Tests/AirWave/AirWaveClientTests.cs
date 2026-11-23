using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
namespace AirWaveApMonitor.Tests.AirWave;

public sealed class AirWaveClientTests
{
    internal static ProfileSnapshot Profile() => Profile(new Uri("https://airwave.example.invalid"));
    private static ProfileSnapshot Profile(Uri origin) => new(Guid.NewGuid(), Guid.NewGuid(), origin, 1, 1, [], BaselineStatus.Unverified, new());
    private static HttpResponseMessage Login()
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("synthetic login response") };
        r.Headers.Add("X-BISCOTTI", "secret-token");
        r.Headers.Add("Set-Cookie", "session=secret-cookie; Path=/; Secure");
        return r;
    }
    private static HttpResponseMessage Ap() => new(HttpStatusCode.OK) { Content = new StringContent(ApListParserTests.Xml(ApListParserTests.Ap()), Encoding.UTF8, "application/xml") };
    [Fact]
    public async Task Login_UsesDocumentedFormAndCookiesAndToken()
    {
        var handler = new ScriptedHandler(async (r, n) => { if (n == 1) { Assert.Equal(HttpMethod.Post, r.Method); Assert.Equal("/LOGIN", r.RequestUri!.AbsolutePath); Assert.Equal("destination=%2Findex.html&credential_0=user%2Bname&credential_1=p%26ss", await r.Content!.ReadAsStringAsync()); return Login(); } Assert.Equal("/ap_list.xml", r.RequestUri!.AbsolutePath); Assert.Equal("secret-token", r.Headers.GetValues("X-BISCOTTI").Single()); Assert.Contains("session=secret-cookie", r.Headers.GetValues("Cookie").Single()); return Ap(); });
        using var session = new AirWaveSession(_ => handler);
        var client = new AirWaveClient(session, new MemoryCredentials(), TimeProvider.System);
        var profile = Profile();
        Assert.Equal(RunOutcome.Succeeded, (await client.ReadAsync(profile, default)).Batch.Outcome);
        var reused = await client.ReadAsync(profile, default);
        Assert.Equal(0, reused.Report.LoginRequests);
        Assert.Equal(3, handler.Calls);
    }
    [Fact]
    public async Task ApiPathSessionCookie_IsAcceptedSentAndRefreshedAtRequestUri()
    {
        var handler = new ScriptedHandler((request, number) =>
        {
            if (number == 1)
            {
                var login = Login();
                login.Headers.Remove("Set-Cookie");
                login.Headers.Add("Set-Cookie", "session=path-cookie; Path=/ap_list.xml; Secure");
                return Task.FromResult(login);
            }
            Assert.Equal("/ap_list.xml", request.RequestUri!.AbsolutePath);
            var cookie = request.Headers.GetValues("Cookie").Single();
            Assert.Contains(number == 2 ? "session=path-cookie" : "session=refreshed-path-cookie", cookie);
            var response = Ap();
            if (number == 2)
                response.Headers.Add("Set-Cookie", "session=refreshed-path-cookie; Path=/ap_list.xml; Secure");
            return Task.FromResult(response);
        });
        using var session = new AirWaveSession(_ => handler);
        var client = new AirWaveClient(session, new MemoryCredentials(), TimeProvider.System);
        var profile = Profile();

        var first = await client.ReadAsync(profile, default);
        Assert.Equal(RunOutcome.Succeeded, first.Batch.Outcome);
        var next = await client.ReadAsync(profile, default);
        Assert.Equal(RunOutcome.Succeeded, next.Batch.Outcome);
        Assert.Equal(0, next.Report.LoginRequests);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task LoginHtml200_IsFailure()
    {
        using var s = new AirWaveSession(_ => new ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><form action='/LOGIN'><input name='credential_1'></form></html>") })));
        var result = await new AirWaveClient(s, new MemoryCredentials(), TimeProvider.System).ReadAsync(Profile(), default);
        Assert.Equal(RunOutcome.Failed, result.Batch.Outcome);
        Assert.NotEmpty(result.Batch.Errors);
    }
    [Fact]
    public async Task ExpiredSession_RetriesOnce()
    {
        var h = new ScriptedHandler((_, n) => Task.FromResult(n is 1 or 3 ? Login() : new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var s = new AirWaveSession(_ => h);
        var r = await new AirWaveClient(s, new MemoryCredentials(), TimeProvider.System).ReadAsync(Profile(), default);
        Assert.Equal(2, r.Report.LoginRequests);
        Assert.Equal(2, r.Report.ApRequests);
        Assert.Equal(RunOutcome.Failed, r.Batch.Outcome);
        Assert.True(s.AuthenticationBlocked);
    }
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.OK)]
    public async Task UnconfirmedReloginFailure_DoesNotBlockNextRead(HttpStatusCode reloginStatus)
    {
        var handler = new ScriptedHandler((request, number) =>
        {
            if (number is 1 or 4) return Task.FromResult(Login());
            if (number == 2) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            if (number == 3) return Task.FromResult(new HttpResponseMessage(reloginStatus)
            {
                Content = new StringContent("<html>unconfirmed service response without a token</html>")
            });
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Ap());
        });
        using var session = new AirWaveSession(_ => handler);
        var client = new AirWaveClient(session, new MemoryCredentials(), TimeProvider.System);
        var profile = Profile();

        var first = await client.ReadAsync(profile, default);
        Assert.Equal(RunOutcome.Failed, first.Batch.Outcome);
        Assert.Equal(2, first.Report.LoginRequests);
        Assert.Equal(1, first.Report.ApRequests);

        var next = await client.ReadAsync(profile, default);
        Assert.Equal(RunOutcome.Succeeded, next.Batch.Outcome);
        Assert.Equal(1, next.Report.LoginRequests);
        Assert.Equal(1, next.Report.ApRequests);
        Assert.Equal(5, handler.Calls);
        Assert.False(first.Report.AuthenticationBlocked);
        Assert.False(session.AuthenticationBlocked);
    }

    [Fact]
    public async Task Forbidden_DoesNotRelogin()
    {
        var h = new ScriptedHandler((_, n) => Task.FromResult(n == 1 ? Login() : new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var s = new AirWaveSession(_ => h);
        var r = await new AirWaveClient(s, new MemoryCredentials(), TimeProvider.System).ReadAsync(Profile(), default);
        Assert.Equal(1, r.Report.LoginRequests);
        Assert.Contains(r.Batch.Errors, e => e.Category == CollectionErrorCategory.Permission);
        Assert.False(s.AuthenticationBlocked);
    }
    [Fact]
    public async Task InvalidCredentials_StopAutomaticLogin()
    {
        var h = new ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var s = new AirWaveSession(_ => h);
        var c = new AirWaveClient(s, new MemoryCredentials(), TimeProvider.System);
        var p = Profile();
        await c.ReadAsync(p, default);
        var blocked = await c.ReadAsync(p, default);
        Assert.Equal(1, h.Calls);
        Assert.True(blocked.Report.AuthenticationBlocked);
        await s.RetryAuthenticationAsync();
        await c.ReadAsync(p, default);
        Assert.Equal(2, h.Calls);
    }
    [Fact]
    public async Task ChangingOnlyProxy_DoesNotClearRejectedCredentialLatch()
    {
        var h = new ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var session = new AirWaveSession(_ => h);
        var client = new AirWaveClient(session, new MemoryCredentials(), TimeProvider.System);
        var profile = Profile();
        await client.ReadAsync(profile, default);
        var result = await client.ReadAsync(profile with
        {
            Proxy = new(ProxyMode.Direct)
        }, default);
        Assert.Equal(1, h.Calls);
        Assert.True(result.Report.AuthenticationBlocked);
        Assert.Equal(0, result.Report.LoginRequests);
    }
    [Fact]
    public async Task Session_IsBoundToDatasetOriginAndCredentialVersion()
    {
        var h = new ScriptedHandler((r, _) => Task.FromResult(r.Method == HttpMethod.Post ? Login() : Ap()));
        using var s = new AirWaveSession(_ => h);
        var c = new AirWaveClient(s, new MemoryCredentials(), TimeProvider.System);
        var p = Profile();
        await c.ReadAsync(p, default);
        Assert.Equal(1, (await c.ReadAsync(p with
        {
            CredentialsVersion = 2
        }, default)).Report.LoginRequests);
        Assert.Equal(1, (await c.ReadAsync(Profile(), default)).Report.LoginRequests);
    }
    [Fact]
    public async Task RedirectToOtherOrigin_DoesNotForwardSecrets()
    {
        var h = new ScriptedHandler((_, _) => { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new Uri("https://evil.example.invalid/LOGIN"); return Task.FromResult(r); });
        using var s = new AirWaveSession(_ => h);
        var r = await new AirWaveClient(s, new MemoryCredentials(), TimeProvider.System).ReadAsync(Profile(), default);
        Assert.Equal(RunOutcome.Failed, r.Batch.Outcome);
        Assert.Single(r.Batch.Errors);
        Assert.Equal(1, h.Calls);
    }
    [Fact]
    public async Task Secrets_AreRedacted()
    {
        var h = new ScriptedHandler((_, _) => throw new HttpRequestException("password=p&ss secret-token secret-cookie"));
        using var s = new AirWaveSession(_ => h);
        var r = await new AirWaveClient(s, new MemoryCredentials(), TimeProvider.System).ReadAsync(Profile(), default);
        var json = JsonSerializer.Serialize(r);
        Assert.DoesNotContain("p&ss", json);
        Assert.DoesNotContain("secret-token", json);
        Assert.DoesNotContain("secret-cookie", json);
        Assert.Contains(r.Batch.Errors, e => e.Category == CollectionErrorCategory.Transport);
    }
    [Fact]
    public async Task TlsFailure_HasNoHttpStatus()
    {
        var h = new ScriptedHandler((_, _) => throw new HttpRequestException(HttpRequestError.SecureConnectionError, "unsafe", null));
        using var s = new AirWaveSession(_ => h);
        var r = await new AirWaveClient(s, new MemoryCredentials(), TimeProvider.System).ReadAsync(Profile(), default);
        Assert.Null(r.Report.HttpStatus);
        Assert.Equal("failed", r.Report.TlsResult);
        Assert.Equal("tls-handshake-or-certificate", r.Report.TlsFailureCode);
        Assert.Contains(r.Batch.Errors, e => e.Category == CollectionErrorCategory.Tls);
        Assert.DoesNotContain("unsafe", JsonSerializer.Serialize(r));
    }
    [Fact]
    public async Task UntrustedLoopbackServerWithWrongName_ReportsBothFailuresAndRejectsConnection()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=private-test.example.invalid", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("private-test.example.invalid");
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Import into the platform's default key provider for Windows Schannel server use.
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.DefaultKeySet);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var profile = Profile(new Uri($"https://127.0.0.1:{port}")) with { Proxy = new(ProxyMode.Direct) };
        using var session = new AirWaveSession();
        var client = new AirWaveClient(session, new MemoryCredentials(), TimeProvider.System);

        var reading = client.ReadAsync(profile, timeout.Token);
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        await using var tls = new SslStream(peer.GetStream());
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
        }
        catch (AuthenticationException) { } // The client may reject the certificate before the server's handshake returns.
        catch (IOException) { }

        var result = await reading;
        Assert.Equal(RunOutcome.Failed, result.Batch.Outcome);
        Assert.Null(result.Report.HttpStatus);
        Assert.Equal("certificate-name-mismatch", result.Report.TlsFailureCode);
        var checks = Assert.IsType<TlsCertificateChecks>(result.Report.TlsCertificateChecks);
        Assert.Equal("mismatch", checks.ServerName);
        Assert.Equal("failed", checks.TrustChain);
        Assert.Contains("untrusted-root", checks.ChainReasons);
        Assert.DoesNotContain("private-test.example.invalid", JsonSerializer.Serialize(result));
        Assert.Contains("신뢰 체인", Assert.Single(result.Batch.Errors).SafeMessage);
        Assert.Equal(0, result.Report.ApRequests);
    }

    [Fact]
    public async Task SecondRequestFailsWithoutCertificate_DoesNotKeepLoginTlsEvidence()
    {
        var handler = new ScriptedHandler((_, n) => n == 1 ? Task.FromResult(Login()) :
            throw new HttpRequestException(HttpRequestError.SecureConnectionError, "sensitive", null));
        using var session = new AirWaveSession(_ => handler);
        var result = await new AirWaveClient(session, new MemoryCredentials(), TimeProvider.System).ReadAsync(Profile(), default);
        Assert.Equal("failed", result.Report.TlsResult);
        Assert.Null(result.Report.HttpStatus);
        var checks = Assert.IsType<TlsCertificateChecks>(result.Report.TlsCertificateChecks);
        Assert.Equal("unknown", checks.ServerName);
        Assert.Equal("unknown", checks.TrustChain);
        Assert.Empty(checks.ChainReasons);
    }

    [Fact]
    public async Task LaterHandshakeFailureWithoutCallback_DoesNotReusePreviousCertificateFailure()
    {
        using var tlsHandler = AirWaveSession.CreateHandler(new(ProxyMode.Direct));
        var handler = new ScriptedHandler((request, n) =>
        {
            if (n == 1)
                Assert.False(tlsHandler.ServerCertificateCustomValidationCallback!(
                    request, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
            throw new HttpRequestException(HttpRequestError.SecureConnectionError, "sensitive", null);
        });
        using var session = new AirWaveSession(_ => handler);
        var client = new AirWaveClient(session, new MemoryCredentials(), TimeProvider.System);
        var profile = Profile();
        var first = await client.ReadAsync(profile, default);
        Assert.Equal("certificate-missing", first.Report.TlsFailureCode);
        Assert.Equal(TlsCertificateChecks.Unknown, first.Report.TlsCertificateChecks);
        var next = await client.ReadAsync(profile, default);
        Assert.Equal("tls-handshake-or-certificate", next.Report.TlsFailureCode);
        Assert.Equal(TlsCertificateChecks.Unknown, next.Report.TlsCertificateChecks);
    }

    [Fact]
    public async Task SuccessfulHttpsResponse_ReportsValidatedChecksEvenWhenConnectionIsReused()
    {
        using var session = new AirWaveSession(_ => new ScriptedHandler((_, n) => Task.FromResult(n == 1 ? Login() : Ap())));
        var client = new AirWaveClient(session, new MemoryCredentials(), TimeProvider.System);
        var profile = Profile();
        await client.ReadAsync(profile, default);
        var result = await client.ReadAsync(profile, default);
        Assert.Equal(0, result.Report.LoginRequests);
        var checks = Assert.IsType<TlsCertificateChecks>(result.Report.TlsCertificateChecks);
        Assert.Equal("matched", checks.ServerName);
        Assert.Equal("valid", checks.TrustChain);
        Assert.Empty(checks.ChainReasons);
    }

    [Theory]
    [InlineData(X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain, "untrusted-root,partial-chain")]
    [InlineData(X509ChainStatusFlags.PartialChain, "partial-chain")]
    [InlineData(X509ChainStatusFlags.NotTimeValid, "time-invalid")]
    [InlineData(X509ChainStatusFlags.Revoked, "other-chain-error")]
    [InlineData(X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.Revoked, "untrusted-root,other-chain-error")]
    [InlineData(X509ChainStatusFlags.NoError, "other-chain-error")]
    public void ChainReasons_DistinguishOnlyObservedStatuses(X509ChainStatusFlags flags, string expected)
    {
        var now = DateTimeOffset.UtcNow;
        var checks = TlsCertificateDiagnostics.DescribeChecks(SslPolicyErrors.RemoteCertificateChainErrors,
            flags, now, now.AddDays(-1), now.AddDays(1));
        Assert.Equal("matched", checks.ServerName);
        Assert.Equal("failed", checks.TrustChain);
        Assert.Equal(expected.Split(','), checks.ChainReasons.ToArray());
    }

    [Fact]
    public void PolicyFlags_ReportNameOnlyAndLeafValiditySeparately()
    {
        var now = DateTimeOffset.UtcNow;
        var nameOnly = TlsCertificateDiagnostics.DescribeChecks(SslPolicyErrors.RemoteCertificateNameMismatch,
            X509ChainStatusFlags.NoError, now, now.AddDays(-1), now.AddDays(1));
        Assert.Equal("mismatch", nameOnly.ServerName);
        Assert.Equal("valid", nameOnly.TrustChain);
        Assert.Empty(nameOnly.ChainReasons);

        const SslPolicyErrors both = SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors;
        var expired = TlsCertificateDiagnostics.DescribeChecks(both, X509ChainStatusFlags.NotTimeValid,
            now, now.AddDays(-2), now.AddDays(-1));
        Assert.Equal("mismatch", expired.ServerName);
        Assert.Equal("failed", expired.TrustChain);
        Assert.Equal("expired", Assert.Single(expired.ChainReasons));
        var future = TlsCertificateDiagnostics.DescribeChecks(both, X509ChainStatusFlags.NotTimeValid,
            now, now.AddDays(1), now.AddDays(2));
        Assert.Equal("not-yet-valid", Assert.Single(future.ChainReasons));
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch, "certificate-name-mismatch")]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable, "certificate-missing")]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, "certificate-trust-chain")]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, "certificate-name-mismatch")]
    public void TlsClassifier_UsesSafeCertificateFailureCodes(SslPolicyErrors policyErrors, string expected)
    {
        var code = TlsCertificateDiagnostics.Classify(policyErrors, DateTimeOffset.UtcNow);
        Assert.Equal(expected, code);
        Assert.Contains("HTTP_STATUS=미수신", TlsCertificateDiagnostics.SafeMessage(code));
    }

    [Fact]
    public void TlsClassifier_DifferentiatesCertificateTimeErrors()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("certificate-expired",
            TlsCertificateDiagnostics.Classify(SslPolicyErrors.RemoteCertificateChainErrors,
                now, now.AddDays(-20), now.AddDays(-1)));
        Assert.Equal("certificate-not-yet-valid",
            TlsCertificateDiagnostics.Classify(SslPolicyErrors.RemoteCertificateChainErrors,
                now, now.AddDays(1), now.AddDays(40)));
        Assert.Equal("validated", TlsCertificateDiagnostics.Classify(SslPolicyErrors.None, now));
    }

    [Fact]
    public void CertificateCallback_NeverAcceptsNameMismatchOrMissingCertificate()
    {
        string? observed = null;
        using var handler = AirWaveSession.CreateHandler(new ProxySettings(ProxyMode.Direct), reason => observed = reason);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://airwave.example.invalid/"));
        var callback = Assert.IsType<Func<HttpRequestMessage, System.Security.Cryptography.X509Certificates.X509Certificate2?,
            System.Security.Cryptography.X509Certificates.X509Chain?, SslPolicyErrors, bool>>(
            handler.ServerCertificateCustomValidationCallback);

        Assert.False(handler.UseProxy);
        Assert.False(callback(request, null, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.Equal("certificate-name-mismatch", observed);
        Assert.False(callback(request, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.Equal("certificate-missing", observed);
    }

    [Fact]
    public void ProxySelectionHint_DoesNotExposeFixedProxyUrl()
    {
        var origin = new Uri("https://airwave.example.invalid/");
        Assert.Equal("direct-selected", AirWaveSession.ProxyRouteHint(new(ProxyMode.Direct), origin));
        Assert.Equal("fixed-proxy-selected",
            AirWaveSession.ProxyRouteHint(new(ProxyMode.Fixed, new Uri("http://proxy.example.invalid:8080")), origin));
    }

    [Fact]
    public async Task Cancellation_StopsBodyRead()
    {
        var h = new ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new WaitingStream()) }));
        using var s = new AirWaveSession(_ => h);
        using var cancel = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AirWaveClient(s, new MemoryCredentials(), TimeProvider.System).ReadAsync(Profile(), cancel.Token));
    }
    [Theory]
    [InlineData("http://proxy.example.invalid/proxy.pac")]
    [InlineData("http://user:password@proxy.example.invalid")]
    public void FixedProxy_RejectsPacAndEmbeddedCredentials(string address) => Assert.Throws<ArgumentException>(() => new ProxySettings(ProxyMode.Fixed, new Uri(address)));
    private sealed class MemoryCredentials : ICredentialStore
    {
        public Task<AirWaveCredentials?> ReadAsync(Guid d, Uri o, CancellationToken c) => Task.FromResult<AirWaveCredentials?>(new("user+name", "p&ss")); public Task SaveAsync(Guid d, Uri o, AirWaveCredentials a, CancellationToken c) => Task.CompletedTask; public Task ClearAsync(Guid d, Uri o, CancellationToken c) => Task.CompletedTask;
    }
    internal sealed class ScriptedHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int Calls
        {
            get; private set;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c) => response(r, ++Calls); protected override void Dispose(bool disposing)
        {
        }
    }
    private sealed class WaitingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException(); public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false; public override long Length => throw new NotSupportedException(); public override long Position
        {
            get => 0; set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long l) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
