using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Storage;

namespace AirWaveApMonitor.Tests.Storage;

public sealed partial class SqliteCollectionStoreTests
{
    private Task<ImportPreview> Csv(string value, SqliteCollectionStore? target = null)
        => (target ?? store).PreviewImportAsync(Encoding.UTF8.GetBytes(value), Profile(), default);

    [Fact]
    public async Task ImportSelectedSubset_AppliesOnlyValidSelectedRows_AndNeverCreatesAp()
    {
        var row = await SeedOperations(); var other = (await Dashboard()).Rows.Single(r => r.SourceApId == "200");
        var preview = await Csv($"ap_id,name\n{row.ApId},선택 값\n{other.ApId},제외 값\n{Guid.NewGuid()},없는 AP\n");
        Assert.True(preview.Rows[0].IsValid); Assert.False(preview.Rows[2].IsValid);
        var apply = new ImportApply(Guid.NewGuid(), preview.PreviewId, preview.Version, [1]);
        var result = await store.ApplyImportAsync(apply, Profile(), default);
        Assert.True(Assert.Single(result.Rows).Applied); Assert.True((await store.ApplyImportAsync(apply, Profile(), default)).AlreadyApplied);
        Assert.Null((await store.GetRegistrationAsync(other.ApId, default)).Registration.Inventory);
        Assert.Equal(EvidenceSource.Import, (await store.GetRegistrationAsync(row.ApId, default)).Registration.Inventory!.Source);
        Assert.Equal(2, Count("aps")); Assert.Equal(1, Count("runs"));
        await Assert.ThrowsAsync<OperationsConflictException>(() => store.ApplyImportAsync(apply with { RowNumbers = [1, 2] }, Profile(), default));
    }

