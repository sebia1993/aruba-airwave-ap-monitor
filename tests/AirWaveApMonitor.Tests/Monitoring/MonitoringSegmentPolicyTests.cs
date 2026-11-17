using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Monitoring;
using static AirWaveApMonitor.Tests.Monitoring.MonitoringFixture;

namespace AirWaveApMonitor.Tests.Monitoring;

public sealed class MonitoringSegmentPolicyTests
{
    [Theory]
    [InlineData(MonitoringStatus.Active, false, false)]
    [InlineData(MonitoringStatus.Paused, false, false)]
    [InlineData(MonitoringStatus.Ended, false, true)]
    [InlineData(MonitoringStatus.Active, true, true)]
    [InlineData(MonitoringStatus.Paused, true, true)]
    [InlineData(MonitoringStatus.Ended, true, true)]
    public void Reentry_SeparatesPhysicalGenerationFromMonitoringInterval(MonitoringStatus status,
        bool replacement, bool startsNew)
    {
        var nextId = Guid.Parse("44444444-4444-4444-4444-444444444442");
        var result = new MonitoringSegmentPolicy().Evaluate(Previous() with { MonitoringStatus = status },
            replacement, newIntervalId: nextId);
        Assert.Equal(startsNew, result.StartsNewInterval);
        Assert.Equal(startsNew, result.ResetsBaseline);
        Assert.Equal(startsNew ? nextId : Interval, result.MonitoringIntervalId);
        Assert.Equal(MonitoringStatus.Active, result.Status);
    }

    [Fact]
    public void ExplicitEnd_RequiresReason()
    {
        Assert.Throws<ArgumentException>(() => new MonitoringSegmentPolicy().Evaluate(Previous(), false,
            MonitoringDirective.EndTracking));
    }
}
