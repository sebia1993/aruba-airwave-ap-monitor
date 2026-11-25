using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using AirWaveApMonitor.Core.Contracts;
using Xunit.Abstractions;

namespace AirWaveApMonitor.Tests.Web;

[CollectionDefinition(nameof(RepeatHistoryScaleCollection), DisableParallelization = true)]
public sealed class RepeatHistoryScaleCollection { }

[Collection(nameof(RepeatHistoryScaleCollection))]
public sealed class RepeatHistoryScaleTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(2000)]
    [InlineData(5000)]
    public async Task RealSqlite_FullPopulationRepeatSummaryAndPageRemainConsistent(int targetCount)
    {
        using var f = new ReadFixture();
        var ids = Enumerable.Range(1, targetCount).Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray();
        var profile = f.Profile(1, ids);
        var seed = await Apply(false);
        // 513 recovered APs exceed the maximum 500-row page size. The remaining APs stay Up.
        var down = await Apply(true);
        var recovered = await Apply(false);
        var query = new DashboardQuery(f.Location.DatasetId, 1, recovered.RunId, Filter: DashboardFilter.RepeatSuspected,
            Sort: DashboardSort.RecentDownCount, Descending: true, PageSize: 500, RepeatThreshold: 1, PriorityThreshold: 2);
        var timer = Stopwatch.StartNew();
        var first = await f.Store.GetDashboardAsync(query, default);
        var second = await f.Store.GetDashboardAsync(query with { Page = 2 }, default);
        output.WriteLine($"Repeat history SQLite {targetCount} targets, two pages {timer.Elapsed.TotalSeconds:F3}s");
        Assert.Equal(targetCount, first.Summary.ConfirmedUp);
        Assert.Equal(513, first.RepeatSuspectedCount);
        Assert.Equal(0, first.RepeatPriorityCount);
        Assert.Equal(513, first.TotalRows);
        Assert.Equal(513, second.TotalRows);
        Assert.Equal(500, first.Rows.Length); Assert.Equal(13, second.Rows.Length);
        Assert.Equal(513, first.Rows.Concat(second.Rows).Select(r => r.ApId).Distinct().Count());
        Assert.All(first.Rows.Concat(second.Rows), r =>
        {
            Assert.Equal(ApState.Up, r.CurrentState);
            Assert.Equal(1, r.StatusHistory!.NewDownCount24Hours);
            Assert.Equal(down.StartedAtUtc, r.StatusHistory.RecentDownAtUtc);
            Assert.Equal(recovered.StartedAtUtc, r.StatusHistory.LastUpObservedAtUtc);
            Assert.Equal(recovered.StartedAtUtc, r.StatusHistory.LastRecoveryAtUtc);
        });
        Assert.Empty((await f.Store.GetDashboardAsync(query with { RunId = seed.RunId }, default)).Rows);
        var selected = first.Rows[0];
        var detail = await f.Store.GetApDetailAsync(new(f.Location.DatasetId, 1, recovered.RunId, selected.ApId, 1), default);
        Assert.Equal(selected.StatusHistory, detail.StatusHistory);

        async Task<CollectionContext> Apply(bool failing)
        {
            var now = f.Now;
            var evidence = new FieldEvidence(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.Demo, now);
            var fields = new[] {"is_up", "name", "serial_number", "lan_ip"}.ToImmutableDictionary(key => key, _ => evidence);
            var observations = ids.Select((id, index) => new ApObservation(id, failing && index < 513 ? ApState.Down : ApState.Up,
                new("synthetic-" + id, null), new("AP " + id, $"198.18.{index / 256}.{index % 256}", "synthetic", "fixture"),
                new(), new(), now, null, null, fields)).ToImmutableArray();
            var context = await f.Store.BeginRunAsync(profile, CollectionKind.ApStatus, RunTrigger.Manual, default);
            var receipt = await f.Store.ApplyApBatchAsync(context, new(RunOutcome.Succeeded, observations, [], now,
                ObservationCompleteness.Complete, []), default);
            Assert.Equal(RunOutcome.Succeeded, receipt.Outcome);
            Assert.Equal(targetCount, receipt.Summary.TargetCount);
            f.AdvanceTime(TimeSpan.FromMinutes(5));
            return context;
        }
    }
}
