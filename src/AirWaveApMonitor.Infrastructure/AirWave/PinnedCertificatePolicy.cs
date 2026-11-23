using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Infrastructure.AirWave;

/// <summary>One policy per transport and connection pool. All application requests on it share the session gate.</summary>
internal sealed class PinnedCertificatePolicy(TemporaryCertificatePin pin, TimeProvider clock) : DelegatingHandler
{
    internal TlsCertificateChecks? AcceptedException { get; private set; }

    internal string? VerifyPeer(Uri? destination, X509Certificate2? certificate, X509Chain? chain,
        SslPolicyErrors errors, TlsCertificateChecks checks, DateTimeOffset now)
    {
        AcceptedException = null;
        var blocked = TemporaryCertificatePinValidation.CheckPeer(pin, destination, certificate, chain, errors, now);
        if (blocked is null && errors != SslPolicyErrors.None) AcceptedException = checks;
        return blocked;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // This check also runs on pooled connections, which do not call the TLS callback again.
        var now = clock.GetUtcNow();
        var failure = TemporaryCertificatePinValidation.CheckScope(pin, request.RequestUri, now);
        if (failure is not null) throw Block(failure);
        using var expiry = new CancellationTokenSource(pin.ExpiresAtUtc - now, clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, expiry.Token);
        try { return await base.SendAsync(request, deadline.Token); }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw Block("pin-expired");
        }
    }

    internal static AirWaveSession.CertificatePolicyException Block(string code) =>
        new(new(code, TlsCertificateChecks.Unknown));
}
