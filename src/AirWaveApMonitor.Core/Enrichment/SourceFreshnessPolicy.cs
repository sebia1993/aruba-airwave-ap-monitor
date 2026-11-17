using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Core.Enrichment;

public static class SourceFreshnessPolicy
{
    /// <summary>Evaluates source age at the supplied reference time; the store enforces read/commit visibility separately.</summary>
    public static FreshnessAssessment Evaluate(SourceTiming timing, DateTimeOffset atUtc, TimeSpan? allowedAge,
        DateTimeOffset? previousSourceAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(timing);
        if (allowedAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(allowedAge));
        if (timing.SourceAtUtc is not { } source || !timing.MeaningVerified ||
            timing.Meaning is not (SourceTimeMeaning.LastObserved or SourceTimeMeaning.Measurement))
            return new(SourceFreshness.Unknown, FreshnessReason.UnverifiedTime, timing.SourceAtUtc);
        if (source > timing.ReadAtUtc || source > atUtc)
            return new(SourceFreshness.Unknown, FreshnessReason.FutureTime, source);
        if (previousSourceAtUtc is { } previous && source < previous)
            return new(SourceFreshness.Unknown, FreshnessReason.BackwardsTime, source);
        if (allowedAge is not { } age)
            return new(SourceFreshness.Unknown, FreshnessReason.UnknownAllowedAge, source);
        return atUtc - source <= age
            ? new(SourceFreshness.Current, FreshnessReason.Current, source)
            : new(SourceFreshness.Delayed, FreshnessReason.Delayed, source);
    }
}
