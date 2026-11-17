using AirWaveApMonitor.Core.Contracts;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Tests.Collection.CollectionCoordinatorTests;

namespace AirWaveApMonitor.Tests.Collection;

public sealed class CollectionFailureTests
{
    [Fact]
    public async Task ExplicitRetryDuringPause_UpdatesAuthenticationHealthWithoutCollecting()
    {
        await using var f = await Fixture.Create(durableStop: true);
        await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Startup, default);
        await f.Coordinator.SetPausedAsync(true, default);
        await f.Session.RetryAuthenticationAsync(f.Profiles.Current!, default);
        await f.Coordinator.TickAsync(default);
        Assert.True(f.Health.GetSnapshot().Paused);
        Assert.False(f.Health.GetSnapshot().AuthenticationBlocked);
        Assert.Null(f.Health.GetSnapshot().AuthenticationStop);
        Assert.Equal(0, f.Source.Calls);
    }

    [Fact]
    public async Task ActualWriteFailure_WithSuccessfulInterruptRetainsStorageCauseUntilValidCommit()
    {
        await using var f = await Fixture.Create();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(f.Directory.Path, "monitor.db"), Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER refuse_valid_commit BEFORE INSERT ON run_results WHEN NEW.outcome != 2 BEGIN SELECT RAISE(ABORT,'synthetic-private-database-detail'); END;";
            command.ExecuteNonQuery();
        }
        var request = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default);
        f.Source.Release.TrySetResult();
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        Assert.Equal(RunLifecycle.Interrupted, (await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1, request.RunId), default)).Lifecycle);
        Assert.False(f.Health.GetSnapshot().StorageRecoveryPending);
        Assert.Equal("storage-write-failed", f.Health.GetSnapshot().LastStorageError);
        Assert.Equal(CollectionErrorCategory.Storage, f.Health.GetSnapshot().LastCollectionError);
        Assert.Null(f.Health.GetSnapshot().LastValidCommittedAtUtc);
        var diagnostic = await File.ReadAllTextAsync(Path.Combine(f.Directory.Path, "collector-diagnostics.log"));
        Assert.Contains("Storage", diagnostic);
        Assert.DoesNotContain("synthetic-private-database-detail", diagnostic);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(f.Directory.Path, "monitor.db"), Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "DROP TRIGGER refuse_valid_commit"; command.ExecuteNonQuery();
        }
        await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default);
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        Assert.Null(f.Health.GetSnapshot().LastStorageError);
        Assert.NotNull(f.Health.GetSnapshot().LastValidCommittedAtUtc);
    }

    [Fact]
    public async Task AuthenticationBlockedStartup_RestoresPriorCommitClocksWithoutNewRun()
    {
        await using var f = await Fixture.Create(durableStop: true);
        var context = await f.Store.BeginRunAsync(f.Profiles.Current!, CollectionKind.ApStatus, RunTrigger.Manual, default);
        var batch = await new AirWaveApMonitor.Infrastructure.Demo.ScenarioApSource(f.Clock).CollectAsync(context, default);
        await f.Store.ApplyApBatchAsync(context, batch, default);
        var result = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Startup, default);
        Assert.Equal(RunRequestStatus.AuthenticationBlocked, result.Status);
        Assert.Equal(f.Clock.GetUtcNow(), f.Health.GetSnapshot().LastValidCommittedAtUtc);
        Assert.Null(f.Health.GetSnapshot().LastCompleteCommittedAtUtc);
        Assert.Equal(0, f.Source.Calls);
    }

    [Fact]
    public async Task AuthenticationBlockedStartup_ReportsPersistedObservationAgeUsingCurrentInterval()
    {
        await using var f = await Fixture.Create(durableStop: true);
        await f.Profiles.UpdateAsync(AirWaveApMonitor.Infrastructure.Demo.ScenarioApSource.DemoOrigin, 2, new(),
            f.Profiles.Current!.ConfigurationVersion, default);
        var context = await f.Store.BeginRunAsync(f.Profiles.Current!, CollectionKind.ApStatus, RunTrigger.Manual, default);
        var batch = await new AirWaveApMonitor.Infrastructure.Demo.ScenarioApSource(f.Clock).CollectAsync(context, default);
        await f.Store.ApplyApBatchAsync(context, batch, default);
        var committedAt = f.Clock.GetUtcNow();
        f.Clock.Advance(TimeSpan.FromMinutes(3));

        var result = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Startup, default);
        Assert.Equal(RunRequestStatus.AuthenticationBlocked, result.Status);
        Assert.False(f.Health.GetSnapshot().UpdateDelayed);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var health = f.Health.GetSnapshot();
        Assert.True(health.UpdateDelayed);
        Assert.Equal(committedAt, health.LastValidCommittedAtUtc);
        Assert.False(health.Configured);
        Assert.True(health.AuthenticationBlocked);
        Assert.NotNull(health.AuthenticationStop);
        Assert.Null(health.LastAttemptRunId);
        Assert.Equal(0, f.Source.Calls);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(f.Directory.Path, "monitor.db"),
            Pooling = false
        }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM runs";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public async Task FinalizingRun_RemainsActiveUntilDurableCommit()
    {
        FaultStore wrapper = null!;
        await using var f = await Fixture.Create(decorate: store => wrapper = new(store) { DelayApply = true });
        await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default);
        f.Source.Release.TrySetResult();
        await wrapper.Applying.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CollectorPhase.Finalizing, f.Health.GetSnapshot().Phase);
        var duplicate = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default);
        Assert.Equal(RunRequestStatus.AlreadyRunning, duplicate.Status);
        Assert.Null(f.Health.GetSnapshot().LastValidCommittedAtUtc);
        wrapper.ReleaseApply.TrySetResult();
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        Assert.Equal(1, f.Source.Calls);
        Assert.Equal(RunOutcome.Succeeded, f.Health.GetSnapshot().LastOutcome);
    }

    [Fact]
    public async Task LostCommitResponse_ResolvesOriginalSuccessWithoutFalseFailure()
    {
        await using var f = await Fixture.Create(decorate: store => new FaultStore(store) { LoseResponse = true });
        var accepted = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default);
        f.Source.Release.TrySetResult();
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        var view = await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1), default);
        Assert.Equal(accepted.RunId, view.RunId);
        Assert.Equal(RunOutcome.Succeeded, view.Outcome);
        Assert.Equal(RunOutcome.Succeeded, f.Health.GetSnapshot().LastOutcome);
        Assert.False(f.Health.GetSnapshot().StorageRecoveryPending);
    }

    [Fact]
    public async Task StorageUnavailable_BlocksNewNetworkUntilOriginalRunIsResolved()
    {
        FaultStore wrapper = null!;
        await using var f = await Fixture.Create(decorate: store => wrapper = new(store) { FailWrites = true });
        var original = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default);
        f.Source.Release.TrySetResult();
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.StorageRecovery);
        for (var i = 0; i < 3; i++)
            Assert.Equal(RunRequestStatus.StorageBlocked, (await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Scheduled, default)).Status);
        Assert.Equal(1, f.Source.Calls);
        Assert.Null(f.Health.GetSnapshot().LastValidCommittedAtUtc);
        var log = await File.ReadAllTextAsync(Path.Combine(f.Directory.Path, "collector-diagnostics.log"));
        Assert.DoesNotContain("synthetic-private-database-detail", log);
        wrapper.FailWrites = false;
        var next = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default);
        Assert.Equal(RunRequestStatus.Accepted, next.Status);
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        var interrupted = await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1, original.RunId), default);
        Assert.Equal(RunLifecycle.Interrupted, interrupted.Lifecycle);
        Assert.Equal(2, f.Source.Calls);
        Assert.NotNull(f.Health.GetSnapshot().LastValidCommittedAtUtc);
    }

    [Fact]
    public async Task Stop_CancelsNetworkAndFinalizesInterruptedWithFreshToken()
    {
        await using var f = await Fixture.Create();
        var accepted = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default);
        await f.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Coordinator.StopAsync(default);
        var view = await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1, accepted.RunId), default);
        Assert.Equal(RunLifecycle.Interrupted, view.Lifecycle);
        Assert.Null(view.Summary.ConfirmedDown);
        Assert.Equal(CollectorPhase.Stopped, f.Health.GetSnapshot().Phase);
    }

    [Fact]
    public async Task Timeout_IsFailedCollectionAndDoesNotAdvanceSuccessClock()
    {
        await using var f = await Fixture.Create();
        await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default);
        await f.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Clock.Advance(TimeSpan.FromSeconds(121));
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        var view = await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1), default);
        Assert.Equal(RunOutcome.Failed, view.Outcome);
        Assert.Equal(RunLifecycle.Finalized, view.Lifecycle);
        Assert.Null(f.Health.GetSnapshot().LastValidCommittedAtUtc);
        Assert.Equal(CollectionErrorCategory.Timeout, f.Health.GetSnapshot().LastCollectionError);
    }

    [Fact]
    public async Task DiagnosticGateBusy_DoesNotAllocateRunOrQueueNetwork()
    {
        await using var f = await Fixture.Create();
        await f.Profiles.OperationGate.WaitAsync();
        try
        {
            var result = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default);
            Assert.Equal(RunRequestStatus.AlreadyRunning, result.Status);
            Assert.Null(result.RunId);
            Assert.Equal(0, f.Source.Calls);
        }
        finally { f.Profiles.OperationGate.Release(); }
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1), default));
    }

    [Fact]
    public async Task LargeClockJump_CoalescesAndBackwardClockReanchors()
    {
        await using var f = await Fixture.Create();
        f.Source.Release.TrySetResult();
        await f.Coordinator.StartAsync(default);
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        f.Clock.Advance(TimeSpan.FromHours(10));
        await f.Coordinator.TickAsync(default);
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        await f.Coordinator.TickAsync(default);
        Assert.Equal(2, f.Source.Calls);
        f.Clock.Advance(TimeSpan.FromHours(-5));
        await f.Coordinator.TickAsync(default);
        Assert.Equal(2, f.Source.Calls);
        Assert.True(f.Health.GetSnapshot().ClockNeedsReview);
        Assert.True(f.Health.GetSnapshot().NextApAtUtc > f.Clock.GetUtcNow());
    }

    [Fact]
    public async Task DiagnosticFileFailure_StillReportsBoundedMemoryStorageFailure()
    {
        await using var f = await Fixture.Create(decorate: store => new FaultStore(store) { FailWrites = true });
        Directory.CreateDirectory(Path.Combine(f.Directory.Path, "collector-diagnostics.log"));
        await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default);
        f.Source.Release.TrySetResult();
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.StorageRecovery);
        Assert.Equal("diagnostic-file-unavailable", f.Health.GetSnapshot().LastDiagnosticFileError);
        Assert.Equal("storage-unavailable", f.Health.GetSnapshot().LastStorageError);
        Assert.NotNull(f.Health.GetSnapshot().LastAttemptRunId);
    }

    private sealed class FaultStore(ICollectionStore inner) : ICollectionStore
    {
        public bool FailWrites, LoseResponse, DelayApply;
        public TaskCompletionSource Applying { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseApply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CollectionContext> BeginRunAsync(ProfileSnapshot profile, CollectionKind kind, RunTrigger trigger, CancellationToken ct)
            => inner.BeginRunAsync(profile, kind, trigger, ct);
        public async Task<CommitReceipt> ApplyApBatchAsync(CollectionContext context, ApCollectionBatch batch, CancellationToken ct)
        {
            Applying.TrySetResult(); if (DelayApply) await ReleaseApply.Task.WaitAsync(ct);
            if (FailWrites) throw new IOException("synthetic-private-database-detail");
            var receipt = await inner.ApplyApBatchAsync(context, batch, ct);
            if (LoseResponse) throw new IOException("synthetic-private-database-detail");
            return receipt;
        }
        public Task<CommitReceipt> InterruptRunAsync(CollectionContext context, CancellationToken ct)
            => FailWrites ? throw new IOException("synthetic-private-database-detail") : inner.InterruptRunAsync(context, ct);
        public Task<EnrichmentCommitReceipt> ApplyEnrichmentBatchAsync(CollectionContext context, EnrichmentBatch batch, CancellationToken ct)
            => inner.ApplyEnrichmentBatchAsync(context, batch, ct);
        public Task<DashboardSnapshot> GetDashboardAsync(DashboardQuery query, CancellationToken ct) => inner.GetDashboardAsync(query, ct);
        public Task<CollectionSuccessTimes> GetSuccessTimesAsync(Guid datasetId, long scopeVersion, CancellationToken ct)
            => inner.GetSuccessTimesAsync(datasetId, scopeVersion, ct);
    }
}
