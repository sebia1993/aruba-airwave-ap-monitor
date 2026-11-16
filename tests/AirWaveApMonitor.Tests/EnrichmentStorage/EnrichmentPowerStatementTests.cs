using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Tests.Storage;

public sealed partial class SqliteCollectionStoreTests
{
    [Fact]
    public async Task PowerBatch_NullableColumnsDoNotLeakBetweenSamples()
    {
        var row = await SeedOperations();
        var point = Guid.NewGuid();
        await store.SavePointAsync(new(Guid.NewGuid(), point, 0, "표본 지점", null, new()), default);
        await store.MovePlacementAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 0, point, 1, "등록"), default);
        var run = await BeginEnrichment(CollectionKind.Power);
        var batch = Enriched(run, await store.GetFrozenEnrichmentTargetsAsync(run, default));
        var first = batch.Detail!.PowerSamples.Single(s => s.Metric == PowerMetric.ApConsumption && s.DirectTarget!.ApId == row.ApId);
        var second = first with { Id = Guid.NewGuid(), Endpoint = new("unattributed", null, EndpointRole.AccessPoint),
            DirectTarget = null, Timing = first.Timing with { SourceAtUtc = null } };
        var third = first with { Id = Guid.NewGuid(), Value = 12m };
        await store.ApplyEnrichmentBatchAsync(run, batch with { Detail = new([], [], [first, second, third]) }, default);
        var saved = (await store.GetEnrichmentRunAsync(location.DatasetId, run.RunId, default)).Evidence.PowerSamples;
        Assert.Equal(3, saved.Length);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(saved[0].Sample));
        Assert.Equal(JsonSerializer.Serialize(second), JsonSerializer.Serialize(saved[1].Sample));
        Assert.Equal(JsonSerializer.Serialize(third), JsonSerializer.Serialize(saved[2].Sample));
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_interface_id,ap_id,generation_id,interval_id,point_id,source_at_utc FROM power_samples ORDER BY sample_sequence";
        using var reader = command.ExecuteReader();
        for (var index = 0; index < 3; index++)
        {
            Assert.True(reader.Read());
            if (index == 1)
            {
                for (var column = 0; column < 6; column++) Assert.True(reader.IsDBNull(column));
            }
            else
            {
                Assert.Equal(first.Endpoint.SourceInterfaceId, reader.GetString(0));
                Assert.Equal(row.ApId.ToString("D"), reader.GetString(1));
                Assert.Equal(row.DeviceGenerationId.ToString("D"), reader.GetString(2));
                Assert.Equal(row.MonitoringIntervalId.ToString("D"), reader.GetString(3));
                Assert.Equal(point.ToString("D"), reader.GetString(4));
                Assert.Equal(first.Timing.SourceAtUtc, DateTimeOffset.Parse(reader.GetString(5)));
            }
        }
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task PowerBatch_DeduplicatesInOrderAndKeepsEachReadOnItsOwnRow()
    {
        await SeedOperations();
        var run = await BeginEnrichment(CollectionKind.Power);
        var batch = Enriched(run, await store.GetFrozenEnrichmentTargetsAsync(run, default));
        var first = batch.Detail!.PowerSamples[0] with { SourceIdentity = new("first", true) };
        var repeated = first with { Id = Guid.NewGuid(), Timing = first.Timing with { ReadAtUtc = first.Timing.ReadAtUtc.AddSeconds(1) } };
        var other = first with { Id = Guid.NewGuid(), Value = 12m, SourceIdentity = new("other", true) };
        await store.ApplyEnrichmentBatchAsync(run, batch with { Detail = new([], [], [first, repeated, other]) }, default);
        Assert.Equal(2, Count("power_samples")); Assert.Equal(3, Count("enrichment_power_reads"));
        using var connection = Connect(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT p.sample_id FROM enrichment_power_reads r JOIN power_samples p USING(sample_sequence) ORDER BY r.ordinal";
        using var reader = command.ExecuteReader();
        foreach (var id in new[] { first.Id, first.Id, other.Id })
        { Assert.True(reader.Read()); Assert.Equal(id.ToString("D"), reader.GetString(0)); }
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task PowerBatch_RegressedSampleSeesEarlierWritesWithoutReplacingCurrent()
    {
        await SeedOperations(); clock.Advance();
        var run = await BeginEnrichment(CollectionKind.Power);
        var batch = Enriched(run, await store.GetFrozenEnrichmentTargetsAsync(run, default));
        var first = batch.Detail!.PowerSamples[0] with { SourceIdentity = new("newer", true) };
        var older = first with { Id = Guid.NewGuid(), Value = 3m, SourceIdentity = new("older", true),
            Timing = first.Timing with { SourceAtUtc = first.Timing.SourceAtUtc!.Value.AddMinutes(-1) } };
        var newest = first with { Id = Guid.NewGuid(), Value = 12m, SourceIdentity = new("newest", true),
            Timing = first.Timing with { SourceAtUtc = first.Timing.SourceAtUtc!.Value.AddMinutes(1) } };
        var receipt = await store.ApplyEnrichmentBatchAsync(run, batch with { Detail = new([], [], [first, older, newest]) }, default);
        Assert.Equal(RunOutcome.Partial, receipt.Outcome);
        Assert.Equal(3, Count("power_samples")); Assert.Equal(3, Count("enrichment_power_reads"));
        Assert.Equal(2, Count("enrichment_accepted_power"));
        using var connection = Connect(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT p.sample_id FROM enrichment_power_current c JOIN power_samples p USING(sample_sequence)";
        Assert.Equal(newest.Id.ToString("D"), command.ExecuteScalar());
    }

    [Fact]
    public async Task PowerBatch_LateKeyConflictRollsBackEarlierWritesAndCanRetrySameRun()
    {
        await SeedOperations();
        var run = await BeginEnrichment(CollectionKind.Power);
        var batch = Enriched(run, await store.GetFrozenEnrichmentTargetsAsync(run, default));
        var first = batch.Detail!.PowerSamples[0] with { SourceIdentity = new("same-key", true) };
        var conflict = first with { Id = Guid.NewGuid(), Value = 123m };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ApplyEnrichmentBatchAsync(run,
            batch with { Detail = new([], [], [first, conflict]) }, default));
        foreach (var table in new[] { "power_samples", "power_sample_keys", "enrichment_power_reads", "enrichment_power_current", "enrichment_accepted_power" })
            Assert.Equal(0, Count(table));
        var receipt = await store.ApplyEnrichmentBatchAsync(run, batch with { Detail = new([], [], [first]) }, default);
        Assert.Equal(RunOutcome.Succeeded, receipt.Outcome);
        Assert.Equal(1, Count("power_samples")); Assert.Equal(1, Count("enrichment_power_reads"));
    }
}
