using System.Net;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Security;

namespace AirWaveApMonitor.Infrastructure.AirWave;

/// <summary>Cookies/tokens stay in memory. The identity's stop is restored before login.</summary>
public sealed class AirWaveSession(Func<ProxySettings, HttpMessageHandler>? factory = null,
    IAuthenticationStateStore? authenticationStates = null) : IDisposable
{
    private readonly IAuthenticationStateStore states = authenticationStates ?? new MemoryAuthenticationStateStore();
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    private string? transportBinding;
    private AuthenticationBinding? identity;
    private bool stopNeedsPersistence;
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
        if (identity == nextIdentity && transportBinding == nextTransport) return;
        if (identity is not null && identity.DatasetId == nextIdentity.DatasetId &&
            identity.NormalizedOrigin == nextIdentity.NormalizedOrigin && identity.CredentialVersion > nextIdentity.CredentialVersion)
            throw new CredentialUnavailableException(); // A late old context cannot use the replacement file.

        var state = identity == nextIdentity
            ? new AuthenticationState(AuthenticationStop, CredentialRegistrationPending)
            : await states.ReadAsync(nextIdentity, ct);
        // Nothing becomes active until the durable state has been read successfully.
        var nextClient = new HttpClient(factory?.Invoke(profile.Proxy) ?? CreateHandler(profile.Proxy)) { Timeout = Timeout.InfiniteTimeSpan };
        Client?.Dispose();
        Client = nextClient;
        InvalidateSession();
        if (identity != nextIdentity) stopNeedsPersistence = false;
        AuthenticationStop = state.Stop;
        CredentialRegistrationPending = state.CredentialRegistrationPending;
        identity = nextIdentity;
        transportBinding = nextTransport;
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

    public static HttpClientHandler CreateHandler(ProxySettings proxy)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            MaxConnectionsPerServer = 1
        };
        if (proxy.Mode == ProxyMode.Direct) handler.UseProxy = false;
        else if (proxy.Mode == ProxyMode.Fixed) handler.Proxy = new WebProxy(proxy.Address!);
        return handler;
    }

    public void Dispose()
    {
        Client?.Dispose();
        Gate.Dispose();
    }
}
