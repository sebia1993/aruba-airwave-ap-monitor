using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Monitoring;
using static AirWaveApMonitor.Tests.Monitoring.MonitoringFixture;

namespace AirWaveApMonitor.Tests.Monitoring;

public sealed class StateTransitionEngineTests
{
    private readonly StateTransitionEngine engine = new();
    private static readonly TransitionIdentifiers NewIds = new(
        Guid.Parse("33333333-3333-3333-3333-333333333332"),
        Guid.Parse("44444444-4444-4444-4444-444444444442"),
        Guid.Parse("55555555-5555-5555-5555-555555555552"));

    [Theory]
    [InlineData(null, ApState.Up, ObservationClassification.Up, 0)]
    [InlineData(null, ApState.Down, ObservationClassification.InitialDown, 1)]
    [InlineData(ApState.Up, ApState.Down, ObservationClassification.NewDown, 1)]
    [InlineData(ApState.Down, ApState.Down, ObservationClassification.ExistingDown, 1)]
    [InlineData(ApState.Down, ApState.Up, ObservationClassification.Recovered, 0)]
    [InlineData(ApState.Up, ApState.Up, ObservationClassification.Up, 0)]
    [InlineData(null, null, ObservationClassification.Unknown, 0)]
    [InlineData(ApState.Up, null, ObservationClassification.Unknown, 0)]
    [InlineData(ApState.Down, null, ObservationClassification.Unknown, 1)]
    public void StateTable_UsesPreviousValidState(ApState? previousState, ApState? current,
        ObservationClassification classification, int open)
    {
        var previous = Previous(previousState);
        var result = engine.Evaluate(previous, Observation(current), Context());
        Assert.Equal(classification, result.Classification);
        Assert.Equal(open, result.Next.OpenIncident is null ? 0 : 1);
        Assert.Equal(current ?? previousState, result.Next.LastValidState);
        Assert.Equal(current is null ? previous.LastValidObservedAtUtc : At(101), result.Next.LastValidObservedAtUtc);
        Assert.Equal(current is null, result.Next.OpenGap is not null);
        Assert.Equal(current, result.CurrentState);
    }

    [Fact]
    public void Up_Down_Down_Up_CreatesOneIncidentAndOneRecovery()
    {
        var first = engine.Evaluate(null, Observation(ApState.Up, 100), Context(100), NewIds);
        var down = engine.Evaluate(first.Next, Observation(ApState.Down, 101), Context(101), NewIds);
        var stillDown = engine.Evaluate(down.Next, Observation(ApState.Down, 102), Context(102));
        var recovered = engine.Evaluate(stillDown.Next, Observation(ApState.Up, 103), Context(103));
        var all = new[] { first, down, stillDown, recovered };
        Assert.Single(all.SelectMany(item => item.Incidents), item => item.Kind == IncidentMutationKind.Created);
        var recovery = Assert.Single(all.SelectMany(item => item.Incidents), item => item.Kind == IncidentMutationKind.Recovered);
        Assert.Equal(NewIds.NewIncidentId, recovery.Incident.IncidentId);
        Assert.Equal(At(101), recovery.Incident.FirstDownAtUtc);
        Assert.Equal(At(102), recovery.Incident.LastDownAtUtc);
        Assert.Equal(At(103), recovery.Incident.RecoveredAtUtc);
        Assert.Null(recovered.Next.OpenIncident);
    }

    [Fact]
    public void FirstDown_HasUnknownActualStart()
    {
        var down = engine.Evaluate(null, Observation(ApState.Down, 100), Context(100), NewIds);
        var stillDown = engine.Evaluate(down.Next, Observation(ApState.Down, 101), Context(101));
        var up = engine.Evaluate(stillDown.Next, Observation(ApState.Up, 102), Context(102));
        var stillUp = engine.Evaluate(up.Next, Observation(ApState.Up, 103), Context(103));
        Assert.Equal(ObservationClassification.InitialDown, down.Classification);
        Assert.True(down.Next.OpenIncident!.ActualStartUnknown);
        Assert.True(stillDown.Next.OpenIncident!.IsInitialDown);
        Assert.Null(down.Next.OpenIncident.LastUpAtUtc);
        Assert.Equal(ObservationClassification.Recovered, up.Classification);
        Assert.Equal(ObservationClassification.Up, stillUp.Classification);
        Assert.Empty(stillUp.Incidents);
    }

