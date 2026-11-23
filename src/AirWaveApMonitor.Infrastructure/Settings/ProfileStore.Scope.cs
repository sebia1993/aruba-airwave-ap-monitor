using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Infrastructure.Settings;

public sealed partial class ProfileStore
{
    public Task UpdateScopeAsync(MonitoringScopeDefinition definition, ImmutableArray<string> knownIds,
        string reason, long expectedVersion, CancellationToken ct)
    {
        var current = CheckedScopeProfile(expectedVersion, reason);
        var known = knownIds.ToHashSet(StringComparer.Ordinal);
        if (definition.Mode == MonitoringScopeMode.SelectedApIds && definition.SourceApIds.Any(id => !known.Contains(id)))
            throw new ArgumentException("유효하게 관측한 적이 있는 정확한 AP ID만 선택할 수 있습니다.");
        var previous = current.MonitoringScope ?? new(MonitoringScopeMode.AllDiscovered, []);
        if (previous.Mode == definition.Mode && previous.SourceApIds.SequenceEqual(definition.SourceApIds))
            throw new ArgumentException("감시 범위가 변경되지 않았습니다.");
        var targets = definition.Mode == MonitoringScopeMode.SelectedApIds ? definition.SourceApIds : knownIds;
        return SaveScopeAsync(current, definition, targets, BaselineStatus.Unverified, current.ScopeVersion + 1,
            "ScopeChanged", reason, null, ct);
    }

    public Task ConfirmBaselineAsync(ScopeComparisonRun compared, int comparedTargetCount,
        string reason, long expectedVersion, CancellationToken ct)
    {
        var current = CheckedScopeProfile(expectedVersion, reason);
        if (compared.ConfigurationVersion != current.ConfigurationVersion || compared.ScopeVersion != current.ScopeVersion)
            throw new ProfileConflictException("현재 설정으로 확정된 최신 회차를 다시 대조하세요.");
        if (compared.Outcome == RunOutcome.Failed || compared.TargetCount <= 0 || comparedTargetCount != compared.TargetCount ||
            compared.SourceApIds.Length != compared.TargetCount)
            throw new ArgumentException("대조한 관리 대상 대수와 확정 회차의 대상 목록이 일치해야 합니다.");
        return SaveScopeAsync(current, current.MonitoringScope ?? new(MonitoringScopeMode.AllDiscovered, []),
            compared.SourceApIds, BaselineStatus.Confirmed, current.ScopeVersion, "BaselineConfirmed", reason, compared.RunId, ct);
    }

    private ProfileSnapshot CheckedScopeProfile(long expectedVersion, string reason)
    {
        var current = Current ?? throw new ArgumentException("서버 설정이 필요합니다.");
        if (current.ConfigurationVersion != expectedVersion)
            throw new ProfileConflictException("설정이 변경되었습니다. 현재 범위와 대조 회차를 다시 읽으세요.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
            throw new ArgumentException("범위 변경 또는 기준 대조 사유를 1~2000자로 기록하세요.");
        return current;
    }

    private Task SaveScopeAsync(ProfileSnapshot current, MonitoringScopeDefinition definition, ImmutableArray<string> ids,
        BaselineStatus baseline, long scopeVersion, string action, string reason, long? comparedRun, CancellationToken ct)
    {
        var revision = new ProfileScopeRevision(current.ConfigurationVersion + 1, scopeVersion, DateTimeOffset.UtcNow,
            action, reason.Trim(), definition, comparedRun, ids.Length);
        // Definition and its audit record are published in the same atomic profile replacement.
        return SaveAsync(new(current.InstanceId, current.DatasetId, current.Origin, revision.ConfigurationVersion, scopeVersion,
            ids.Order(StringComparer.Ordinal).Select(id => new ExpectedTarget(id)), baseline, current.Settings)
        {
            Proxy = current.Proxy, TemporaryCertificatePin = current.TemporaryCertificatePin, CredentialsVersion = current.CredentialsVersion, MonitoringScope = definition,
            ScopeRevisions = (current.ScopeRevisions ?? []).Add(revision),
            BaselineConfirmedSourceApIds = baseline == BaselineStatus.Confirmed ? ids : null
        }, ct);
    }
}
