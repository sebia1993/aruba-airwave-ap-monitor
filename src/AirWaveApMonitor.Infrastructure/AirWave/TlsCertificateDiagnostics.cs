using System.Collections.Immutable;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Infrastructure.AirWave;

/// <summary>
/// Safe, certificate-free diagnostic codes. The callback that uses this classifier
/// still rejects every non-None SslPolicyErrors value; it never bypasses trust checks.
/// </summary>
public static class TlsCertificateDiagnostics
{
    public static string Classify(SslPolicyErrors errors, DateTimeOffset nowUtc,
        DateTimeOffset? notBeforeUtc = null, DateTimeOffset? notAfterUtc = null)
    {
        if (errors == SslPolicyErrors.None) return "validated";
        if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
            return "certificate-missing";
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            return "certificate-name-mismatch";
        if ((errors & SslPolicyErrors.RemoteCertificateChainErrors) != 0)
        {
            if (notAfterUtc is not null && nowUtc >= notAfterUtc)
                return "certificate-expired";
            if (notBeforeUtc is not null && nowUtc < notBeforeUtc)
                return "certificate-not-yet-valid";
            return "certificate-trust-chain";
        }
        return "certificate-validation-failed";
    }

    public static TlsCertificateChecks DescribeChecks(SslPolicyErrors errors, X509ChainStatusFlags chainFlags,
        DateTimeOffset nowUtc, DateTimeOffset? notBeforeUtc, DateTimeOffset? notAfterUtc)
    {
        if (notBeforeUtc is null || notAfterUtc is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            return TlsCertificateChecks.Unknown;

        var serverName = errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ? "mismatch" : "matched";
        if (!errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
            return new(serverName, "valid", []);

        var reasons = ImmutableArray.CreateBuilder<string>();
        if (chainFlags.HasFlag(X509ChainStatusFlags.UntrustedRoot)) reasons.Add("untrusted-root");
        if (chainFlags.HasFlag(X509ChainStatusFlags.PartialChain)) reasons.Add("partial-chain");
        if (nowUtc >= notAfterUtc.Value) reasons.Add("expired");
        else if (nowUtc < notBeforeUtc.Value) reasons.Add("not-yet-valid");
        else if (chainFlags.HasFlag(X509ChainStatusFlags.NotTimeValid)) reasons.Add("time-invalid");

        const X509ChainStatusFlags recognized = X509ChainStatusFlags.UntrustedRoot |
            X509ChainStatusFlags.PartialChain | X509ChainStatusFlags.NotTimeValid;
        if ((chainFlags & ~recognized) != 0 || reasons.Count == 0) reasons.Add("other-chain-error");
        return new(serverName, "failed", reasons.ToImmutable());
    }

    public static string SafeMessage(string? reason) => reason switch
    {
        "certificate-name-mismatch" =>
            "TLS 인증서 서버 이름 불일치: 입력한 AirWave 주소와 인증서의 DNS 이름을 확인하세요. HTTP_STATUS=미수신",
        "certificate-expired" =>
            "TLS 인증서 유효기간 만료: AirWave 인증서 갱신 및 PC 시각을 확인하세요. HTTP_STATUS=미수신",
        "certificate-not-yet-valid" =>
            "TLS 인증서 유효기간 시작 전: 서버 인증서와 PC 시각을 확인하세요. HTTP_STATUS=미수신",
        "certificate-missing" =>
            "TLS 서버 인증서를 받지 못했습니다. 프록시와 AirWave HTTPS 설정을 확인하세요. HTTP_STATUS=미수신",
        "certificate-trust-chain" =>
            "TLS 인증서 신뢰 체인 실패: 승인된 사내 CA·중간 인증서와 프록시 SSL 검사를 확인하세요. HTTP_STATUS=미수신",
        "certificate-validation-failed" =>
            "TLS 인증서 검증 실패: AirWave 인증서와 Windows 신뢰 정책을 확인하세요. HTTP_STATUS=미수신",
        _ => "TLS 보안 연결 실패: 인증서·TLS 협상·사내 프록시 경로를 확인하세요. HTTP_STATUS=미수신"
    };
}
