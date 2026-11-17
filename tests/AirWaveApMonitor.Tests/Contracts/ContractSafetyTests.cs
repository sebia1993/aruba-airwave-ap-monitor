using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Tests.Contracts;

public sealed class ContractSafetyTests
{
    [Fact]
    public void Credentials_CannotBeSerializedOrPrintedAsSecrets()
    {
        var credentials = new AirWaveCredentials("secret-user", "secret-password");
        Assert.DoesNotContain("secret-user", credentials.ToString());
        Assert.DoesNotContain("secret-password", credentials.ToString());
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(credentials));
        var loggedProperties = credentials.GetType().GetProperties().Where(property => property.CanRead)
            .ToDictionary(property => property.Name, property => property.GetValue(credentials));
        var destructured = JsonSerializer.Serialize(loggedProperties);
        Assert.DoesNotContain("secret-user", destructured);
        Assert.DoesNotContain("secret-password", destructured);
    }

    [Fact]
    public void Profile_FreezesTargetsAndNormalizesHttpsOrigin()
    {
        var targets = new List<ExpectedTarget> { new("ap-1") };
        var profile = new ProfileSnapshot(Guid.NewGuid(), Guid.NewGuid(), new Uri("https://DEMO.EXAMPLE.INVALID:443/"),
            1, 1, targets, BaselineStatus.Confirmed, new CollectionSettings());
        targets.Add(new("ap-2"));
        Assert.Single(profile.ExpectedTargets);
        Assert.Equal("https://demo.example.invalid/", profile.Origin.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://example.invalid")]
    [InlineData("https://user:password@example.invalid")]
    [InlineData("https://example.invalid/api")]
    [InlineData("https://example.invalid/?token=secret")]
    public void Profile_RejectsUnsafeOrNonOriginAddress(string address)
    {
        Assert.Throws<ArgumentException>(() => new ProfileSnapshot(Guid.NewGuid(), Guid.NewGuid(), new Uri(address),
            1, 1, [], BaselineStatus.Unverified, new CollectionSettings()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void CollectionSettings_RejectsOutsideSupportedInterval(int minutes)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new CollectionSettings(minutes));
}
