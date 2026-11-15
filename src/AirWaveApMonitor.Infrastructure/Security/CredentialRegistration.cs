using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Settings;

namespace AirWaveApMonitor.Infrastructure.Security;

/// <summary>Caller holds ProfileStore.OperationGate; lock order is operation, session, database.</summary>
public sealed class CredentialRegistration(ProfileStore profiles, AirWaveSession session, ICredentialStore credentials)
{
    public async Task RegisterAsync(AirWaveCredentials value, CancellationToken ct)
    {
        var profile = profiles.Current ?? throw new ArgumentException("서버 설정을 먼저 저장하세요.");
        // A known unsupported platform cannot replace the credential file. Reject before
        // committing an intent that would incorrectly require registration recovery.
        if (credentials is DpapiCredentialStore && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("실제 자격 증명은 Windows 실행 계정 DPAPI가 필요합니다.");
        DpapiCredentialStore.Validate(value);
        await session.Gate.WaitAsync(ct);
        try
        {
            // DPAPI and profile.json cannot share a transaction. This durable intent prevents
            // a crash or either write failure from pairing a replacement file with the old identity.
            // It preserves any old rejection, and only successful re-registration can supersede it.
            await session.PrepareCredentialRegistrationAsync(profile, ct);
            await credentials.SaveAsync(profile.DatasetId, profile.Origin, value, ct);
            // After credential replacement completes, caller cancellation must not strand
            // a successful write before its generation update. IO failures remain failures.
            await profiles.CredentialsChangedAsync(CancellationToken.None);
            await session.BindAsync(profiles.Current!, CancellationToken.None);
        }
        finally { session.Gate.Release(); }
    }
}
