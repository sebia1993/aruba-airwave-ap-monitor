using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace AirWaveApMonitor.Tests.Storage;

public sealed partial class SqliteCollectionStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "airwave-storage-" + Guid.NewGuid());
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly StorageLocation location;
    private readonly TestClock clock = new();
    private readonly SqliteCollectionStore store;
    private readonly Guid instance = Guid.NewGuid();

    public SqliteCollectionStoreTests()
    {
        location = StoragePaths.Open(AppMode.Demo, directory);
        store = NewStore();
    }

    private SqliteCollectionStore NewStore(Action<StorageTransactionBoundary>? observer = null)
        => new(location, gate, clock, observer);

    private ProfileSnapshot Profile(long scope = 1, string[]? ids = null, BaselineStatus baseline = BaselineStatus.Unverified)
        => new(instance, location.DatasetId, new("https://airwave.example.invalid/"), scope, scope,
            (ids ?? ["100", "200"]).Select(id => new ExpectedTarget(id)).ToImmutableArray(), baseline, new());

    private Task<CollectionContext> Begin(SqliteCollectionStore? target = null, ProfileSnapshot? profile = null)
        => (target ?? store).BeginRunAsync(profile ?? Profile(), CollectionKind.ApStatus, RunTrigger.Manual, default);

    private ApObservation Observation(string id, ApState? state, string? name = null, string? serial = null)
    {
        var evidence = new FieldEvidence(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.Demo, clock.GetUtcNow());
        var fields = ImmutableDictionary<string, FieldEvidence>.Empty
            .Add("name", evidence)
            .Add("lan_ip", evidence)
            .Add("serial_number", evidence)
            .Add("is_up", state is null ? evidence with { Validity = FieldValidity.Unknown } : evidence);
        return new(id, state, new(serial ?? "SERIAL-" + id, null), new(name ?? "AP-" + id, "10.0.0." + id, null, null),
            new(), new(), clock.GetUtcNow(), null, null, fields);
    }

    private ApCollectionBatch Batch(params ApObservation[] observations)
        => new(RunOutcome.Succeeded, observations.ToImmutableArray(), [], clock.GetUtcNow(), ObservationCompleteness.Complete, []);

    private Task<DashboardSnapshot> Dashboard(long? run = null, long scope = 1, DashboardQuery? query = null, SqliteCollectionStore? target = null)
        => (target ?? store).GetDashboardAsync(query ?? new(location.DatasetId, scope, run), default);

    private async Task<CommitReceipt> Apply(params ApObservation[] observations)
    {
        var run = await Begin();
        var receipt = await store.ApplyApBatchAsync(run, Batch(observations), default);
        clock.Advance();
        return receipt;
    }

    [Fact]
    public async Task SameRunAppliedTwice_IsIdempotent()
    {
        var run = await Begin();
        var first = await store.ApplyApBatchAsync(run, Batch(Observation("100", ApState.Down), Observation("200", ApState.Up)), default);
        var second = await store.ApplyApBatchAsync(run, Batch(Observation("100", ApState.Up)), default);
        Assert.False(first.AlreadyApplied);
        Assert.True(second.AlreadyApplied);
        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(1, Count("incidents"));
        Assert.Equal(1, Count("run_results"));
        Assert.Equal(2, Count("historical_ap_rows"));
    }

    [Fact]
    public async Task CommitFailure_RollsBackAllApChanges()
    {
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        var before = await Dashboard();
        var failure = NewStore(boundary =>
        {
            if (boundary == StorageTransactionBoundary.BeforeFinalCommit)
                throw new IOException("simulated commit boundary failure");
        });
        var run = await Begin(failure);
        await Assert.ThrowsAsync<IOException>(() => failure.ApplyApBatchAsync(run,
            Batch(Observation("100", ApState.Down, "changed"), Observation("200", ApState.Up)), default));
        Assert.Equal(0, Count("incidents"));
        Assert.Equal(1, Count("run_results"));
        Assert.Equal(before.RunId, (await Dashboard()).RunId);
        Assert.Equal("AP-100", (await Dashboard()).Rows.Single(row => row.SourceApId == "100").Device.Name);
        Assert.Equal(before.RunId, Scalar("SELECT last_valid_run_id FROM success_times"));
    }

    [Fact]
    public async Task CommittedRunRetried_DoesNotDuplicate()
    {
        var throwOnce = true;
        var target = NewStore(boundary =>
        {
            if (boundary == StorageTransactionBoundary.AfterFinalCommit && throwOnce)
            {
                throwOnce = false;
                throw new IOException("committed response lost");
            }
        });
        var run = await Begin(target);
        var batch = Batch(Observation("100", ApState.Down), Observation("200", ApState.Up));
        await Assert.ThrowsAsync<IOException>(() => target.ApplyApBatchAsync(run, batch, default));
        var retry = await target.ApplyApBatchAsync(run, batch, default);
        Assert.True(retry.AlreadyApplied);
        Assert.Equal(1, retry.Summary.InitialDown);
        Assert.Equal(1, Count("incidents"));
        Assert.Equal(1, Count("incident_snapshots"));
    }

    [Fact]
    public async Task ReopenDatabase_ContinuesOpenIncident()
    {
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        var old = (await Dashboard()).Rows.Single(row => row.SourceApId == "100");
        var reopened = NewStore();
        var run = await Begin(reopened);
        var receipt = await reopened.ApplyApBatchAsync(run, Batch(Observation("100", ApState.Down), Observation("200", ApState.Up)), default);
        var current = (await Dashboard(target: reopened)).Rows.Single(row => row.SourceApId == "100");
        Assert.Equal(old.OpenIncidentId, current.OpenIncidentId);
        Assert.Equal(old.FirstDownAtUtc, current.FirstDownAtUtc);
        Assert.Equal(1, receipt.Summary.ExistingDown);
        Assert.Equal(0, receipt.Summary.NewDown);
    }

    [Fact]
    public async Task PartialSuccess_DoesNotRepeatTransitions()
    {
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        var down = await Apply(Observation("100", ApState.Down));
        var repeated = await Apply(Observation("100", ApState.Down));
        var recovery = await Apply(Observation("100", ApState.Up));
        var repeatedUp = await Apply(Observation("100", ApState.Up));
        Assert.Equal(RunOutcome.Partial, down.Outcome);
        Assert.Equal(1, down.Summary.NewDown);
        Assert.Equal(0, repeated.Summary.NewDown);
        Assert.Equal(1, repeated.Summary.ExistingDown);
        Assert.Equal(1, recovery.Summary.Recovered);
        Assert.Equal(0, repeatedUp.Summary.Recovered);
        Assert.Equal(1, Count("incidents"));
        Assert.Equal(1, Scalar("SELECT last_complete_run_id FROM success_times"));
    }

    [Fact]
    public async Task HistoricalRun_DoesNotUseCurrentMetadata()
    {
        var original = await Apply(Observation("100", ApState.Down, "Old name"), Observation("200", ApState.Up, "Other"));
        await Apply(Observation("100", ApState.Up, "New name"), Observation("200", ApState.Up, "Other"));
        var past = await Dashboard(query: new(location.DatasetId, 1, original.RunId, "Old name", DashboardFilter.Down));
        var row = Assert.Single(past.Rows);
        Assert.Equal("Old name", row.Device.Name);
        Assert.Equal(ObservationClassification.InitialDown, row.Classification);
        Assert.Equal(1, past.Summary.InitialDown);
        Assert.Empty((await Dashboard(query: new(location.DatasetId, 1, original.RunId, "New name"))).Rows);
        Assert.Equal(2, Count("incident_snapshots"));
    }

    [Fact]
    public async Task FailedRun_HasUnknownCountsAndPreservedOpenIncidents()
    {
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        var before = (await Dashboard()).Rows.Single(row => row.SourceApId == "100");
        var failed = await Apply();
        var snapshot = await Dashboard();
        var current = snapshot.Rows.Single(row => row.SourceApId == "100");
        Assert.Equal(RunOutcome.Failed, failed.Outcome);
        Assert.Null(failed.Summary.ConfirmedDown);
        Assert.Null(failed.Summary.Recovered);
        Assert.Equal(2, failed.Summary.Unknown);
        Assert.Equal(1, failed.Summary.OpenIncidents);
        Assert.Equal(before.OpenIncidentId, current.OpenIncidentId);
        Assert.Equal(before.LastValidObservedAtUtc, current.LastValidObservedAtUtc);
        Assert.Equal(ApState.Down, current.LastValidState);
        Assert.Null(current.CurrentState);
        Assert.Equal(failed.RunId, snapshot.RunId);
        Assert.Equal(2, Count("observation_gaps"));
    }

    [Fact]
    public async Task InitialZero_HasUnknownUniverseUntilTargetsDiscovered()
    {
        var run = await Begin(profile: Profile(ids: []));
        var receipt = await store.ApplyApBatchAsync(run, Batch(), default);
        Assert.Equal(RunOutcome.Failed, receipt.Outcome);
        Assert.Null(receipt.Summary.Unknown);
        Assert.Null(receipt.Summary.TargetCount);
        Assert.Contains((await Dashboard()).Errors, error => error.Category == CollectionErrorCategory.Unverified);
        var discovery = await Begin(profile: Profile(ids: []));
        await store.ApplyApBatchAsync(discovery, Batch(Observation("100", ApState.Up)), default);
        var next = await Begin(profile: Profile(ids: []));
        Assert.Equal("100", Assert.Single(next.Profile.ExpectedTargets).SourceApId);
        Assert.Equal(BaselineStatus.Unverified, next.Profile.BaselineStatus);
    }

    [Fact]
    public async Task MissingKnownTarget_IsPartial_AndDoesNotDeleteExpectedTarget()
    {
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up), Observation("300", ApState.Up));
        var next = await Begin();
        Assert.Equal(3, next.Profile.ExpectedTargets.Length);
        var receipt = await store.ApplyApBatchAsync(next, Batch(Observation("100", ApState.Up)), default);
        Assert.Equal(RunOutcome.Partial, receipt.Outcome);
        Assert.Equal(2, receipt.Summary.Unknown);
        Assert.Equal(3, (await Begin()).Profile.ExpectedTargets.Length);
    }

    [Fact]
    public async Task AllUnknown_RejectsIdentityAndMetadataChanges()
    {
        await Apply(Observation("100", ApState.Down, "original"), Observation("200", ApState.Up));
        var before = (await Dashboard()).Rows.Single(row => row.SourceApId == "100");
        var failed = await Apply(Observation("100", null, "overwrite", "replacement"));
        var after = (await Dashboard()).Rows.Single(row => row.SourceApId == "100");
        Assert.Equal(RunOutcome.Failed, failed.Outcome);
        Assert.Equal(before.DeviceGenerationId, after.DeviceGenerationId);
        Assert.Equal(before.OpenIncidentId, after.OpenIncidentId);
        Assert.Equal("original", after.Device.Name);
        Assert.Equal(1, Count("incidents"));
    }

    [Fact]
    public async Task ReplacementWithUnknown_RequiresOtherwiseValidPartial()
    {
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        var old = (await Dashboard()).Rows.Single(row => row.SourceApId == "100");
        var partial = await Apply(Observation("100", null, serial: "replacement"), Observation("200", ApState.Up));
        var changed = (await Dashboard()).Rows.Single(row => row.SourceApId == "100");
        Assert.Equal(RunOutcome.Partial, partial.Outcome);
        Assert.NotEqual(old.DeviceGenerationId, changed.DeviceGenerationId);
        Assert.Null(changed.OpenIncidentId);
        Assert.Null(changed.LastValidState);
        var down = await Apply(Observation("100", ApState.Down, serial: "replacement"), Observation("200", ApState.Up));
        Assert.Equal(1, down.Summary.InitialDown);
        Assert.Equal(0, down.Summary.Recovered);
        Assert.Equal(2, Count("incidents"));
    }

    [Fact]
    public async Task DuplicateId_ExcludesAllItsChanges_AndSerialCollisionNeverMerges()
    {
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        var original = (await Dashboard()).Rows.Single(row => row.SourceApId == "100");
        var partial = await Apply(Observation("100", ApState.Up, "wrong", "wrong"),
            Observation("100", ApState.Down, "wrong2", "wrong2"), Observation("200", ApState.Up));
        Assert.Equal(RunOutcome.Partial, partial.Outcome);
        Assert.Equal(original.Identity, (await Dashboard()).Rows.Single(row => row.SourceApId == "100").Identity);
        await Apply(Observation("100", ApState.Up, serial: "same"), Observation("200", ApState.Up, serial: "same"));
        var rows = (await Dashboard()).Rows;
        Assert.Equal(2, rows.Select(row => row.ApId).Distinct().Count());
        Assert.Equal(2, rows.Select(row => row.DeviceGenerationId).Distinct().Count());
    }

    [Fact]
    public async Task InterruptedRestart_FreezesOriginalTargetsAndCannotBeOverwritten()
    {
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        var running = await Begin();
        var reopened = NewStore();
        var snapshot = await Dashboard(target: reopened);
        Assert.Equal(running.RunId, snapshot.RunId);
        Assert.Equal(RunLifecycle.Interrupted, snapshot.Lifecycle);
        Assert.Equal(2, snapshot.Summary.Unknown);
        Assert.Null(snapshot.Summary.ConfirmedDown);
        Assert.Equal(1, snapshot.Summary.OpenIncidents);
        var retry = await reopened.ApplyApBatchAsync(running, Batch(Observation("100", ApState.Up)), default);
        Assert.True(retry.AlreadyApplied);
        Assert.Equal(RunOutcome.Failed, retry.Outcome);
        Assert.Equal(1, Count("incidents"));
        Assert.Equal(1, Count("incident_snapshots"));
        Assert.Equal(2, Count("run_results"));
    }

    [Fact]
    public async Task FrozenContext_RejectsAlteration_AndOldScopeResultStaysOldScope()
    {
        var old = await Begin();
        var altered = old with { Profile = Profile(scope: 2, ids: ["100", "300"]) };
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyApBatchAsync(altered, Batch(Observation("100", ApState.Up)), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Begin(profile: Profile(scope: 2, ids: ["100", "300"])));
        await store.ApplyApBatchAsync(old, Batch(Observation("100", ApState.Down), Observation("200", ApState.Up)), default);
        var newer = await Begin(profile: Profile(scope: 2, ids: ["100", "300"]));
        Assert.Equal(2, (await Dashboard()).Summary.TargetCount);
        Assert.DoesNotContain((await Dashboard()).Rows, row => row.SourceApId == "300");
        Assert.Equal(new[] { "100", "300" }, newer.Profile.ExpectedTargets.Select(target => target.SourceApId).ToArray());
    }

    [Fact]
    public async Task ScopeChange_CommonApContinuesIncident_ExcludedApReentryPreservesGap()
    {
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Down));
        var original = await Dashboard();
        var next = await Begin(profile: Profile(scope: 2, ids: ["100", "300"]));
        var receipt = await store.ApplyApBatchAsync(next, Batch(Observation("100", ApState.Down), Observation("300", ApState.Up)), default);
        var current = await Dashboard(scope: 2);
        Assert.Equal(1, receipt.Summary.ExistingDown);
        Assert.Equal(0, receipt.Summary.InitialDown);
        Assert.Equal(1, receipt.Summary.ExcludedCount);
        Assert.Equal(original.Rows.Single(row => row.SourceApId == "100").OpenIncidentId,
            current.Rows.Single(row => row.SourceApId == "100").OpenIncidentId);
        var reentry = await Begin(profile: Profile(scope: 3));
        var returned = await store.ApplyApBatchAsync(reentry, Batch(Observation("100", ApState.Down), Observation("200", ApState.Down)), default);
        Assert.Equal(2, returned.Summary.ExistingDown);
        Assert.Equal(2, Count("incidents"));
        Assert.Equal(original.Rows.Single(row => row.SourceApId == "200").OpenIncidentId,
            (await Dashboard(scope: 3)).Rows.Single(row => row.SourceApId == "200").OpenIncidentId);
    }

    [Fact]
    public async Task ClockRollback_PreservesRawTimesAndRunOrder()
    {
        var first = await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        clock.Set(clock.GetUtcNow().AddHours(-1));
        var second = await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        var latest = await Dashboard();
        Assert.True(second.RunId > first.RunId);
        Assert.Equal(second.RunId, latest.RunId);
        Assert.True(latest.Rows.Single(row => row.SourceApId == "100").LastValidObservedAtUtc <
            latest.Rows.Single(row => row.SourceApId == "100").FirstDownAtUtc);
    }

    [Fact]
    public async Task UnchangedMetadata_IsSharedAcrossRunRows()
    {
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        Assert.Equal(2, Count("metadata_versions"));
        Assert.Equal(4, Count("historical_ap_rows"));
        Assert.Equal(0, Count("run_rows"));
        Assert.Equal(2, Count("ap_history_versions"));
        var first = await Dashboard(1);
        var second = await Dashboard(2);
        Assert.True(first.Rows[0].Fields["name"].CollectedAtUtc < second.Rows[0].Fields["name"].CollectedAtUtc);
    }

    [Fact]
    public async Task DatabaseConstraints_RejectDuplicateFinalApplicationAndOpenIncident()
    {
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO run_results SELECT * FROM run_results";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        command.CommandText = "INSERT INTO incidents(incident_id, ap_id, generation_id, interval_id, is_open, tracking_json) SELECT 'duplicate', ap_id, generation_id, interval_id, is_open, tracking_json FROM incidents";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public async Task FutureSchema_IsRejectedWithoutDiscardingExistingData()
    {
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        using (var connection = Connect())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version=999";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Dashboard(target: NewStore()));
        Assert.Equal(2, Count("historical_ap_rows"));
    }

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "monitor.db"), Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private long Count(string table) => Scalar("SELECT COUNT(*) FROM " + table);

    private long Scalar(string sql)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Dispose()
    {
        gate.Dispose();
        Directory.Delete(directory, true);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-11-15T01:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance() => now = now.AddMinutes(5);
        public void Set(DateTimeOffset at) => now = at;
    }
}
