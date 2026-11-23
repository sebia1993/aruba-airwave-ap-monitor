using System.Text.Json;
using AirWaveApMonitor.App.Collection;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Demo;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Infrastructure.Security;
using static AirWaveApMonitor.Tests.Collection.CollectionCoordinatorTests;
namespace AirWaveApMonitor.Tests.Collection;
public sealed class CoordinatorEnrichmentTests
{
    [Fact]
    public async Task NoRunnableKind_HasNoNextEnrichmentTimestamp()
    {
        await using var f = await Fixture.Create();
        Assert.Null(f.Health.GetSnapshot().NextEnrichmentAtUtc);
        await f.Coordinator.TickAsync(default);
        Assert.Null(f.Health.GetSnapshot().NextEnrichmentAtUtc);
    }
    [Fact]
    public async Task ProfileAwareCapabilityGate_RejectsWithoutRunnerExecution()
    {
        var runner = new ControlledRunner { Available = false };
        await using var f = await Fixture.Create(enrichment: runner);
        Assert.Equal(RunRequestStatus.Unsupported, (await f.Coordinator.RequestAsync(CollectionKind.ConnectionNeighbor, RunTrigger.Manual, default)).Status);
        Assert.Equal(0, runner.Executions);
    }
    [Fact]
    public async Task UnresolvedEnrichmentStorage_BlocksApBeforeAnyNetwork()
    {
        var runner = new ControlledRunner { PendingRecovery = true, CanRecover = false };
        await using var f = await Fixture.Create(enrichment: runner);
        var receipt = await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default);
        Assert.Equal(RunRequestStatus.StorageBlocked, receipt.Status); Assert.Equal(0, f.Source.Calls);
        Assert.True(f.Health.GetSnapshot().StorageRecoveryPending);
        runner.CanRecover = true;
        Assert.Equal(RunRequestStatus.Accepted, (await f.Coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default)).Status);
        await f.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(f.Health.GetSnapshot().StorageRecoveryPending);
    }
    [Fact]
    public async Task SameScheduledTick_RunsApThenConnectionThenPowerWithoutOverlap()
    {
        var runner = new ControlledRunner();
        await using var f = await Fixture.Create();
        await f.Coordinator.DisposeAsync();
        var original = f.Profiles.Current!;
        var profile = new ProfileSnapshot(original.InstanceId, original.DatasetId, original.Origin, original.ConfigurationVersion, original.ScopeVersion,
            original.ExpectedTargets, original.BaselineStatus, new(1, 1, true, true));
        await File.WriteAllTextAsync(Path.Combine(f.Location.DataDirectory, "profile.json"), JsonSerializer.Serialize(profile));
        var profiles = new ProfileStore(f.Location);
        await using var coordinator = new CollectionCoordinator(profiles, f.Source, f.Store, f.Session, new DemoCredentialStore(), f.Location, f.Health, f.Clock, runner);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await coordinator.TickAsync(default);
        await f.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(0, runner.Executions);
        f.Source.Release.TrySetResult();
        await Eventually(() => runner.Executions == 2);
        Assert.Equal(new[] { CollectionKind.ConnectionNeighbor, CollectionKind.Power }, runner.Kinds.ToArray());
    }
    [Fact]
    public async Task ApPendingDuringConnection_DiscardsPowerAndCoalescesOneAp()
    {
        var runner = new ControlledRunner { BlockConnection = true };
        await using var f = await Fixture.Create();
        await using var coordinator = await EnabledCoordinator(f, runner);
        f.Source.Release.TrySetResult();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await coordinator.TickAsync(default);
        await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var first = await coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Scheduled, default);
        var second = await coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Scheduled, default);
        Assert.Equal(first.QueueId, second.QueueId);
        runner.Release.TrySetResult();
        await Eventually(() => f.Source.Calls == 2 && f.Health.GetSnapshot().Phase == CollectorPhase.Idle);
        Assert.True(runner.Yielded); Assert.Equal(new[] { CollectionKind.ConnectionNeighbor }, runner.Kinds.ToArray());
    }
    [Fact]
    public async Task PauseDuringConnection_DiscardsRemainingCycle()
    {
        var runner = new ControlledRunner { BlockConnection = true };
        await using var f = await Fixture.Create();
        await using var coordinator = await EnabledCoordinator(f, runner);
        f.Source.Release.TrySetResult(); f.Clock.Advance(TimeSpan.FromMinutes(1));
        await coordinator.TickAsync(default); await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await coordinator.SetPausedAsync(true, default); runner.Release.TrySetResult();
        await Eventually(() => f.Health.GetSnapshot().ActiveKind is null);
        Assert.Equal(1, runner.Executions); Assert.True(runner.Yielded);
    }
    [Fact]
    public async Task EnrichmentRecoveryPending_DiscardsRemainingCycleAndBlocksAp()
    {
        var runner = new ControlledRunner { FailFinalization = true };
        await using var f = await Fixture.Create();
        await using var coordinator = await EnabledCoordinator(f, runner);
        f.Source.Release.TrySetResult(); f.Clock.Advance(TimeSpan.FromMinutes(1));
        await coordinator.TickAsync(default);
        await Eventually(() => f.Health.GetSnapshot().Phase == CollectorPhase.StorageRecovery);
        Assert.Equal(1, runner.Executions);
        Assert.Equal(RunRequestStatus.StorageBlocked, (await coordinator.RequestAsync(CollectionKind.ApStatus, RunTrigger.Manual, default)).Status);
        Assert.Equal(1, f.Source.Calls);
    }
    [Fact]
    public async Task PauseAndResumeBeforeCurrentApCompletes_DoesNotReviveDiscardedCycle()
    {
        var runner = new ControlledRunner(); await using var f = await Fixture.Create();
        await using var coordinator = await EnabledCoordinator(f, runner);
        f.Clock.Advance(TimeSpan.FromMinutes(1)); await coordinator.TickAsync(default);
        await f.Source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await coordinator.SetPausedAsync(true, default); await coordinator.SetPausedAsync(false, default);
        f.Source.Release.TrySetResult(); await Eventually(() => f.Health.GetSnapshot().ActiveKind is null);
        Assert.Equal(0, runner.Executions);
    }
    private static async Task<CollectionCoordinator> EnabledCoordinator(Fixture f, IEnrichmentRunner runner)
    {
        await f.Coordinator.DisposeAsync(); var original = f.Profiles.Current!;
        var profile = new ProfileSnapshot(original.InstanceId, original.DatasetId, original.Origin, original.ConfigurationVersion, original.ScopeVersion,
            original.ExpectedTargets, original.BaselineStatus, new(1, 1, true, true));
        await File.WriteAllTextAsync(Path.Combine(f.Location.DataDirectory, "profile.json"), JsonSerializer.Serialize(profile));
        return new(new ProfileStore(f.Location), f.Source, f.Store, f.Session, new DemoCredentialStore(), f.Location, f.Health, f.Clock, runner);
    }
    internal sealed class ControlledRunner : IEnrichmentRunner
    {
        public bool Available = true, PendingRecovery, CanRecover, BlockConnection, Yielded, FailFinalization;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Executions; public List<CollectionKind> Kinds { get; } = [];
        public bool Supports(CollectionKind kind) => kind != CollectionKind.ApStatus;
        public bool Supports(ProfileSnapshot profile, CollectionKind kind) => Available;
        public bool RecoveryPending => PendingRecovery;
        public Task<bool> RecoverAsync(CancellationToken ct) { if (CanRecover) PendingRecovery = false; return Task.FromResult(CanRecover); }
        public async Task ExecuteAsync(ProfileSnapshot profile, CollectionKind kind, Func<bool> shouldYield, Action<long> started, CancellationToken ct)
        {
            lock (Kinds) Kinds.Add(kind); Interlocked.Increment(ref Executions);
            if (kind == CollectionKind.ConnectionNeighbor && BlockConnection)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); Yielded = shouldYield(); }
            if (FailFinalization) PendingRecovery = true;
        }
    }
}
