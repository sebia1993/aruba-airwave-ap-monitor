using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Core.Monitoring;

public sealed class StateTransitionEngine
{
    private readonly IdentityResolver identityResolver = new();
    private readonly MonitoringSegmentPolicy segmentPolicy = new();

    /// <summary>
    /// Evaluates a uniquely attributed record. A globally failed batch must use EvaluateUnobserved
    /// for every target instead, even if that batch contains identity-only records.
    /// </summary>
    public ApTransitionDecision Evaluate(ApTrackingSnapshot? previous, ApObservation current,
        CollectionContext context, TransitionIdentifiers? identifiers = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        Validate(previous, current.SourceApId, context);
        identifiers ??= new();
        var identity = identityResolver.Resolve(previous?.Identity, current);
        var pendingBaseline = previous is { LastValidState: null } &&
            previous.Identity.DeviceGenerationId is null && previous.MonitoringIntervalId is null &&
            !identity.StartsNewGeneration;
        if (pendingBaseline)
        {
            identity = identity with { Change = IdentityChange.FirstGeneration };
        }
        var segment = segmentPolicy.Evaluate(previous, identity.StartsNewGeneration,
            newIntervalId: identifiers.NewMonitoringIntervalId);
        if (pendingBaseline)
        {
            segment = segment with { EndsPreviousInterval = false, ResetsBaseline = false, EndReason = null };
        }
        var identitySnapshot = new ApIdentitySnapshot(context.Profile.InstanceId, context.Profile.DatasetId,
            current.SourceApId, identity.StartsNewGeneration ? identifiers.NewGenerationId : previous!.Identity.DeviceGenerationId,
            identity.Identity, identity.Fields);
        var next = previous is null || segment.ResetsBaseline
            ? new ApTrackingSnapshot(identitySnapshot, segment.MonitoringIntervalId)
            : previous with
            {
                Identity = identitySnapshot,
                MonitoringStatus = MonitoringStatus.Active,
                MonitoringIntervalId = segment.MonitoringIntervalId
            };
        var timeNeedsReview = NeedsTimeReview(previous, current.CollectedAtUtc);
        var mutations = ImmutableArray.CreateBuilder<IncidentMutation>();
        var endedGap = segment.EndsPreviousInterval
            ? EndGap(previous?.OpenGap, context.RunId, current.CollectedAtUtc, segment.EndReason!)
            : null;
        if (segment.EndsPreviousInterval && previous?.OpenIncident is { } oldIncident)
        {
            mutations.Add(EndIncident(oldIncident, context.RunId, current.CollectedAtUtc,
                segment.EndReason!, timeNeedsReview));
        }
        next = next with
        {
            LastProcessedRunId = context.RunId,
            LastProcessedAtUtc = current.CollectedAtUtc,
            TimeNeedsReview = timeNeedsReview
        };
        var state = IdentityResolver.HasValidEvidence(current.Fields, MonitoringFieldNames.State) &&
            current.State is ApState.Up or ApState.Down
            ? current.State
            : null;
        if (state is null)
        {
            next = OpenGap(next, MissingObservationReason.UnknownState, context.RunId,
                current.CollectedAtUtc, mutations);
            return new(context, next, ObservationClassification.Unknown, null, identity, segment,
                mutations.ToImmutable(), endedGap, MissingObservationReason.UnknownState, current,
                identity.Change == IdentityChange.Replaced);
        }

        var closedGap = next.OpenGap is null ? null : next.OpenGap with
        {
            ResumedRunId = context.RunId,
            ResumedAtUtc = current.CollectedAtUtc
        };
        ObservationClassification classification;
        if (state == ApState.Down)
        {
            if (next.LastValidState == ApState.Down)
            {
                classification = ObservationClassification.ExistingDown;
                var incident = next.OpenIncident! with
                {
                    LastDownRunId = context.RunId,
                    LastDownAtUtc = current.CollectedAtUtc,
                    HasObservationGap = next.OpenIncident.HasObservationGap || closedGap is not null,
                    TimeNeedsReview = next.OpenIncident.TimeNeedsReview || timeNeedsReview
                };
                next = next with { OpenIncident = incident };
                mutations.Add(new(IncidentMutationKind.Updated, incident));
            }
            else
            {
                var initial = next.LastValidState is null;
                classification = initial ? ObservationClassification.InitialDown : ObservationClassification.NewDown;
                var incident = new IncidentTrackingSnapshot(identifiers.NewIncidentId,
                    next.Identity.DeviceGenerationId, next.MonitoringIntervalId,
                    context.RunId, current.CollectedAtUtc, context.RunId, current.CollectedAtUtc,
                    next.LastUpAtUtc, initial, closedGap is not null, TimeNeedsReview: timeNeedsReview);
                next = next with { OpenIncident = incident };
                mutations.Add(new(IncidentMutationKind.Created, incident));
            }
        }
        else if (next.OpenIncident is { } openIncident)
        {
            classification = ObservationClassification.Recovered;
            var incident = openIncident with
            {
                RecoveredRunId = context.RunId,
                RecoveredAtUtc = current.CollectedAtUtc,
                HasObservationGap = openIncident.HasObservationGap || closedGap is not null,
                TimeNeedsReview = openIncident.TimeNeedsReview || timeNeedsReview
            };
            mutations.Add(new(IncidentMutationKind.Recovered, incident));
            next = next with { OpenIncident = null };
        }
        else
        {
            classification = ObservationClassification.Up;
        }
        next = next with
        {
            LastValidState = state,
            LastValidRunId = context.RunId,
            LastValidObservedAtUtc = current.CollectedAtUtc,
            LastUpAtUtc = state == ApState.Up ? current.CollectedAtUtc : next.LastUpAtUtc,
            OpenGap = null
        };
        return new(context, next, classification, state, identity, segment, mutations.ToImmutable(),
            endedGap ?? closedGap, null, current, identity.Change == IdentityChange.Replaced);
    }