    [Theory]
    [InlineData(ApState.Up, ObservationClassification.Up)]
    [InlineData(ApState.Down, ObservationClassification.InitialDown)]
    [InlineData(null, ObservationClassification.Unknown)]
    public void HardwareReplacement_PrecedesRecovery(ApState? state, ObservationClassification expected)
    {
        var result = engine.Evaluate(Previous(), Observation(state, serial: "SERIAL-B"), Context(), NewIds);
        Assert.Equal(expected, result.Classification);
        Assert.Equal(NewIds.NewGenerationId, result.Next.Identity.DeviceGenerationId);
        Assert.Equal(NewIds.NewMonitoringIntervalId, result.Next.MonitoringIntervalId);
        var ended = Assert.Single(result.Incidents, item => item.Kind == IncidentMutationKind.TrackingEnded);
        Assert.Equal(Incident, ended.Incident.IncidentId);
        Assert.Null(ended.Incident.RecoveredAtUtc);
        Assert.Equal(At(101), ended.Incident.TrackingEndedAtUtc);
        Assert.NotEmpty(ended.Incident.TrackingEndReason!);
        Assert.DoesNotContain(result.Incidents, item => item.Kind == IncidentMutationKind.Recovered);
        Assert.True(result.InvalidatesEnrichment);
        Assert.True(result.Identity!.ReplacementNeedsConfirmation);
        if (state is null)
        {
            var later = engine.Evaluate(result.Next, Observation(ApState.Down, 102, serial: "SERIAL-B"), Context(102));
            Assert.Equal(ObservationClassification.InitialDown, later.Classification);
        }
    }

    [Theory]
    [InlineData(MissingObservationReason.MissingRecord)]
    [InlineData(MissingObservationReason.UnknownState)]
    [InlineData(MissingObservationReason.DuplicateIdentity)]
    [InlineData(MissingObservationReason.CollectionFailed)]
    [InlineData(MissingObservationReason.Interrupted)]
    public void ObservationGap_PreservesIncident(MissingObservationReason reason)
    {
        var previous = Previous();
        var gap = engine.EvaluateUnobserved(previous, new("100", reason, At(101)), Context(101));
        var repeatedGap = engine.EvaluateUnobserved(gap.Next,
            new("100", MissingObservationReason.CollectionFailed, At(102)), Context(102));
        var resumed = engine.Evaluate(repeatedGap.Next, Observation(ApState.Down, 103), Context(103));
        Assert.Equal(ObservationClassification.Unknown, gap.Classification);
        Assert.False(gap.AllowsAutomaticMetadataChanges);
        Assert.Equal(previous.Identity, gap.Next.Identity);
        Assert.Equal(T0, gap.Next.LastValidObservedAtUtc);
        Assert.Equal(Incident, resumed.Next.OpenIncident!.IncidentId);
        Assert.Equal(T0, resumed.Next.OpenIncident.FirstDownAtUtc);
        Assert.Equal(At(103), resumed.Next.OpenIncident.LastDownAtUtc);
        Assert.True(resumed.Next.OpenIncident.HasObservationGap);
        Assert.Equal(At(101), resumed.ClosedGap!.StartedAtUtc);
        Assert.Equal(Incident, resumed.ClosedGap.IncidentId);
        Assert.Equal(reason, resumed.ClosedGap.Reason);
        Assert.Null(resumed.Next.OpenGap);
    }

