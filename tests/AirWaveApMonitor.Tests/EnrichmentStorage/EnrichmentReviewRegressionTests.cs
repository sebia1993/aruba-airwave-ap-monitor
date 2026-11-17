using System.Collections.Immutable;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Tests.Storage;

public sealed partial class SqliteCollectionStoreTests
{
    private async Task<PowerEvidenceView> MeasuredPort(CollectionContext run, DateTimeOffset measuredAt, decimal watts = 8m)
    {
        var profile = EnrichmentProfileBinding.From(run.Profile, EvidenceSource.Demo);
        var sample = new PowerSample(Guid.NewGuid(), run.RunId, profile, new("switch-1", "100", EndpointRole.Switch), PowerMetric.PortActual, watts, "W", true,
            new(clock.GetUtcNow(), measuredAt, SourceTimeMeaning.Measurement, true), new(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.Demo, clock.GetUtcNow(), measuredAt));
        await store.ApplyEnrichmentBatchAsync(run, new(CollectionKind.Power, RunOutcome.Succeeded, [], [], ObservationCompleteness.Complete, clock.GetUtcNow(), []) { Detail = new([], [], [sample]) }, default);
        return Assert.Single((await store.GetEnrichmentRunAsync(location.DatasetId, run.RunId, default)).Evidence.PowerSamples);
    }
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task EnrichmentReview_EndedGenerationBoundsHistoricalAttribution(int minutes, bool attributed)
    {
        var old = await SeedOperations(); await ApplyEnrichment(CollectionKind.ConnectionNeighbor); clock.Advance();
        var replacedAt = clock.GetUtcNow(); await Apply(Observation("100", ApState.Up, serial: "NEW"), Observation("200", ApState.Up));
        var run = await BeginEnrichment(CollectionKind.Power); var current = (await store.GetFrozenEnrichmentTargetsAsync(run, default)).Single(t => t.SourceApId == "100");
        Assert.NotEqual(old.DeviceGenerationId, current.DeviceGenerationId);
        var saved = await MeasuredPort(run, replacedAt.AddMinutes(minutes));
        if (attributed) Assert.Equal(old.DeviceGenerationId, saved.Attribution.Target!.DeviceGenerationId);
        else { Assert.NotEqual(PowerAttributionStatus.Attributed, saved.Attribution.Status); Assert.Null(saved.Attribution.Target); }
        Assert.Equal(8m, saved.Normalized.Watts);
    }
    [Fact]
    public async Task EnrichmentReview_ReplacementDuringPowerRun_DoesNotUseEndedIdentityAfterBoundary()
    {
        await SeedOperations(); await ApplyEnrichment(CollectionKind.ConnectionNeighbor);
        var run = await BeginEnrichment(CollectionKind.Power); clock.Advance(); var replacedAt = clock.GetUtcNow();
        await Apply(Observation("100", ApState.Up, serial: "NEW"), Observation("200", ApState.Up));
        var saved = await MeasuredPort(run, replacedAt.AddMinutes(1));
        Assert.Null(saved.Attribution.Target);
        Assert.Equal(EnrichmentApplicationDisposition.HistoricalOnly, (await store.GetEnrichmentRunAsync(location.DatasetId, run.RunId, default)).Run.Disposition);
    }
    [Fact]
    public async Task EnrichmentReview_EndedMonitoringIntervalCannotBeReusedAfterReentry()
    {
        var old = await SeedOperations(); await ApplyEnrichment(CollectionKind.ConnectionNeighbor); clock.Advance(); var endedAt = clock.GetUtcNow();
        await store.SaveTrackingPolicyAsync(new(Guid.NewGuid(), old.ApId, old.DeviceGenerationId, 0, OperationalStatus.Decommissioned, "종료"), default);
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        await store.SaveTrackingPolicyAsync(new(Guid.NewGuid(), old.ApId, old.DeviceGenerationId, 1, OperationalStatus.Operating, "재편입"), default);
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        var run = await BeginEnrichment(CollectionKind.Power); var target = (await store.GetFrozenEnrichmentTargetsAsync(run, default)).Single(t => t.ApId == old.ApId);
        Assert.Equal(old.DeviceGenerationId, target.DeviceGenerationId); Assert.NotEqual(old.MonitoringIntervalId, target.MonitoringIntervalId);
        Assert.Null((await MeasuredPort(run, endedAt.AddMinutes(1))).Attribution.Target);
        var history = await BeginEnrichment(CollectionKind.Power);
        Assert.Equal(old.MonitoringIntervalId, (await MeasuredPort(history, endedAt.AddMinutes(-1))).Attribution.Target!.MonitoringIntervalId);
    }
    [Fact]
    public async Task EnrichmentReview_ClosedPlacementCannotReceiveNewMeasurement_ButKeepsEarlierSample()
    {
        var row = await SeedOperations(); var point1 = Guid.NewGuid(); var point2 = Guid.NewGuid();
        await store.SavePointAsync(new(Guid.NewGuid(), point1, 0, "이전", null, new()), default);
        await store.SavePointAsync(new(Guid.NewGuid(), point2, 0, "현재", null, new()), default);
        await store.MovePlacementAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 0, point1, 1, "배치"), default);
        await ApplyEnrichment(CollectionKind.ConnectionNeighbor); clock.Advance(); var movedAt = clock.GetUtcNow();
        await store.MovePlacementAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 1, point2, 1, "이동"), default); clock.Advance();
        Assert.Null((await MeasuredPort(await BeginEnrichment(CollectionKind.Power), movedAt)).Attribution.Target);
        Assert.Equal(point1, (await MeasuredPort(await BeginEnrichment(CollectionKind.Power), movedAt.AddMinutes(-1))).Attribution.Target!.Placement!.InstallationPointId);
    }
    [Fact]
    public async Task EnrichmentReview_HigherRunCommittedFirst_IsExcludedFromApAndIncidentSnapshot()
    {
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        var ap = await Begin(); await ApplyEnrichment(CollectionKind.Power, 17m);
        await store.ApplyApBatchAsync(ap, Batch(Observation("100", ApState.Down), Observation("200", ApState.Up)), default);
        var row = (await Dashboard(ap.RunId)).Rows.Single(r => r.SourceApId == "100");
        Assert.Empty(row.EnrichmentDetail!.PowerSamples);
        var incident = await store.GetIncidentAsync(new(location.DatasetId, 1, ap.RunId), row.OpenIncidentId!.Value, default);
        Assert.Empty(incident.Detection.Device.EnrichmentDetail!.PowerSamples);
        Assert.Empty((await store.GetPowerSamplesAsync(new(location.DatasetId, 1, ap.RunId, row.ApId, row.DeviceGenerationId), default)).Rows);
    }
    [Fact]
    public async Task EnrichmentReview_HigherCurrentPointerFallsBackToEarlierAcceptedPowerAndConnection()
    {
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up)); await ApplyEnrichment(CollectionKind.ConnectionNeighbor); await ApplyEnrichment(CollectionKind.Power, 9m);
        var ap = await Begin(); await ApplyEnrichment(CollectionKind.ConnectionNeighbor); await ApplyEnrichment(CollectionKind.Power, 17m);
        await store.ApplyApBatchAsync(ap, Batch(Observation("100", ApState.Down), Observation("200", ApState.Up)), default);
        var row = (await Dashboard(ap.RunId)).Rows.Single(r => r.SourceApId == "100");
        Assert.NotEmpty(row.EnrichmentDetail!.PowerSamples); Assert.All(row.EnrichmentDetail.PowerSamples, p => { Assert.Equal(9m, p.Normalized.Watts); Assert.True(p.Sample.RunId <= ap.RunId); });
        Assert.All(row.EnrichmentDetail.Connections, c => Assert.True(c.RunId <= ap.RunId)); Assert.All(row.EnrichmentDetail.Ports, p => Assert.True(p.RunId <= ap.RunId));
        Assert.All(row.EnrichmentDetail.PowerStates, p => Assert.True(p.RunId <= ap.RunId));
        var before = JsonSerializer.Serialize(row); await ApplyEnrichment(CollectionKind.Power, 23m);
        Assert.Equal(before, JsonSerializer.Serialize((await Dashboard(ap.RunId)).Rows.Single(r => r.ApId == row.ApId)));
    }
    [Fact]
    public async Task EnrichmentReview_FallbackDoesNotPromotePreviouslyRejectedRegressedPower()
    {
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up)); await ApplyEnrichment(CollectionKind.ConnectionNeighbor);
        var accepted = await BeginEnrichment(CollectionKind.Power); await MeasuredPort(accepted, clock.GetUtcNow(), 9m);
        var rejected = await BeginEnrichment(CollectionKind.Power); await MeasuredPort(rejected, clock.GetUtcNow().AddMinutes(-1), 88m);
        var ap = await Begin(); await ApplyEnrichment(CollectionKind.Power, 17m);
        await store.ApplyApBatchAsync(ap, Batch(Observation("100", ApState.Down), Observation("200", ApState.Up)), default);
        var row = (await Dashboard(ap.RunId)).Rows.Single(r => r.SourceApId == "100");
        var stored = Assert.Single(row.EnrichmentDetail!.PowerSamples); Assert.Equal(9m, stored.Normalized.Watts); Assert.Equal(accepted.RunId, stored.Sample.RunId);
    }
    [Fact]
    public async Task EnrichmentReview_GapsUseRunBoundaryEvenWhenHigherRunClosesCurrentGap()
    {
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        var failed = await BeginEnrichment(CollectionKind.Power);
        await store.ApplyEnrichmentBatchAsync(failed, new(CollectionKind.Power, RunOutcome.Failed, [], [], ObservationCompleteness.Unknown, clock.GetUtcNow(), []), default);
        var ap = await Begin(); await ApplyEnrichment(CollectionKind.Power);
        await store.ApplyApBatchAsync(ap, Batch(Observation("100", ApState.Down), Observation("200", ApState.Up)), default);
        var gap = Assert.Single((await Dashboard(ap.RunId)).Rows[0].EnrichmentDetail!.Gaps);
        Assert.Equal(failed.RunId, gap.FirstRunId); Assert.Null(gap.ResumedRunId);
    }
    [Fact]
    public async Task EnrichmentReview_KindDetailDoesNotAcquireHigherRunGapAtLateCommit()
    {
        await SeedOperations(); var connection = await BeginEnrichment(CollectionKind.ConnectionNeighbor);
        var higher = await BeginEnrichment(CollectionKind.Power);
        await store.ApplyEnrichmentBatchAsync(higher, new(CollectionKind.Power, RunOutcome.Failed, [], [], ObservationCompleteness.Unknown, clock.GetUtcNow(), []), default);
        await store.ApplyEnrichmentBatchAsync(connection, Enriched(connection, await store.GetFrozenEnrichmentTargetsAsync(connection, default)), default);
        Assert.Empty((await store.GetEnrichmentRunAsync(location.DatasetId, connection.RunId, default)).Evidence.Gaps);
    }

}
