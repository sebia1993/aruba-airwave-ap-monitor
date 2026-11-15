using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace AirWaveApMonitor.Tests.Storage;

public sealed partial class SqliteCollectionStoreTests
{
    [Fact]
    public async Task PreviewRejectsMissingPreferredValue_AndExplicitClearMustChooseAutomaticFallback()
    {
        var row = await SeedOperations();
        var missing = await Csv($"ap_id,manual_preferred_fields\n{row.ApId},name\n");
        Assert.False(missing.Rows.Single().IsValid);
        await store.SaveInventoryAsync(Edit(row), default);
        var badClear = await Csv($"ap_id,clear_fields,clear_reason\n{row.ApId},name,등록 해제\n");
        Assert.False(badClear.Rows.Single().IsValid);
        var clear = await Csv($"ap_id,clear_fields,clear_reason,manual_preferred_fields\n{row.ApId},name,등록 해제,none\n");
        Assert.True(clear.Rows.Single().IsValid);
        await store.ApplyImportAsync(new(Guid.NewGuid(), clear.PreviewId, clear.Version, [1]), Profile(), default);
        var saved = (await store.GetRegistrationAsync(row.ApId, default)).Registration.Inventory!;
        Assert.Empty(saved.Values); Assert.Empty(saved.ManualPreferredFields);
        Assert.Equal("AP-100", (await store.GetRegistrationAsync(row.ApId, default)).Automatic.Device.Name);
    }