    [Theory]
    [InlineData(ApState.Up, ApState.Down, ObservationClassification.NewDown)]
    [InlineData(ApState.Down, ApState.Up, ObservationClassification.Recovered)]
    [InlineData(null, ApState.Down, ObservationClassification.InitialDown)]
    public void Gap_UsesFirstResumedValidObservation(ApState? before, ApState current,
        ObservationClassification expected)
    {
        var gap = engine.EvaluateUnobserved(Previous(before),
            new("100", MissingObservationReason.CollectionFailed, At(101)), Context(101));
        var result = engine.Evaluate(gap.Next, Observation(current, 102), Context(102));
        Assert.Equal(expected, result.Classification);
        var incident = Assert.Single(result.Incidents).Incident;
        Assert.True(incident.HasObservationGap);
        if (current == ApState.Up)
        {
            Assert.Equal(At(102), incident.RecoveredAtUtc);
            Assert.True(incident.ActualRecoveryUnknown);
        }
        else
        {
            Assert.Equal(At(102), incident.FirstDownAtUtc);
            Assert.Equal(before is null ? null : T0, incident.LastUpAtUtc);
        }
    }

    [Theory]
    [InlineData(ApState.Down, ObservationClassification.ExistingDown)]
    [InlineData(ApState.Up, ObservationClassification.Recovered)]
    public void ScopePauseAndReentry_ContinuesIncident(ApState state, ObservationClassification expected)
    {
        var paused = engine.ChangeMonitoring(Previous(), MonitoringDirective.TemporarilyExclude,
            "scope changed", At(101), Context(101));
        Assert.False(paused.IsMonitoringTarget);
        Assert.Equal(Incident, paused.Next.OpenIncident!.IncidentId);
        Assert.DoesNotContain(paused.Incidents, item => item.Kind == IncidentMutationKind.Recovered);
        var result = engine.Evaluate(paused.Next, Observation(state, 102), Context(102));
        Assert.Equal(expected, result.Classification);
        Assert.Equal(Generation, result.Next.Identity.DeviceGenerationId);
        Assert.Equal(Interval, result.Next.MonitoringIntervalId);
        Assert.True(Assert.Single(result.Incidents).Incident.HasObservationGap);
    }

    [Theory]
    [InlineData(ApState.Down, ObservationClassification.InitialDown)]
    [InlineData(ApState.Up, ObservationClassification.Up)]
    public void ExplicitEndAndReentry_StartsNewSegment(ApState state, ObservationClassification expected)
    {
        var ended = engine.ChangeMonitoring(Previous(), MonitoringDirective.EndTracking,
            "operator ended tracking", At(101), Context(101));
        var terminated = Assert.Single(ended.Incidents).Incident;
        Assert.Null(terminated.RecoveredAtUtc);
        Assert.Equal("operator ended tracking", terminated.TrackingEndReason);
        Assert.Null(ended.Next.OpenIncident);
        var result = engine.Evaluate(ended.Next, Observation(state, 102), Context(102), NewIds);
        Assert.Equal(expected, result.Classification);
        Assert.Equal(Generation, result.Next.Identity.DeviceGenerationId);
        Assert.Equal(NewIds.NewMonitoringIntervalId, result.Next.MonitoringIntervalId);
        Assert.DoesNotContain(result.Incidents, item => item.Kind == IncidentMutationKind.Recovered);
    }

    [Fact]
    public void PausedHardwareReplacement_PrecedesReentry()
    {
        var paused = engine.ChangeMonitoring(Previous(), MonitoringDirective.TemporarilyExclude,
            "scope changed", At(101), Context(101));
        var result = engine.Evaluate(paused.Next, Observation(ApState.Up, 102, serial: "SERIAL-B"), Context(102), NewIds);
        Assert.Equal(ObservationClassification.Up, result.Classification);
        Assert.Equal(NewIds.NewGenerationId, result.Next.Identity.DeviceGenerationId);
        Assert.Equal(IncidentMutationKind.TrackingEnded, Assert.Single(result.Incidents).Kind);
    }

    [Fact]
    public void ClockRollback_DoesNotProduceNegativeDuration()
    {
        var down = engine.Evaluate(Previous(ApState.Up), Observation(ApState.Down), Context());
        var rollback = engine.Evaluate(down.Next,
            Observation(ApState.Down, 102) with { CollectedAtUtc = T0.AddMinutes(3) }, Context(102));
        var up = engine.Evaluate(rollback.Next,
            Observation(ApState.Up, 103) with { CollectedAtUtc = T0.AddMinutes(4) }, Context(103));
        var incident = Assert.Single(up.Incidents).Incident;
        Assert.Equal(At(101), incident.FirstDownAtUtc);
        Assert.Equal(T0.AddMinutes(3), incident.LastDownAtUtc);
        Assert.Equal(T0.AddMinutes(4), incident.RecoveredAtUtc);
        Assert.Equal(102, incident.LastDownRunId);
        Assert.Equal(103, incident.RecoveredRunId);
        Assert.True(incident.TimeNeedsReview);
        Assert.True(up.Next.TimeNeedsReview);
        Assert.Null(incident.ConfirmedElapsed);
    }

