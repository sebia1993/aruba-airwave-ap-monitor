using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Storage;

namespace AirWaveApMonitor.Tests.Storage;

public sealed partial class SqliteCollectionStoreTests
{
    [Fact]
    public async Task RestoredDirectInventory_RejectsOldDraftEvenWhenEntityVersionIsReused()
    {
        var row = await SeedOperations(); Assert.Equal(0, (await store.GetVersionAsync(default)).RestoreEpoch);
        await store.SaveInventoryAsync(Edit(row), default);
        var backups = new BackupService(location, gate, clock); var backup = await backups.CreateAsync(default);
        await store.SaveInventoryAsync(Edit(row, 1, "복원 전 v2"), default);
        var staleDraft = Edit(row, 2, "복원 전 편집으로 덮기");
        await backups.RestoreAsync(backup.BackupPath, default);
        var restored = NewStore(); var epoch = (await restored.GetVersionAsync(default)).RestoreEpoch;
        Assert.True(epoch > 0);
        await restored.SaveInventoryAsync(Edit(row, 1, "복원 후 새 v2") with { ExpectedRestoreEpoch = epoch }, default);
        var count = Count("operation_commands");
        await Assert.ThrowsAsync<OperationsConflictException>(() => restored.SaveInventoryAsync(staleDraft, default));
        Assert.Equal(count, Count("operation_commands"));
        var current = (await restored.GetRegistrationAsync(row.ApId, default)).Registration.Inventory!;
        Assert.Equal(2, current.Version); Assert.Equal("복원 후 새 v2", current.Values[InventoryField.Name]);
        // Epoch rejection did not consume the command ID; an explicit fresh draft may reuse it.
        await restored.SaveInventoryAsync(staleDraft with { ExpectedRestoreEpoch = epoch, SetValues = staleDraft.SetValues.SetItem(InventoryField.Name, "다시 확인한 편집") }, default);
        Assert.Equal(3, (await restored.GetRegistrationAsync(row.ApId, default)).Registration.Inventory!.Version);
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("tracking")]
    [InlineData("point")]
    [InlineData("placement")]
    [InlineData("work")]
    [InlineData("confirmation")]
    public async Task EveryDirectCommand_RequiresCurrentRestoreEpochBeforeWriting(string kind)
    {
        var old = await SeedOperations();
        await Apply(Observation("100", ApState.Down, serial: "NEW"), Observation("200", ApState.Up));
        var current = (await Dashboard()).Rows.Single(r => r.ApId == old.ApId);
        var source = (await store.GetTimelineAsync(new(ApId: old.ApId, Kind: OperationHistoryKind.HardwareReplacement), default)).Rows.Single();
        var point = Guid.NewGuid(); await store.SavePointAsync(new(Guid.NewGuid(), point, 0, "지점", null, new()), default);
        var work = Guid.NewGuid(); await store.SaveWorkAsync(new(Guid.NewGuid(), work, 0,
            Work(new WorkTarget(old.ApId, old.DeviceGenerationId), new WorkTarget(current.ApId, current.DeviceGenerationId)), null), default);
        var backups = new BackupService(location, gate, clock); var backup = await backups.CreateAsync(default);
        await backups.RestoreAsync(backup.BackupPath, default);
        var restored = NewStore(); var epoch = (await restored.GetVersionAsync(default)).RestoreEpoch;
        var commandId = Guid.NewGuid(); var newPoint = Guid.NewGuid(); var newWork = Guid.NewGuid();
        Task<OperationCommandReceipt> Save(long expectedEpoch) => kind switch
        {
            "inventory" => restored.SaveInventoryAsync(Edit(current, command: commandId) with { ExpectedRestoreEpoch = expectedEpoch }, default),
            "tracking" => restored.SaveTrackingPolicyAsync(new(commandId, current.ApId, current.DeviceGenerationId, 0, OperationalStatus.PlannedWork, "점검", ExpectedRestoreEpoch: expectedEpoch), default),
            "point" => restored.SavePointAsync(new(commandId, newPoint, 0, "새 지점", null, new(), ExpectedRestoreEpoch: expectedEpoch), default),
            "placement" => restored.MovePlacementAsync(new(commandId, current.ApId, current.DeviceGenerationId, 0, point, 1, "배치", ExpectedRestoreEpoch: expectedEpoch), default),
            "work" => restored.SaveWorkAsync(new(commandId, newWork, 0, Work(new WorkTarget(current.ApId, current.DeviceGenerationId)), null, expectedEpoch), default),
            _ => restored.ConfirmReplacementAsync(new(commandId, source.EventId, old.DeviceGenerationId, current.DeviceGenerationId, work, 1, "실제 작업 확인", ExpectedRestoreEpoch: expectedEpoch), default)
        };
        var commands = Count("operation_commands"); var events = Count("operation_events");
        await Assert.ThrowsAsync<OperationsConflictException>(() => Save(0));
        Assert.Equal(commands, Count("operation_commands")); Assert.Equal(events, Count("operation_events"));
        var receipt = await Save(epoch); Assert.False(receipt.AlreadyApplied);
        Assert.True((await Save(epoch)).AlreadyApplied); Assert.Equal(commands + 1, Count("operation_commands"));
    }

