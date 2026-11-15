using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using AirWaveApMonitor.App.Collection;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Demo;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Storage;
using AirWaveApMonitor.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace AirWaveApMonitor.Tests.Storage;

// The production finalization budget is the oracle. Avoid unrelated test CPU/I/O
// contention, rather than changing that budget or adding a machine-specific timer.
[CollectionDefinition(nameof(EnrichmentScaleCollection), DisableParallelization = true)]
public sealed class EnrichmentScaleCollection { }

[Collection(nameof(EnrichmentScaleCollection))]
public sealed class EnrichmentScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Demo2048Targets_RealRunnerCommitsAllPowerWithinItsProductionFinalizationBudget()
    {
        const int targetCount = 2048;
        using var directory = new TemporaryDirectory();
        var location = StoragePaths.Open(AppMode.Demo, directory.Path);
        using var gate = new SemaphoreSlim(1, 1);
        var clock = TimeProvider.System;
        var store = new SqliteCollectionStore(location, gate, clock);
        // Match the normal Demo discovery path: the first AP batch persists targets.
        var profile = new ProfileSnapshot(Guid.NewGuid(), location.DatasetId, ScenarioApSource.DemoOrigin, 1, 1, [],
            BaselineStatus.Confirmed, new(connectionCollectionEnabled: true, powerCollectionEnabled: true))
        { CredentialsVersion = 1 };

        var seedWatch = Stopwatch.StartNew();
        var seed = await store.BeginRunAsync(profile, CollectionKind.ApStatus, RunTrigger.Manual, default);
        var now = clock.GetUtcNow();
        var evidence = new FieldEvidence(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.Demo, now);
        var fields = new[] { "is_up", "name", "serial_number", "lan_ip" }.ToImmutableDictionary(key => key, _ => evidence);
        var observations = Enumerable.Range(1, targetCount).Select(id => new ApObservation(id.ToString(CultureInfo.InvariantCulture),
            ApState.Up, new("synthetic-" + id, null), new("AP " + id, $"198.18.{id / 256}.{id % 256}", "synthetic", "fixture"),
            new(), new(), now, null, null, fields)).ToImmutableArray();
        var seeded = await store.ApplyApBatchAsync(seed, new(RunOutcome.Succeeded, observations, [], now,
            ObservationCompleteness.Complete, []), default);
        Assert.Equal(RunOutcome.Succeeded, seeded.Outcome);
        Assert.Equal(targetCount, seeded.Summary.ConfirmedUp);
        var apClocks = await store.GetSuccessTimesAsync(location.DatasetId, 1, default);
        Assert.Equal(seed.RunId, apClocks.LastValidRunId);
        Assert.Equal(seed.RunId, apClocks.LastCompleteRunId);
        Assert.NotNull(apClocks.LastValidCommittedAtUtc);
        Assert.NotNull(apClocks.LastCompleteCommittedAtUtc);
        output.WriteLine($"AP seed: {targetCount} actual SQLite targets, {seedWatch.Elapsed.TotalSeconds:F3}s");

        var health = new CollectorHealth(clock);
        var runner = new EnrichmentRunner(store, new DemoEnrichmentSource(clock), health, clock);
        // This fixture is the sole collection caller: no coordinator/background worker
        // changes the immutable profile or runs alongside the real runner.
        var neighbors = await Enrich(CollectionKind.ConnectionNeighbor);
        Assert.Equal(targetCount, neighbors.Run.PortCount);
        var power = await Enrich(CollectionKind.Power);
        Assert.Equal(apClocks, await store.GetSuccessTimesAsync(location.DatasetId, 1, default));
        Assert.Equal(12288, power.Run.PowerSampleCount);
        Assert.Equal(12288, power.Evidence.PowerSamples.Length);
        Assert.Equal(targetCount, power.Run.PowerStateCount);

        var expected = new Dictionary<Guid, (string Source, Guid Generation, Guid Interval)>();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(location.DataDirectory, "monitor.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT a.ap_id,a.source_ap_id,c.generation_id,c.interval_id FROM aps a JOIN ap_current c USING(ap_id)";
                using var reader = command.ExecuteReader();
                while (reader.Read()) expected.Add(Guid.Parse(reader.GetString(0)),
                    (reader.GetString(1), Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3))));
            }
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM power_samples WHERE run_id=$run";
            count.Parameters.AddWithValue("$run", power.Run.RunId);
            Assert.Equal(12288L, (long)count.ExecuteScalar()!);
        }
        Assert.Equal(targetCount, expected.Count);
        Assert.All(power.Evidence.PowerSamples, value =>
        {
            Assert.Equal(PowerValueReason.Valid, value.Normalized.Reason);
            Assert.NotNull(value.Normalized.Watts);
            Assert.Equal(PowerAttributionStatus.Attributed, value.Attribution.Status);
            var target = Assert.IsType<EnrichmentTargetBinding>(value.Attribution.Target);
            Assert.True(expected.TryGetValue(target.ApId, out var original));
            Assert.Equal(original.Source, target.SourceApId);
            Assert.Equal(original.Generation, target.DeviceGenerationId);
            Assert.Equal(original.Interval, target.MonitoringIntervalId);
            Assert.Equal(power.Run.RunId, value.Sample.RunId);
        });
        var perTarget = power.Evidence.PowerSamples.GroupBy(value => value.Attribution.Target!.ApId).ToArray();
        Assert.Equal(targetCount, perTarget.Length);
        foreach (var target in perTarget)
            Assert.Equal(new[] { PowerMetric.ApConsumption, PowerMetric.PortActual, PowerMetric.Requested,
                PowerMetric.Allocated, PowerMetric.Reserved, PowerMetric.Maximum }, target.Select(value => value.Sample.Metric).Order().ToArray());
        Assert.False(runner.RecoveryPending);

        async Task<EnrichmentRunDetail> Enrich(CollectionKind kind)
        {
            long runId = 0;
            var watch = Stopwatch.StartNew();
            await runner.ExecuteAsync(profile, kind, RunTrigger.Manual, () => false, id => runId = id, default);
            Assert.True(runId > 0);
            var detail = await store.GetEnrichmentRunAsync(location.DatasetId, runId, default);
            output.WriteLine($"{kind}: {watch.Elapsed.TotalSeconds:F3}s, lifecycle={detail.Run.Lifecycle}, outcome={detail.Run.Outcome}, samples={detail.Run.PowerSampleCount}, recovery={runner.RecoveryPending}");
            Assert.Equal(RunLifecycle.Finalized, detail.Run.Lifecycle);
            Assert.Equal(RunOutcome.Succeeded, detail.Run.Outcome);
            Assert.Equal(ObservationCompleteness.Complete, detail.Run.Completeness);
            Assert.Equal(EnrichmentApplicationDisposition.Current, detail.Run.Disposition);
            Assert.Empty(detail.Run.Errors);
            Assert.False(runner.RecoveryPending);
            var durable = await store.GetEnrichmentHealthAsync(profile, kind, default);
            Assert.Equal(runId, durable.LastFinalRunId);
            Assert.Equal(RunOutcome.Succeeded, durable.LastOutcome);
            Assert.NotNull(durable.LastValidCommittedAtUtc);
            Assert.NotNull(durable.LastCompleteCommittedAtUtc);
            Assert.False(durable.StorageRecoveryPending);
            Assert.False(durable.HasQualityGap);
            return detail;
        }
    }
}