    [Fact]
    public void SourceTimes_DoNotReplaceCollectedTime()
    {
        var old = T0.AddDays(-1);
        var result = engine.Evaluate(Previous(ApState.Up),
            Observation(ApState.Down) with { SourceAtUtc = old, LastContactedAtUtc = old }, Context());
        Assert.Equal(At(101), result.Next.OpenIncident!.FirstDownAtUtc);
        Assert.Equal(old, result.AcceptedObservation!.LastContactedAtUtc);
        Assert.Equal(old, result.AcceptedObservation.SourceAtUtc);
    }

    [Fact]
    public void InvalidStateEvidence_CannotDriveTransition()
    {
        var current = Observation(ApState.Up);
        current = current with { Fields = current.Fields.SetItem(MonitoringFieldNames.State,
            current.Fields[MonitoringFieldNames.State] with { Validity = FieldValidity.Invalid }) };
        var result = engine.Evaluate(Previous(), current, Context());
        Assert.Equal(ObservationClassification.Unknown, result.Classification);
        Assert.Equal(ApState.Down, result.Next.LastValidState);
        Assert.Equal(Incident, result.Next.OpenIncident!.IncidentId);
    }

    [Fact]
    public void UnsupportedStateEnum_CannotRecoverAnOpenIncidentEvenWithValidEvidence()
    {
        var result = engine.Evaluate(Previous(), Observation((ApState)2), Context());
        Assert.Equal(ObservationClassification.Unknown, result.Classification);
        Assert.Null(result.CurrentState);
        Assert.Equal(ApState.Down, result.Next.LastValidState);
        Assert.Equal(T0, result.Next.LastValidObservedAtUtc);
        Assert.Equal(Incident, result.Next.OpenIncident!.IncidentId);
        Assert.Equal(T0, result.Next.OpenIncident.FirstDownAtUtc);
        Assert.Equal(T0, result.Next.OpenIncident.LastDownAtUtc);
        Assert.Equal(MissingObservationReason.UnknownState, result.UnknownReason);
        Assert.DoesNotContain(result.Incidents, mutation => mutation.Kind == IncidentMutationKind.Recovered);
    }

    [Fact]
    public void DifferentInstanceDatasetOrOutOfOrderRun_IsRejected()
    {
        var previous = Previous();
        Assert.Throws<ArgumentException>(() => engine.Evaluate(previous with
            { Identity = previous.Identity with { InstanceId = Guid.Empty } }, Observation(), Context()));
        Assert.Throws<ArgumentException>(() => engine.Evaluate(previous with
            { Identity = previous.Identity with { DatasetId = Guid.Empty } }, Observation(), Context()));
        Assert.Throws<ArgumentException>(() => engine.Evaluate(previous, Observation(id: "101"), Context()));
        Assert.Throws<ArgumentException>(() => engine.Evaluate(previous, Observation(run: 100), Context(100)));
    }

    [Fact]
    public void Decisions_AreDeterministicAndIdsRemainAllocationIntentions()
    {
        var first = engine.Evaluate(null, Observation(ApState.Down), Context());
        var second = engine.Evaluate(null, Observation(ApState.Down), Context());
        Assert.Null(first.Next.Identity.DeviceGenerationId);
        Assert.Null(first.Next.MonitoringIntervalId);
        Assert.Null(first.Next.OpenIncident!.IncidentId);
        Assert.True(first.Identity!.StartsNewGeneration);
        Assert.True(first.Segment.StartsNewInterval);
        Assert.Equal(first.Next with { Identity = second.Next.Identity }, second.Next);
        Assert.Equal(first.Next.Identity.Identity, second.Next.Identity.Identity);
        Assert.Equal(first.Next.Identity.Fields.OrderBy(item => item.Key), second.Next.Identity.Fields.OrderBy(item => item.Key));
        Assert.Equal(first.Incidents.ToArray(), second.Incidents.ToArray());
    }

