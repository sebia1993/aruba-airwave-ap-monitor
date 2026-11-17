using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
namespace AirWaveApMonitor.App.Collection;

/// <summary>Called only while the coordinator owns ProfileOperationGate and its sole collection slot.
/// A failed finalization retains its original context; recovery never repeats source requests.</summary>
public sealed class EnrichmentRunner : IEnrichmentRunner
{
    private readonly ICollectionStore store;
    private readonly IEnrichmentInterruptionStore interrupts;
    private readonly IEnrichmentTargetStore targets;
    private readonly IEnrichmentHealthStore durableHealth;
    private readonly IBoundedEnrichmentSource source;
    private readonly CollectorHealth health;
    private readonly TimeProvider clock;
    private CollectionContext? recovery;
    private ProfileSnapshot? uncertainBegin;
    private CollectionKind uncertainKind;
    private volatile bool recoveryPending;
    private DateTimeOffset? nextAt;
    public EnrichmentRunner(ICollectionStore store, IBoundedEnrichmentSource source, CollectorHealth health, TimeProvider clock)
    {
        this.store = store; this.source = source; this.health = health; this.clock = clock;
        interrupts = store as IEnrichmentInterruptionStore ?? throw new ArgumentException("종류별 중단 저장 계약이 필요합니다.", nameof(store));
        targets = store as IEnrichmentTargetStore ?? throw new ArgumentException("시작 시점 대상 저장 계약이 필요합니다.", nameof(store));
        durableHealth = store as IEnrichmentHealthStore ?? throw new ArgumentException("종류별 건강 상태 저장 계약이 필요합니다.", nameof(store));
    }
    public bool Supports(CollectionKind kind) => kind is CollectionKind.ConnectionNeighbor or CollectionKind.Power;
    public bool Supports(ProfileSnapshot profile, CollectionKind kind) => Supports(kind) && source.Capability(profile, kind).CanCollect;
    public bool RecoveryPending => recoveryPending;

