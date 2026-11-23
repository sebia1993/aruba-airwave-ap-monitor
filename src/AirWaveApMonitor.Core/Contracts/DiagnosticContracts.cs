using System.Collections.Immutable;
using System.Text.Json.Serialization;
namespace AirWaveApMonitor.Core.Contracts;

public enum ProxyMode
{
    System, Direct, Fixed
}
public sealed record ProxySettings
{
    public ProxyMode Mode
    {
        get;
    }
    public Uri? Address
    {
        get;
    }
    public ProxySettings(ProxyMode mode = ProxyMode.System, Uri? address = null)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentException("프록시 모드가 올바르지 않습니다.");
        if (mode == ProxyMode.Fixed && (address is null || !address.IsAbsoluteUri || address.Scheme is not ("http" or "https") || address.UserInfo.Length > 0 || address.AbsolutePath != "/" || address.Query.Length > 0 || address.Fragment.Length > 0))
            throw new ArgumentException("고정 프록시는 서버 출처만 허용합니다. PAC URL은 입력하지 마세요.");
        if (mode != ProxyMode.Fixed && address != null)
            throw new ArgumentException("고정 프록시 모드에서만 주소를 입력하세요.");
        Mode = mode;
        Address = address;
    }
}
public sealed record TlsCertificateChecks(string ServerName, string TrustChain, ImmutableArray<string> ChainReasons)
{
    public static TlsCertificateChecks Unknown { get; } = new("unknown", "unknown", []);
    public static TlsCertificateChecks Validated { get; } = new("matched", "valid", []);
}
public sealed record DiagnosticStage(string Name, string Status, int? HttpStatus, long ElapsedMilliseconds, string Message);
public sealed record DiagnosticReport(Guid DiagnosticId, DateTimeOffset StartedAtUtc, string ExecutionAccount, string ConnectionPath, string TlsResult, int? HttpStatus,
    int LoginRequests, int ApRequests, int ParsedRecords, int ExcludedRecords, long ElapsedMilliseconds, RunOutcome Outcome, bool AuthenticationBlocked,
    ImmutableArray<DiagnosticStage> Stages, ImmutableArray<CollectionError> Errors, ImmutableArray<string> VerificationNotes)
{
    // Stable safe codes only; never include a certificate, hostname, exception, or proxy URL.
    public string? TlsFailureCode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TlsCertificateChecks? TlsCertificateChecks { get; init; }
    // Resolved/default proxy selection is a hint, never an observed packet path.
    public string? ProxyRouteHint { get; init; }
}
