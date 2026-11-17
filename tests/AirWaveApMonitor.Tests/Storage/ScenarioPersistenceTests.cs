using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Demo;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Storage;
using AirWaveApMonitor.Tests.Support;

namespace AirWaveApMonitor.Tests.Storage;

public sealed class ScenarioPersistenceTests
{
    [Fact]
    public async Task Parsed2048ApScenario_PersistsFailureAndPartialWithoutLosingOpenIncident()
    {
        using var directory = new TemporaryDirectory();
        var location = StoragePaths.Open(AppMode.Demo, directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var clock = new ManualTimeProvider();
        var store = new SqliteCollectionStore(location, gate, clock);
        var source = new ScenarioApSource(clock);
        var profile = new ProfileSnapshot(Guid.NewGuid(), location.DatasetId, ScenarioApSource.DemoOrigin,
            1, 1, [], BaselineStatus.Unverified, new());
        (int? Up, int? Down, int Unknown, int Open)[] expected =
        [
            (2047, 0, 1, 0), (2040, 7, 1, 7), (2040, 8, 0, 8),
            (2038, 10, 0, 10), (2040, 8, 0, 8), (null, null, 2048, 8), (2038, 7, 3, 8)
        ];
        long failedRunId = 0;
        for (var index = 0; index < expected.Length; index++)
        {
            var context = await store.BeginRunAsync(profile, CollectionKind.ApStatus, RunTrigger.Manual, default);
            var batch = await source.CollectAsync(context, default);
            var receipt = await store.ApplyApBatchAsync(context, batch, default);
            var view = await store.GetDashboardAsync(new(location.DatasetId, profile.ScopeVersion), default);
            Assert.Equal(context.RunId, view.RunId);
            Assert.Equal(receipt.Summary, view.Summary);
            Assert.Equal(2048, view.TotalRows);
            Assert.Equal(expected[index].Up, view.Summary.ConfirmedUp);
            Assert.Equal(expected[index].Down, view.Summary.ConfirmedDown);
            Assert.Equal(expected[index].Unknown, view.Summary.Unknown);
            Assert.Equal(expected[index].Open, view.Summary.OpenIncidents);
            if (index == 2)
            {
                Assert.Equal(2, view.Summary.NewDown);
                Assert.Equal(1, view.Summary.InitialDown);
                Assert.Equal(5, view.Summary.ExistingDown);
                Assert.Equal(2, view.Summary.Recovered);
            }
            if (index == 5)
            {
                failedRunId = context.RunId;
                Assert.Equal(RunOutcome.Failed, view.Outcome);
            }
            clock.Advance(TimeSpan.FromMinutes(5));
        }
        // Reopen the real database, then query both the latest partial and the prior failure.
        var reopened = new SqliteCollectionStore(location, gate, clock);
        var unknown = await reopened.GetDashboardAsync(new(location.DatasetId, profile.ScopeVersion,
            Filter: DashboardFilter.Unknown), default);
        Assert.Equal(3, unknown.TotalRows);
        var missedDown = Assert.Single(unknown.Rows, row => row.SourceApId == "3");
        Assert.Null(missedDown.CurrentState);
        Assert.Equal(ApState.Down, missedDown.LastValidState);
        Assert.NotNull(missedDown.OpenIncidentId);
        var failed = await reopened.GetDashboardAsync(new(location.DatasetId, profile.ScopeVersion, failedRunId), default);
        Assert.Equal(RunOutcome.Failed, failed.Outcome);
        Assert.Null(failed.Summary.ConfirmedDown);
        Assert.Equal(2048, failed.Summary.Unknown);
        Assert.Equal(8, failed.Summary.OpenIncidents);
    }
}
