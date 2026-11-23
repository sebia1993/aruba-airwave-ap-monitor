using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Infrastructure.AirWave;

/// <summary>Observes the target leaf in a separate TLS handshake. The collector's login session is never used.</summary>
public static class CertificatePinProbe
{
    public static async Task<string> ProbeAsync(ProfileSnapshot profile, CancellationToken ct)
    {
        var origin = profile.Origin;
        if (origin.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
            origin.IdnHost.Contains('%') || origin.Port <= 0)
            throw new ArgumentException("저장된 HTTPS IP·포트에서만 서버 인증서를 조회할 수 있습니다.");

        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Head, origin);
        using var handler = AirWaveSession.CreateHandler(profile.Proxy);
        var standardValidation = handler.ServerCertificateCustomValidationCallback!;
        handler.ServerCertificateCustomValidationCallback = (peerRequest, certificate, chain, errors) =>
        {
            // The proxy's TLS handshake belongs to a separate, internal CONNECT request.
            if (peerRequest.Method == HttpMethod.Connect)
                return standardValidation(peerRequest, certificate, chain, errors);
            if (!ReferenceEquals(peerRequest, request) || certificate is null ||
                errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
                return false;

            // Abort even a PKI-valid handshake before HEAD or any other HTTP request can reach the origin.
            throw new ObservedCertificate(certificate.GetCertHashString(HashAlgorithmName.SHA256));
        };
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = profile.Settings.RequestTimeout };
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException error) when (error.GetBaseException() is ObservedCertificate observed)
        {
            ct.ThrowIfCancellationRequested();
            return observed.Sha256;
        }
        catch (HttpRequestException error) when (error.GetBaseException() is AirWaveSession.CertificatePolicyException)
        {
            throw new CertificatePinProbeException("HTTPS 프록시 인증서 검증에 실패했습니다. 프록시 인증서와 연결 경로를 확인하세요.");
        }
        catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.ProxyTunnelError)
        {
            throw new CertificatePinProbeException("프록시 터널을 열 수 없습니다. 수집기의 프록시 설정을 확인하세요.");
        }
        catch (HttpRequestException)
        {
            throw new CertificatePinProbeException("서버 인증서를 조회하지 못했습니다. 저장된 IP·포트와 연결 경로를 확인하세요.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CertificatePinProbeException("서버 인증서 조회 시간이 초과됐습니다.");
        }

        throw new CertificatePinProbeException("서버 인증서를 확인하지 못했습니다.");
    }

    private sealed class ObservedCertificate(string sha256) : AuthenticationException("Target certificate observed.")
    {
        public string Sha256 { get; } = sha256;
    }
}

public sealed class CertificatePinProbeException(string message) : Exception(message);