    [Fact]
    public void UnobservedFirstTarget_FirstValidObservationCanMaterializeIdsWithoutLosingGap()
    {
        var unknown = engine.EvaluateUnobserved(null,
            new("100", MissingObservationReason.CollectionFailed, At(100)), Context(100));
        var down = engine.Evaluate(unknown.Next, Observation(ApState.Down), Context(), NewIds);
        Assert.Equal(NewIds.NewGenerationId, down.Next.Identity.DeviceGenerationId);
        Assert.Equal(NewIds.NewMonitoringIntervalId, down.Next.MonitoringIntervalId);
        Assert.Equal(NewIds.NewGenerationId, down.Next.OpenIncident!.DeviceGenerationId);
        Assert.Equal(NewIds.NewMonitoringIntervalId, down.Next.OpenIncident.MonitoringIntervalId);
        Assert.Equal(NewIds.NewIncidentId, down.Next.OpenIncident.IncidentId);
        Assert.Equal(ObservationClassification.InitialDown, down.Classification);
        Assert.True(down.Next.OpenIncident.HasObservationGap);
        Assert.Equal(At(100), down.ClosedGap!.StartedAtUtc);
        Assert.True(down.Identity!.StartsNewGeneration);
        Assert.True(down.Segment.StartsNewInterval);
    }

    [Fact]
    public void FirstUnobservedTarget_ReturnsCreationIntentionsWithoutClaimingSourceMetadata()
    {
        var unknown = engine.EvaluateUnobserved(null,
            new("100", MissingObservationReason.CollectionFailed, At(100)), Context(100));
        Assert.NotNull(unknown.Identity);
        Assert.True(unknown.Identity.StartsNewGeneration);
        Assert.True(unknown.Segment.StartsNewInterval);
        Assert.False(unknown.AllowsAutomaticMetadataChanges);
        Assert.Null(unknown.Next.LastValidState);
    }

    [Fact]
    public void RestoredSnapshot_RejectsRunOlderThanLastValidEvenWithoutLastProcessedMarker()
    {
        var restored = Previous() with { LastProcessedRunId = null };
        Assert.Throws<ArgumentException>(() => engine.Evaluate(restored, Observation(run: 99), Context(99)));
    }

    [Fact]
    public void SavedEndedInterval_CannotSilentlyDiscardAnOpenIncident()
    {
        Assert.Throws<ArgumentException>(() => engine.Evaluate(Previous() with
            { MonitoringStatus = MonitoringStatus.Ended }, Observation(), Context()));
    }

    [Fact]
    public void UnknownTimeRollback_AlsoMarksPreservedIncidentForReview()
    {
        var result = engine.EvaluateUnobserved(Previous(),
            new("100", MissingObservationReason.CollectionFailed, T0.AddMinutes(-1)), Context());
        Assert.True(result.Next.TimeNeedsReview);
        Assert.True(result.Next.OpenIncident!.TimeNeedsReview);
        Assert.Null(result.Next.OpenIncident.ConfirmedElapsed);
        Assert.Equal(T0, result.Next.OpenIncident.LastDownAtUtc);
    }

    [Fact]
    public void FirstUnobservedTarget_CanMaterializeCallerIdsImmediately()
    {
        var unknown = engine.EvaluateUnobserved(null,
            new("100", MissingObservationReason.CollectionFailed, At(100)), Context(100), NewIds);
        Assert.Equal(NewIds.NewGenerationId, unknown.Next.Identity.DeviceGenerationId);
        Assert.Equal(NewIds.NewMonitoringIntervalId, unknown.Next.MonitoringIntervalId);
        var down = engine.Evaluate(unknown.Next, Observation(ApState.Down), Context(), NewIds);
        Assert.Equal(NewIds.NewGenerationId, down.Next.OpenIncident!.DeviceGenerationId);
        Assert.Equal(NewIds.NewMonitoringIntervalId, down.Next.OpenIncident.MonitoringIntervalId);
        Assert.False(down.Identity!.StartsNewGeneration);
        Assert.False(down.Segment.StartsNewInterval);
        Assert.Equal(ObservationClassification.InitialDown, down.Classification);
        Assert.NotNull(down.ClosedGap);
    }

