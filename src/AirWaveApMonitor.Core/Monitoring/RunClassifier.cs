using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Core.Monitoring;

public sealed class RunClassifier
{
    public RunSummary Summarize(IReadOnlyList<ApTransitionDecision> decisions,
        IReadOnlySet<string> expectedIds, RunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        return Summarize(decisions, expectedIds, outcome,
            new(BaselineStatus.Unverified, expectedIds.Count > 0,
                decisions.Count(decision => !decision.IsMonitoringTarget)));
    }

    public RunSummary Summarize(IReadOnlyList<ApTransitionDecision> decisions,
        IReadOnlySet<string> expectedIds, RunOutcome outcome, RunClassificationContext context)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(expectedIds);
        ArgumentNullException.ThrowIfNull(context);
        if (context.MonitoringExcludedApCount < 0)
        {
            throw new ArgumentException("Excluded AP count cannot be negative.", nameof(context));
        }
        if (expectedIds.Any(string.IsNullOrWhiteSpace) ||
            decisions.Select(decision => decision.SourceApId).Distinct(StringComparer.Ordinal).Count() != decisions.Count)
        {
            throw new ArgumentException("Each AP requires exactly one attributable decision.", nameof(decisions));
        }
        var active = decisions.Where(decision => decision.IsMonitoringTarget).ToArray();
        if (!expectedIds.SetEquals(active.Select(decision => decision.SourceApId)))
        {
            throw new ArgumentException("Decisions must cover the frozen target set including discoveries and missing APs.", nameof(decisions));
        }
        if (decisions.Count - active.Length > context.MonitoringExcludedApCount)
        {
            throw new ArgumentException("Excluded AP decisions exceed the declared excluded AP count.", nameof(context));
        }
        var first = decisions.FirstOrDefault()?.Context;
        if (first is not null && decisions.Any(decision => !SameRun(first, decision.Context)))
        {
            throw new ArgumentException("Decisions from different runs, instances, datasets or frozen scopes cannot be combined.", nameof(decisions));
        }
        if (!context.TargetUniverseKnown && (expectedIds.Count != 0 || outcome != RunOutcome.Failed))
        {
            throw new ArgumentException("An unknown target universe cannot claim a successful or enumerated inventory.", nameof(context));
        }
        var open = active.Count(decision => decision.Next.OpenIncident is not null);
        if (outcome == RunOutcome.Failed)
        {
            if (active.Any(decision => decision.Classification != ObservationClassification.Unknown ||
                decision.AcceptedObservation is not null || decision.Incidents.Any(mutation =>
                    mutation.Kind is not IncidentMutationKind.Updated)))
            {
                throw new ArgumentException("Failed runs must preserve state using unobserved decisions.", nameof(decisions));
            }
            return new(null, null, context.TargetUniverseKnown ? expectedIds.Count : null,
                null, null, null, null, open, context.TargetUniverseKnown ? expectedIds.Count : null,
                context.MonitoringExcludedApCount, context.BaselineStatus);
        }
        var up = active.Count(decision => decision.Classification is ObservationClassification.Up or ObservationClassification.Recovered);
        var newDown = active.Count(decision => decision.Classification == ObservationClassification.NewDown);
        var initialDown = active.Count(decision => decision.Classification == ObservationClassification.InitialDown);
        var existingDown = active.Count(decision => decision.Classification == ObservationClassification.ExistingDown);
        var recovered = active.Count(decision => decision.Classification == ObservationClassification.Recovered);
        var unknown = active.Count(decision => decision.Classification == ObservationClassification.Unknown);
        var down = newDown + initialDown + existingDown;
        if (up + down == 0 || (outcome == RunOutcome.Succeeded && unknown != 0) ||
            up + down + unknown != expectedIds.Count)
        {
            throw new ArgumentException("Run outcome disagrees with the target observations.", nameof(outcome));
        }
        return new(up, down, unknown, newDown, initialDown, existingDown, recovered, open,
            expectedIds.Count, context.MonitoringExcludedApCount, context.BaselineStatus);
    }

    private static bool SameRun(CollectionContext left, CollectionContext right)
        => left.RunId == right.RunId && left.Kind == right.Kind &&
            left.Profile.InstanceId == right.Profile.InstanceId && left.Profile.DatasetId == right.Profile.DatasetId &&
            left.Profile.ScopeVersion == right.Profile.ScopeVersion &&
            left.Profile.ConfigurationVersion == right.Profile.ConfigurationVersion &&
            left.Profile.Origin == right.Profile.Origin &&
            left.Profile.ExpectedTargets.SequenceEqual(right.Profile.ExpectedTargets);
}
