using System.Collections.Immutable;

namespace AirWaveApMonitor.Core.Contracts;

public enum MonitoringScopeMode { AllDiscovered, SelectedApIds }

/// <summary>Local monitoring membership, independent of discovered expected targets and UI filters.</summary>
public sealed record MonitoringScopeDefinition
{
    public MonitoringScopeMode Mode { get; }
    public ImmutableArray<string> SourceApIds { get; }
    public MonitoringScopeDefinition(MonitoringScopeMode mode, ImmutableArray<string> sourceApIds)
    {
        if (!Enum.IsDefined(mode) || sourceApIds.IsDefault ||
            sourceApIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 1024 || id != id.Trim()) ||
            sourceApIds.Distinct(StringComparer.Ordinal).Count() != sourceApIds.Length ||
            (mode == MonitoringScopeMode.AllDiscovered && sourceApIds.Length != 0) ||
            (mode == MonitoringScopeMode.SelectedApIds && sourceApIds.Length == 0))
            throw new ArgumentException("전체 발견 대상 또는 중복 없는 원천 AP ID를 명시하세요.");
        Mode = mode; SourceApIds = sourceApIds.Order(StringComparer.Ordinal).ToImmutableArray();
    }
}

public sealed record ProfileScopeRevision(long ConfigurationVersion, long ScopeVersion, DateTimeOffset ChangedAtUtc,
    string Action, string Reason, MonitoringScopeDefinition Definition, long? ComparedRunId, int TargetCount);
public sealed record KnownScopeTarget(string SourceApId, string? Name, string? IpAddress, long LastValidRunId);
public sealed record ScopeComparisonRun(long RunId, long ConfigurationVersion, long ScopeVersion,
    int TargetCount, RunOutcome Outcome, BaselineStatus BaselineStatus, ImmutableArray<string> SourceApIds);
public sealed record ScopeSettingsView(ProfileSnapshot Profile, ImmutableArray<KnownScopeTarget> KnownTargets,
    ScopeComparisonRun? ComparisonRun);
