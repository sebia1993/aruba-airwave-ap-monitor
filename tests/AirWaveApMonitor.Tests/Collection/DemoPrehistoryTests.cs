using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AirWaveApMonitor.App.Collection;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Demo;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Security;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Infrastructure.Storage;
using AirWaveApMonitor.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;
using static AirWaveApMonitor.Tests.Collection.CollectionCoordinatorTests;

namespace AirWaveApMonitor.Tests.Collection;

[Collection("Host environment")]
public sealed class DemoPrehistoryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FreshDemo_CommitsPrehistoryBeforeAcceptedVisibleRun()
    {
        await using var f = await DemoFixture.Create();
        var accepted = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Startup, default);
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        var snapshot = await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1), default);
        Assert.Equal(accepted.RunId, snapshot.RunId);
        Assert.Equal(3, f.RunCount());
        Assert.Equal(2, await f.Checkpoint());
        AssertNormal(snapshot.Summary);
    }

    [Fact]
    public async Task RestartedDemo_DoesNotReseedCommittedPrehistory()
    {
        using var directory = new TemporaryDirectory();
        await using (var first = await DemoFixture.Create(directory.Path))
        {
            await StartAndWaitForIdle(first, "first");
            Assert.Equal(2, await first.Checkpoint());
        }
        await using var restarted = await DemoFixture.Create(directory.Path);
        await StartAndWaitForIdle(restarted, "restarted");
        var snapshot = await restarted.Store.GetDashboardAsync(new(restarted.Location.DatasetId, 1), default);
        Assert.Equal(4, restarted.RunCount());
        Assert.Equal(3, await restarted.Checkpoint());
        Assert.Equal(10, snapshot.Summary.ConfirmedDown);
        Assert.Equal(2, snapshot.Summary.NewDown);
        Assert.Equal(10, snapshot.Summary.OpenIncidents);

        async Task StartAndWaitForIdle(DemoFixture fixture, string stage)
        {
            var timer = Stopwatch.StartNew();
            try { await fixture.Coordinator.StartAsync(default); }
            finally { output.WriteLine($"{stage} StartAsync: {timer.Elapsed.TotalMilliseconds:F3} ms; {HealthText()}"); }
            timer.Restart();
            var idleObserved = false;
            try
            {
                await Eventually(() => fixture.Health.GetSnapshot().Phase == CollectorPhase.Idle,
                    () => $"{stage} Idle wait after {timer.Elapsed.TotalMilliseconds:F3} ms; {HealthText()}");
                idleObserved = true;
            }
            finally
            {
                output.WriteLine($"{stage} Idle wait: {timer.Elapsed.TotalMilliseconds:F3} ms; passed={idleObserved}; {HealthText()}");
            }

            string HealthText()
            {
                var health = fixture.Health.GetSnapshot();
                return JsonSerializer.Serialize(new
                {
                    phase = health.Phase.ToString(), activeKind = health.ActiveKind?.ToString(), health.ActiveRunId,
                    health.LastAttemptRunId, health.LastFinalRunId, lastOutcome = health.LastOutcome?.ToString(),
                    health.LastValidCommittedAtUtc, health.LastCompleteCommittedAtUtc, health.StorageRecoveryPending,
                    health.LastStorageError, lastCollectionError = health.LastCollectionError?.ToString(),
                    health.BlockedReason, health.LastDiagnosticFileError, health.Paused
                });
            }
        }
    }

    [Fact]
    public async Task PausedHost_CreatesNoSeedRunsThenResumeInitializes()
    {
        await using var f = await DemoFixture.Create();
        await f.Coordinator.SetPausedAsync(true, default);
        await using var app = f.BuildHost();
        await app.StartAsync();
        Assert.Equal(0, f.RunCount());
        Assert.Null(await f.Checkpoint());
        await f.Coordinator.SetPausedAsync(false, default);
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        Assert.Equal(3, f.RunCount());
        AssertNormal((await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1), default)).Summary);
        await app.StopAsync();
    }

    [Fact]
    public async Task OccupiedPort_DoesNotInitializePrehistory()
    {
        await using var f = await DemoFixture.Create();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var app = f.BuildHost(((IPEndPoint)listener.LocalEndpoint).Port);
        await Assert.ThrowsAnyAsync<IOException>(() => app.StartAsync());
        Assert.Equal(0, f.RunCount());
        Assert.Null(await f.Checkpoint());
    }

    [Fact]
    public async Task PauseDuringSeed_FinalizesThatSeedAndDoesNotBeginAnother()
    {
        BlockingScenario source = null!;
        await using var f = await DemoFixture.Create(decorateSource: inner => source = new(inner));
        var request = f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await f.Profiles.OperationGate.WaitAsync(0));
        Assert.Equal(CollectorPhase.Collecting, f.Health.GetSnapshot().Phase);
        Assert.Equal(RunRequestStatus.AlreadyRunning,
            (await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default)).Status);
        await f.Coordinator.SetPausedAsync(true, default);
        source.Release.TrySetResult();
        Assert.Equal(RunRequestStatus.Paused, (await request).Status);
        Assert.Equal(1, f.RunCount());
        Assert.Equal(0, await f.Checkpoint());
        var timer = Stopwatch.StartNew();
        await f.Coordinator.SetPausedAsync(false, default);
        output.WriteLine($"pause-resume admission: {timer.Elapsed.TotalMilliseconds:F3} ms; {HealthText()}");
        timer.Restart();
        var idleObserved = false;
        try
        {
            await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle,
                () => $"pause-resume Idle wait after {timer.Elapsed.TotalMilliseconds:F3} ms; {HealthText()}");
            idleObserved = true;
        }
        finally
        {
            output.WriteLine($"pause-resume Idle wait: {timer.Elapsed.TotalMilliseconds:F3} ms; passed={idleObserved}; {HealthText()}");
        }
        Assert.Equal(3, source.Calls);
        AssertNormal((await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1), default)).Summary);

        string HealthText()
        {
            var health = f.Health.GetSnapshot();
            return JsonSerializer.Serialize(new
            {
                sourceCalls = Volatile.Read(ref source.Calls), phase = health.Phase.ToString(),
                activeKind = health.ActiveKind?.ToString(), health.ActiveRunId,
                health.LastAttemptRunId, health.LastFinalRunId, lastOutcome = health.LastOutcome?.ToString(),
                health.StorageRecoveryPending, health.LastStorageError,
                lastCollectionError = health.LastCollectionError?.ToString(), health.BlockedReason, health.Paused
            });
        }
    }

    [Fact]
    public async Task CancelBeforeAcceptance_InterruptsSeedAndRetriesCommittedPosition()
    {
        BlockingScenario source = null!;
        await using var f = await DemoFixture.Create(decorateSource: inner => source = new(inner));
        using var browser = new CancellationTokenSource();
        var request = f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, browser.Token);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        browser.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Null(await f.Checkpoint());
        Assert.Equal(RunLifecycle.Interrupted, (await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1), default)).Lifecycle);
        Assert.Equal(0, f.RunningCount());
        source.Release.TrySetResult();
        var accepted = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default);
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        Assert.Equal(4, f.RunCount());
        Assert.Equal(accepted.RunId, (await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1), default)).RunId);
        AssertNormal((await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1), default)).Summary);
    }

    [Fact]
    public async Task HostStopDuringSeed_FinalizesInterruptedWithFreshToken()
    {
        BlockingScenario source = null!;
        await using var f = await DemoFixture.Create(decorateSource: inner => source = new(inner));
        var request = f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Startup, default);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Coordinator.StopAsync(default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(CollectorPhase.Stopped, f.Health.GetSnapshot().Phase);
        Assert.Null(await f.Checkpoint());
        Assert.Equal(0, f.RunningCount());
        Assert.Equal(RunLifecycle.Interrupted, (await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1), default)).Lifecycle);
    }

    [Fact]
    public async Task SeedStorageFailure_BlocksNewSourceUntilSameRunIsResolved()
    {
        FailingStore wrapper = null!;
        CountingScenario source = null!;
        await using var f = await DemoFixture.Create(decorateStore: store => wrapper = new(store) { FailWrites = true },
            decorateSource: inner => source = new(inner));
        var first = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Startup, default);
        Assert.Equal(RunRequestStatus.StorageBlocked, first.Status);
        Assert.True(f.Health.GetSnapshot().StorageRecoveryPending);
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, f.RunningCount());
        Assert.Equal(RunRequestStatus.StorageBlocked,
            (await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default)).Status);
        Assert.Equal(1, source.Calls);
        wrapper.FailWrites = false;
        await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default);
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        Assert.Equal(4, source.Calls);
        Assert.Equal(0, f.RunningCount());
        Assert.Equal(RunLifecycle.Interrupted, (await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1, first.RunId), default)).Lifecycle);
        AssertNormal((await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1), default)).Summary);
    }

    [Fact]
    public async Task LostSeedCommitResponse_UsesCommittedCheckpointWithoutDuplicateSeed()
    {
        await using var f = await DemoFixture.Create(decorateStore: store => new FailingStore(store) { LoseFirstResponse = true });
        await f.Coordinator.StartAsync(default);
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        Assert.Equal(3, f.RunCount());
        Assert.Equal(2, await f.Checkpoint());
        Assert.False(f.Health.GetSnapshot().StorageRecoveryPending);
        AssertNormal((await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1), default)).Summary);
    }

    [Fact]
    public async Task FailedSeed_StopsCurrentAdmissionWithoutBusyLoop()
    {
        CountingScenario source = null!;
        await using var f = await DemoFixture.Create(decorateSource: inner => source = new(inner) { FailFirst = true });
        var timer = Stopwatch.StartNew();
        var first = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Startup, default);
        output.WriteLine($"failed-seed admission: {timer.Elapsed.TotalMilliseconds:F3} ms; receipt={first.Status}/{first.RunId}; {HealthText()}");
        Assert.Equal(RunRequestStatus.Accepted, first.Status);
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, f.RunCount());
        Assert.Null(await f.Checkpoint());
        Assert.Equal(RunOutcome.Failed, (await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1, first.RunId), default)).Outcome);
        timer.Restart();
        var retry = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default);
        output.WriteLine($"retry admission: {timer.Elapsed.TotalMilliseconds:F3} ms; receipt={retry.Status}/{retry.RunId}; {HealthText()}");
        Assert.Equal(RunRequestStatus.Accepted, retry.Status);
        timer.Restart();
        var idleObserved = false;
        try
        {
            await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle,
                () => $"retry Idle wait after {timer.Elapsed.TotalMilliseconds:F3} ms; acceptedRun={retry.RunId}; {HealthText()}");
            idleObserved = true;
        }
        finally
        {
            // Capture the failure state before fixture disposal cancels the active run.
            output.WriteLine($"retry Idle wait: {timer.Elapsed.TotalMilliseconds:F3} ms; passed={idleObserved}; acceptedRun={retry.RunId}; {HealthText()}");
        }
        Assert.Equal(4, source.Calls);
        AssertNormal((await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1), default)).Summary);

        string HealthText()
        {
            var health = f.Health.GetSnapshot();
            return JsonSerializer.Serialize(new
            {
                sourceCalls = Volatile.Read(ref source.Calls), phase = health.Phase.ToString(),
                activeKind = health.ActiveKind?.ToString(), health.ActiveRunId,
                health.LastAttemptRunId, health.LastFinalRunId, lastOutcome = health.LastOutcome?.ToString(),
                health.StorageRecoveryPending, health.LastStorageError,
                lastCollectionError = health.LastCollectionError?.ToString(), health.BlockedReason, health.Paused
            });
        }
    }

    [Fact]
    public async Task MissingSeedCheckpoint_StopsBeforeAnotherSourceCall()
    {
        NoCheckpointScenario source = null!;
        await using var f = await DemoFixture.Create(decorateSource: inner => source = new(inner));
        var request = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Startup, default);
        Assert.Equal(RunRequestStatus.StorageBlocked, request.Status);
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, f.RunCount());
        Assert.True(f.Health.GetSnapshot().StorageRecoveryPending);
    }

    [Fact]
    public async Task AcceptedVisibleRun_StillSurvivesBrowserCancellation()
    {
        BlockingScenario source = null!;
        await using var f = await DemoFixture.Create(decorateSource: inner => source = new(inner, 3));
        using var browser = new CancellationTokenSource();
        var accepted = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, browser.Token);
        Assert.Equal(RunRequestStatus.Accepted, accepted.Status);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        browser.Cancel();
        source.Release.TrySetResult();
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        var snapshot = await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1, accepted.RunId), default);
        Assert.Equal(RunLifecycle.Finalized, snapshot.Lifecycle);
        AssertNormal(snapshot.Summary);
        Assert.Equal(2, await f.Checkpoint());
    }

    private static void AssertNormal(RunSummary summary)
    {
        Assert.Equal(2040, summary.ConfirmedUp);
        Assert.Equal(8, summary.ConfirmedDown);
        Assert.Equal(2, summary.NewDown);
        Assert.Equal(1, summary.InitialDown);
        Assert.Equal(5, summary.ExistingDown);
        Assert.Equal(2, summary.Recovered);
        Assert.Equal(8, summary.OpenIncidents);
    }

    private sealed class DemoFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory? ownedDirectory;
        public StorageLocation Location { get; }
        public ProfileStore Profiles { get; }
        public ManualTimeProvider Clock { get; } = new();
        private readonly SemaphoreSlim writeGate = new(1, 1);
        public SqliteCollectionStore Store { get; }
        public CollectorHealth Health { get; }
        public CollectionCoordinator Coordinator { get; }
        private readonly AirWaveSession session = new();
        private DemoFixture(string? path, Func<IApSource, IApSource>? decorateSource, Func<ICollectionStore, ICollectionStore>? decorateStore)
        {
            if (path is null) path = (ownedDirectory = new()).Path;
            Location = StoragePaths.Open(AppMode.Demo, path);
            Profiles = new(Location);
            Store = new(Location, writeGate, Clock);
            Health = new(Clock);
            IApSource source = new ScenarioApSource(Clock, Store);
            source = decorateSource?.Invoke(source) ?? source;
            Coordinator = new(Profiles, source, decorateStore?.Invoke(Store) ?? Store, session, new DemoCredentialStore(), Location, Health, Clock,
                demoPrehistory: new(Store));
        }
        public static async Task<DemoFixture> Create(string? path = null, Func<IApSource, IApSource>? decorateSource = null, Func<ICollectionStore, ICollectionStore>? decorateStore = null)
        {
            var f = new DemoFixture(path, decorateSource, decorateStore);
            await f.Store.InitializeAsync(default);
            return f;
        }
        public async Task<int?> Checkpoint() => (await Store.GetSourceCheckpointAsync(Location.DatasetId, ScenarioApSource.CheckpointSource, default))?.Position;
        public long RunCount() => Count("SELECT COUNT(*) FROM runs");
        public long RunningCount() => Count("SELECT COUNT(*) FROM runs WHERE lifecycle=0");
        private long Count(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Location.DataDirectory, "monitor.db"), Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
            return (long)command.ExecuteScalar()!;
        }
        public WebApplication BuildHost(int port = 0)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production", ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, port));
            builder.Services.AddSingleton(Coordinator);
            builder.Services.AddSingleton<TimeProvider>(Clock);
            builder.Services.AddHostedService<CollectionWorker>();
            return builder.Build();
        }
        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync(); session.Dispose(); writeGate.Dispose(); ownedDirectory?.Dispose();
        }
    }
    private sealed class BlockingScenario(IApSource inner, int blockCall = 1) : IApSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async Task<ApCollectionBatch> CollectAsync(CollectionContext context, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Calls) == blockCall)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return await inner.CollectAsync(context, ct);
        }
    }
    private sealed class CountingScenario(IApSource inner) : IApSource
    {
        public int Calls;
        public bool FailFirst;
        public Task<ApCollectionBatch> CollectAsync(CollectionContext context, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Calls) == 1 && FailFirst) throw new HttpRequestException("synthetic-failure");
            return inner.CollectAsync(context, ct);
        }
    }
    private sealed class NoCheckpointScenario(IApSource inner) : IApSource
    {
        public int Calls;
        public async Task<ApCollectionBatch> CollectAsync(CollectionContext context, CancellationToken ct)
        {
            // A second call fails fast so the RED assertion does not leave an unbounded loop.
            if (Interlocked.Increment(ref Calls) > 1) throw new HttpRequestException("unexpected-second-call");
            return (await inner.CollectAsync(context, ct)) with { Checkpoint = null };
        }
    }
    private sealed class FailingStore(ICollectionStore inner) : ICollectionStore
    {
        public bool FailWrites, LoseFirstResponse;
        public Task<CollectionContext> BeginRunAsync(ProfileSnapshot p, CollectionKind k, RunTrigger t, CancellationToken ct) => inner.BeginRunAsync(p, k, t, ct);
        public async Task<CommitReceipt> ApplyApBatchAsync(CollectionContext c, ApCollectionBatch b, CancellationToken ct)
        {
            if (FailWrites) throw new IOException("synthetic-storage-failure");
            var receipt = await inner.ApplyApBatchAsync(c, b, ct);
            if (LoseFirstResponse) { LoseFirstResponse = false; throw new IOException("synthetic-lost-response"); }
            return receipt;
        }
        public Task<CommitReceipt> InterruptRunAsync(CollectionContext c, CancellationToken ct)
            => FailWrites ? throw new IOException("synthetic-storage-failure") : inner.InterruptRunAsync(c, ct);
        public Task<EnrichmentCommitReceipt> ApplyEnrichmentBatchAsync(CollectionContext c, EnrichmentBatch b, CancellationToken ct) => inner.ApplyEnrichmentBatchAsync(c, b, ct);
        public Task<DashboardSnapshot> GetDashboardAsync(DashboardQuery q, CancellationToken ct) => inner.GetDashboardAsync(q, ct);
        public Task<CollectionSuccessTimes> GetSuccessTimesAsync(Guid datasetId, long scopeVersion, CancellationToken ct) => inner.GetSuccessTimesAsync(datasetId, scopeVersion, ct);
    }

}
