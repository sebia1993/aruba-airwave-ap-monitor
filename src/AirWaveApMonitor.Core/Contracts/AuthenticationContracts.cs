namespace AirWaveApMonitor.Core.Contracts;

public sealed record AuthenticationBinding
{
    public Guid DatasetId { get; }
    public string NormalizedOrigin { get; }
    public long CredentialVersion { get; }

    public AuthenticationBinding(Guid datasetId, Uri origin, long credentialVersion)
    {
        if (datasetId == Guid.Empty || !origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps ||
            origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ArgumentException("인증 상태의 데이터셋과 HTTPS 출처를 확인하세요.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(credentialVersion);
        DatasetId = datasetId;
        NormalizedOrigin = new UriBuilder(Uri.UriSchemeHttps, origin.IdnHost.ToLowerInvariant(), origin.IsDefaultPort ? -1 : origin.Port).Uri.AbsoluteUri;
        CredentialVersion = credentialVersion;
    }

    public static AuthenticationBinding From(ProfileSnapshot profile) => new(profile.DatasetId, profile.Origin, profile.CredentialsVersion);
}

public enum AuthenticationStopReason { LoginRejected, RepeatedSessionExpiry }

public sealed record AuthenticationStop(AuthenticationBinding Binding, AuthenticationStopReason Reason, DateTimeOffset OccurredAtUtc);
public sealed record AuthenticationState(AuthenticationStop? Stop, bool CredentialRegistrationPending);

/// <summary>No passwords, cookies, or tokens cross this persistence boundary.</summary>
public interface IAuthenticationStateStore
{
    Task<AuthenticationState> ReadAsync(AuthenticationBinding binding, CancellationToken ct);
    Task SaveStopAsync(AuthenticationStop stop, CancellationToken ct);
    Task ClearStopAsync(AuthenticationBinding binding, CancellationToken ct);
    Task MarkCredentialRegistrationPendingAsync(AuthenticationBinding binding, CancellationToken ct);
}

public sealed class AuthenticationStateStoreException() : IOException("인증 중단 상태를 저장하거나 확인하지 못했습니다. 데이터 폴더 권한과 저장 공간을 확인하세요.");
