using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Security;
using AirWaveApMonitor.Infrastructure.Settings;

namespace AirWaveApMonitor.App.Collection;

/// <summary>One active network unit through finalization. State locks never span I/O.</summary>
public sealed class CollectionCoordinator : ICollectorControl, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly ProfileStore profiles;
    private readonly IApSource source;
    private readonly ICollectionStore store;
    private readonly AirWaveSession session;
    private readonly ICredentialStore credentials;
    private readonly CollectorHealth health;
    private readonly TimeProvider clock;
    private readonly IEnrichmentRunner? enrichment;
    private readonly DemoPrehistoryInitializer? demoPrehistory;
    private readonly CollectorDiagnostics diagnostics;
    private readonly CancellationTokenSource lifetime = new();
    private ActiveRun? active;
    private Guid? pendingAp;
    private RunTrigger pendingTrigger;
    private CollectionContext? recovery;
    private bool paused, stopping, disposed, storageUnavailable;
    private long configurationVersion;
    private bool configurationWake;
    private DateTimeOffset nextAp, nextEnrichment, lastTick;

    private sealed class ActiveRun(CollectionKind kind, RunTrigger trigger, CollectionKind[]? following = null, long? plannedVersion = null)
    {
        public CollectionKind Kind { get; } = kind;
        public CollectionKind[] Following { get; set; } = following ?? [];
        public long? PlannedVersion { get; } = plannedVersion;
        public RunTrigger Trigger { get; } = trigger;
        public Guid QueueId { get; } = Guid.NewGuid();
        public CollectionContext? Context { get; set; }
        public long? ReportedRunId { get; set; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public CollectionCoordinator(ProfileStore profiles, IApSource source, ICollectionStore store, AirWaveSession session,
        ICredentialStore credentials, StorageLocation location, CollectorHealth health, TimeProvider clock, IEnrichmentRunner? enrichment = null,
        DemoPrehistoryInitializer? demoPrehistory = null)
    {
        this.profiles = profiles; this.source = source; this.store = store; this.session = session;
        this.credentials = credentials; this.health = health; this.clock = clock; this.enrichment = enrichment;
        this.demoPrehistory = demoPrehistory;
        diagnostics = new(location, clock);
        lastTick = clock.GetUtcNow();
        configurationVersion = profiles.Current?.ConfigurationVersion ?? 0;
        ResetSchedule(lastTick);
        health.Update(s => s with { Phase = CollectorPhase.Idle });
    }

    public async Task StartAsync(CancellationToken ct)
    {
        lock (gate) ResetSchedule(clock.GetUtcNow());
        await RequestAsync(CollectionKind.ApStatus, RunTrigger.Startup, ct);
    }

    public Task<RunRequestReceipt> RequestAsync(CollectionKind kind, RunTrigger trigger, CancellationToken ct)
        => RequestCoreAsync(kind, trigger, [], null, ct);

    private async Task<RunRequestReceipt> RequestCoreAsync(CollectionKind kind, RunTrigger trigger, CollectionKind[] following, long? plannedVersion, CancellationToken ct)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(trigger)) throw new ArgumentException("수집 종류와 시작 사유를 확인하세요.");
        ct.ThrowIfCancellationRequested();
        ActiveRun run;
        lock (gate)
        {
            if (stopping) return Receipt(RunRequestStatus.Stopping);
            if (paused) return Receipt(RunRequestStatus.Paused);
            if (kind != CollectionKind.ApStatus && (profiles.Current is not { } currentProfile || enrichment?.Supports(currentProfile, kind) != true)) return Receipt(RunRequestStatus.Unsupported);
            if (active is { } current)
            {
                if (kind == CollectionKind.ApStatus && current.Kind != CollectionKind.ApStatus)
                {
                    pendingAp ??= Guid.NewGuid(); pendingTrigger = trigger;
                    health.Update(s => s with { PendingApQueueId = pendingAp });
                    return Receipt(RunRequestStatus.Queued, queue: pendingAp);
                }
                if (trigger == RunTrigger.Scheduled)
                {
                    CountSkip(kind); return Receipt(RunRequestStatus.ScheduleSkipped, current.Context?.RunId ?? current.ReportedRunId);
                }
                return Receipt(RunRequestStatus.AlreadyRunning, current.Context?.RunId ?? current.ReportedRunId, current.QueueId);
            }
            active = run = new(kind, trigger, following, plannedVersion);
            health.Update(s => s with { Phase = CollectorPhase.Preparing, ActiveKind = kind, ActiveRunId = null });
        }
        return await AdmitAsync(run, ct);
    }

    private async Task<RunRequestReceipt> AdmitAsync(ActiveRun run, CancellationToken ct)
    {
        var kind = run.Kind;
        var trigger = run.Trigger;
        var ownsOperation = false;
        var launched = false;
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        try
        {
            ownsOperation = await profiles.OperationGate.WaitAsync(0, admission.Token);
            if (!ownsOperation)
            {
                if (trigger == RunTrigger.Scheduled) { lock (gate) CountSkip(kind); return Receipt(RunRequestStatus.ScheduleSkipped); }
                return Receipt(RunRequestStatus.AlreadyRunning);
            }
            lock (gate)
            {
                if (stopping) return Receipt(RunRequestStatus.Stopping);
                if (paused) return Receipt(RunRequestStatus.Paused);
            }
            if (recovery is { } unfinished)
            {
                var recovered = await store.InterruptRunAsync(unfinished, admission.Token);
                await ReflectCommitAsync(unfinished, recovered, admission.Token);
                recovery = null;
            }
            if (enrichment?.RecoveryPending == true && !await enrichment.RecoverAsync(admission.Token))
            {
                MarkStorageFailure(null);
                return Receipt(RunRequestStatus.StorageBlocked);
            }
            var profile = profiles.Current;
            if (profile is null) return Unconfigured("server-not-configured");
            if (run.PlannedVersion is { } version && version != profile.ConfigurationVersion)
                return Receipt(RunRequestStatus.Unsupported);
            if (kind != CollectionKind.ApStatus && enrichment?.Supports(profile, kind) != true)
                return Receipt(RunRequestStatus.Unsupported);
            health.SetInterval(TimeSpan.FromMinutes(profile.Settings.ApIntervalMinutes));
            await profiles.EnsureInitializedAsync(admission.Token);
            if (enrichment is not null) await enrichment.RefreshHealthAsync(profile, admission.Token);
            var success = await store.GetSuccessTimesAsync(profile.DatasetId, profile.ScopeVersion, admission.Token);
            health.Update(s => s with
            {
                LastValidCommittedAtUtc = success.LastValidCommittedAtUtc,
                LastCompleteCommittedAtUtc = success.LastCompleteCommittedAtUtc
            });
            await session.RestoreAuthenticationStateAsync(profile, admission.Token);
            health.Update(s => s with { AuthenticationBlocked = session.AuthenticationBlocked, AuthenticationStop = session.AuthenticationStop });
            if (session.AuthenticationBlocked)
            {
                health.Update(s => s with { BlockedReason = "authentication-rejected" });
                return Receipt(RunRequestStatus.AuthenticationBlocked);
            }
            if (session.CredentialRegistrationPending) return Unconfigured("credential-reregistration-required");
            if (credentials is DpapiCredentialStore && !OperatingSystem.IsWindows()) return Unconfigured("unsupported-platform");
            if (await credentials.ReadAsync(profile.DatasetId, profile.Origin, admission.Token) is null)
                return Unconfigured("credentials-missing");
            storageUnavailable = false;
            health.Update(s => s with
            {
                Configured = true,
                BlockedReason = null,
                StorageRecoveryPending = false,
                LastValidCommittedAtUtc = success.LastValidCommittedAtUtc,
                LastCompleteCommittedAtUtc = success.LastCompleteCommittedAtUtc
            });
            if (kind == CollectionKind.ApStatus)
            {
                while (demoPrehistory is not null && await demoPrehistory.GetNextPositionAsync(profile, admission.Token) is { } seedPosition)
                {
                    lock (gate)
                    {
                        if (stopping) return Receipt(RunRequestStatus.Stopping);
                        if (paused) return Receipt(RunRequestStatus.Paused);
                    }
                    var seed = await BeginApAsync(run, profile, admission.Token);
                    var receipt = await ExecuteApUnitAsync(seed, admission.Token);
                    admission.Token.ThrowIfCancellationRequested();
                    if (receipt is null) return Receipt(RunRequestStatus.StorageBlocked, seed.RunId);
                    // A failed/interrupted seed is a real final run. Stop this admission;
                    // the next request retries the same uncommitted checkpoint, never a busy loop.
                    if (receipt.Outcome == RunOutcome.Failed) return Receipt(RunRequestStatus.Accepted, seed.RunId);
                    if (await demoPrehistory.GetNextPositionAsync(profile, admission.Token) == seedPosition)
                        throw new InvalidOperationException("Demo 선행 관측의 저장된 진행 위치가 전진하지 않았습니다.");
                    lock (gate) run.Context = null;
                    health.Update(s => s with { Phase = CollectorPhase.Preparing, ActiveRunId = null });
                }
                lock (gate)
                {
                    if (stopping) return Receipt(RunRequestStatus.Stopping);
                    if (paused) return Receipt(RunRequestStatus.Paused);
                }
                var context = await BeginApAsync(run, profile, admission.Token);
                launched = true;
                _ = Task.Run(() => ExecuteApAsync(run, context), CancellationToken.None);
                return Receipt(RunRequestStatus.Accepted, context.RunId);
            }
            health.Update(s => s with { Phase = CollectorPhase.Preparing });
            launched = true;
            _ = Task.Run(() => ExecuteEnrichmentAsync(run, profile), CancellationToken.None);
            return Receipt(RunRequestStatus.Queued, queue: run.QueueId);
        }
        catch (CredentialUnavailableException) { return Unconfigured("credential-reregistration-required"); }
        catch (PlatformNotSupportedException) { return Unconfigured("unsupported-platform"); }
        catch (OperationCanceledException) when (admission.IsCancellationRequested) { throw; }
        catch (Exception error) when (IsStorageFailure(error))
        {
            MarkStorageFailure(run.Context);
            return Receipt(RunRequestStatus.StorageBlocked, run.Context?.RunId);
        }
        finally
        {
            if (!launched)
            {
                run.Following = [];
                if (ownsOperation) profiles.OperationGate.Release();
                Finish(run);
            }
        }
    }

    private async Task<CollectionContext> BeginApAsync(ActiveRun run, ProfileSnapshot profile, CancellationToken ct)
    {
        health.Update(s => s with { LastAttemptAtUtc = clock.GetUtcNow(), LastAttemptRunId = null });
        var context = await store.BeginRunAsync(profile, CollectionKind.ApStatus, run.Trigger, ct);
        lock (gate) run.Context = context;
        health.Update(s => s with { LastAttemptRunId = context.RunId, ActiveRunId = context.RunId, Phase = CollectorPhase.Collecting });
        return context;
    }

    private async Task ExecuteApAsync(ActiveRun run, CollectionContext context)
    {
        try { await ExecuteApUnitAsync(context, lifetime.Token); }
        finally
        {
            profiles.OperationGate.Release();
            Finish(run);
        }
    }

    private async Task<CommitReceipt?> ExecuteApUnitAsync(CollectionContext context, CancellationToken ownerToken)
    {
        CommitReceipt? result = null;
        ApCollectionBatch? batch = null;
        var remaining = context.DeadlineUtc - clock.GetUtcNow();
        using var budget = new CancellationTokenSource(TimeSpan.FromTicks(Math.Clamp(remaining.Ticks, 0, context.Profile.Settings.RunTimeout.Ticks)), clock);
        if (remaining <= TimeSpan.Zero) budget.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ownerToken, budget.Token);
        try { batch = await source.CollectAsync(context, request.Token); }
        catch (OperationCanceledException) when (ownerToken.IsCancellationRequested) { }
        catch (OperationCanceledException) { batch = Failure(CollectionErrorCategory.Timeout); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            batch = Failure(error is IOException or HttpRequestException ? CollectionErrorCategory.Transport : CollectionErrorCategory.InvalidResponse);
        }
        health.Update(s => s with { Phase = CollectorPhase.Finalizing });
        using var finalization = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (ownerToken.IsCancellationRequested) batch = null;
        else if (budget.IsCancellationRequested) batch = Failure(CollectionErrorCategory.Timeout);
        try
        {
            var receipt = batch is null
                ? await store.InterruptRunAsync(context, finalization.Token)
                : await store.ApplyApBatchAsync(context, batch, request.IsCancellationRequested ? finalization.Token : request.Token);
            result = receipt;
            await ReflectCommitAsync(context, receipt, finalization.Token);
            ReflectCollectionError(receipt, batch, batch is null);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The first call may have committed before losing its response. Resolve the
            // same context; never manufacture a second failure over an existing result.
            using var resolution = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var receipt = await store.InterruptRunAsync(context, resolution.Token);
                result = receipt;
                await ReflectCommitAsync(context, receipt, resolution.Token);
                ReflectCollectionError(receipt, batch, !receipt.AlreadyApplied);
                if (!receipt.AlreadyApplied && IsStorageFailure(error)) RecordResolvedWriteFailure(context);
            }
            catch (Exception resolutionError) when (resolutionError is not OutOfMemoryException) { result = null; MarkStorageFailure(context); }
        }
        health.Update(s => s with
        {
            AuthenticationBlocked = session.AuthenticationBlocked,
            AuthenticationStop = session.AuthenticationStop,
            BlockedReason = session.AuthenticationBlocked ? "authentication-rejected" : s.BlockedReason
        });
        return result;
    }

    private async Task ExecuteEnrichmentAsync(ActiveRun run, ProfileSnapshot profile)
    {
        try
        {
            await enrichment!.ExecuteAsync(profile, run.Kind, run.Trigger,
                () => { lock (gate) return pendingAp is not null || paused || stopping; },
                id => { lock (gate) run.ReportedRunId = id; health.Update(s => s with { ActiveRunId = id, Phase = CollectorPhase.Collecting }); }, lifetime.Token);
            if (enrichment.RecoveryPending) MarkStorageFailure(null);
            health.Update(s => s with
            {
                AuthenticationBlocked = session.AuthenticationBlocked,
                AuthenticationStop = session.AuthenticationStop,
                BlockedReason = session.AuthenticationBlocked ? "authentication-rejected" : s.BlockedReason
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (enrichment?.RecoveryPending == true) MarkStorageFailure(null);
            if (!diagnostics.Record(CollectionErrorCategory.Transport, run.ReportedRunId))
                health.Update(s => s with { LastDiagnosticFileError = "diagnostic-file-unavailable" });
        }
        finally { profiles.OperationGate.Release(); Finish(run); }
    }

    private async Task ReflectCommitAsync(CollectionContext context, CommitReceipt receipt, CancellationToken ct)
    {
        var success = await store.GetSuccessTimesAsync(context.Profile.DatasetId, context.Profile.ScopeVersion, ct);
        storageUnavailable = false;
        recovery = null;
        health.Update(s => s with
        {
            LastValidCommittedAtUtc = success.LastValidCommittedAtUtc,
            LastCompleteCommittedAtUtc = success.LastCompleteCommittedAtUtc,
            StorageRecoveryPending = false,
            LastStorageError = receipt.Outcome == RunOutcome.Failed ? s.LastStorageError : null,
            LastFinalRunId = context.RunId,
            LastOutcome = receipt.Outcome
        });
    }

    private void ReflectCollectionError(CommitReceipt receipt, ApCollectionBatch? batch, bool interrupted)
        => health.Update(s => s with
        {
            LastCollectionError = receipt.Outcome == RunOutcome.Succeeded ? null : interrupted
            ? CollectionErrorCategory.Interrupted : batch?.Errors.FirstOrDefault()?.Category
        });

    private void MarkStorageFailure(CollectionContext? context)
    {
        recovery = context ?? recovery;
        storageUnavailable = true;
        var fileOk = diagnostics.Record(CollectionErrorCategory.Storage, context?.RunId);
        health.Update(s => s with
        {
            StorageRecoveryPending = true,
            LastStorageError = "storage-unavailable",
            LastDiagnosticFileError = fileOk ? s.LastDiagnosticFileError : "diagnostic-file-unavailable",
            BlockedReason = "storage-recovery"
        });
    }

    private void RecordResolvedWriteFailure(CollectionContext context)
    {
        var fileOk = diagnostics.Record(CollectionErrorCategory.Storage, context.RunId);
        health.Update(s => s with
        {
            LastStorageError = "storage-write-failed",
            LastCollectionError = CollectionErrorCategory.Storage,
            LastDiagnosticFileError = fileOk ? s.LastDiagnosticFileError : "diagnostic-file-unavailable"
        });
    }

    private void Finish(ActiveRun run)
    {
        ActiveRun? next = null;
        lock (gate)
        {
            if (ReferenceEquals(active, run)) active = null;
            if (pendingAp is not null && !paused && !stopping)
                active = next = new(CollectionKind.ApStatus, pendingTrigger);
            else if (!paused && !stopping && !storageUnavailable && !session.AuthenticationBlocked &&
                run.Following.Length > 0 && profiles.Current is { } profile && run.PlannedVersion == profile.ConfigurationVersion &&
                enrichment?.Supports(profile, run.Following[0]) == true)
                active = next = new(run.Following[0], RunTrigger.Scheduled, run.Following.Skip(1).ToArray(), run.PlannedVersion);
            // A pending AP preempts and discards the rest of this one scheduled enrichment cycle.
            pendingAp = null;
            health.Update(s => s with
            {
                Phase = stopping ? CollectorPhase.Stopped : next is not null ? CollectorPhase.Preparing
                : storageUnavailable ? CollectorPhase.StorageRecovery : CollectorPhase.Idle,
                ActiveKind = next?.Kind,
                ActiveRunId = null,
                PendingApQueueId = null
            });
        }
        run.Completion.TrySetResult();
        // Reserve the AP or next item of this bounded cycle while releasing the old slot.
        // No competing request can interleave between priority selection and admission.
        if (next is not null) _ = RunPendingAsync(next);
    }

    private async Task RunPendingAsync(ActiveRun run)
    {
        try { await AdmitAsync(run, lifetime.Token); }
        catch (OperationCanceledException) { }
    }

    public async Task SetPausedAsync(bool value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool resume;
        lock (gate)
        {
            resume = paused && !value;
            paused = value;
            if (value) { pendingAp = null; if (active is not null) active.Following = []; }
            if (resume) ResetSchedule(clock.GetUtcNow());
            health.Update(s => s with { Paused = value, PendingApQueueId = pendingAp });
        }
        if (resume) await RequestAsync(CollectionKind.ApStatus, RunTrigger.Resume, ct);
    }

    public async Task TickAsync(CancellationToken ct)
    {
        bool apDue;
        CollectionKind[] cycle;
        RunTrigger trigger;
        long? plannedVersion;
        lock (gate)
        {
            if (stopping) return;
            var now = clock.GetUtcNow();
            if (now < lastTick)
            {
                health.Update(s => s with { ClockNeedsReview = true }); ResetSchedule(now);
            }
            lastTick = now;
            var profile = profiles.Current;
            plannedVersion = profile?.ConfigurationVersion;
            if ((profile?.ConfigurationVersion ?? 0) != configurationVersion)
            {
                configurationVersion = profile?.ConfigurationVersion ?? 0;
                configurationWake = true; ResetSchedule(now);
            }
            var retryCleared = health.GetSnapshot().AuthenticationBlocked && !session.AuthenticationBlocked;
            health.Update(s => s with
            {
                AuthenticationBlocked = session.AuthenticationBlocked,
                AuthenticationStop = session.AuthenticationStop,
                BlockedReason = retryCleared && s.BlockedReason == "authentication-rejected" ? null : s.BlockedReason
            });
            apDue = now >= nextAp || configurationWake || retryCleared;
            trigger = configurationWake ? RunTrigger.Startup : retryCleared ? RunTrigger.Resume : RunTrigger.Scheduled;
            if (now >= nextAp) nextAp = now + TimeSpan.FromMinutes(profile?.Settings.ApIntervalMinutes ?? 5);
            var enrichmentDue = now >= nextEnrichment;
            cycle = enrichmentDue && profile is not null
                ? new[] { CollectionKind.ConnectionNeighbor, CollectionKind.Power }.Where(kind => KindEnabled(profile, kind) && enrichment?.Supports(profile, kind) == true).ToArray() : [];
            if (enrichmentDue) nextEnrichment = now + TimeSpan.FromMinutes(profile?.Settings.EnrichmentIntervalMinutes ?? 15);
            PublishSchedule(profile);
        }
        if (apDue)
        {
            // Same-tick AP has priority, then at most one Connection/Power cycle. This avoids
            // permanent starvation when the 5-minute AP and 15-minute enrichment clocks coincide.
            var receipt = await RequestCoreAsync(CollectionKind.ApStatus, trigger, cycle, plannedVersion, ct);
            if (receipt.Status != RunRequestStatus.AlreadyRunning) { lock (gate) configurationWake = false; }
            if (receipt.Status is not RunRequestStatus.Accepted && cycle.Length > 0) { lock (gate) foreach (var kind in cycle) CountSkip(kind); }
        }
        else if (cycle.Length > 0)
            await RequestCoreAsync(cycle[0], RunTrigger.Scheduled, cycle.Skip(1).ToArray(), plannedVersion, ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Task? pending;
        lock (gate)
        {
            stopping = true; pendingAp = null; if (active is not null) active.Following = []; pending = active?.Completion.Task;
            health.Update(s => s with { Phase = CollectorPhase.Stopping, PendingApQueueId = null });
        }
        try { lifetime.Cancel(); }
        catch (ObjectDisposedException) { /* A completed concurrent disposal has already stopped this coordinator. */ }
        if (pending is not null) await pending.WaitAsync(ct);
        health.Update(s => s with { Phase = CollectorPhase.Stopped });
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await StopAsync(CancellationToken.None);
        lifetime.Dispose(); disposed = true;
    }

    private void ResetSchedule(DateTimeOffset now)
    {
        var settings = profiles.Current?.Settings ?? new CollectionSettings();
        nextAp = now + TimeSpan.FromMinutes(settings.ApIntervalMinutes);
        nextEnrichment = now + TimeSpan.FromMinutes(settings.EnrichmentIntervalMinutes);
        health.SetInterval(TimeSpan.FromMinutes(settings.ApIntervalMinutes));
        PublishSchedule(profiles.Current);
    }
    private void PublishSchedule(ProfileSnapshot? profile)
    {
        var runnable = profile is not null && new[] { CollectionKind.ConnectionNeighbor, CollectionKind.Power }
            .Any(kind => KindEnabled(profile, kind) && enrichment?.Supports(profile, kind) == true);
        health.Update(s => s with { NextApAtUtc = nextAp, NextEnrichmentAtUtc = runnable ? nextEnrichment : null });
        enrichment?.UpdateSchedule(profile, nextEnrichment);
    }
    private static bool KindEnabled(ProfileSnapshot profile, CollectionKind kind)
        => kind == CollectionKind.ConnectionNeighbor ? profile.Settings.ConnectionCollectionEnabled : profile.Settings.PowerCollectionEnabled;
    private RunRequestReceipt Unconfigured(string reason)
    {
        health.Update(s => s with { Configured = false, BlockedReason = reason });
        return Receipt(RunRequestStatus.Unconfigured);
    }
    private void CountSkip(CollectionKind kind)
        => health.Update(s => kind == CollectionKind.ApStatus ? s with { SkippedApTicks = s.SkippedApTicks + 1 } : s with { SkippedEnrichmentTicks = s.SkippedEnrichmentTicks + 1 });
    private static bool IsStorageFailure(Exception error)
        => error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException;
    private ApCollectionBatch Failure(CollectionErrorCategory category)
        => new(RunOutcome.Failed, [], [], clock.GetUtcNow(), ObservationCompleteness.Unknown, [new(category, 1, SafeMessages.For(category))]);
    private static RunRequestReceipt Receipt(RunRequestStatus status, long? run = null, Guid? queue = null)
        => new(status, run, queue, status.ToString());
}
