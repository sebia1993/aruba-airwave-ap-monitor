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

}
