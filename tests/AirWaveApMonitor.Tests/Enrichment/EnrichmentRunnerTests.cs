using System.Collections.Immutable;
using AirWaveApMonitor.App.Collection;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Demo;
using AirWaveApMonitor.Tests.Support;
namespace AirWaveApMonitor.Tests.Enrichment;
public sealed class EnrichmentRunnerTests
{
    [Fact]
    public async Task BeginBeforeNetwork_RealRunId_ManualTrigger_OnlyKindHealthAdvances()
    {
        var f = new Fixture(); long? reported = null;
        await f.Runner.ExecuteAsync(f.Profile, CollectionKind.ConnectionNeighbor, RunTrigger.Manual, () => false, id => reported = id, default);
        Assert.Equal(101, reported); Assert.Equal(RunTrigger.Manual, f.Store.Context!.Trigger);
        Assert.Equal(101, f.Store.Batch!.Detail!.Neighbors[0].Neighbors[0].RunId);
        var health = f.Health.GetSnapshot(); Assert.Null(health.LastValidCommittedAtUtc); Assert.Null(health.LastFinalRunId);
        var kind = Assert.Single(health.EnrichmentKinds!.Value.Where(k => k.Kind == CollectionKind.ConnectionNeighbor));
        Assert.Equal(101, kind.LastFinalRunId); Assert.Equal(f.Clock.GetUtcNow(), kind.LastValidCommittedAtUtc);
        Assert.False(kind.HasQualityGap);
    }
    [Fact]
    public async Task ResponseLostAfterCommit_ResolvesOriginalReceipt_NoSecondNetworkOrRun()
    {
        var f = new Fixture(); f.Store.LoseApplyResponse = true;
        await f.Runner.ExecuteAsync(f.Profile, CollectionKind.Power, () => false, _ => { }, default);
        Assert.Equal(1, f.Store.Begins); Assert.Equal(1, f.Store.Applies); Assert.Equal(1, f.Store.Interrupts);
        Assert.False(f.Runner.RecoveryPending);
        Assert.Equal(RunOutcome.Succeeded, f.Health.GetSnapshot().EnrichmentKinds!.Value.Single(k => k.Kind == CollectionKind.Power).LastOutcome);
    }
    [Fact]
    public async Task FailedFinalization_BlocksNextNetworkUntilSameContextRecovered()
    {
        var f = new Fixture(); f.Store.FailApply = true; f.Store.FailInterrupt = true;
        await f.Runner.ExecuteAsync(f.Profile, CollectionKind.Power, () => false, _ => { }, default);
        Assert.True(f.Runner.RecoveryPending);
        await f.Runner.ExecuteAsync(f.Profile, CollectionKind.ConnectionNeighbor, () => false, _ => { }, default);
        Assert.Equal(1, f.Store.Begins); Assert.Equal(1, f.Store.Applies);
        f.Store.FailInterrupt = false;
        Assert.True(await f.Runner.RecoverAsync(default)); Assert.False(f.Runner.RecoveryPending);
        var kind = f.Health.GetSnapshot().EnrichmentKinds!.Value.Single(k => k.Kind == CollectionKind.Power);
        Assert.True(kind.HasQualityGap); Assert.Null(kind.LastValidCommittedAtUtc); Assert.Equal(CollectionErrorCategory.Interrupted, kind.LastError);
    }
    [Fact]
    public async Task FrozenTargetReadFailure_UsesTypedInterruptWithoutApFinalization()
    {
        var f = new Fixture(); f.Store.FailTargets = true;
        await f.Runner.ExecuteAsync(f.Profile, CollectionKind.Power, () => false, _ => { }, default);
        Assert.Equal(1, f.Store.Interrupts); Assert.Equal(0, f.Store.Applies); Assert.False(f.Runner.RecoveryPending);
        Assert.Null(f.Health.GetSnapshot().LastOutcome);
    }
    [Fact]
    public async Task HistoricalOnly_DoesNotAdvanceCurrentKindSuccess()
    {
        var f = new Fixture(); f.Store.Disposition = EnrichmentApplicationDisposition.HistoricalOnly;
        await f.Runner.ExecuteAsync(f.Profile, CollectionKind.Power, () => false, _ => { }, default);
        var kind = f.Health.GetSnapshot().EnrichmentKinds!.Value.Single(k => k.Kind == CollectionKind.Power);
        Assert.Null(kind.LastValidCommittedAtUtc); Assert.Null(kind.LastCompleteCommittedAtUtc);
    }
    [Fact]
    public async Task DisabledCapability_DoesNotBeginRun()
    {
        var f = new Fixture(); var disabled = new ProfileSnapshot(f.Profile.InstanceId, f.Profile.DatasetId, f.Profile.Origin, 1, 1, [], BaselineStatus.Unverified, new());
        await f.Runner.ExecuteAsync(disabled, CollectionKind.Power, () => false, _ => { }, default);
        Assert.False(f.Runner.Supports(disabled, CollectionKind.Power)); Assert.Equal(0, f.Store.Begins);
        f.Runner.UpdateSchedule(disabled, f.Clock.GetUtcNow().AddMinutes(15));
        Assert.All(f.Health.GetSnapshot().EnrichmentKinds!.Value, k => { Assert.False(k.Enabled); Assert.Null(k.NextAtUtc); });
    }
    [Fact]
    public async Task CancellationAfterBegin_UsesTypedInterruptAndNoSourceApply()
    {
        var f = new Fixture(); using var cancel = new CancellationTokenSource();
        await f.Runner.ExecuteAsync(f.Profile, CollectionKind.Power, () => false, _ => cancel.Cancel(), cancel.Token);
        Assert.Equal(1, f.Store.Interrupts); Assert.Equal(0, f.Store.Applies); Assert.False(f.Runner.RecoveryPending);
    }
    [Fact]
    public async Task BeginResponseLossWithDurableRunning_RemainsBlockedWithoutNewRun()
    {
        var f = new Fixture(); f.Store.LoseBeginResponse = true;
        await f.Runner.ExecuteAsync(f.Profile, CollectionKind.Power, () => false, _ => { }, default);
        Assert.True(f.Runner.RecoveryPending);
        await f.Runner.ExecuteAsync(f.Profile, CollectionKind.Power, () => false, _ => { }, default);
        Assert.Equal(1, f.Store.Begins); Assert.Equal(0, f.Store.Applies); Assert.False(await f.Runner.RecoverAsync(default));
    }
    [Fact]
    public async Task PartialValidBatch_AdvancesValidButNotCompleteClock()
    {
        var f = new Fixture(); f.Source.Scenario = DemoEnrichmentScenario.MissingPower;
        await f.Runner.ExecuteAsync(f.Profile, CollectionKind.Power, () => false, _ => { }, default);
        var kind = f.Health.GetSnapshot().EnrichmentKinds!.Value.Single(k => k.Kind == CollectionKind.Power);
        Assert.NotNull(kind.LastValidCommittedAtUtc); Assert.Null(kind.LastCompleteCommittedAtUtc); Assert.True(kind.HasQualityGap);
    }
    [Fact]
    public async Task ReceiptWhileStoreStillReportsRunning_DoesNotClearRecoveryBlock()
    {
        var f = new Fixture(); f.Store.ReportsRunning = true;
        await f.Runner.ExecuteAsync(f.Profile, CollectionKind.Power, () => false, _ => { }, default);
        Assert.True(f.Runner.RecoveryPending);
        await f.Runner.ExecuteAsync(f.Profile, CollectionKind.Power, () => false, _ => { }, default);
        Assert.Equal(1, f.Store.Begins);
    }
    [Theory]
    [InlineData("conflicting-neighbor", false)]
    [InlineData("failed-neighbor", false)]
    [InlineData("unknown-power-state", false)]
    [InlineData("undefined-port-state", false)]
    [InlineData("conflicting-neighbor", true)]
    [InlineData("failed-neighbor", true)]
    [InlineData("unknown-power-state", true)]
    [InlineData("undefined-port-state", true)]
    public async Task PolicyRejectedFacts_DoNotAdvanceCompleteHealth_AndZeroValidDoesNotAdvanceValidHealth(string scenario, bool includeValid)
    {
        var f = new Fixture(); var kind = BoundedEnrichmentSourceTests.ScenarioKind(scenario);
        var runner = new EnrichmentRunner(f.Store, new PolicySource(f.Clock, scenario, includeValid), f.Health, f.Clock);
        await runner.ExecuteAsync(f.Profile, kind, () => false, _ => { }, default);
        var status = f.Health.GetSnapshot().EnrichmentKinds!.Value.Single(k => k.Kind == kind);
        Assert.Equal(includeValid ? RunOutcome.Partial : RunOutcome.Failed, status.LastOutcome);
        Assert.True(status.HasQualityGap); Assert.Null(status.LastCompleteCommittedAtUtc);
        Assert.Equal(includeValid, status.LastValidCommittedAtUtc is not null);
        Assert.Null(f.Health.GetSnapshot().LastValidCommittedAtUtc); Assert.Null(f.Health.GetSnapshot().LastOutcome);
    }
    private sealed class PolicySource(TimeProvider clock, string scenario, bool includeValid) : IBoundedEnrichmentSource
    {
        public CapabilityDecision Capability(ProfileSnapshot profile, CollectionKind kind) => new(CapabilityAvailability.AvailableConfirmed, true, CapabilityDecisionReason.Available);
        public Task<EnrichmentBatch> CollectAsync(CollectionContext context, CancellationToken ct) => CollectAsync(context, [], () => false, ct);
        public Task<EnrichmentBatch> CollectAsync(CollectionContext context, ImmutableArray<EnrichmentTargetBinding> targets, Func<bool> shouldYield, CancellationToken ct)
            => new BoundedEnrichmentQueryRunner(clock).CollectAsync(context,
                [new("policy-fixture", _ => Task.FromResult(BoundedEnrichmentSourceTests.PolicyPayload(context, scenario, includeValid)))], shouldYield, ct);
    }
    internal sealed class Fixture
    {
        public ManualTimeProvider Clock { get; } = new();
        public ProfileSnapshot Profile { get; }
        public MemoryStore Store { get; }
        public CollectorHealth Health { get; }
        public EnrichmentRunner Runner { get; }
        public DemoEnrichmentSource Source { get; }
        public Fixture()
        {
            Profile = BoundedEnrichmentSourceTests.Context(Clock).Profile;
            Store = new(Clock); Health = new(Clock); Source = new(Clock); Runner = new(Store, Source, Health, Clock);
        }
    }
    // Fault-injecting persistence boundary until the independently implemented SQLite store is integrated.
    internal sealed class MemoryStore(TimeProvider clock) : ICollectionStore, IEnrichmentTargetStore, IEnrichmentHealthStore, IEnrichmentInterruptionStore
    {
        public int Begins, Applies, Interrupts;
        public bool LoseApplyResponse, FailApply, FailInterrupt, FailTargets, LoseBeginResponse, ReportsRunning;
        public EnrichmentApplicationDisposition Disposition = EnrichmentApplicationDisposition.Current;
        public CollectionContext? Context; public EnrichmentBatch? Batch; private EnrichmentCommitReceipt? receipt;
        public Task<CollectionContext> BeginRunAsync(ProfileSnapshot profile, CollectionKind kind, RunTrigger trigger, CancellationToken ct)
        { Begins++; Context = new(100 + Begins, profile, kind, trigger, clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(2)); if (LoseBeginResponse) throw new IOException(); return Task.FromResult(Context); }
        public Task<ImmutableArray<EnrichmentTargetBinding>> GetFrozenEnrichmentTargetsAsync(CollectionContext context, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); if (FailTargets) throw new IOException(); return Task.FromResult(DemoEnrichmentSourceTests.Targets(context)); }
        public Task<EnrichmentCommitReceipt> ApplyEnrichmentBatchAsync(CollectionContext context, EnrichmentBatch batch, CancellationToken ct)
        {
            Applies++; if (FailApply) throw new IOException(); Batch = batch;
            receipt = new(context.Profile.DatasetId, context.Kind, context.RunId, false, true, batch.Outcome, batch.Completeness, batch.Errors) { Disposition = Disposition };
            if (LoseApplyResponse) throw new IOException(); return Task.FromResult(receipt);
        }
        public Task<EnrichmentCommitReceipt> InterruptEnrichmentRunAsync(CollectionContext context, CancellationToken ct)
        {
            Interrupts++; if (FailInterrupt) throw new IOException();
            if (receipt is not null) return Task.FromResult(receipt with { AlreadyApplied = true });
            receipt = new(context.Profile.DatasetId, context.Kind, context.RunId, false, true, RunOutcome.Failed, ObservationCompleteness.Unknown,
                [new(CollectionErrorCategory.Interrupted, 1, "interrupted")]) { Disposition = Disposition }; return Task.FromResult(receipt);
        }
        public Task<EnrichmentKindHealthSnapshot> GetEnrichmentHealthAsync(ProfileSnapshot profile, CollectionKind kind, CancellationToken ct)
        {
            var current = receipt?.Kind == kind && receipt.Disposition == EnrichmentApplicationDisposition.Current;
            var good = current && receipt!.Outcome != RunOutcome.Failed;
            return Task.FromResult(new EnrichmentKindHealthSnapshot(kind, LastAttemptAtUtc: Context?.Kind == kind ? Context.StartedAtUtc : null,
                LastAttemptRunId: Context?.Kind == kind ? Context.RunId : null, LastValidCommittedAtUtc: good ? clock.GetUtcNow() : null,
                LastCompleteCommittedAtUtc: good && receipt!.Outcome == RunOutcome.Succeeded ? clock.GetUtcNow() : null,
                LastFinalRunId: current ? receipt!.RunId : null, LastOutcome: current ? receipt!.Outcome : null,
                Completeness: current ? receipt!.Completeness : null, HasQualityGap: current && receipt!.Outcome != RunOutcome.Succeeded,
                StorageRecoveryPending: ReportsRunning || LoseBeginResponse && receipt is null, LastError: current ? receipt!.Errors.FirstOrDefault()?.Category : null));
        }
        public Task<CommitReceipt> InterruptRunAsync(CollectionContext c, CancellationToken ct) => throw new InvalidOperationException("AP interrupt must not be called");
        public Task<CommitReceipt> ApplyApBatchAsync(CollectionContext c, ApCollectionBatch b, CancellationToken ct) => throw new NotSupportedException();
        public Task<DashboardSnapshot> GetDashboardAsync(DashboardQuery q, CancellationToken ct) => throw new NotSupportedException();
        public Task<CollectionSuccessTimes> GetSuccessTimesAsync(Guid id, long scope, CancellationToken ct) => throw new NotSupportedException();
    }
}
