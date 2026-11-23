using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Security;

namespace AirWaveApMonitor.Infrastructure.AirWave;

/// <summary>Cookies/tokens stay in memory. The identity's stop is restored before login.</summary>
public sealed class AirWaveSession(Func<ProxySettings, HttpMessageHandler>? factory = null,
    IAuthenticationStateStore? authenticationStates = null, TimeProvider? clock = null) : IDisposable
{
    private readonly IAuthenticationStateStore states = authenticationStates ?? new MemoryAuthenticationStateStore();
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    private string? transportBinding;
    private TemporaryCertificatePin? boundPin;
    private PinnedCertificatePolicy? pinPolicy;
    private long highestConfigurationVersion;
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    internal TlsCertificateChecks? AcceptedPinException => pinPolicy?.AcceptedException;
    private AuthenticationBinding? identity;
    private bool stopNeedsPersistence;
    internal sealed record CertificateFailure(string Code, TlsCertificateChecks Checks);
    internal sealed class CertificatePolicyException(CertificateFailure failure) : AuthenticationException("TLS certificate policy rejected the connection.")
    {
        public CertificateFailure Failure { get; } = failure;
    }
    internal static readonly HttpRequestOptionsKey<CertificateFailure> CertificateFailureKey =
        new("AirWave.CertificateFailure");
    internal HttpClient? Client { get; private set; }
    internal CookieContainer Cookies { get; set; } = new();
    internal string? Token { get; set; }
    public AuthenticationStop? AuthenticationStop { get; private set; }
    public bool AuthenticationBlocked => AuthenticationStop is not null;
    public bool CredentialRegistrationPending { get; private set; }

    public async Task RestoreAuthenticationStateAsync(ProfileSnapshot profile, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await BindAsync(profile, ct);
            await PersistPendingStopAsync(ct);
        }
        finally { Gate.Release(); }
    }

    public Task RetryAuthenticationAsync(CancellationToken ct = default) => RetryAsync(null, ct);
    public Task RetryAuthenticationAsync(ProfileSnapshot profile, CancellationToken ct = default) => RetryAsync(profile, ct);

    private async Task RetryAsync(ProfileSnapshot? profile, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (profile is not null) await BindAsync(profile, ct);
            if (CredentialRegistrationPending) throw new CredentialUnavailableException();
            // A failed or cancelled durable clear must leave the memory stop and cookies unchanged.
            if (identity is not null) await states.ClearStopAsync(identity, ct);
            AuthenticationStop = null;
            stopNeedsPersistence = false;
            InvalidateSession();
        }
        finally { Gate.Release(); }
    }

    internal async Task BindAsync(ProfileSnapshot profile, CancellationToken ct)
    {
        var nextIdentity = AuthenticationBinding.From(profile);
        var nextTransport = $"{nextIdentity}|{profile.Proxy.Mode}|{profile.Proxy.Address}";
        var sameSource = identity?.DatasetId == nextIdentity.DatasetId && identity.NormalizedOrigin == nextIdentity.NormalizedOrigin;
        var nextPin = profile.TemporaryCertificatePin;
        if (sameSource && nextPin != boundPin && profile.ConfigurationVersion <= highestConfigurationVersion)
            throw PinnedCertificatePolicy.Block("pin-disallowed");
        if (identity == nextIdentity && transportBinding == nextTransport && nextPin == boundPin)
        {
            highestConfigurationVersion = Math.Max(highestConfigurationVersion, profile.ConfigurationVersion);
            return;
        }
        if (identity is not null && identity.DatasetId == nextIdentity.DatasetId &&
            identity.NormalizedOrigin == nextIdentity.NormalizedOrigin && identity.CredentialVersion > nextIdentity.CredentialVersion)
            throw new CredentialUnavailableException(); // A late old context cannot use the replacement file.

        var state = identity == nextIdentity
            ? new AuthenticationState(AuthenticationStop, CredentialRegistrationPending)
            : await states.ReadAsync(nextIdentity, ct);
        // Nothing becomes active until the durable state has been read successfully.
        var nextPolicy = nextPin is null ? null : new PinnedCertificatePolicy(nextPin, time);
        HttpMessageHandler handler;
        if (nextPolicy is null) handler = factory?.Invoke(profile.Proxy) ?? CreateHandler(profile.Proxy, null, null, time);
        else
        {
            nextPolicy.InnerHandler = CreateHandler(profile.Proxy, null, nextPolicy, time);
            handler = nextPolicy;
        }
        var nextClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        Client?.Dispose();
        Client = nextClient;
        InvalidateSession();
        if (identity != nextIdentity) stopNeedsPersistence = false;
        AuthenticationStop = state.Stop;
        CredentialRegistrationPending = state.CredentialRegistrationPending;
        identity = nextIdentity;
        transportBinding = nextTransport;
        boundPin = nextPin;
        pinPolicy = nextPolicy;
        highestConfigurationVersion = sameSource ? Math.Max(highestConfigurationVersion, profile.ConfigurationVersion) : profile.ConfigurationVersion;
    }

    internal async Task StopAuthenticationAsync(AuthenticationStopReason reason, DateTimeOffset occurredAtUtc)
    {
        var binding = identity ?? throw new InvalidOperationException("인증 대상이 설정되지 않았습니다.");
        AuthenticationStop = new(binding, reason, occurredAtUtc.ToUniversalTime());
        stopNeedsPersistence = true;
        InvalidateSession();
        // A confirmed rejection remains relevant if a browser disconnects at this boundary.
        // The SQLite operation has a bounded busy timeout; its database gate is never held during HTTP.
        await PersistPendingStopAsync(CancellationToken.None);
    }

    internal async Task PersistPendingStopAsync(CancellationToken ct)
    {
        if (!stopNeedsPersistence || AuthenticationStop is null) return;
        await states.SaveStopAsync(AuthenticationStop, ct);
        stopNeedsPersistence = false;
    }

    internal async Task PrepareCredentialRegistrationAsync(ProfileSnapshot profile, CancellationToken ct)
    {
        await BindAsync(profile, ct);
        InvalidateSession();
        await states.MarkCredentialRegistrationPendingAsync(identity!, ct);
        CredentialRegistrationPending = true;
    }

    private void InvalidateSession()
    {
        Token = null;
        Cookies = new();
    }

    public static HttpClientHandler CreateHandler(ProxySettings proxy, Action<string>? tlsFailureObserver = null) =>
        CreateHandler(proxy, tlsFailureObserver, null, TimeProvider.System);

    private static HttpClientHandler CreateHandler(ProxySettings proxy, Action<string>? tlsFailureObserver,
        PinnedCertificatePolicy? pinPolicy, TimeProvider time)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            MaxConnectionsPerServer = 1
        };
        if (pinPolicy is not null) handler.CheckCertificateRevocationList = true;
        // An HTTPS proxy's own certificate never receives the origin pin exception.
        handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
        {
            var isProxyHandshake = request.Method == HttpMethod.Connect;
            var pin = isProxyHandshake ? null : pinPolicy;
            if (errors == SslPolicyErrors.None && certificate is not null && pin is null) return true;

            var now = time.GetUtcNow();
            DateTimeOffset? notBefore = certificate?.NotBefore.ToUniversalTime();
            DateTimeOffset? notAfter = certificate?.NotAfter.ToUniversalTime();
            if (errors == SslPolicyErrors.None && certificate is null) errors = SslPolicyErrors.RemoteCertificateNotAvailable;
            var code = TlsCertificateDiagnostics.Classify(errors, now, notBefore, notAfter);
            var flags = X509ChainStatusFlags.NoError;
            if (chain is not null)
                foreach (var status in chain.ChainStatus) flags |= status.Status;
            var checks = TlsCertificateDiagnostics.DescribeChecks(errors, flags, now, notBefore, notAfter)
                with { Peer = isProxyHandshake ? "proxy" : "server" };
            if (pin is not null)
            {
                var rejected = pin.VerifyPeer(request.RequestUri, certificate, chain, errors, checks, now);
                if (rejected is null) return true;
                code = rejected;
            }
            var failure = new CertificateFailure(code, checks);
            tlsFailureObserver?.Invoke(code);
            // An HTTPS proxy uses an internal CONNECT request with separate Options.
            // Carry only the failed checks through its TLS exception to the outer request.
            if (isProxyHandshake) throw new CertificatePolicyException(failure);
            request.Options.Set(CertificateFailureKey, failure);
            return false;
        };
        if (proxy.Mode == ProxyMode.Direct) handler.UseProxy = false;
        else if (proxy.Mode == ProxyMode.Fixed) handler.Proxy = new WebProxy(proxy.Address!);
        return handler;
    }

    /// <summary>
    /// Reports the .NET default proxy resolver's current selection, not a packet
    /// trace or proof of the path used by an individual request. No proxy URL is exposed.
    /// </summary>
    public static string ProxyRouteHint(ProxySettings proxy, Uri origin)
    {
        if (proxy.Mode == ProxyMode.Direct) return "direct-selected";
        if (proxy.Mode == ProxyMode.Fixed) return "fixed-proxy-selected";
        try
        {
            var resolver = HttpClient.DefaultProxy;
            var selected = resolver.GetProxy(origin);
            if (resolver.IsBypassed(origin) || selected is null ||
                selected.Scheme == origin.Scheme &&
                selected.IdnHost.Equals(origin.IdnHost, StringComparison.OrdinalIgnoreCase) &&
                selected.Port == origin.Port)
                return "system-direct-selected";
            return "system-proxy-selected";
        }
        catch (Exception error) when (error is not OutOfMemoryException && error is not OperationCanceledException)
        {
            return "system-unresolved";
        }
    }

    public void Dispose()
    {
        Client?.Dispose();
        Gate.Dispose();
    }
}
