using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Infrastructure.AirWave;

public static class TemporaryCertificatePinValidation
{
    internal static string? CheckScope(TemporaryCertificatePin pin, Uri? destination, DateTimeOffset now)
    {
        if (now >= pin.ExpiresAtUtc) return "pin-expired";
        if (destination is not { IsAbsoluteUri: true } || destination.Scheme != Uri.UriSchemeHttps ||
            destination.UserInfo.Length != 0 || destination.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
            !destination.IdnHost.Equals(pin.Origin.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            destination.Port != pin.Origin.Port) return "pin-disallowed";
        return null;
    }

    /// <summary>Checks an origin certificate against the configured DER SHA-256 and the allowed OS validation errors.</summary>
    public static string? CheckPeer(TemporaryCertificatePin pin, Uri? destination, X509Certificate2? certificate, X509Chain? chain,
        SslPolicyErrors errors, DateTimeOffset now)
    {
        var blocked = CheckScope(pin, destination, now);
        if (blocked is not null) return blocked;
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return "pin-disallowed";
        if (!CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), Convert.FromHexString(pin.Sha256)))
            return "pin-mismatch";

        const SslPolicyErrors permittedPolicy = SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors;
        if ((errors & ~permittedPolicy) != 0 || now < certificate.NotBefore.ToUniversalTime()) return "pin-disallowed";
        if (!errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
            return now < certificate.NotAfter.ToUniversalTime() ? null : "pin-disallowed";
        if (chain is null || chain.ChainElements.Count == 0) return "pin-disallowed";

        var flags = X509ChainStatusFlags.NoError;
        foreach (var status in chain.ChainStatus) flags |= status.Status;
        if (!AllowsChainFlags(flags)) return "pin-disallowed";
        if (!flags.HasFlag(X509ChainStatusFlags.NotTimeValid))
            return now < certificate.NotAfter.ToUniversalTime() ? null : "pin-disallowed";

        // The aggregate time flag can refer to any certificate. Only an identified expired leaf is eligible.
        var first = chain.ChainElements[0];
        if (now < certificate.NotAfter.ToUniversalTime() || !first.Certificate.RawDataMemory.Span.SequenceEqual(certificate.RawDataMemory.Span) ||
            !first.ChainElementStatus.Any(status => status.Status.HasFlag(X509ChainStatusFlags.NotTimeValid))) return "pin-disallowed";
        for (var i = 1; i < chain.ChainElements.Count; i++)
        {
            var issuer = chain.ChainElements[i];
            if (issuer.ChainElementStatus.Any(status => status.Status.HasFlag(X509ChainStatusFlags.NotTimeValid)) ||
                now < issuer.Certificate.NotBefore.ToUniversalTime() || now >= issuer.Certificate.NotAfter.ToUniversalTime()) return "pin-disallowed";
        }
        return null;
    }

    public static bool AllowsChainFlags(X509ChainStatusFlags flags)
    {
        const X509ChainStatusFlags permitted = X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.NotTimeValid;
        return flags != X509ChainStatusFlags.NoError && (flags & ~permitted) == 0;
    }
}
