using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Security;
using AirWaveApMonitor.Tests.Support;
namespace AirWaveApMonitor.Tests.Security;

public sealed class CredentialBoundaryTests
{
    [UnixFact]
    public async Task LiveCredentials_OnMacAreExplicitlyUnsupportedWithoutPlaintextFallback()
    {
        using var temp = new TemporaryDirectory();
        var store = new DpapiCredentialStore(temp.Path);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => store.SaveAsync(Guid.NewGuid(), new Uri("https://server.example.invalid"), new("name", "password"), default));
        Assert.Empty(Directory.EnumerateFiles(temp.Path));
    }
}
