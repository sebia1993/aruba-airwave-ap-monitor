using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Monitoring;
using static AirWaveApMonitor.Tests.Monitoring.MonitoringFixture;

namespace AirWaveApMonitor.Tests.Monitoring;

public sealed class DemoTimelineTests
{
    [Fact]
    public void Fixed2048ApTimeline_ReproducesCountsFromTransitionsAndPreservesApSpecificGaps()
    {
        var engine = new StateTransitionEngine();
        var classifier = new RunClassifier();
        var snapshots = new Dictionary<string, ApTrackingSnapshot>();
        var ids = Enumerable.Range(1, 2048).Where(id => id != 6).Select(id => id.ToString()).ToHashSet();
        int[][] seedDowns = [[], [1], [1, 2], [1, 2, 3], [1, 2, 3, 9, 10],
            [1, 2, 3, 4, 9, 10], [1, 2, 3, 4, 5, 9, 10], [1, 2, 3, 4, 5, 9, 10]];
        string[] times = ["2026-11-14T07:15Z", "2026-11-14T07:20Z", "2026-11-14T23:35Z",
            "2026-11-15T00:10Z", "2026-11-15T01:15Z", "2026-11-15T01:20Z",
            "2026-11-15T01:30Z", "2026-11-15T02:00Z", "2026-11-15T02:05Z",
            "2026-11-15T02:10Z", "2026-11-15T02:15Z", "2026-11-15T02:20Z", "2026-11-15T02:25Z"];

        ApTransitionDecision[] Apply(long run, int[] downs, int[] missing, RunOutcome outcome)
        {
            var at = DateTimeOffset.Parse(times[run - 1]);
            var context = Context(run, ids) with { StartedAtUtc = at, DeadlineUtc = at.AddMinutes(2) };
            var decisions = new List<ApTransitionDecision>();
            foreach (var id in ids)
            {
                snapshots.TryGetValue(id, out var previous);
                var decision = outcome == RunOutcome.Failed || missing.Contains(int.Parse(id))
                    ? engine.EvaluateUnobserved(previous, new(id, outcome == RunOutcome.Failed
                        ? MissingObservationReason.CollectionFailed : MissingObservationReason.MissingRecord, at), context)
                    : engine.Evaluate(previous, Observation(downs.Contains(int.Parse(id)) ? ApState.Down : ApState.Up,
                        run, id, serial: "DEMO-SERIAL-" + id) with { CollectedAtUtc = at }, context);
                snapshots[id] = decision.Next;
                decisions.Add(decision);
            }
            return decisions.ToArray();
        }

        for (var index = 0; index < seedDowns.Length; index++)
        {
            Apply(index + 1, seedDowns[index], [], RunOutcome.Succeeded);
        }
        ids.Add("6");
        var first = classifier.Summarize(Apply(9, [1, 2, 3, 4, 5, 6, 7, 8], [], RunOutcome.Succeeded), ids, RunOutcome.Succeeded);
        var second = classifier.Summarize(Apply(10, [1, 2, 3, 4, 5, 6, 7, 8, 11, 12], [], RunOutcome.Succeeded), ids, RunOutcome.Succeeded);
        var third = classifier.Summarize(Apply(11, [1, 3, 5, 6, 7, 8, 11, 12], [], RunOutcome.Succeeded), ids, RunOutcome.Succeeded);
        var failed = classifier.Summarize(Apply(12, [], [], RunOutcome.Failed), ids, RunOutcome.Failed);
        var partial = classifier.Summarize(Apply(13, [1, 5, 6, 7, 8, 11, 12], [3, 20, 21], RunOutcome.Partial), ids, RunOutcome.Partial);
        Assert.Equal(new RunSummary(2040, 8, 0, 2, 1, 5, 2, 8, 2048, 0, BaselineStatus.Unverified), first);
        Assert.Equal(new RunSummary(2038, 10, 0, 2, 0, 8, 0, 10, 2048, 0, BaselineStatus.Unverified), second);
        Assert.Equal(new RunSummary(2040, 8, 0, 0, 0, 8, 2, 8, 2048, 0, BaselineStatus.Unverified), third);
        Assert.Equal(new RunSummary(null, null, 2048, null, null, null, null, 8, 2048, 0, BaselineStatus.Unverified), failed);
        Assert.Equal(new RunSummary(2038, 7, 3, 0, 0, 7, 0, 8, 2048, 0, BaselineStatus.Unverified), partial);
        Assert.NotNull(snapshots["3"].OpenGap);
        Assert.NotNull(snapshots["3"].OpenIncident);
        Assert.Equal(DateTimeOffset.Parse("2026-11-15T02:15Z"), snapshots["3"].LastValidObservedAtUtc);
        Assert.Null(snapshots["1"].OpenGap);
        Assert.Equal(DateTimeOffset.Parse("2026-11-15T02:25Z"), snapshots["1"].LastValidObservedAtUtc);
        Assert.Equal(ApState.Up, snapshots["20"].LastValidState);
        Assert.NotNull(snapshots["20"].OpenGap);
        Assert.Null(snapshots["20"].OpenIncident);
        Assert.True(snapshots["6"].OpenIncident!.IsInitialDown);
    }
}
