using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Storage;

namespace AirWaveApMonitor.Tests.Storage;

public sealed partial class SqliteCollectionStoreTests
{
    private static InventoryEdit Edit(ApSnapshotRow row, long version = 0, string value = "수동 이름", Guid? command = null)
        => new(command ?? Guid.NewGuid(), row.ApId, row.DeviceGenerationId, version,
            ImmutableDictionary<InventoryField, string>.Empty.Add(InventoryField.Name, value), [], [InventoryField.Name]);
    private async Task<ApSnapshotRow> SeedOperations()
    { await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up)); return (await Dashboard()).Rows.Single(r => r.SourceApId == "100"); }

    [Fact]
    public async Task ManualAndAutomaticValues_AreKeptSeparately_AndPastRunsStayImmutable()
    {
        var row = await SeedOperations(); var old = await Dashboard(); var before = JsonSerializer.Serialize(old);
        var receipt = await store.SaveInventoryAsync(Edit(row), default);
        Assert.Equal(1, receipt.Version);
        var registered = await store.GetRegistrationAsync(row.ApId, default);
        Assert.True(registered.Registration.PendingNextCollection);
        Assert.Equal("수동 이름", registered.Registration.Inventory!.Values[InventoryField.Name]);
        Assert.Equal("AP-100", registered.Automatic.Device.Name);
        Assert.Equal(before, JsonSerializer.Serialize(await Dashboard(old.RunId)));
        Assert.Equal(1, Count("runs"));
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        var after = await store.GetRegistrationAsync(row.ApId, default);
        Assert.False(after.Registration.PendingNextCollection);
    }

    [Fact]
    public async Task OperationCommandReplay_IsIdempotentAndPayloadConflictIsRejected()
    {
        var row = await SeedOperations(); var edit = Edit(row);
        Assert.False((await store.SaveInventoryAsync(edit, default)).AlreadyApplied);
        Assert.True((await store.SaveInventoryAsync(edit, default)).AlreadyApplied);
        await Assert.ThrowsAsync<OperationsConflictException>(() => store.SaveInventoryAsync(edit with { SetValues = edit.SetValues.SetItem(InventoryField.Name, "다른 값") }, default));
        await Assert.ThrowsAsync<OperationsConflictException>(() => store.SaveInventoryAsync(Edit(row), default));
        Assert.Equal(1, (await store.GetRegistrationAsync(row.ApId, default)).Registration.Inventory!.Version);
    }

    [Fact]
    public async Task PersistentExclusion_DoesNotRecoverOrReplaceAndRequiresExplicitReentry()
    {
        var row = await SeedOperations();
        await store.SaveTrackingPolicyAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 0, OperationalStatus.MonitoringExcluded, "점검"), default);
        await Apply(Observation("100", ApState.Up, serial: "OTHER"), Observation("200", ApState.Up));
        var registered = await store.GetRegistrationAsync(row.ApId, default);
        Assert.Equal(row.DeviceGenerationId, registered.Registration.CurrentDeviceGenerationId);
        Assert.Equal(1, Count("incidents")); Assert.Equal(1, Scalar("SELECT COUNT(*) FROM incidents WHERE is_open=1"));
        Assert.DoesNotContain((await Dashboard()).Rows, r => r.ApId == row.ApId);
        await store.SaveTrackingPolicyAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 1, OperationalStatus.Operating, "재개"), default);
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        var active = (await Dashboard()).Rows.Single(r => r.ApId == row.ApId);
        Assert.Equal(row.MonitoringIntervalId, active.MonitoringIntervalId); Assert.Equal(row.OpenIncidentId, active.OpenIncidentId);
    }

    [Fact]
    public async Task NoteCorrection_KeepsPreviousVersion_AndNotesDoNotRecoverAp()
    {
        var row = await SeedOperations(); var workId = Guid.NewGuid();
        var content = new WorkContent("점검", WorkStatus.Planned, "최초 메모", null, null, CauseStatus.Investigating, null,
            clock.GetUtcNow(), clock.GetUtcNow().AddHours(1), null, null, [new(row.ApId, row.DeviceGenerationId, row.OpenIncidentId)]);
        await store.SaveWorkAsync(new(Guid.NewGuid(), workId, 0, content, null), default);
        await store.SaveWorkAsync(new(Guid.NewGuid(), workId, 1, content with { Content = "정정 메모", Status = WorkStatus.Completed }, "오타 정정"), default);
        await store.SaveWorkAsync(new(Guid.NewGuid(), workId, 2, content with { Status = WorkStatus.Cancelled }, "잘못 연결한 기록"), default);
        var saved = await store.GetWorkAsync(workId, default);
        Assert.Equal(3, saved.Versions.Length); Assert.Equal("최초 메모", saved.Versions[0].Content.Content);
        Assert.Equal(WorkStatus.Cancelled, saved.Current.Content.Status);
        Assert.Equal(1, Count("runs")); Assert.Equal(1, Scalar("SELECT COUNT(*) FROM incidents WHERE is_open=1"));
    }

    [Fact]
    public async Task CsvPreview_IsSideEffectFreeAndApplyRechecksManualDriftAtomically()
    {
        var row = await SeedOperations();
        var preview = await store.PreviewImportAsync(Encoding.UTF8.GetBytes($"ap_id,name\n{row.ApId},CSV 이름\n"), Profile(), default);
        Assert.True(preview.Rows.Single().IsValid);
        Assert.Null((await store.GetRegistrationAsync(row.ApId, default)).Registration.Inventory);
        await store.SaveInventoryAsync(Edit(row), default);
        await Assert.ThrowsAsync<OperationsConflictException>(() => store.ApplyImportAsync(new(Guid.NewGuid(), preview.PreviewId, preview.Version, [1]), Profile(), default));
        Assert.Equal("수동 이름", (await store.GetRegistrationAsync(row.ApId, default)).Registration.Inventory!.Values[InventoryField.Name]);
    }

    [Fact]
    public async Task PlacementChange_DoesNotRewriteOldIncident_AndPointHistoryDoesNotMergeAps()
    {
        var row = await SeedOperations(); var point1 = Guid.NewGuid(); var point2 = Guid.NewGuid();
        await store.SavePointAsync(new(Guid.NewGuid(), point1, 0, "P1", "KEY1", new()), default);
        await store.SavePointAsync(new(Guid.NewGuid(), point2, 0, "P2", "KEY2", new()), default);
        var incident = await store.GetIncidentAsync(new(location.DatasetId, 1, 1), row.OpenIncidentId!.Value, default);
        var before = JsonSerializer.Serialize(incident);
        await store.MovePlacementAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 0, point1, 1, "설치"), default);
        await store.MovePlacementAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 1, point2, 1, "이동"), default);
        var other = (await Dashboard()).Rows.Single(r => r.SourceApId == "200");
        await store.MovePlacementAsync(new(Guid.NewGuid(), other.ApId, other.DeviceGenerationId, 0, point1, 1, "새 배치"), default);
        var point = await store.GetPointAsync(point1, default);
        Assert.Equal(2, point.Placements.Length); Assert.Equal(2, point.Placements.Select(p => p.ApId).Distinct().Count());
        Assert.Equal(before, JsonSerializer.Serialize(await store.GetIncidentAsync(new(location.DatasetId, 1, 1), row.OpenIncidentId.Value, default)));
    }
}
