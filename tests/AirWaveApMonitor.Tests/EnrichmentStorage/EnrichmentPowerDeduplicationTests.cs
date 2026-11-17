using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Enrichment;

namespace AirWaveApMonitor.Tests.Storage;

public sealed partial class SqliteCollectionStoreTests
{
    [Fact]
    public async Task EnrichmentUniqueSample_ReReadAfterPlacementMoveKeepsOriginalAcceptedAttribution()
    {
        var row = await SeedOperations();
        var originalPoint = Guid.NewGuid(); var currentPoint = Guid.NewGuid();
        await store.SavePointAsync(new(Guid.NewGuid(), originalPoint, 0, "원래 지점", null, new()), default);
        await store.SavePointAsync(new(Guid.NewGuid(), currentPoint, 0, "현재 지점", null, new()), default);
        await store.MovePlacementAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 0, originalPoint, 1, "등록"), default);

        var firstRun = await BeginEnrichment(CollectionKind.Power);
        var firstBatch = Enriched(firstRun, await store.GetFrozenEnrichmentTargetsAsync(firstRun, default));
        var original = firstBatch.Detail!.PowerSamples.Single(s => s.Metric == PowerMetric.ApConsumption && s.DirectTarget!.ApId == row.ApId)
            with { SourceIdentity = new("same-source-measurement", true) };
        var firstReceipt = await store.ApplyEnrichmentBatchAsync(firstRun, firstBatch with { Detail = new([], [], [original]) }, default);
        Assert.Equal(RunOutcome.Succeeded, firstReceipt.Outcome);
        var originalEvidence = Assert.Single((await store.GetEnrichmentRunAsync(location.DatasetId, firstRun.RunId, default)).Evidence.PowerSamples);
        Assert.Equal(PowerAttributionStatus.Attributed, originalEvidence.Attribution.Status);
        Assert.Equal(originalPoint, originalEvidence.Attribution.Target!.Placement!.InstallationPointId);

        clock.Advance();
        await store.MovePlacementAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 1, currentPoint, 1, "이동"), default);
        var repeatRun = await BeginEnrichment(CollectionKind.Power);
        var currentTarget = (await store.GetFrozenEnrichmentTargetsAsync(repeatRun, default)).Single(t => t.ApId == row.ApId);
        var repeat = original with
        {
            Id = Guid.NewGuid(), RunId = repeatRun.RunId, DirectTarget = currentTarget,
            Timing = original.Timing with { ReadAtUtc = clock.GetUtcNow() }
        };
        // This later registration cannot attribute the earlier measurement. Deduplication must
        // retain the original stored attribution, even when this read's calculation is unassigned.
        var recalculated = PowerAttributionPolicy.Resolve(repeat, []);
        Assert.Equal(PowerAttributionStatus.OutsideBinding, recalculated.Status);
        Assert.Null(recalculated.Target);
        var repeatedReceipt = await store.ApplyEnrichmentBatchAsync(repeatRun,
            firstBatch with { CollectedAtUtc = clock.GetUtcNow(), Detail = new([], [], [repeat]) }, default);
        Assert.Equal(RunOutcome.Succeeded, repeatedReceipt.Outcome);
        Assert.Equal(EnrichmentApplicationDisposition.Current, repeatedReceipt.Disposition);
        Assert.Equal(1, Count("power_samples"));
        Assert.Equal(2, Count("enrichment_power_reads"));
        Assert.Equal(2, Count("enrichment_accepted_power"));
        var repeatedEvidence = Assert.Single((await store.GetEnrichmentRunAsync(location.DatasetId, repeatRun.RunId, default)).Evidence.PowerSamples);
        Assert.Equal(JsonSerializer.Serialize(originalEvidence), JsonSerializer.Serialize(repeatedEvidence));

        // A previous accepted row can hide a bad latest target in snapshot fallback. Check this
        // new acceptance itself so replacing it with the recalculated null target fails the test.
        using (var connection = Connect())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT ap_id,generation_id,interval_id FROM enrichment_accepted_power WHERE run_id=$run";
            command.Parameters.AddWithValue("$run", repeatRun.RunId);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.False(reader.IsDBNull(0), "Re-read acceptance must keep the original AP attribution.");
            Assert.Equal(row.ApId.ToString("D"), reader.GetString(0));
            Assert.Equal(row.DeviceGenerationId.ToString("D"), reader.GetString(1));
            Assert.Equal(row.MonitoringIntervalId.ToString("D"), reader.GetString(2));
            Assert.False(reader.Read());
        }
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        var snapshot = (await Dashboard()).Rows.Single(r => r.ApId == row.ApId);
        Assert.Equal(JsonSerializer.Serialize(originalEvidence), JsonSerializer.Serialize(Assert.Single(snapshot.EnrichmentDetail!.PowerSamples)));
        Assert.Equal(currentPoint, (await store.GetRegistrationAsync(row.ApId, default)).Registration.Placement!.InstallationPointId);
    }
}
