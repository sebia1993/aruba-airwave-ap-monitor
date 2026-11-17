using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Monitoring;
using static AirWaveApMonitor.Tests.Monitoring.MonitoringFixture;

namespace AirWaveApMonitor.Tests.Monitoring;

public sealed class RunClassifierTests
{
    private readonly StateTransitionEngine engine = new();
    private readonly RunClassifier classifier = new();

    [Fact]
    public void FailedRun_PreservesOpenIncidentsAndHasNoConfirmedCounts()
    {
        var ids = new HashSet<string> { "100", "101", "102" };
        var context = Context(ids: ids);
        var decisions = ids.Select(id => engine.EvaluateUnobserved(
            Previous(id == "100" ? ApState.Down : ApState.Up, id),
            new(id, MissingObservationReason.CollectionFailed, At(101)), context)).ToArray();
        var result = classifier.Summarize(decisions, ids, RunOutcome.Failed,
            new(BaselineStatus.Unverified, true));
        Assert.Equal(new RunSummary(null, null, 3, null, null, null, null, 1, 3, 0, BaselineStatus.Unverified), result);
        Assert.All(decisions, item => Assert.Equal(T0, item.Next.LastValidObservedAtUtc));
    }

    [Fact]
    public void UnknownTargetUniverse_IsDistinctFromKnownProvisionalTargets()
    {
        var result = classifier.Summarize([], new HashSet<string>(), RunOutcome.Failed,
            new(BaselineStatus.Unverified, false));
        Assert.Equal(new RunSummary(null, null, null, null, null, null, null, 0, null, 0, BaselineStatus.Unverified), result);
    }

    [Fact]
    public void PartialRun_KeepsMissingTargetAndCountsEachApOnce()
    {
        var ids = new HashSet<string> { "100", "101", "102" };
        var context = Context(ids: ids);
        var decisions = new[]
        {
            engine.Evaluate(Previous(ApState.Up), Observation(ApState.Down), context),
            engine.Evaluate(Previous(ApState.Up, "101"), Observation(id: "101"), context),
            engine.EvaluateUnobserved(Previous(ApState.Up, "102"),
                new("102", MissingObservationReason.MissingRecord, At(101)), context)
        };
        var result = classifier.Summarize(decisions, ids, RunOutcome.Partial);
        Assert.Equal(new RunSummary(1, 1, 1, 1, 0, 0, 0, 1, 3, 0, BaselineStatus.Unverified), result);
    }

    [Fact]
    public void NewlyDiscoveredAp_UsesInitialDownWithoutRewritingFrozenTargets()
    {
        var context = Context(ids: ["100", "101"]);
        var decisions = new[]
        {
            engine.Evaluate(Previous(ApState.Up), Observation(), context),
            engine.Evaluate(Previous(ApState.Up, "101"), Observation(id: "101"), context),
            engine.Evaluate(null, Observation(ApState.Down, id: "102"), context)
        };
        var result = classifier.Summarize(decisions, new HashSet<string> { "100", "101", "102" }, RunOutcome.Succeeded);
        Assert.Equal(new RunSummary(2, 1, 0, 0, 1, 0, 0, 1, 3, 0, BaselineStatus.Unverified), result);
        Assert.Equal(new[] { "100", "101" }, context.Profile.ExpectedTargets.Select(item => item.SourceApId));
    }

    [Fact]
    public void ExcludedIncident_IsPreservedOutsideCurrentScopeCounts()
    {
        var context = Context(ids: ["101"]);
        var paused = engine.ChangeMonitoring(Previous(), MonitoringDirective.TemporarilyExclude,
            "scope changed", At(101), context);
        var active = engine.Evaluate(Previous(ApState.Up, "101"), Observation(id: "101"), context);
        var result = classifier.Summarize([paused, active], new HashSet<string> { "101" }, RunOutcome.Succeeded,
            new(BaselineStatus.Confirmed, true, MonitoringExcludedApCount: 1));
        Assert.Equal(new RunSummary(1, 0, 0, 0, 0, 0, 0, 0, 1, 1, BaselineStatus.Confirmed), result);
        Assert.Equal(Incident, paused.Next.OpenIncident!.IncidentId);
    }

    [Fact]
    public void MissingDuplicateUnexpectedOrMixedScopeDecisions_AreRejected()
    {
        var up = engine.Evaluate(Previous(ApState.Up), Observation(), Context());
        var ids = new HashSet<string> { "100" };
        Assert.Throws<ArgumentException>(() => classifier.Summarize([], ids, RunOutcome.Succeeded));
        Assert.Throws<ArgumentException>(() => classifier.Summarize([up, up], ids, RunOutcome.Succeeded));
        Assert.Throws<ArgumentException>(() => classifier.Summarize([up], new HashSet<string>(), RunOutcome.Succeeded));
        var otherScope = engine.Evaluate(Previous(ApState.Up, "101"), Observation(id: "101"), Context(scope: 2));
        Assert.Throws<ArgumentException>(() => classifier.Summarize([up, otherScope],
            new HashSet<string> { "100", "101" }, RunOutcome.Succeeded));
    }

    [Fact]
    public void OutcomeCannotConcealTransitionsOrClaimSuccessWithoutValidState()
    {
        var up = engine.Evaluate(Previous(ApState.Up), Observation(), Context());
        var unknown = engine.EvaluateUnobserved(Previous(ApState.Up),
            new("100", MissingObservationReason.CollectionFailed, At(101)), Context());
        var ids = new HashSet<string> { "100" };
        Assert.Throws<ArgumentException>(() => classifier.Summarize([up], ids, RunOutcome.Failed));
        Assert.Throws<ArgumentException>(() => classifier.Summarize([unknown], ids, RunOutcome.Succeeded));
        Assert.Throws<ArgumentException>(() => classifier.Summarize([unknown], ids, RunOutcome.Partial));
    }

    [Fact]
    public void PartialRuns_DoNotRecountPreviousDownOrRecovery()
    {
        var first = engine.Evaluate(Previous(ApState.Up), Observation(ApState.Down, 101), Context(101));
        var second = engine.Evaluate(first.Next, Observation(ApState.Down, 102), Context(102));
        var third = engine.Evaluate(second.Next, Observation(ApState.Up, 103), Context(103));
        var ids = new HashSet<string> { "100" };
        var a = classifier.Summarize([first], ids, RunOutcome.Partial);
        var b = classifier.Summarize([second], ids, RunOutcome.Partial);
        var c = classifier.Summarize([third], ids, RunOutcome.Succeeded);
        Assert.Equal(new int?[] { 1, 0, 0 }, new[] { a.NewDown, b.NewDown, c.NewDown });
        Assert.Equal(new int?[] { 0, 0, 1 }, new[] { a.Recovered, b.Recovered, c.Recovered });
    }
}
