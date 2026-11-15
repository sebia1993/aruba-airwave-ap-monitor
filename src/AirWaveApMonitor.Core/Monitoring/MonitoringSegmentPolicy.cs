using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Core.Monitoring;

public sealed class MonitoringSegmentPolicy
{
    public MonitoringSegmentDecision Evaluate(ApTrackingSnapshot? previous, bool startsNewGeneration,
        MonitoringDirective directive = MonitoringDirective.Observe, Guid? newIntervalId = null,
        string? reason = null)
    {
        if (directive != MonitoringDirective.Observe && previous is null)
        {
            throw new ArgumentException("An existing monitoring interval is required.", nameof(previous));
        }
        if (directive != MonitoringDirective.Observe && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A monitoring change requires a reason.", nameof(reason));
        }
        if (directive == MonitoringDirective.EndTracking)
        {
            return new(MonitoringStatus.Ended, previous!.MonitoringIntervalId, false,
                previous.MonitoringStatus != MonitoringStatus.Ended, false, reason, reason);
        }
        if (directive == MonitoringDirective.TemporarilyExclude)
        {
            if (previous!.MonitoringStatus == MonitoringStatus.Ended)
            {
                throw new ArgumentException("An ended interval cannot be paused.", nameof(previous));
            }
            return new(MonitoringStatus.Paused, previous.MonitoringIntervalId, false, false, false,
                ChangeReason: reason);
        }
        var startsNew = previous is null || startsNewGeneration || previous.MonitoringStatus == MonitoringStatus.Ended;
        var endsPrevious = startsNewGeneration && previous is not null && previous.MonitoringStatus != MonitoringStatus.Ended;
        return new(MonitoringStatus.Active, startsNew ? newIntervalId : previous!.MonitoringIntervalId,
            startsNew, endsPrevious, startsNew,
            endsPrevious ? "Hardware identity changed; physical replacement requires confirmation." : null);
    }
}
