using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Infrastructure.Security;

public static class CredentialStoreSelection
{
    public static ICredentialStore Create(AppMode mode, Func<ICredentialStore> liveFactory) => mode switch
    {
        AppMode.Demo => new DemoCredentialStore(),
        AppMode.Live => liveFactory(),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    // Demo requires no login; accepting credentials here could retain real secrets by mistake.
    private sealed class DemoCredentialStore : ICredentialStore
    {
        public Task SaveAsync(Guid datasetId, Uri origin, AirWaveCredentials credentials, CancellationToken ct)
            => throw new NotSupportedException("Demo 모드에는 실제 자격 증명을 등록할 수 없습니다.");
        public Task<AirWaveCredentials?> ReadAsync(Guid datasetId, Uri origin, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<AirWaveCredentials?>(null);
        }
        public Task ClearAsync(Guid datasetId, Uri origin, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
