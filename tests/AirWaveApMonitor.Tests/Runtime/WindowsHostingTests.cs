using AirWaveApMonitor.App.Runtime;

namespace AirWaveApMonitor.Tests.Runtime;

public sealed class WindowsHostingTests
{
    [Fact]
    public void ValidServiceName_IsPreservedForUniqueScmRegistration()
        => Assert.Equal("AirWave-CI_123", WindowsHosting.NormalizeServiceName("AirWave-CI_123"));

    [Theory]
    [InlineData("")]
    [InlineData("AirWave Service")]
    [InlineData("AirWave/Other")]
    [InlineData("AirWave'Other")]
    [InlineData("AirWave\nOther")]
    public void AmbiguousOrCommandLikeServiceName_IsRejected(string name)
        => Assert.Throws<ArgumentException>(() => WindowsHosting.NormalizeServiceName(name));

    [Fact]
    public void OverlongServiceName_IsRejected()
        => Assert.Throws<ArgumentException>(() => WindowsHosting.NormalizeServiceName(new string('a', 81)));
}