    [Fact]
    public async Task RestoredSuccessfulCommandReplay_ReturnsSavedReceiptWithoutWrite_ButDifferentPayloadConflicts()
    {
        var row = await SeedOperations(); var edit = Edit(row);
        await store.SaveInventoryAsync(edit, default);
        var backups = new BackupService(location, gate, clock); var backup = await backups.CreateAsync(default);
        await backups.RestoreAsync(backup.BackupPath, default);
        var restored = NewStore(); var epoch = (await restored.GetVersionAsync(default)).RestoreEpoch;
        var before = Count("operation_events");
        var receipt = await restored.SaveInventoryAsync(edit, default);
        Assert.True(receipt.AlreadyApplied); Assert.Equal(1, receipt.Version); Assert.Equal(before, Count("operation_events"));
        await Assert.ThrowsAsync<OperationsConflictException>(() => restored.SaveInventoryAsync(edit with { ExpectedRestoreEpoch = epoch }, default));
        await Assert.ThrowsAsync<OperationsConflictException>(() => restored.SaveInventoryAsync(edit with { SetValues = edit.SetValues.SetItem(InventoryField.Name, "변경된 요청") }, default));
        Assert.Equal(1, Count("operation_commands"));
    }

    [Fact]
    public async Task CommandLostByRestore_IsNotReexecutedWithOldEpoch_AndNewEpochReceiptSurvivesResponseLoss()
    {
        var row = await SeedOperations(); var backups = new BackupService(location, gate, clock);
        var backup = await backups.CreateAsync(default); var edit = Edit(row);
        await store.SaveInventoryAsync(edit, default);
        await backups.RestoreAsync(backup.BackupPath, default);
        var restored = NewStore(); var epoch = (await restored.GetVersionAsync(default)).RestoreEpoch;
        await Assert.ThrowsAsync<OperationsConflictException>(() => restored.SaveInventoryAsync(edit, default));
        Assert.Equal(0, Count("operation_commands")); Assert.Equal(0, Count("manual_inventory_versions"));
        var fresh = edit with { ExpectedRestoreEpoch = epoch };
        var beforeCommit = NewStore(b => { if (b == StorageTransactionBoundary.BeforeFinalCommit) throw new IOException("uncommitted restored write"); });
        await Assert.ThrowsAsync<IOException>(() => beforeCommit.SaveInventoryAsync(fresh, default));
        Assert.Equal(0, Count("operation_commands")); Assert.Equal(0, Count("manual_inventory_versions"));
        var failing = NewStore(b => { if (b == StorageTransactionBoundary.AfterFinalCommit) throw new IOException("lost restored receipt"); });
        await Assert.ThrowsAsync<IOException>(() => failing.SaveInventoryAsync(fresh, default));
        Assert.True((await failing.SaveInventoryAsync(fresh, default)).AlreadyApplied);
        Assert.Equal(1, Count("operation_commands")); Assert.Equal(1, Count("manual_inventory_versions"));
    }

    [Fact]
    public async Task InitialEpochZero_RemainsWireCompatibleAndVersionReadDoesNotCreateRun()
    {
        var row = await SeedOperations(); var runs = Count("runs");
        var wire = JsonNode.Parse(JsonSerializer.Serialize(Edit(row)))!.AsObject(); wire.Remove("ExpectedRestoreEpoch");
        var edit = JsonSerializer.Deserialize<InventoryEdit>(wire.ToJsonString())!;
        Assert.Equal(0, edit.ExpectedRestoreEpoch);
        Assert.Equal(0, (await store.GetVersionAsync(default)).RestoreEpoch);
        await store.SaveInventoryAsync(edit, default);
        Assert.Equal(runs, Count("runs"));
    }
    [Fact]
    public async Task InitialEpochZeroFingerprint_RemainsCompatibleWithStoredPreEpochCommands()
    {
        var row = await SeedOperations(); var edit = Edit(row);
        var oldPayload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ApId"] = edit.ApId, ["ClearFields"] = edit.ClearFields, ["ClearReason"] = edit.ClearReason,
            ["CommandId"] = edit.CommandId, ["ExpectedCurrentGenerationId"] = edit.ExpectedCurrentGenerationId,
            ["ExpectedVersion"] = edit.ExpectedVersion, ["ManualPreferredFields"] = edit.ManualPreferredFields,
            ["SetValues"] = edit.SetValues
        };
        var legacyFingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(oldPayload)));
        await store.SaveInventoryAsync(edit, default);
        using var connection = Connect(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_hash FROM operation_commands WHERE command_id=$id";
        command.Parameters.AddWithValue("$id", edit.CommandId.ToString("D"));
        Assert.Equal(legacyFingerprint, command.ExecuteScalar());
    }

    [Fact]
    public async Task NonzeroDraftEpoch_AlsoExpiresAfterAnotherRestoreWithUnchangedClock()
    {
        var row = await SeedOperations(); var backups = new BackupService(location, gate, clock);
        var first = await backups.CreateAsync(default); await backups.RestoreAsync(first.BackupPath, default);
        var once = NewStore(); var epochOne = (await once.GetVersionAsync(default)).RestoreEpoch;
        var second = await backups.CreateAsync(default);
        var draft = Edit(row) with { ExpectedRestoreEpoch = epochOne };
        await backups.RestoreAsync(second.BackupPath, default);
        var twice = NewStore(); var epochTwo = (await twice.GetVersionAsync(default)).RestoreEpoch;
        Assert.True(epochTwo > epochOne);
        await Assert.ThrowsAsync<OperationsConflictException>(() => twice.SaveInventoryAsync(draft, default));
        Assert.Equal(0, Count("manual_inventory_versions")); Assert.Equal(0, Count("operation_commands"));
        await twice.SaveInventoryAsync(draft with { ExpectedRestoreEpoch = epochTwo }, default);
        Assert.Equal(1, Count("manual_inventory_versions"));
    }

}