    /// <summary>
    /// Evaluates an AP included in the current target set but lacking a valid source observation.
    /// Membership reentry is a configuration fact and is applied once in the same run, independently
    /// of source availability. It never changes hardware identity or applies source metadata.
    /// </summary>
    public ApTransitionDecision EvaluateUnobserved(ApTrackingSnapshot? previous, UnobservedAp current,
        CollectionContext context, TransitionIdentifiers? identifiers = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        Validate(previous, current.SourceApId, context);
        if (current.Reason is MissingObservationReason.ScopeExcluded or MissingObservationReason.TrackingEnded)
        {
            throw new ArgumentException("Use ChangeMonitoring for explicit monitoring changes.", nameof(current));
        }
        var segment = segmentPolicy.Evaluate(previous, false,
            newIntervalId: identifiers?.NewMonitoringIntervalId);
        var identitySnapshot = previous?.Identity ?? new ApIdentitySnapshot(context.Profile.InstanceId,
            context.Profile.DatasetId, current.SourceApId, identifiers?.NewGenerationId, new(null, null), []);
        var next = previous is null || segment.ResetsBaseline
            ? new ApTrackingSnapshot(identitySnapshot, segment.MonitoringIntervalId)
            : previous with { MonitoringStatus = MonitoringStatus.Active };
        next = next with
        {
            LastProcessedRunId = context.RunId,
            LastProcessedAtUtc = current.RecordedAtUtc,
            TimeNeedsReview = NeedsTimeReview(previous, current.RecordedAtUtc)
        };
        var mutations = ImmutableArray.CreateBuilder<IncidentMutation>();
        next = OpenGap(next, current.Reason, context.RunId, current.RecordedAtUtc, mutations);
        var identity = previous is null
            ? new IdentityDecision(IdentityChange.FirstGeneration, next.Identity.Identity, next.Identity.Fields, false, false)
            : null;
        return new(context, next, ObservationClassification.Unknown, null, identity, segment,
            mutations.ToImmutable(), null, current.Reason, null, false);
    }

    public ApTransitionDecision ChangeMonitoring(ApTrackingSnapshot previous, MonitoringDirective directive,
        string reason, DateTimeOffset recordedAtUtc, CollectionContext context)
    {
        ArgumentNullException.ThrowIfNull(previous);
        Validate(previous, previous.Identity.SourceApId, context);
        if (directive == MonitoringDirective.Observe)
        {
            throw new ArgumentException("Use Evaluate or EvaluateUnobserved for a current monitoring target.", nameof(directive));
        }
        var segment = segmentPolicy.Evaluate(previous, false, directive, reason: reason);
        var timeNeedsReview = NeedsTimeReview(previous, recordedAtUtc);
        var next = previous with
        {
            MonitoringStatus = segment.Status,
            LastProcessedRunId = context.RunId,
            LastProcessedAtUtc = recordedAtUtc,
            TimeNeedsReview = timeNeedsReview
        };
        var mutations = ImmutableArray.CreateBuilder<IncidentMutation>();
        var missingReason = directive == MonitoringDirective.EndTracking
            ? MissingObservationReason.TrackingEnded
            : MissingObservationReason.ScopeExcluded;
        ObservationGap? endedGap = null;
        if (directive == MonitoringDirective.EndTracking)
        {
            if (next.OpenIncident is { } incident)
            {
                mutations.Add(EndIncident(incident, context.RunId, recordedAtUtc, reason, timeNeedsReview));
            }
            endedGap = EndGap(next.OpenGap, context.RunId, recordedAtUtc, reason);
            next = next with { OpenIncident = null, OpenGap = null };
        }
        else
        {
            next = OpenGap(next, missingReason, context.RunId, recordedAtUtc, mutations);
        }
        return new(context, next, ObservationClassification.Unknown, null, null, segment,
            mutations.ToImmutable(), endedGap, missingReason, null, false);
    }