    [Fact]
    public void TemporaryExclusion_PreservesOperatorReasonForStorage()
    {
        var result = engine.ChangeMonitoring(Previous(), MonitoringDirective.TemporarilyExclude,
            "Scope 2 temporarily excludes this AP", At(101), Context());
        Assert.Equal("Scope 2 temporarily excludes this AP", result.Segment.ChangeReason);
        Assert.False(result.Segment.EndsPreviousInterval);
        Assert.Null(result.Next.OpenIncident!.TrackingEndedAtUtc);
    }

    [Fact]
    public void DuplicateIdentityBatch_UsesUnobservedPathAndCannotApplyAnyReplacementMetadata()
    {
        var first = Observation(ApState.Up, serial: "NEW-SERIAL");
        var second = first with { Device = first.Device with { Name = "changed name", IpAddress = "10.240.0.200" } };
        var conflicts = new IdentityResolver().FindBatchConflicts([first, second]);
        Assert.Contains("100", conflicts.DuplicateApIds);
        var result = engine.EvaluateUnobserved(Previous(),
            new("100", MissingObservationReason.DuplicateIdentity, At(101)), Context());
        Assert.Equal(Generation, result.Next.Identity.DeviceGenerationId);
        Assert.Equal(Serial, result.Next.Identity.Identity.SerialNumber);
        Assert.Equal(Incident, result.Next.OpenIncident!.IncidentId);
        Assert.Null(result.AcceptedObservation);
        Assert.Null(result.Identity);
    }

    [Fact]
    public void CrossIdSerialCollision_PreservesIndependentIncidentTransitions()
    {
        var down = engine.Evaluate(Previous(ApState.Up), Observation(ApState.Down), Context());
        var up = engine.Evaluate(Previous(ApState.Down, "101"), Observation(ApState.Up, id: "101"), Context());
        Assert.Equal("100", down.SourceApId);
        Assert.Equal("101", up.SourceApId);
        Assert.Equal(ObservationClassification.NewDown, down.Classification);
        Assert.Equal(ObservationClassification.Recovered, up.Classification);
        Assert.Equal(Generation, up.Next.Identity.DeviceGenerationId);
    }

    [Fact]
    public void Replacement_EndsOldGapWithoutClaimingObservationResumedOrInheritingGap()
    {
        var gap = engine.EvaluateUnobserved(Previous(),
            new("100", MissingObservationReason.CollectionFailed, At(101)), Context(101));
        var replaced = engine.Evaluate(gap.Next,
            Observation(ApState.Down, 102, serial: "SERIAL-B"), Context(102), NewIds);
        Assert.NotNull(replaced.ClosedGap);
        Assert.Equal(102, replaced.ClosedGap.TrackingEndedRunId);
        Assert.Equal(At(102), replaced.ClosedGap.TrackingEndedAtUtc);
        Assert.Null(replaced.ClosedGap.ResumedAtUtc);
        Assert.NotEmpty(replaced.ClosedGap.TrackingEndReason!);
        Assert.False(replaced.Next.OpenIncident!.HasObservationGap);
        Assert.Equal(ObservationClassification.InitialDown, replaced.Classification);
    }

    [Fact]
    public void ExplicitEnd_ClosesExistingGapWithoutInventingResumption()
    {
        var gap = engine.EvaluateUnobserved(Previous(),
            new("100", MissingObservationReason.CollectionFailed, At(101)), Context(101));
        var ended = engine.ChangeMonitoring(gap.Next, MonitoringDirective.EndTracking,
            "operator ended tracking", At(102), Context(102));
        Assert.Null(ended.Next.OpenGap);
        Assert.NotNull(ended.ClosedGap);
        Assert.Equal(At(102), ended.ClosedGap.TrackingEndedAtUtc);
        Assert.Null(ended.ClosedGap.ResumedAtUtc);
        var reentered = engine.Evaluate(ended.Next, Observation(ApState.Down, 103), Context(103), NewIds);
        Assert.False(reentered.Next.OpenIncident!.HasObservationGap);
    }

