using System.Collections.Immutable;
using AirWaveApMonitor.App.Collection;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Demo;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Security;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Infrastructure.Storage;
using AirWaveApMonitor.Tests.Support;
using static AirWaveApMonitor.Tests.Collection.CollectionCoordinatorTests;

namespace AirWaveApMonitor.Tests.Acceptance;

public sealed class RestartObservationGapTests
{
    [Fact]
    public async Task DownFailureRestartDownUp_PreservesOneIncidentItsGapAndFrozenDetection()
    {
        using var directory = new TemporaryDirectory();
        var location = StoragePaths.Open(AppMode.Demo, directory.Path);
        var clock = new ManualTimeProvider();
        Guid incidentId;
        IncidentSnapshot detection;
        long failedRun;
        long firstDownRun;
        await using (var before = await Open(location, clock, [ApState.Up, ApState.Down, null]))
        {
            await Collect(before);
            clock.Advance(TimeSpan.FromMinutes(5));
            firstDownRun = await Collect(before);
            var down = await before.Store.GetIncidentsAsync(new(location.DatasetId, 1, firstDownRun), default);
            var incident = Assert.Single(down.Rows);
            incidentId = incident.IncidentId;
            detection = incident.Detection;
            clock.Advance(TimeSpan.FromMinutes(5));
            failedRun = await Collect(before);
            var failure = await before.Store.GetDashboardAsync(new(location.DatasetId, 1, failedRun), default);
            Assert.Equal(RunOutcome.Failed, failure.Outcome);
            Assert.Null(failure.Summary.ConfirmedDown);
            Assert.Equal(1, failure.Summary.Unknown);
            Assert.Equal(1, failure.Summary.OpenIncidents);
        }

        // A fresh profile, session, coordinator, gate and real SQLite store reopen the same folder.
        clock.Advance(TimeSpan.FromMinutes(5));
        await using var after = await Open(location, clock, [ApState.Down, ApState.Up]);
        var repeatedDownRun = await Collect(after);
        var stillDown = Assert.Single((await after.Store.GetIncidentsAsync(
            new(location.DatasetId, 1, repeatedDownRun), default)).Rows);
        Assert.Equal(incidentId, stillDown.IncidentId);
        Assert.Equal(firstDownRun, stillDown.Tracking.FirstDownRunId);
        Assert.Equal(repeatedDownRun, stillDown.Tracking.LastDownRunId);
        Assert.Equal(IncidentStatus.Open, stillDown.Status);
        clock.Advance(TimeSpan.FromMinutes(5));
        var recoveredRun = await Collect(after);
        var recovered = Assert.Single((await after.Store.GetIncidentsAsync(
            new(location.DatasetId, 1, recoveredRun), default)).Rows);
        Assert.Equal(incidentId, recovered.IncidentId);
        Assert.Equal(IncidentStatus.Recovered, recovered.Status);
        Assert.Equal(firstDownRun, recovered.Tracking.FirstDownRunId);
        Assert.Equal(firstDownRun, recovered.Detection.RunId);
        Assert.Equal(detection.RecordedAtUtc, recovered.Detection.RecordedAtUtc);
        Assert.Equal(detection.Device.Metadata, recovered.Detection.Device.Metadata);
        Assert.Equal(recoveredRun, recovered.Recovery!.RunId);
        var gap = Assert.Single(recovered.ObservationGaps);
        Assert.Equal(failedRun, gap.FirstRunId);
        Assert.Equal(repeatedDownRun, gap.ResumedRunId);
        Assert.NotNull(gap.ResumedAtUtc);
        var original = Assert.Single((await after.Store.GetIncidentsAsync(
            new(location.DatasetId, 1, firstDownRun), default)).Rows);
        Assert.Equal(IncidentStatus.Open, original.Status);
        Assert.Null(original.Recovery);
        Assert.Empty(original.ObservationGaps);
        var oldFailure = await after.Store.GetDashboardAsync(new(location.DatasetId, 1, failedRun), default);
        Assert.Null(oldFailure.Summary.ConfirmedDown);
        Assert.Equal(1, oldFailure.Summary.OpenIncidents);
    }

    private static async Task<long> Collect(Harness harness)
    {
        var accepted = await harness.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default);
        Assert.Equal(RunRequestStatus.Accepted, accepted.Status);
        await Eventually(() => harness.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        return accepted.RunId!.Value;
    }

    private static async Task<Harness> Open(StorageLocation location, ManualTimeProvider clock, ApState?[] states)
    {
        var profiles = new ProfileStore(location);
        if (profiles.Current is null)
            await profiles.UpdateAsync(ScenarioApSource.DemoOrigin, 5, new(), 1, default);
        var gate = new SemaphoreSlim(1, 1);
        var store = new SqliteCollectionStore(location, gate, clock);
        await store.InitializeAsync(default);
        var session = new AirWaveSession(authenticationStates: new SqliteAuthenticationStateStore(location, gate));
        var health = new CollectorHealth(clock);
        var coordinator = new CollectionCoordinator(profiles, new ScriptedSource(states), store, session,
            new DemoCredentialStore(), location, health, clock);
        return new(coordinator, health, store, session, gate);
    }

    private sealed record Harness(CollectionCoordinator Coordinator, CollectorHealth Health,
        SqliteCollectionStore Store, AirWaveSession Session, SemaphoreSlim Gate) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            Session.Dispose();
            Gate.Dispose();
        }
    }

    private sealed class ScriptedSource(ApState?[] states) : IApSource
    {
        private int index;
        public Task<ApCollectionBatch> CollectAsync(CollectionContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var state = states[index++];
            if (state is null)
                return Task.FromResult(new ApCollectionBatch(RunOutcome.Failed, [], [], context.StartedAtUtc,
                    ObservationCompleteness.Unknown, [new(CollectionErrorCategory.Transport, 1, "synthetic read failure")]));
            var evidence = new FieldEvidence(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.Demo, context.StartedAtUtc);
            var fields = new Dictionary<string, FieldEvidence>
            {
                ["is_up"] = evidence, ["serial_number"] = evidence, ["name"] = evidence
            }.ToImmutableDictionary();
            return Task.FromResult(new ApCollectionBatch(RunOutcome.Succeeded,
                [new("1", state, new("synthetic-1", "02:00:00:00:00:01"), new("Synthetic AP", "192.0.2.1", null, null),
                    new(), new(), context.StartedAtUtc, null, null, fields)], [], context.StartedAtUtc,
                ObservationCompleteness.Complete, []));
        }
    }
}
