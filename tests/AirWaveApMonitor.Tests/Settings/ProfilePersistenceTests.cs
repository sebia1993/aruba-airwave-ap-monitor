using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Tests.AirWave;
namespace AirWaveApMonitor.Tests.Settings;

public sealed class ProfilePersistenceTests
{
    [Fact]
    public void ProfileJson_RoundTripPreservesBindingAndFrozenTargets()
    {
        var p = AirWaveClientTests.Profile() with
        {
            CredentialsVersion = 4,
            Proxy = new(ProxyMode.Direct)
        };
        var restored = JsonSerializer.Deserialize<ProfileSnapshot>(JsonSerializer.Serialize(p));
        Assert.NotNull(restored);
        Assert.Equal(p.DatasetId, restored.DatasetId);
        Assert.Equal(p.Origin, restored.Origin);
        Assert.Equal(4, restored.CredentialsVersion);
        Assert.Equal(ProxyMode.Direct, restored.Proxy.Mode);
        Assert.Equal(5, restored.Settings.ApIntervalMinutes);
        var json = JsonSerializer.Serialize(p);
        Assert.DoesNotContain("MonitoringScope", json);
        Assert.DoesNotContain("ScopeRevisions", json);
        Assert.DoesNotContain("BaselineConfirmedSourceApIds", json);
    }
}