    [Fact]
    public async Task TimelineCombinedFilters_RequireSameExplicitTargetRelationship()
    {
        var row = await SeedOperations(); var other = (await Dashboard()).Rows.Single(r => r.SourceApId == "200");
        var pointA = Guid.NewGuid(); var pointB = Guid.NewGuid();
        await store.SavePointAsync(new(Guid.NewGuid(), pointA, 0, "A", null, new()), default);
        await store.SavePointAsync(new(Guid.NewGuid(), pointB, 0, "B", null, new()), default);
        await store.SaveWorkAsync(new(Guid.NewGuid(), Guid.NewGuid(), 0,
            Work(new WorkTarget(row.ApId, row.DeviceGenerationId, InstallationPointId: pointA),
                new WorkTarget(other.ApId, other.DeviceGenerationId, InstallationPointId: pointB)), null), default);
        Assert.Single((await store.GetTimelineAsync(new(ApId: row.ApId, InstallationPointId: pointA, Kind: OperationHistoryKind.WorkAction), default)).Rows);
        Assert.Empty((await store.GetTimelineAsync(new(ApId: row.ApId, InstallationPointId: pointB, Kind: OperationHistoryKind.WorkAction), default)).Rows);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveWorkAsync(new(Guid.NewGuid(), Guid.NewGuid(), 0,
            Work(new WorkTarget(other.ApId, row.DeviceGenerationId, row.OpenIncidentId)), null), default));
    }

    [Fact]
    public async Task ManualClockRollback_PreservesRecordedTimeAndFlagsReview()
    {
        var row = await SeedOperations(); await store.SaveInventoryAsync(Edit(row), default);
        var earlier = clock.GetUtcNow().AddHours(-1); clock.Set(earlier);
        await store.SaveInventoryAsync(Edit(row, 1, "시계 역행 후 등록"), default);
        var change = (await store.GetTimelineAsync(new(ApId: row.ApId), default)).Rows.Single(e => e.AfterValue == "시계 역행 후 등록");
        Assert.Equal(earlier, change.RecordedAtUtc); Assert.True(change.TimeNeedsReview); Assert.Null(change.ActualWorkAtUtc);
    }

    [Fact]
    public async Task ImportLostReceipt_RetryReturnsOriginalRowsAndNeverRepeatsWrites()
    {
        var row = await SeedOperations(); var preview = await Csv($"ap_id,name\n{row.ApId},가져온 값\n");
        var edit = new ImportApply(Guid.NewGuid(), preview.PreviewId, preview.Version, [1]);
        var failing = NewStore(b => { if (b == StorageTransactionBoundary.AfterFinalCommit) throw new IOException("receipt disconnected"); });
        await Assert.ThrowsAsync<IOException>(() => failing.ApplyImportAsync(edit, Profile(), default));
        var replay = await failing.ApplyImportAsync(edit, Profile(), default);
        Assert.True(replay.AlreadyApplied); Assert.Equal(1, Assert.Single(replay.Rows).ManualVersion);
        Assert.Equal(1, Count("manual_inventory_versions")); Assert.Equal(1, Count("operation_commands"));
    }

    [Fact]
    public async Task CurrentPointView_DistinguishesOldGenerationOpenRegistrationAfterReplacement()
    {
        var row = await SeedOperations(); var point = Guid.NewGuid();
        await store.SavePointAsync(new(Guid.NewGuid(), point, 0, "지점", null, new()), default);
        await store.MovePlacementAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 0, point, 1, "등록"), default);
        Assert.Contains(row.DeviceGenerationId, (await store.GetPointAsync(point, default)).CurrentDeviceGenerationIds!);
        await Apply(Observation("100", ApState.Down, serial: "NEW"), Observation("200", ApState.Up));
        var history = await store.GetPointAsync(point, default);
        Assert.Null(Assert.Single(history.Placements).RecordedEndAtUtc);
        Assert.DoesNotContain(row.DeviceGenerationId, history.CurrentDeviceGenerationIds!);
        Assert.Null((await store.GetRegistrationAsync(row.ApId, default)).Registration.Placement);
    }

    [Fact]
    public async Task OperationHistoryDatabaseConstraints_PreventVersionAndTargetRewriteOrDeletion()
    {
        var row = await SeedOperations(); await store.SaveInventoryAsync(Edit(row), default);
        await store.SaveWorkAsync(new(Guid.NewGuid(), Guid.NewGuid(), 0, Work(new WorkTarget(row.ApId, row.DeviceGenerationId, row.OpenIncidentId)), null), default);
        using var connection = Connect(); using var command = connection.CreateCommand();
        foreach (var table in new[] { "manual_inventory_versions", "work_versions", "work_targets", "operation_events", "operation_event_targets", "operation_commands" })
        {
            command.CommandText = $"DELETE FROM {table}";
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        }
        command.CommandText = "UPDATE work_versions SET version_json='{}'"; Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public async Task ApCommitFailure_RollsBackAutomaticEventsTogetherWithGenerationAndIncident()
    {
        var row = await SeedOperations(); var before = Count("operation_events");
        var failing = NewStore(b => { if (b == StorageTransactionBoundary.BeforeFinalCommit) throw new IOException("AP transaction rollback"); });
        var run = await Begin(failing);
        await Assert.ThrowsAsync<IOException>(() => failing.ApplyApBatchAsync(run, Batch(Observation("100", ApState.Up, "changed", "NEW"), Observation("200", ApState.Up)), default));
        Assert.Equal(before, Count("operation_events")); Assert.Equal(2, Count("generations"));
        Assert.Equal(row.DeviceGenerationId, (await store.GetRegistrationAsync(row.ApId, default)).Registration.CurrentDeviceGenerationId);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM incidents WHERE is_open=1"));
    }

    [Fact]
    public async Task EmptyOrForbiddenCsvCannotCreateObservationOrSilentlyClearManualValues()
    {
        var row = await SeedOperations(); await store.SaveInventoryAsync(Edit(row), default);
        var empty = await Csv($"ap_id,name\n{row.ApId},\n");
        var result = await store.ApplyImportAsync(new(Guid.NewGuid(), empty.PreviewId, empty.Version, [1]), Profile(), default);
        Assert.False(result.Rows.Single().Applied); Assert.Equal(1, Count("manual_inventory_versions"));
        var forbidden = await Csv($"ap_id,is_up,power_w,lldp\n{row.ApId},true,15,healthy\n");
        Assert.False(forbidden.IsValidDocument);
        await Assert.ThrowsAsync<OperationsConflictException>(() => store.ApplyImportAsync(new(Guid.NewGuid(), forbidden.PreviewId, forbidden.Version, [1]), Profile(), default));
        Assert.Equal(1, Count("runs")); Assert.Equal(1, Scalar("SELECT COUNT(*) FROM incidents WHERE is_open=1"));
    }
    [Fact]
    public async Task TimelineRejectsFutureWatermark_AndDateAndKindFiltersCountExactEvents()
    {
        var row = await SeedOperations();
        var at = clock.GetUtcNow(); await store.SaveInventoryAsync(Edit(row), default);
        await Assert.ThrowsAsync<OperationsConflictException>(() => store.GetTimelineAsync(new(ThroughEventId: long.MaxValue), default));
        var filtered = await store.GetTimelineAsync(new(ApId: row.ApId, Kind: OperationHistoryKind.BasicInformation, FromUtc: at, ToUtc: at), default);
        Assert.Equal(2, filtered.TotalRows); Assert.All(filtered.Rows, e => Assert.Equal(at, e.RecordedAtUtc));
        var before = await store.GetTimelineAsync(new(ApId: row.ApId, Kind: OperationHistoryKind.BasicInformation, ToUtc: at.AddTicks(-1)), default);
        Assert.DoesNotContain(before.Rows, e => e.Source == EvidenceSource.Manual);
    }

}