    [Theory]
    [InlineData(MonitoringDirective.TemporarilyExclude, MissingObservationReason.MissingRecord)]
    [InlineData(MonitoringDirective.TemporarilyExclude, MissingObservationReason.CollectionFailed)]
    [InlineData(MonitoringDirective.EndTracking, MissingObservationReason.MissingRecord)]
    [InlineData(MonitoringDirective.EndTracking, MissingObservationReason.CollectionFailed)]
    public void CurrentTargetWithoutObservation_ReentersOnceAndPreservesCorrectIncidentBoundary(
        MonitoringDirective previousDirective, MissingObservationReason reason)
    {
        var excluded = engine.ChangeMonitoring(Previous(), previousDirective,
            "operator changed monitoring", At(101), Context(101));
        var oldIncident = Assert.Single(excluded.Incidents).Incident;
        var context = Context(102, ["100", "101"], scope: 2);
        var reentered = engine.EvaluateUnobserved(excluded.Next,
            new("100", reason, At(102)), context, NewIds);
        var ended = previousDirective == MonitoringDirective.EndTracking;

        Assert.True(reentered.IsMonitoringTarget);
        Assert.Equal(ObservationClassification.Unknown, reentered.Classification);
        Assert.Equal(102, reentered.Next.LastProcessedRunId);
        Assert.Equal(excluded.Next.Identity, reentered.Next.Identity);
        Assert.Equal(ended ? NewIds.NewMonitoringIntervalId : Interval, reentered.Next.MonitoringIntervalId);
        Assert.Equal(ended, reentered.Segment.StartsNewInterval);
        Assert.Equal(ended, reentered.Segment.ResetsBaseline);
        Assert.Equal(ended ? null : ApState.Down, reentered.Next.LastValidState);
        Assert.Equal(ended ? null : T0, reentered.Next.LastValidObservedAtUtc);
        Assert.Equal(ended ? At(102) : At(101), reentered.Next.OpenGap!.StartedAtUtc);
        Assert.Equal(ended ? null : Incident, reentered.Next.OpenIncident?.IncidentId);
        Assert.Null(reentered.AcceptedObservation);
        Assert.Null(reentered.Identity);
        Assert.False(reentered.InvalidatesEnrichment);
        Assert.DoesNotContain(reentered.Incidents,
            mutation => mutation.Kind != IncidentMutationKind.Updated);

        var failed = reason == MissingObservationReason.CollectionFailed;
        var other = failed
            ? engine.EvaluateUnobserved(Previous(ApState.Up, "101"),
                new("101", reason, At(102)), context)
            : engine.Evaluate(Previous(ApState.Up, "101"), Observation(ApState.Up, 102, "101"), context);
        var summary = new RunClassifier().Summarize([reentered, other],
            new HashSet<string> { "100", "101" }, failed ? RunOutcome.Failed : RunOutcome.Partial);
        Assert.Equal(failed
            ? new RunSummary(null, null, 2, null, null, null, null, ended ? 0 : 1, 2, 0, BaselineStatus.Unverified)
            : new RunSummary(1, 0, 1, 0, 0, 0, 0, ended ? 0 : 1, 2, 0, BaselineStatus.Unverified), summary);

        var down = engine.Evaluate(reentered.Next, Observation(ApState.Down, 103), Context(103, scope: 2), NewIds);
        Assert.Equal(ended ? ObservationClassification.InitialDown : ObservationClassification.ExistingDown,
            down.Classification);
        Assert.Equal(ended ? NewIds.NewIncidentId : Incident, down.Next.OpenIncident!.IncidentId);
        Assert.Equal(Generation, down.Next.Identity.DeviceGenerationId);
        Assert.Equal(ended ? At(103) : T0, down.Next.OpenIncident.FirstDownAtUtc);
        Assert.Equal(ended ? At(101) : null, oldIncident.TrackingEndedAtUtc);
        Assert.Null(oldIncident.RecoveredAtUtc);
    }
}
