using System.Text.RegularExpressions;

namespace AirWaveApMonitor.Core.Contracts;

/// <summary>A locally authorized leaf certificate for exactly one HTTPS IP endpoint.</summary>
public sealed partial record TemporaryCertificatePin
{
    public Uri Origin { get; }
    public string Sha256 { get; }
    public DateTimeOffset ExpiresAtUtc { get; }

    public TemporaryCertificatePin(Uri origin, string sha256, DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps ||
            origin.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
            origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 ||
            origin.Fragment.Length != 0 || origin.IdnHost.Contains('%') || origin.Port <= 0)
            throw new ArgumentException("저장된 HTTPS IP·포트에서만 지문 예외를 사용할 수 있습니다.");

        var value = sha256?.Trim() ?? "";
        if (value.Length > 95 || !Sha256Format().IsMatch(value))
            throw new ArgumentException("서버 인증서의 SHA-256 지문 64자리를 확인하세요.");
        Origin = new Uri(origin.GetLeftPart(UriPartial.Authority) + "/");
        Sha256 = value.Replace(":", "").Replace("-", "").Replace(" ", "").ToUpperInvariant();
        ExpiresAtUtc = expiresAtUtc.ToUniversalTime();
    }

    [GeneratedRegex(@"\A(?:[a-fA-F0-9]{64}|[a-fA-F0-9]{2}(?<sep>[: -])[a-fA-F0-9]{2}(?:\k<sep>[a-fA-F0-9]{2}){30})\z")]
    private static partial Regex Sha256Format();
}