    [Fact]
    public async Task ImportRollback_RevertsEverySelectedRowAndAssociatedEventAndReceipt()
    {
        await SeedOperations(); var rows = (await Dashboard()).Rows;
        var preview = await Csv($"ap_id,name\n{rows[0].ApId},첫째\n{rows[1].ApId},둘째\n");
        var before = Count("operation_events");
        var failing = NewStore(b => { if (b == StorageTransactionBoundary.BeforeFinalCommit) throw new IOException("rollback both rows"); });
        await Assert.ThrowsAsync<IOException>(() => failing.ApplyImportAsync(new(Guid.NewGuid(), preview.PreviewId, preview.Version, [1, 2]), Profile(), default));
        Assert.Equal(0, Count("manual_inventory_versions")); Assert.Equal(0, Count("operation_commands")); Assert.Equal(before, Count("operation_events"));
        Assert.Equal(1, Count("import_previews"));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("tracking")]
    [InlineData("scope")]
    [InlineData("configuration")]
    [InlineData("placement")]
    [InlineData("point")]
    public async Task ImportPreview_RevalidatesEveryRelevantVersion(string drift)
    {
        var row = await SeedOperations(); var point = Guid.NewGuid();
        await store.SavePointAsync(new(Guid.NewGuid(), point, 0, "설치점", null, new()), default);
        var preview = await Csv($"ap_id,name,installation_point_id\n{row.ApId},가져올 값,{point}\n");
        var profile = Profile();
        switch (drift)
        {
            case "generation": await Apply(Observation("100", ApState.Down, serial: "NEW"), Observation("200", ApState.Up)); break;
            case "tracking": await store.SaveTrackingPolicyAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 0, OperationalStatus.PlannedWork, "점검"), default); break;
            case "scope": profile = new(instance, location.DatasetId, profile.Origin, 1, 2, profile.ExpectedTargets, profile.BaselineStatus, profile.Settings); break;
            case "configuration": profile = new(instance, location.DatasetId, profile.Origin, 2, 1, profile.ExpectedTargets, profile.BaselineStatus, profile.Settings); break;
            case "placement": await store.MovePlacementAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 0, point, 1, "배치"), default); break;
            case "point": await store.SavePointAsync(new(Guid.NewGuid(), point, 1, "새 표기", null, new(), "지점 정정"), default); break;
        }
        await Assert.ThrowsAsync<OperationsConflictException>(() => store.ApplyImportAsync(new(Guid.NewGuid(), preview.PreviewId, preview.Version, [1]), profile, default));
        Assert.Equal(0, Count("manual_inventory_versions"));
    }

    [Fact]
    public async Task ImportPreview_RejectsPlacementChangedAwayAndBackToUnassigned()
    {
        var row = await SeedOperations(); var point = Guid.NewGuid();
        await store.SavePointAsync(new(Guid.NewGuid(), point, 0, "설치점", null, new()), default);
        var preview = await Csv($"ap_id,name\n{row.ApId},가져올 값\n");
        await store.MovePlacementAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 0, point, 1, "배치"), default);
        await store.MovePlacementAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 1, null, null, "해제"), default);
        await Assert.ThrowsAsync<OperationsConflictException>(() => store.ApplyImportAsync(new(Guid.NewGuid(), preview.PreviewId, preview.Version, [1]), Profile(), default));
        Assert.Equal(0, Count("manual_inventory_versions"));
    }

    [Fact]
    public async Task ImportPreview_RechecksSerialUniquenessAfterNewCollection()
    {
        var row = await SeedOperations(); var preview = await Csv("serial_number,name\nSERIAL-100,가져올 값\n");
        Assert.True(preview.Rows.Single().IsValid);
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up, serial: "SERIAL-100"));
        await Assert.ThrowsAsync<OperationsConflictException>(() => store.ApplyImportAsync(new(Guid.NewGuid(), preview.PreviewId, preview.Version, [1]), Profile(), default));
        var ambiguous = await Csv("serial_number,name\nSERIAL-100,애매한 값\n");
        Assert.False(ambiguous.Rows.Single().IsValid); Assert.Equal(2, Count("aps"));
    }

    [Fact]
    public async Task RestoredPreview_IsInvalidatedEvenWhenItsOriginalDatabaseAndClockReturn()
    {
        var row = await SeedOperations(); var preview = await Csv($"ap_id,name\n{row.ApId},가져올 값\n");
        var backupService = new BackupService(location, gate, clock); var original = await backupService.CreateAsync(default);
        await backupService.RestoreAsync(original.BackupPath, default);
        var reopened = NewStore(); await reopened.InitializeAsync(default);
        await Assert.ThrowsAsync<OperationsConflictException>(() => reopened.ApplyImportAsync(new(Guid.NewGuid(), preview.PreviewId, preview.Version, [1]), Profile(), default));
        var second = await Csv($"ap_id,name\n{row.ApId},다시 가져올 값\n", reopened);
        var restoredBackup = await backupService.CreateAsync(default);
        await backupService.RestoreAsync(restoredBackup.BackupPath, default);
        var again = NewStore(); await again.InitializeAsync(default);
        await Assert.ThrowsAsync<OperationsConflictException>(() => again.ApplyImportAsync(new(Guid.NewGuid(), second.PreviewId, second.Version, [1]), Profile(), default));
        var fresh = await Csv($"ap_id,name\n{row.ApId},새 미리보기\n", again);
        Assert.True(fresh.Version.RestoreEpoch > second.Version.RestoreEpoch);
        await again.ApplyImportAsync(new(Guid.NewGuid(), fresh.PreviewId, fresh.Version, [1]), Profile(), default);
        Assert.Equal("새 미리보기", (await again.GetRegistrationAsync(row.ApId, default)).Registration.Inventory!.Values[InventoryField.Name]);
    }

    [Fact]
    public async Task SchemaThreeUpgrade_BackfillsOnlySavedEvidenceAndKeepsOldSnapshotsImmutable()
    {
        await SeedOperations(); await Apply(Observation("100", ApState.Up, name: "Renamed"), Observation("200", ApState.Up));
        var before = JsonSerializer.Serialize(await Dashboard(1)); var automatic = await store.GetTimelineAsync(new(PageSize: 500), default);
        // Remove exactly migration 004 objects to construct the preceding real SQLite schema and source rows.
        var assembly = typeof(SchemaMigrator).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith("004_operations.sql")))!;
        using var read = new StreamReader(stream); var sql = await read.ReadToEndAsync();
        using (var connection = Connect())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys=OFF"; command.ExecuteNonQuery();
            foreach (Match match in Regex.Matches(sql, @"CREATE TABLE (\w+)"))
            { command.CommandText = $"DROP TABLE {match.Groups[1].Value}"; command.ExecuteNonQuery(); }
            command.CommandText = "DROP INDEX run_rows_ap_run; DELETE FROM schema_migrations WHERE version=4; UPDATE dataset SET schema_version=3; PRAGMA user_version=3";
            command.ExecuteNonQuery();
        }
        var migrated = NewStore(); await migrated.InitializeAsync(default);
        Assert.Equal(4, Scalar("PRAGMA user_version")); Assert.Equal(before, JsonSerializer.Serialize(await Dashboard(1, target: migrated)));
        var backfill = await migrated.GetTimelineAsync(new(PageSize: 500), default);
        Assert.Equal(automatic.TotalRows, backfill.TotalRows);
        var name = Assert.Single(backfill.Rows.Where(e => e.Field == "name" && e.Action == "ValueChanged"));
        Assert.Equal("AP-100", name.BeforeValue); Assert.Equal("Renamed", name.AfterValue); Assert.Equal(EvidenceSource.Demo, name.Source);
        Assert.Null(name.AutomaticChange!.AfterEvidence!.SourceAtUtc); Assert.Null(name.ActualWorkAtUtc);
        await NewStore().InitializeAsync(default); Assert.Equal(backfill.TotalRows, Count("operation_events"));
    }

    [Fact]
    public async Task Retention_PreservesWorkTargetsManualVersionsAndAutomaticTimelineReferences()
    {
        var row = await SeedOperations(); await store.SaveInventoryAsync(Edit(row), default);
        var work = Guid.NewGuid(); await store.SaveWorkAsync(new(Guid.NewGuid(), work, 0, Work(new WorkTarget(row.ApId, row.DeviceGenerationId, row.OpenIncidentId)), null), default);
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        var timeline = await store.GetTimelineAsync(new(PageSize: 500), default);
        await new RetentionService(location, gate, store).PruneAsync(clock.GetUtcNow().AddYears(1), default);
        Assert.Equal(timeline.TotalRows, Count("operation_events")); Assert.Equal(1, Count("work_targets")); Assert.Equal(1, Count("manual_inventory_versions"));
        Assert.Equal(row.OpenIncidentId, (await store.GetWorkAsync(work, default)).Current.Content.Targets.Single().IncidentId);
        Assert.NotNull(await store.GetIncidentAsync(new(location.DatasetId, 1, (await Dashboard()).RunId), row.OpenIncidentId!.Value, default));
    }
}