    public async Task<bool> RecoverAsync(CancellationToken ct)
    {
        if (!recoveryPending) return true;
        try
        {
            if (recovery is { } context)
            {
                var receipt = await interrupts.InterruptEnrichmentRunAsync(context, ct);
                await ReflectAsync(context, receipt, ct);
            }
            else if (uncertainBegin is { } profile)
            {
                // Begin may have failed before returning a context. A durable Running result
                // must stay blocked; only startup recovery can reconcile an unknown RunId.
                var status = await durableHealth.GetEnrichmentHealthAsync(profile, uncertainKind, ct);
                if (status.StorageRecoveryPending) return false;
                health.UpdateKind(uncertainKind, old => PreserveSchedule(profile, status, old.NextAtUtc));
            }
            recoveryPending = false; recovery = null; uncertainBegin = null;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }
    public async Task RefreshHealthAsync(ProfileSnapshot profile, CancellationToken ct)
    {
        foreach (var kind in new[] { CollectionKind.ConnectionNeighbor, CollectionKind.Power })
        {
            var status = await durableHealth.GetEnrichmentHealthAsync(profile, kind, ct);
            health.UpdateKind(kind, old => PreserveSchedule(profile, status, old.NextAtUtc));
        }
    }
    public void UpdateSchedule(ProfileSnapshot? profile, DateTimeOffset nextAt)
    {
        this.nextAt = nextAt;
        foreach (var kind in new[] { CollectionKind.ConnectionNeighbor, CollectionKind.Power })
            health.UpdateKind(kind, old => profile is null ? old with { Enabled = false, CanCollect = false, NextAtUtc = null, CapabilityReason = "Unconfigured" }
                : PreserveSchedule(profile, old, nextAt));
    }
    private EnrichmentKindHealthSnapshot PreserveSchedule(ProfileSnapshot profile, EnrichmentKindHealthSnapshot status, DateTimeOffset? due)
    {
        var capability = source.Capability(profile, status.Kind);
        var enabled = status.Kind == CollectionKind.ConnectionNeighbor ? profile.Settings.ConnectionCollectionEnabled : profile.Settings.PowerCollectionEnabled;
        return status with { Enabled = enabled, CanCollect = capability.CanCollect, NextAtUtc = capability.CanCollect ? due : null, CapabilityReason = capability.Reason.ToString() };
    }
    public Task ExecuteAsync(ProfileSnapshot profile, CollectionKind kind, Func<bool> shouldYield, Action<long> started, CancellationToken ct)
        => ExecuteAsync(profile, kind, RunTrigger.Scheduled, shouldYield, started, ct);

    public async Task ExecuteAsync(ProfileSnapshot profile, CollectionKind kind, RunTrigger trigger, Func<bool> shouldYield, Action<long> started, CancellationToken ct)
    {
        if (!Supports(profile, kind)) return;
        if (recoveryPending && !await RecoverAsync(ct)) return;
        CollectionContext context;
        health.UpdateKind(kind, old => PreserveSchedule(profile, old with { LastAttemptAtUtc = clock.GetUtcNow(), LastAttemptRunId = null }, nextAt));
        try
        {
            context = await store.BeginRunAsync(profile, kind, trigger, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            uncertainBegin = profile; uncertainKind = kind; MarkPending(null, kind); return;
        }
        health.UpdateKind(kind, old => old with { LastAttemptAtUtc = context.StartedAtUtc, LastAttemptRunId = context.RunId });
        started(context.RunId);
        EnrichmentBatch? batch = null;
        try
        {
            var frozen = await targets.GetFrozenEnrichmentTargetsAsync(context, ct);
            try { batch = await source.CollectAsync(context, frozen, shouldYield, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (OperationCanceledException) { batch = Failure(context, CollectionErrorCategory.Timeout); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { batch = Failure(context, error is IOException or HttpRequestException ? CollectionErrorCategory.Transport : CollectionErrorCategory.InvalidResponse); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // No source request has started if reading the frozen binding failed.
            batch = null;
        }
        health.Update(s => s with { Phase = CollectorPhase.Finalizing });
        using var finalization = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (ct.IsCancellationRequested) batch = null;
        try
        {
            var receipt = batch is null ? await interrupts.InterruptEnrichmentRunAsync(context, finalization.Token)
                : await store.ApplyEnrichmentBatchAsync(context, batch, finalization.Token);
            await ReflectAsync(context, receipt, finalization.Token);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            using var resolution = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var receipt = await interrupts.InterruptEnrichmentRunAsync(context, resolution.Token);
                await ReflectAsync(context, receipt, resolution.Token);
            }
            catch (Exception resolutionError) when (resolutionError is not OutOfMemoryException) { MarkPending(context, kind); }
        }
    }
    private async Task ReflectAsync(CollectionContext context, EnrichmentCommitReceipt receipt, CancellationToken ct)
    {
        if (receipt.RunId != context.RunId || receipt.Kind != context.Kind || receipt.DatasetId != context.Profile.DatasetId || receipt.Disposition is null)
            throw new InvalidOperationException("부가 수집 확정 응답의 실행 바인딩을 확인할 수 없습니다.");
        // The durable getter owns clocks and current-vs-historical filtering; never invent commit time or update AP clocks.
        var status = await durableHealth.GetEnrichmentHealthAsync(context.Profile, context.Kind, ct);
        if (status.StorageRecoveryPending)
            throw new InvalidOperationException("부가 수집 실행의 최종 확정을 확인할 수 없습니다.");
        health.UpdateKind(context.Kind, old => PreserveSchedule(context.Profile, status with { StorageRecoveryPending = false }, old.NextAtUtc));
        recoveryPending = false; recovery = null; uncertainBegin = null;
    }
    private void MarkPending(CollectionContext? context, CollectionKind kind)
    {
        recovery = context; recoveryPending = true;
        health.UpdateKind(kind, old => old with { StorageRecoveryPending = true, HasQualityGap = true, LastError = CollectionErrorCategory.Storage });
    }
    private EnrichmentBatch Failure(CollectionContext context, CollectionErrorCategory category)
        => new(context.Kind, RunOutcome.Failed, [], [], ObservationCompleteness.Unknown, clock.GetUtcNow(), [new(category, 1, SafeMessages.For(category))]);
}
