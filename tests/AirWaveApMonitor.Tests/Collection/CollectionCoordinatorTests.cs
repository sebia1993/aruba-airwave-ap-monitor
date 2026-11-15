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

namespace AirWaveApMonitor.Tests.Collection;

public sealed class CollectionCoordinatorTests
{
    [Fact]
    public async Task ScheduledAndManualRequests_NeverOverlap()
    {
        await using var f = await Fixture.Create();
        var accepted = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default);
        Assert.Equal(RunRequestStatus.Accepted, accepted.Status);
        await f.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var scheduled = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Scheduled, default);
        var manual = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default);
        Assert.Equal(RunRequestStatus.ScheduleSkipped, scheduled.Status);
        Assert.Equal(RunRequestStatus.AlreadyRunning, manual.Status);
        Assert.Equal(accepted.RunId, manual.RunId);
        Assert.Equal(1, f.Source.Calls);
        f.Source.Release.TrySetResult();
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        Assert.Equal(accepted.RunId, (await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1), default)).RunId);
    }

    [Fact]
    public async Task LongApRun_SkipsNextTick()
    {
        await using var f = await Fixture.Create();
        await f.Coordinator.StartAsync(default);
        await f.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await f.Coordinator.TickAsync(default);
        Assert.Equal(1, f.Health.GetSnapshot().SkippedApTicks);
        f.Source.Release.TrySetResult();
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        Assert.Equal(1, f.Source.Calls);
    }

    [Fact]
    public async Task Resume_CollectsOnceWithoutCatchupStorm()
    {
        await using var f = await Fixture.Create();
        await f.Coordinator.SetPausedAsync(true, default);
        await f.Coordinator.StartAsync(default);
        f.Clock.Advance(TimeSpan.FromHours(10));
        await f.Coordinator.TickAsync(default);
        Assert.Equal(0, f.Source.Calls);
        await f.Coordinator.SetPausedAsync(false, default);
        await f.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Coordinator.SetPausedAsync(false, default);
        f.Source.Release.TrySetResult();
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        Assert.Equal(1, f.Source.Calls);
    }

    [Fact]
    public async Task AuthRejected_RemainsPausedAfterRestart()
    {
        await using var f = await Fixture.Create(durableStop: true);
        var result = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Startup, default);
        Assert.Equal(RunRequestStatus.AuthenticationBlocked, result.Status);
        await f.Coordinator.SetPausedAsync(true, default);
        await f.Coordinator.SetPausedAsync(false, default);
        f.Clock.Advance(TimeSpan.FromHours(1));
        await f.Coordinator.TickAsync(default);
        Assert.Equal(0, f.Source.Calls);
        Assert.True(f.Health.GetSnapshot().AuthenticationBlocked);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1), default));
    }

    [Fact]
    public async Task EnrichmentYieldsToOnePendingApRun()
    {
        var enrichment = new YieldingRunner();
        await using var f = await Fixture.Create(enrichment: enrichment);
        Assert.Equal(RunRequestStatus.Queued, (await f.Coordinator.RequestAsync(CollectionKind.ConnectionNeighbor, RunTrigger.Manual, default)).Status);
        await enrichment.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Scheduled, default);
        var second = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Scheduled, default);
        Assert.Equal(RunRequestStatus.Queued, first.Status);
        Assert.Equal(first.QueueId, second.QueueId);
        enrichment.Release.TrySetResult();
        await f.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(enrichment.Yielded);
        Assert.False(enrichment.SecondRequestStarted);
        f.Source.Release.TrySetResult();
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        Assert.Equal(1, f.Source.Calls);
    }

    [Fact]
    public async Task AcceptedRun_SurvivesBrowserCancellationAndFreezesProfile()
    {
        await using var f = await Fixture.Create();
        using var browser = new CancellationTokenSource();
        var result = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, browser.Token);
        Assert.Equal(RunRequestStatus.Accepted, result.Status);
        browser.Cancel();
        await f.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await f.Profiles.OperationGate.WaitAsync(0));
        f.Source.Release.TrySetResult();
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        Assert.NotNull(f.Health.GetSnapshot().LastValidCommittedAtUtc);
        Assert.Equal(1, f.Source.Context!.Profile.ScopeVersion);
    }

    [Fact]
    public async Task UnsupportedEnrichment_DoesNotCreateRunOrNetwork()
    {
        await using var f = await Fixture.Create();
        var result = await f.Coordinator.RequestAsync(CollectionKind.Power, RunTrigger.Manual, default);
        Assert.Equal(RunRequestStatus.Unsupported, result.Status);
        Assert.Null(result.RunId);
        Assert.Equal(0, f.Source.Calls);
    }

    internal static async Task Eventually(Func<bool> condition, Func<string>? diagnostics = null)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(condition(), diagnostics?.Invoke());
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public TemporaryDirectory Directory { get; } = new();
        public StorageLocation Location { get; private set; } = null!;
        public ProfileStore Profiles { get; private set; } = null!;
        public ManualTimeProvider Clock { get; } = new();
        public ControlledSource Source { get; } = new();
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public SqliteCollectionStore Store { get; private set; } = null!;
        public CollectorHealth Health { get; private set; } = null!;
        public CollectionCoordinator Coordinator { get; private set; } = null!;
        public AirWaveSession Session { get; private set; } = null!;
        public static async Task<Fixture> Create(bool durableStop = false, IEnrichmentRunner? enrichment = null,
            Func<ICollectionStore, ICollectionStore>? decorate = null)
        {
            var f = new Fixture();
            f.Location = StoragePaths.Open(AppMode.Demo, f.Directory.Path);
            f.Profiles = new(f.Location);
            await f.Profiles.UpdateAsync(ScenarioApSource.DemoOrigin, 1, new(), 1, default);
            f.Store = new(f.Location, f.Gate, f.Clock);
            await f.Store.InitializeAsync(default);
            var auth = new SqliteAuthenticationStateStore(f.Location, f.Gate);
            if (durableStop) await auth.SaveStopAsync(new(AuthenticationBinding.From(f.Profiles.Current!),
                AuthenticationStopReason.LoginRejected, f.Clock.GetUtcNow()), default);
            f.Session = new(authenticationStates: auth);
            f.Health = new(f.Clock);
            f.Coordinator = new(f.Profiles, f.Source, decorate?.Invoke(f.Store) ?? f.Store, f.Session, new DemoCredentialStore(), f.Location, f.Health, f.Clock, enrichment);
            return f;
        }
        public async ValueTask DisposeAsync()
        {
            Source.Release.TrySetResult();
            await Coordinator.DisposeAsync(); Session.Dispose(); Gate.Dispose(); Directory.Dispose();
        }
    }

    internal sealed class ControlledSource : IApSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public CollectionContext? Context;
        public async Task<ApCollectionBatch> CollectAsync(CollectionContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); Context = context; Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            var evidence = new FieldEvidence(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.Demo, context.StartedAtUtc);
            return new(RunOutcome.Succeeded, [new("1", ApState.Up, new("serial-1", "02:00:00:00:00:01"), new("AP one", "192.0.2.1", null, null), new(), new(),
                context.StartedAtUtc, null, null, new Dictionary<string, FieldEvidence> { ["is_up"] = evidence, ["serial_number"] = evidence, ["name"] = evidence }.ToImmutableDictionary())],
                [], context.StartedAtUtc, ObservationCompleteness.Complete, []);
        }
    }
    private sealed class YieldingRunner : IEnrichmentRunner
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Yielded, SecondRequestStarted;
        public bool Supports(CollectionKind kind) => kind == CollectionKind.ConnectionNeighbor;
        public async Task ExecuteAsync(ProfileSnapshot profile, CollectionKind kind, Func<bool> shouldYield, Action<long> started, CancellationToken ct)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(ct);
            Yielded = shouldYield(); if (!Yielded) SecondRequestStarted = true;
        }
    }
}
