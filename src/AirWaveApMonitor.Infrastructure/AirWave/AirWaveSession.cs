using System.Net;
using AirWaveApMonitor.Core.Contracts;
namespace AirWaveApMonitor.Infrastructure.AirWave;

/// <summary>Process-memory session only. The caller serializes reads and credential changes.</summary>
public sealed class AirWaveSession(Func<ProxySettings, HttpMessageHandler>? factory = null) : IDisposable
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    private string? binding;
    private string? identity;
    internal HttpClient? Client
    {
        get; private set;
    }
    internal CookieContainer Cookies { get; set; } = new();
    internal string? Token
    {
        get; set;
    }
    public bool AuthenticationBlocked
    {
        get; internal set;
    }
    public async Task RetryAuthenticationAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            AuthenticationBlocked = false;
            Token = null;
            Cookies = new();
        }
        finally { Gate.Release(); }
    }
    internal void Bind(ProfileSnapshot profile)
    {
        var credentialIdentity = $"{profile.DatasetId}|{profile.Origin.AbsoluteUri}|{profile.CredentialsVersion}";
        var key = $"{credentialIdentity}|{profile.Proxy.Mode}|{profile.Proxy.Address}";
        if (binding == key)
            return;
        Client?.Dispose();
        Client = new HttpClient(factory?.Invoke(profile.Proxy) ?? CreateHandler(profile.Proxy)) { Timeout = Timeout.InfiniteTimeSpan };
        Cookies = new();
        Token = null;
        if (identity != credentialIdentity)
            AuthenticationBlocked = false;
        identity = credentialIdentity;
        binding = key;
    }
    public static HttpClientHandler CreateHandler(ProxySettings proxy)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli, MaxConnectionsPerServer = 1 };
        if (proxy.Mode == ProxyMode.Direct)
            handler.UseProxy = false;
        else if (proxy.Mode == ProxyMode.Fixed)
            handler.Proxy = new WebProxy(proxy.Address!);
        return handler;
    }
    public void Dispose()
    {
        Client?.Dispose();
        Gate.Dispose();
    }
}
