using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Settings;

namespace AirWaveApMonitor.Infrastructure.Security;

/// <summary>Registration boundary used while the shared profile operation gate is held.</summary>
public sealed class CredentialRegistration(ProfileStore profiles, AirWaveSession session, ICredentialStore credentials)
{
    public async Task RegisterAsync(AirWaveCredentials value, CancellationToken ct)
    {
        var profile = profiles.Current ?? throw new ArgumentException("서버 설정을 먼저 저장하세요.");
        // Invalidate first: even a failed/cancelled write cannot leave a newer credential file
        // paired with cookies or a token from the old account.
        await session.RetryAuthenticationAsync(ct);
        await credentials.SaveAsync(profile.DatasetId, profile.Origin, value, ct);
        // The durable credential replacement is complete. Browser cancellation must not
        // interrupt the generation update; IO failures still propagate as registration failure.
        await profiles.CredentialsChangedAsync(CancellationToken.None);
    }
}
