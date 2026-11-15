using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using Microsoft.Data.Sqlite;
namespace AirWaveApMonitor.Tests.Storage;

public sealed partial class SqliteCollectionStoreTests
{
    [Fact]
    public async Task EnrichmentRead_RunWatermarkPinsMembershipWhileRunningCanFinalize()
    {
        await SeedOperations(); var running = await BeginEnrichment(CollectionKind.ConnectionNeighbor);
        var first = await BeginEnrichment(CollectionKind.Power); FinalizeReadFixture(first);
        var page = await store.GetEnrichmentRunsAsync(new(location.DatasetId, PageSize: 1), default);
        Assert.Equal(2, page.TotalRows); Assert.Equal(first.RunId, page.ThroughRunId); Assert.Equal(first.RunId, Assert.Single(page.Rows).RunId);
        var later = await BeginEnrichment(CollectionKind.Power); FinalizeReadFixture(later);
        FinalizeReadFixture(running, RunOutcome.Failed);
        var next = await store.GetEnrichmentRunsAsync(new(location.DatasetId, ThroughRunId: page.ThroughRunId, Page: 2, PageSize: 1), default);
        Assert.Equal(2, next.TotalRows); var row = Assert.Single(next.Rows);
        Assert.Equal(running.RunId, row.RunId); Assert.Equal(RunLifecycle.Finalized, row.Lifecycle); Assert.Equal(RunOutcome.Failed, row.Outcome);
        Assert.True(row.HasQualityGap); Assert.Equal(EnrichmentApplicationDisposition.Current, row.Disposition);
        Assert.Equal(EvidenceSource.Demo, row.Source);
    }
    [Fact]
    public async Task EnrichmentRead_DetailUsesTypedReceipt_AndRejectsApRun()
    {
        await SeedOperations(); var ap = (await Dashboard()).RunId;
        var run = await BeginEnrichment(CollectionKind.Power); FinalizeReadFixture(run, RunOutcome.Failed);
        var detail = await store.GetEnrichmentRunAsync(location.DatasetId, run.RunId, default);
        Assert.Equal(CollectionKind.Power, detail.Run.Kind); Assert.Equal(RunOutcome.Failed, detail.Run.Outcome);
        Assert.Contains(detail.Run.Errors, e => e.Category == CollectionErrorCategory.Transport);
        Assert.Equal(run.RunId, detail.Evidence.ThroughRunId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.GetEnrichmentRunAsync(location.DatasetId, ap, default));
    }
    [Fact]
    public async Task EnrichmentRead_CurrentPowerCommitTokenExcludesLaterCommitWithLowerRunId()
    {
        var target = await SeedOperations(); var late = await BeginEnrichment(CollectionKind.Power);
        var ap = await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up)); EnsureReadCommit(ap.RunId);
        var before = await store.GetPowerSamplesAsync(new(location.DatasetId, 1), default);
        Assert.Null(before.ThroughRunId); Assert.True(before.ThroughCommitSequence > 0);
        FinalizeReadFixture(late); AddReadSample(late, target, 88m);
        var current = await store.GetPowerSamplesAsync(new(location.DatasetId, 1), default);
        Assert.Single(current.Rows); Assert.Null(current.ThroughRunId); Assert.True(current.ThroughCommitSequence > before.ThroughCommitSequence);
        Assert.Empty((await store.GetPowerSamplesAsync(new(location.DatasetId, 1, ThroughCommitSequence: before.ThroughCommitSequence), default)).Rows);
        Assert.Empty((await store.GetPowerSamplesAsync(new(location.DatasetId, 1, ThroughRunId: ap.RunId), default)).Rows);
        Assert.Empty((await store.GetPowerSamplesAsync(new(location.DatasetId, 1, ThroughCommitSequence: 0), default)).Rows);
        Assert.Single((await store.GetPowerSamplesAsync(new(location.DatasetId, 1, ThroughRunId: late.RunId), default)).Rows);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetPowerSamplesAsync(new(location.DatasetId, 1, ThroughRunId: ap.RunId, ThroughCommitSequence: current.ThroughCommitSequence), default));
    }
    [Fact]
    public async Task EnrichmentRead_PowerFiltersFrozenGeneration_NotCurrentGeneration()
    {
        var old = await SeedOperations(); var oldRun = await BeginEnrichment(CollectionKind.Power); FinalizeReadFixture(oldRun); AddReadSample(oldRun, old, 7m);
        await Apply(Observation("100", ApState.Down, serial: "replacement"), Observation("200", ApState.Up));
        var current = (await Dashboard()).Rows.Single(r => r.ApId == old.ApId); Assert.NotEqual(old.DeviceGenerationId, current.DeviceGenerationId);
        var newRun = await BeginEnrichment(CollectionKind.Power); FinalizeReadFixture(newRun); AddReadSample(newRun, current, 13m);
        var all = await store.GetPowerSamplesAsync(new(location.DatasetId, 1, ApId: old.ApId), default);
        Assert.Equal(2, all.TotalRows);
        var oldPage = await store.GetPowerSamplesAsync(new(location.DatasetId, 1, ApId: old.ApId, DeviceGenerationId: old.DeviceGenerationId), default);
        Assert.Equal(7m, Assert.Single(oldPage.Rows).Normalized.Watts);
        Assert.Equal(old.DeviceGenerationId, oldPage.Rows[0].Attribution.Target!.DeviceGenerationId);
        var other = (await Dashboard()).Rows.Single(r => r.ApId != old.ApId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.GetPowerSamplesAsync(new(location.DatasetId, 1, ApId: other.ApId, DeviceGenerationId: old.DeviceGenerationId), default));
    }
    [Fact]
    public async Task EnrichmentRead_TimeRangeUsesReadTime_AndInvalidRawSampleStaysUnknown()
    {
        var row = await SeedOperations(); var run = await BeginEnrichment(CollectionKind.Power); FinalizeReadFixture(run);
        AddReadSample(run, row, 5m, valid: false);
        var page = await store.GetPowerSamplesAsync(new(location.DatasetId, 1, FromUtc: run.StartedAtUtc, ToUtc: run.StartedAtUtc), default);
        var sample = Assert.Single(page.Rows); Assert.Null(sample.Normalized.Watts); Assert.Equal("dBm", sample.Sample.Unit);
        Assert.Equal(run.StartedAtUtc.AddHours(-2), sample.Sample.Timing.SourceAtUtc);
        Assert.Empty((await store.GetPowerSamplesAsync(new(location.DatasetId, 1, ToUtc: run.StartedAtUtc.AddMinutes(-1)), default)).Rows);
        Assert.Null(sample.Attribution.Target);
    }
    [Fact]
    public async Task EnrichmentRead_ExpiryUsesDurableCutoff_WithoutConvertingEmptyDataToZero()
    {
        await SeedOperations(); var cutoff = clock.GetUtcNow().AddDays(-30);
        ReadSql("INSERT INTO enrichment_retention_state VALUES('power',$at)", ("$at", cutoff));
        var all = await store.GetPowerSamplesAsync(new(location.DatasetId, 1), default);
        Assert.Empty(all.Rows); Assert.Equal(cutoff, all.ExpiredBeforeUtc); Assert.True(all.MayIncludeExpiredSamples); Assert.Equal(30, all.RetentionDays);
        var retained = await store.GetPowerSamplesAsync(new(location.DatasetId, 1, FromUtc: cutoff), default);
        Assert.False(retained.MayIncludeExpiredSamples);
    }
    [Fact]
    public async Task EnrichmentRead_EmptyLegitimatePointIntersectionIsNotAnUnknownPoint()
    {
        var row = await SeedOperations(); var point = Guid.NewGuid();
        await store.SavePointAsync(new(Guid.NewGuid(), point, 0, "unobserved point", null, new()), default);
        Assert.Empty((await store.GetPowerSamplesAsync(new(location.DatasetId, 1, ApId: row.ApId, InstallationPointId: point), default)).Rows);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.GetPowerSamplesAsync(new(location.DatasetId, 1, InstallationPointId: Guid.NewGuid()), default));
    }
    [Fact]
    public async Task EnrichmentRead_RejectsInvalidQueriesAndUnfinalizedHistoricalBoundary()
    {
        await SeedOperations(); var running = await BeginEnrichment(CollectionKind.Power);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.GetPowerSamplesAsync(new(location.DatasetId, 1, ThroughRunId: running.RunId), default));
        foreach (var query in new[] { new PowerSampleQuery(Guid.NewGuid(), 1), new(location.DatasetId, 0), new(location.DatasetId, 1, Page: 0),
            new(location.DatasetId, 1, PageSize: 501), new(location.DatasetId, 1, Metric: (PowerMetric)999), new(location.DatasetId, 1, EndpointRole: (EndpointRole)999),
            new(location.DatasetId, 1, ThroughCommitSequence: -1),
            new(location.DatasetId, 1, FromUtc: clock.GetUtcNow(), ToUtc: clock.GetUtcNow().AddDays(-1)) })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetPowerSamplesAsync(query, default));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetEnrichmentRunsAsync(new(location.DatasetId, Kind: CollectionKind.ApStatus), default));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetEnrichmentRunsAsync(new(location.DatasetId, Page: 0), default));
    }
    [Fact]
    public async Task EnrichmentRead_PowerPagingUsesStableSequence_AndIndependentSourceFilters()
    {
        var firstAp = await SeedOperations(); var secondAp = (await Dashboard()).Rows.Single(row => row.ApId != firstAp.ApId);
        var run = await BeginEnrichment(CollectionKind.Power); FinalizeReadFixture(run);
        AddReadSample(run, firstAp, 7m); AddReadSample(run, secondAp, 11m);
        var page = await store.GetPowerSamplesAsync(new(location.DatasetId, 1, PageSize: 1), default);
        Assert.Equal(2, page.TotalRows); Assert.Equal(11m, Assert.Single(page.Rows).Normalized.Watts);
        var later = await BeginEnrichment(CollectionKind.Power); FinalizeReadFixture(later); AddReadSample(later, firstAp, 99m);
        var next = await store.GetPowerSamplesAsync(new(location.DatasetId, 1, Page: 2, PageSize: 1, ThroughCommitSequence: page.ThroughCommitSequence), default);
        Assert.Equal(2, next.TotalRows); Assert.Equal(7m, Assert.Single(next.Rows).Normalized.Watts);
        Assert.Equal(3, (await store.GetPowerSamplesAsync(new(location.DatasetId, 1, SourceInterfaceId: "eth0"), default)).TotalRows);
        Assert.Equal(2, (await store.GetPowerSamplesAsync(new(location.DatasetId, 1, SourceDeviceId: firstAp.SourceApId, SourceInterfaceId: "eth0"), default)).TotalRows);
        Assert.Empty((await store.GetPowerSamplesAsync(new(location.DatasetId, 1, Metric: PowerMetric.PortActual), default)).Rows);
        Assert.Empty((await store.GetPowerSamplesAsync(new(location.DatasetId, 1, EndpointRole: EndpointRole.Switch), default)).Rows);
        Assert.Empty((await store.GetPowerSamplesAsync(new(location.DatasetId, 1, SourceDeviceId: "' OR 1=1 --"), default)).Rows);
    }
    private void FinalizeReadFixture(CollectionContext run, RunOutcome outcome = RunOutcome.Succeeded)
    {
        var errors = outcome == RunOutcome.Failed ? ImmutableArray.Create(new CollectionError(CollectionErrorCategory.Transport, 1, "fixture failure")) : [];
        var completeness = outcome == RunOutcome.Succeeded ? ObservationCompleteness.Complete : ObservationCompleteness.Unknown;
        var receipt = new EnrichmentCommitReceipt(location.DatasetId, run.Kind, run.RunId, false, true, outcome, completeness, errors) { Disposition = EnrichmentApplicationDisposition.Current };
        ReadSql("INSERT INTO run_results VALUES($run,$dataset,$kind,$at,$outcome,$receipt,$complete,$errors,'null')", ("$run", run.RunId), ("$dataset", location.DatasetId),
            ("$kind", run.Kind), ("$at", clock.GetUtcNow()), ("$outcome", outcome), ("$receipt", JsonSerializer.Serialize(receipt)), ("$complete", completeness), ("$errors", JsonSerializer.Serialize(errors)));
        ReadSql("UPDATE runs SET lifecycle=1 WHERE run_id=$run", ("$run", run.RunId));
        ReadSql("INSERT INTO enrichment_run_stats SELECT run_id,profile_key,'read-fixture',0,0,0,0,0,$valid,$gap FROM enrichment_run_bindings WHERE run_id=$run",
            ("$run", run.RunId), ("$valid", outcome == RunOutcome.Failed ? 0 : 1), ("$gap", outcome == RunOutcome.Failed ? 1 : 0));
        EnsureReadCommit(run.RunId);
    }
    private void EnsureReadCommit(long run) => ReadSql("INSERT OR IGNORE INTO collection_commits(run_id) VALUES($run)", ("$run", run));
    private void AddReadSample(CollectionContext run, ApSnapshotRow row, decimal watts, bool valid = true)
    {
        var profile = EnrichmentProfileBinding.From(run.Profile, EvidenceSource.Demo);
        var target = new EnrichmentTargetBinding(profile, row.ApId, row.SourceApId, row.DeviceGenerationId, row.MonitoringIntervalId);
        var sample = new PowerSample(Guid.NewGuid(), run.RunId, profile, new(row.SourceApId, "eth0", EndpointRole.AccessPoint), PowerMetric.ApConsumption, watts,
            valid ? "W" : "dBm", true, new(run.StartedAtUtc, run.StartedAtUtc.AddHours(-2), SourceTimeMeaning.Measurement, true),
            new(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.Demo, run.StartedAtUtc), target);
        var normalized = new NormalizedPower(valid ? watts : null, valid ? PowerValueReason.Valid : PowerValueReason.DifferentQuantity);
        var attribution = new PowerAttribution(sample.Id, valid ? PowerAttributionStatus.Attributed : PowerAttributionStatus.Unknown,
            valid ? PowerAttributionReason.DirectApMeasurement : PowerAttributionReason.InvalidSample, sample.Timing.SourceAtUtc, valid ? target : null, null);
        ReadSql("""
            INSERT INTO power_samples(sample_id,run_id,profile_key,scope_version,endpoint_role,source_device_id,source_interface_id,metric,ap_id,generation_id,interval_id,point_id,read_at_utc,source_at_utc,sample_json,normalized_json,attribution_json)
            SELECT $id,run_id,profile_key,$scope,$role,$device,'eth0',$metric,$ap,$gen,$interval,NULL,$read,$source,$sample,$normalized,$attribution FROM enrichment_run_bindings WHERE run_id=$run
            """,
            ("$id", sample.Id), ("$run", run.RunId), ("$scope", run.Profile.ScopeVersion), ("$role", sample.Endpoint.Role), ("$device", row.SourceApId), ("$metric", sample.Metric),
            ("$ap", valid ? row.ApId : null), ("$gen", valid ? row.DeviceGenerationId : null), ("$interval", valid ? row.MonitoringIntervalId : null),
            ("$read", sample.Timing.ReadAtUtc), ("$source", sample.Timing.SourceAtUtc), ("$sample", JsonSerializer.Serialize(sample)), ("$normalized", JsonSerializer.Serialize(normalized)), ("$attribution", JsonSerializer.Serialize(attribution)));
    }
    private void ReadSql(string sql, params (string Key, object? Value)[] values)
    {
        using var connection = Connect(); using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value switch { null => DBNull.Value, Guid id => id.ToString("D"),
            DateTimeOffset at => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), Enum e => Convert.ToInt32(e), _ => value });
        command.ExecuteNonQuery();
    }
}
