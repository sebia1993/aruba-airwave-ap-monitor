using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;

namespace AirWaveApMonitor.Tests.AirWave;

public sealed class TemporaryPinValidationTests
{
    private static readonly Uri Target = new("https://192.0.2.7:4343");

    [Theory]
    [InlineData(X509ChainStatusFlags.NoError)]
    [InlineData(X509ChainStatusFlags.Revoked)]
    [InlineData(X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.OfflineRevocation)]
    [InlineData(X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.RevocationStatusUnknown)]
    [InlineData(X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.NotSignatureValid)]
    [InlineData(X509ChainStatusFlags.PartialChain)]
    [InlineData(X509ChainStatusFlags.NotTimeValid | X509ChainStatusFlags.ExplicitDistrust)]
    public void UnexplainedOrUnapprovedChainError_IsRejected(X509ChainStatusFlags flags) =>
        Assert.False(TemporaryCertificatePinValidation.AllowsChainFlags(flags));

    [Fact]
    public void StandardValidationCannotOverridePinnedDigestOrScope()
    {
        var now = DateTimeOffset.UtcNow;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=pin-fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        var digest = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var valid = new TemporaryCertificatePin(Target, digest, now.AddHours(1));

        string? Check(TemporaryCertificatePin pin, Uri uri) =>
            TemporaryCertificatePinValidation.CheckPeer(pin, uri, certificate, null, SslPolicyErrors.None, now);
        Assert.Null(Check(valid, new Uri(Target, "/LOGIN")));
        Assert.Equal("pin-expired", Check(new TemporaryCertificatePin(Target, digest, now), Target));
        Assert.Equal("pin-mismatch", Check(new TemporaryCertificatePin(Target, new string('0', 64), now.AddHours(1)), Target));
        Assert.Equal("pin-disallowed", Check(valid, new Uri("https://192.0.2.8:4343")));
        Assert.Equal("pin-disallowed", Check(valid, new Uri("https://192.0.2.7:443")));
        Assert.Equal("pin-disallowed", Check(valid, new Uri("https://example.invalid:4343")));
        Assert.Equal("pin-disallowed", Check(valid, new Uri("http://192.0.2.7:4343")));
        Assert.Equal("pin-disallowed", TemporaryCertificatePinValidation.CheckPeer(valid, Target,
            certificate, null, SslPolicyErrors.RemoteCertificateChainErrors, now));
    }

    [Fact]
    public void ExpiredIssuerAlongsideExpiredLeaf_IsRejected()
    {
        var now = DateTimeOffset.UtcNow;
        using var rootKey = RSA.Create(2048);
        using var issuerKey = RSA.Create(2048);
        using var leafKey = RSA.Create(2048);
        var rootRequest = Request("root", rootKey, isCa: true);
        using var root = rootRequest.CreateSelfSigned(now.AddDays(-10), now.AddDays(10));
        var issuerRequest = Request("issuer", issuerKey, isCa: true);
        using var issuerPublic = issuerRequest.Create(root, now.AddDays(-8), now.AddDays(-1), [2]);
        using var issuer = issuerPublic.CopyWithPrivateKey(issuerKey);
        var leafRequest = Request("leaf", leafKey, isCa: false);
        using var leaf = leafRequest.Create(issuer, now.AddDays(-7), now.AddDays(-2), [3]);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.ExtraStore.Add(issuer);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = now.LocalDateTime;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        Assert.False(chain.Build(leaf));
        Assert.Equal(3, chain.ChainElements.Count);
        Assert.Contains(chain.ChainStatus, status => status.Status.HasFlag(X509ChainStatusFlags.NotTimeValid));

        var pin = new TemporaryCertificatePin(Target, leaf.GetCertHashString(HashAlgorithmName.SHA256), now.AddHours(1));
        Assert.Equal("pin-disallowed", TemporaryCertificatePinValidation.CheckPeer(pin, Target, leaf, chain,
            SslPolicyErrors.RemoteCertificateChainErrors, now));
    }

    private static CertificateRequest Request(string name, RSA key, bool isCa)
    {
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(isCa, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(isCa ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature, true));
        return request;
    }
}