    private static ApTrackingSnapshot OpenGap(ApTrackingSnapshot next, MissingObservationReason reason,
        long runId, DateTimeOffset recordedAtUtc, ImmutableArray<IncidentMutation>.Builder mutations)
    {
        var gap = next.OpenGap ?? new ObservationGap(runId, recordedAtUtc, reason, next.OpenIncident?.IncidentId);
        var incident = next.OpenIncident;
        if (incident is not null)
        {
            incident = incident with
            {
                HasObservationGap = true,
                TimeNeedsReview = incident.TimeNeedsReview || next.TimeNeedsReview
            };
            mutations.Add(new(IncidentMutationKind.Updated, incident));
        }
        return next with { OpenGap = gap, OpenIncident = incident };
    }

    private static IncidentMutation EndIncident(IncidentTrackingSnapshot incident, long runId,
        DateTimeOffset at, string reason, bool timeNeedsReview)
        => new(IncidentMutationKind.TrackingEnded, incident with
        {
            TrackingEndedRunId = runId,
            TrackingEndedAtUtc = at,
            TrackingEndReason = reason,
            TimeNeedsReview = incident.TimeNeedsReview || timeNeedsReview
        });

    private static ObservationGap? EndGap(ObservationGap? gap, long runId, DateTimeOffset at, string reason)
        => gap is null ? null : gap with
        {
            TrackingEndedRunId = runId,
            TrackingEndedAtUtc = at,
            TrackingEndReason = reason
        };

    private static bool NeedsTimeReview(ApTrackingSnapshot? previous, DateTimeOffset at)
        => previous?.TimeNeedsReview == true || at < previous?.LastProcessedAtUtc ||
            at < previous?.LastValidObservedAtUtc;

    private static void Validate(ApTrackingSnapshot? previous, string sourceApId, CollectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(sourceApId))
        {
            throw new ArgumentException("An attributable AP ID is required.", nameof(sourceApId));
        }
        if (context.Kind != CollectionKind.ApStatus || context.RunId <= 0)
        {
            throw new ArgumentException("A positive AP-status run is required.", nameof(context));
        }
        if (previous is null)
        {
            return;
        }
        if (previous.Identity.InstanceId != context.Profile.InstanceId ||
            previous.Identity.DatasetId != context.Profile.DatasetId || previous.Identity.SourceApId != sourceApId)
        {
            throw new ArgumentException("Previous state belongs to another instance, dataset or AP.", nameof(previous));
        }
        if (context.RunId <= previous.LastProcessedRunId || context.RunId <= previous.LastValidRunId)
        {
            throw new ArgumentException("Runs must be applied in increasing order; storage handles replay.", nameof(context));
        }
        if (previous.MonitoringStatus != MonitoringStatus.Ended &&
            (previous.LastValidState == ApState.Down) != (previous.OpenIncident is not null))
        {
            throw new ArgumentException("Saved Down state and open incident must agree.", nameof(previous));
        }
        if (previous.MonitoringStatus == MonitoringStatus.Ended && previous.OpenIncident is not null)
        {
            throw new ArgumentException("An ended interval cannot retain an open incident.", nameof(previous));
        }
        if (previous.OpenIncident is { } incident &&
            (incident.DeviceGenerationId != previous.Identity.DeviceGenerationId ||
             incident.MonitoringIntervalId != previous.MonitoringIntervalId ||
             incident.RecoveredAtUtc is not null || incident.TrackingEndedAtUtc is not null))
        {
            throw new ArgumentException("Open incident must belong to the active generation and interval.", nameof(previous));
        }
    }
}
