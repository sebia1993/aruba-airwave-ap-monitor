using System.Collections.Immutable;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Storage;

namespace AirWaveApMonitor.Tests.Storage;

public sealed partial class SqliteCollectionStoreTests
{
    private static WorkContent Work(params WorkTarget[] targets)
        => new("점검", WorkStatus.Completed, "작업 기록", "확인", null, CauseStatus.Investigating, null,
            null, null, null, "작업자", targets.ToImmutableArray());

    [Fact]
    public async Task OperationRollback_PreservesInventoryTimelineAndReceiptTogether()
    {
        var row = await SeedOperations(); var before = await store.GetTimelineAsync(new(), default);
        var failing = NewStore(boundary => { if (boundary == StorageTransactionBoundary.BeforeFinalCommit) throw new IOException("disk commit failed"); });
        await Assert.ThrowsAsync<IOException>(() => failing.SaveInventoryAsync(Edit(row), default));
        Assert.Null((await store.GetRegistrationAsync(row.ApId, default)).Registration.Inventory);
        Assert.Equal(0, Count("operation_commands")); Assert.Equal(0, Count("manual_inventory_versions"));
        Assert.Equal(before.ThroughEventId, (await store.GetTimelineAsync(new(), default)).ThroughEventId);
    }

    [Fact]
    public async Task BeginFreezesManualPolicyAndPlacement_AndLaterEditsWaitForNextRun()
    {
        var row = await SeedOperations(); var point = Guid.NewGuid();
        await store.SavePointAsync(new(Guid.NewGuid(), point, 0, "이전 지점", null, new()), default);
        await store.SaveInventoryAsync(Edit(row), default);
        await store.MovePlacementAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 0, point, 1, "등록"), default);
        var frozen = await Begin();
        await store.SaveInventoryAsync(Edit(row, 1, "다음 회차 이름"), default);
        await store.SavePointAsync(new(Guid.NewGuid(), point, 1, "정정 지점", null, new(), "표기 정정"), default);
        await store.SaveTrackingPolicyAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 0, OperationalStatus.MonitoringExcluded, "점검"), default);
        await store.ApplyApBatchAsync(frozen, Batch(Observation("100", ApState.Down), Observation("200", ApState.Up)), default);
        var saved = (await Dashboard(frozen.RunId)).Rows.Single(r => r.ApId == row.ApId);
        Assert.Equal("수동 이름", saved.ManualInventory!.Values[InventoryField.Name]);
        Assert.Equal(1, saved.ManualInventory.Placement!.PointVersion); Assert.Null(saved.ManualManagement);
        Assert.True((await store.GetRegistrationAsync(row.ApId, default)).Registration.PendingNextCollection);
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        Assert.DoesNotContain((await Dashboard()).Rows, r => r.ApId == row.ApId);
        Assert.False((await store.GetRegistrationAsync(row.ApId, default)).Registration.PendingNextCollection);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM incidents WHERE is_open=1"));
    }

    [Fact]
    public async Task EndTracking_ReentryWithoutObservationStartsNewIntervalAndDoesNotRecoverOldIncident()
    {
        var row = await SeedOperations();
        await store.SaveTrackingPolicyAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 0, OperationalStatus.MonitoringExcluded, "추적 종료", MonitoringDirective.EndTracking), default);
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM incidents WHERE is_open=1"));
        await store.SaveTrackingPolicyAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 1, OperationalStatus.Operating, "다시 추적"), default);
        var reentry = await Apply(Observation("200", ApState.Up));
        var returned = (await Dashboard()).Rows.Single(r => r.ApId == row.ApId);
        Assert.NotEqual(row.MonitoringIntervalId, returned.MonitoringIntervalId);
        Assert.Null(returned.CurrentState); Assert.Null(returned.LastValidState); Assert.Null(returned.OpenIncidentId);
        Assert.Equal(0, reentry.Summary.Recovered);
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        Assert.Equal(2, Count("incidents"));
    }

    [Fact]
    public async Task Replacement_DoesNotInheritRegistration_AndOldWorkRetainsExactGeneration()
    {
        var old = await SeedOperations(); var point = Guid.NewGuid();
        await store.SaveInventoryAsync(Edit(old), default);
        await store.SavePointAsync(new(Guid.NewGuid(), point, 0, "설치점", null, new()), default);
        await store.MovePlacementAsync(new(Guid.NewGuid(), old.ApId, old.DeviceGenerationId, 0, point, 1, "설치"), default);
        await Apply(Observation("100", ApState.Down, serial: "NEW-SERIAL"), Observation("200", ApState.Up));
        var current = (await Dashboard()).Rows.Single(r => r.ApId == old.ApId);
        Assert.NotEqual(old.DeviceGenerationId, current.DeviceGenerationId); Assert.Null(current.ManualInventory);
        var registered = (await store.GetRegistrationAsync(old.ApId, default)).Registration;
        Assert.Null(registered.Inventory); Assert.Null(registered.Placement);
        await Assert.ThrowsAsync<OperationsConflictException>(() => store.SaveInventoryAsync(Edit(old, 1), default));
        var workId = Guid.NewGuid();
        await store.SaveWorkAsync(new(Guid.NewGuid(), workId, 0, Work(new WorkTarget(old.ApId, old.DeviceGenerationId, old.OpenIncidentId, point)), null), default);
        Assert.Equal(old.DeviceGenerationId, (await store.GetWorkAsync(workId, default)).Current.Content.Targets.Single().DeviceGenerationId);
        Assert.Single((await store.GetTimelineAsync(new(DeviceGenerationId: old.DeviceGenerationId, Kind: OperationHistoryKind.WorkAction), default)).Rows);
        Assert.Empty((await store.GetTimelineAsync(new(DeviceGenerationId: current.DeviceGenerationId, Kind: OperationHistoryKind.WorkAction), default)).Rows);
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM incidents WHERE json_extract(tracking_json,'$.RecoveredRunId') IS NOT NULL"));
    }

    [Fact]
    public async Task ReplacementConfirmation_RequiresCompletedExactTargetsAndPreservesOriginalAutomaticEvidence()
    {
        var old = await SeedOperations(); await Apply(Observation("100", ApState.Down, serial: "REPLACED"), Observation("200", ApState.Up));
        var current = (await Dashboard()).Rows.Single(r => r.ApId == old.ApId);
        var detected = Assert.Single((await store.GetTimelineAsync(new(ApId: old.ApId, Kind: OperationHistoryKind.HardwareReplacement), default)).Rows);
        var original = JsonSerializer.Serialize(detected); var workId = Guid.NewGuid();
        var content = Work(new WorkTarget(old.ApId, old.DeviceGenerationId), new(old.ApId, current.DeviceGenerationId)) with { Status = WorkStatus.Planned };
        await store.SaveWorkAsync(new(Guid.NewGuid(), workId, 0, content, null), default);
        var confirm = new ReplacementConfirmationEdit(Guid.NewGuid(), detected.EventId, old.DeviceGenerationId, current.DeviceGenerationId, workId, 1, "교체 작업 확인");
        await Assert.ThrowsAsync<ArgumentException>(() => store.ConfirmReplacementAsync(confirm, default));
        await store.SaveWorkAsync(new(Guid.NewGuid(), workId, 1, content with { Status = WorkStatus.Completed }, "완료 확인"), default);
        confirm = confirm with { ExpectedWorkVersion = 2 };
        await store.ConfirmReplacementAsync(confirm, default);
        Assert.True((await store.ConfirmReplacementAsync(confirm, default)).AlreadyApplied);
        var events = (await store.GetTimelineAsync(new(ApId: old.ApId, Kind: OperationHistoryKind.HardwareReplacement), default)).Rows;
        Assert.Equal(2, events.Length); Assert.Equal(original, JsonSerializer.Serialize(events.Single(e => e.EventId == detected.EventId)));
        Assert.True(events.Single(e => e.EventId == detected.EventId).AutomaticChange!.NeedsConfirmation);
        Assert.Null(events.Single(e => e.ReplacementConfirmation is not null).ReplacementConfirmation!.ActualWorkAtUtc);
        await Assert.ThrowsAsync<OperationsConflictException>(() => store.ConfirmReplacementAsync(confirm with { CommandId = Guid.NewGuid() }, default));
    }

    [Fact]
    public async Task StaleSerialAndMac_AreFilteredBeforeIdentityResolution_UnknownSourceTimeStillAllowsReplacement()
    {
        ApObservation Identity(string serial, string mac, DateTimeOffset? sourceAt)
        {
            var observation = Observation("100", ApState.Down, serial: serial);
            var evidence = observation.Fields["serial_number"] with { SourceAtUtc = sourceAt };
            return observation with { Identity = new(serial, mac), Fields = observation.Fields.SetItem("serial_number", evidence).SetItem("lan_mac", evidence) };
        }
        var acceptedAt = clock.GetUtcNow();
        await Apply(Identity("FRESH", "00:11:22:33:44:55", acceptedAt), Observation("200", ApState.Up));
        var old = (await Dashboard()).Rows.Single(r => r.SourceApId == "100");
        await Apply(Identity("STALE", "00:11:22:33:44:66", acceptedAt.AddMinutes(-5)), Observation("200", ApState.Up));
        var unchanged = (await Dashboard()).Rows.Single(r => r.ApId == old.ApId);
        Assert.Equal(old.DeviceGenerationId, unchanged.DeviceGenerationId); Assert.Equal(old.Identity, unchanged.Identity);
        Assert.Equal(acceptedAt, unchanged.Fields["serial_number"].SourceAtUtc); Assert.Equal(acceptedAt, unchanged.Fields["lan_mac"].SourceAtUtc);
        Assert.Empty((await store.GetTimelineAsync(new(ApId: old.ApId, Kind: OperationHistoryKind.HardwareReplacement), default)).Rows);
        await Apply(Identity("NEW-WITHOUT-TIME", "00:11:22:33:44:77", null), Observation("200", ApState.Up));
        Assert.NotEqual(old.DeviceGenerationId, (await Dashboard()).Rows.Single(r => r.ApId == old.ApId).DeviceGenerationId);
        Assert.Single((await store.GetTimelineAsync(new(ApId: old.ApId, Kind: OperationHistoryKind.HardwareReplacement), default)).Rows);
    }

    [Fact]
    public async Task Timeline_AllSixCategoriesShareStableSequenceAndWatermark_WithoutTargetDuplicates()
    {
        ApObservation Rich(string name, string firmware, string port, string serial)
        {
            var item = Observation("100", ApState.Down, name, serial); var evidence = item.Fields["name"];
            return item with { Device = item.Device with { Firmware = firmware }, Upstream = new("switch-id", "1", port, "eth0", evidence),
                Fields = item.Fields.SetItem("firmware", evidence).SetItem("upstream_device_id", evidence).SetItem("upstream_port_name", evidence) };
        }
        await Apply(Rich("A", "1", "Gi1", "OLD"), Observation("200", ApState.Up));
        var old = (await Dashboard()).Rows.Single(r => r.SourceApId == "100");
        var baseline = await store.GetTimelineAsync(new(ApId: old.ApId), default);
        Assert.All(baseline.Rows.Where(e => e.AutomaticChange is not null), e => Assert.Equal(ChangeSemantics.BaselineRegistered, e.AutomaticChange!.Semantics));
        await Apply(Rich("B", "2", "Gi2", "OLD"), Observation("200", ApState.Up));
        await Apply(Rich("C", "3", "Gi3", "NEW"), Observation("200", ApState.Up));
        var current = (await Dashboard()).Rows.Single(r => r.ApId == old.ApId);
        await store.SaveWorkAsync(new(Guid.NewGuid(), Guid.NewGuid(), 0, Work(new WorkTarget(old.ApId, old.DeviceGenerationId), new(current.ApId, current.DeviceGenerationId)), null), default);
        var all = await store.GetTimelineAsync(new(ApId: old.ApId, PageSize: 500), default);
        Assert.Equal(6, all.Rows.Select(e => e.Kind).Distinct().Count());
        Assert.Single(all.Rows.Where(e => e.Kind == OperationHistoryKind.WorkAction));
        clock.Set(clock.GetUtcNow().AddHours(-1));
        await store.SaveInventoryAsync(Edit(current), default);
        var paged = new List<long>();
        for (var page = 1; paged.Count < all.TotalRows; page++)
        {
            var part = await store.GetTimelineAsync(new(ApId: old.ApId, ThroughEventId: all.ThroughEventId, Page: page, PageSize: 3), default);
            Assert.Equal(all.TotalRows, part.TotalRows); paged.AddRange(part.Rows.Select(e => e.EventId));
        }
        Assert.Equal(all.Rows.Select(e => e.EventId), paged); Assert.Equal(paged.Count, paged.Distinct().Count());
        Assert.All(all.Rows.Where(e => e.Kind == OperationHistoryKind.Connection), e => Assert.StartsWith("upstream_", e.Field));
    }

    [Fact]
    public async Task ManualPreferredSearchAndSort_UseFrozenSnapshotWithoutChangingAutomaticValues()
    {
        var row = await SeedOperations(); var oldRun = (await Dashboard()).RunId;
        await store.SaveInventoryAsync(Edit(row, value: "ZZZ 등록 이름"), default);
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        var manual = await Dashboard(query: new(location.DatasetId, 1, Search: "ZZZ"));
        Assert.Equal("AP-100", Assert.Single(manual.Rows).Device.Name);
        Assert.Single((await Dashboard(query: new(location.DatasetId, 1, Search: "AP-100"))).Rows);
        Assert.Empty((await Dashboard(query: new(location.DatasetId, 1, oldRun, "ZZZ"))).Rows);
        var sorted = await Dashboard(query: new(location.DatasetId, 1, Sort: DashboardSort.Name));
        Assert.Equal("200", sorted.Rows[0].SourceApId);
    }
}
