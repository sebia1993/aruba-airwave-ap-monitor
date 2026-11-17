using System.Collections.Immutable;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using Microsoft.Data.Sqlite;
using AirWaveApMonitor.Infrastructure.Storage;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.AirWave;
using System.Text;

namespace AirWaveApMonitor.Tests.Storage;

public sealed partial class SqliteCollectionStoreTests
{
    [Fact]
    public async Task CompactHistory_Repeated2000UpReusesFactsAndMembershipButPreservesEachObservation()
    {
        var ids = Enumerable.Range(1, 2000).Select(i => i.ToString()).ToArray();
        var snapshots = new List<(long Run, DateTimeOffset At)>();
        for (var index = 0; index < 4; index++)
        {
            var run = await Begin(profile: Profile(ids: ids));
            var at = clock.GetUtcNow();
            var receipt = await store.ApplyApBatchAsync(run, Batch(ids.Select(id => Observation(id, ApState.Up)).ToArray()), default);
            Assert.Equal(2000, receipt.Summary.ConfirmedUp);
            snapshots.Add((run.RunId, at));
            clock.Advance();
        }

        // The regression is full normal-AP facts accumulating for every run, not a timing threshold.
        Assert.Equal(0, Count("run_rows"));
        Assert.Equal(2000, Count("ap_history_versions"));
        Assert.Equal(1, Count("ap_history_sets"));
        Assert.Equal(2000, Count("ap_history_members"));
        Assert.Equal(4, Count("run_history_frames"));
        foreach (var (run, at) in snapshots)
        {
            var page = await Dashboard(query: new(location.DatasetId, 1, run, Filter: DashboardFilter.Up, PageSize: 2000));
            Assert.Equal(2000, page.TotalRows);
            Assert.All(page.Rows, row =>
            {
                Assert.Equal(ApState.Up, row.CurrentState);
                Assert.Equal(ObservationClassification.Up, row.Classification);
                Assert.Equal(run, row.LastValidRunId);
                Assert.Equal(at, row.LastValidObservedAtUtc);
                Assert.All(row.Fields.Values, field => Assert.Equal(at, field.CollectedAtUtc));
            });
        }
        var final = await Dashboard();
        var detail = await store.GetApDetailAsync(new(location.DatasetId, 1, snapshots[^1].Run, final.Rows[0].ApId), default);
        Assert.Equal(snapshots.Select(s => s.Run).Reverse(), detail.Observations.Select(o => o.RunId));
    }

    [Fact]
    public async Task CompactHistory_SealedMembershipCannotAcquireFutureAp()
    {
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        var before = JsonSerializer.Serialize(await Dashboard(1));
        var run = await Begin();
        await store.ApplyApBatchAsync(run, Batch(Observation("100", ApState.Up), Observation("200", ApState.Up), Observation("300", ApState.Up)), default);
        using var connection = Connect(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ap_history_members SELECT 1,v.ap_id,v.version_id FROM ap_history_versions v JOIN aps a USING(ap_id) WHERE a.source_ap_id='300'";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(before, JsonSerializer.Serialize(await Dashboard(1)));
    }

    [Fact]
    public async Task CompactHistory_VersionCannotCrossApGenerationBoundary()
    {
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        using var connection = Connect(); using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ap_history_versions(ap_id,generation_id,interval_id,metadata_version_id,facts_hash,facts_template_json)
            SELECT a.ap_id,b.generation_id,b.interval_id,a.metadata_version_id,'wrong-binding',a.facts_template_json
            FROM ap_history_versions a JOIN ap_history_versions b ON a.ap_id!=b.ap_id LIMIT 1
            """;
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public async Task CompactHistory_FinalizedRunCannotAcquireDetailedDuplicateOfCompactAp()
    {
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        var before = JsonSerializer.Serialize(await Dashboard(1));
        using var connection = Connect(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO run_rows SELECT 1,ap_id,generation_id,interval_id,metadata_version_id,'{}' FROM ap_history_versions LIMIT 1";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(before, JsonSerializer.Serialize(await Dashboard(1)));
    }

    [Fact]
    public async Task CompactHistory_FinalizedLegacyRunCannotAcquireNewDetailedAp()
    {
        LoadSchemaFiveHistory(); await store.InitializeAsync(default);
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up), Observation("300", ApState.Up));
        using var connection = Connect(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO run_rows SELECT 1,v.ap_id,v.generation_id,v.interval_id,v.metadata_version_id,'{}' FROM ap_history_versions v JOIN aps a USING(ap_id) WHERE a.source_ap_id='300'";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(2, (await Dashboard(1)).TotalRows);
    }

    [Fact]
    public async Task CompactHistory_PermanentDetailedRowsCannotBeDeleted()
    {
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        using var connection = Connect(); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM run_rows";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(2, (await Dashboard()).TotalRows);
    }

    [Fact]
    public async Task CompactHistory_ExactFieldTimesStaleValuesRawAndMeasurementsSurviveLaterChanges()
    {
        clock.Set(DateTimeOffset.Parse("2026-11-15T10:00:00+09:00"));
        var observed = Observation("100", ApState.Up, "처음 이름");
        var field = observed.Fields["name"] with
        {
            CollectedAtUtc = DateTimeOffset.Parse("2026-11-15T09:57:00+09:00"),
            SourceAtUtc = DateTimeOffset.Parse("2026-11-15T00:52:00Z"), Freshness = SourceFreshness.Delayed
        };
        observed = observed with
        {
            CollectedAtUtc = DateTimeOffset.Parse("2026-11-15T01:00:02Z"),
            LastContactedAtUtc = DateTimeOffset.Parse("2026-11-15T00:45:00Z"),
            SourceAtUtc = DateTimeOffset.Parse("2026-11-15T00:54:00Z"),
            Device = observed.Device with { Uptime = TimeSpan.FromSeconds(123), RebootedAtUtc = DateTimeOffset.Parse("2026-11-14T13:00:00Z") },
            Fields = observed.Fields.SetItem("name", field).Add("snmp_uptime", observed.Fields["name"]).Add("last_reboot", field).Add("raw", field),
            RawSourceValues = ImmutableDictionary<string, string>.Empty.Add("raw", "{\"$history\":\"run\"}")
        };
        await Apply(observed, Observation("200", ApState.Up));
        var old = await Dashboard(1); var oldJson = JsonSerializer.Serialize(old); var row = old.Rows.Single(r => r.SourceApId == "100");
        Assert.Equal(DateTimeOffset.Parse("2026-11-15T01:00:02Z"), row.LastValidObservedAtUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-11-15T00:57:00Z"), row.Fields["name"].CollectedAtUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-11-15T00:52:00Z"), row.Fields["name"].SourceAtUtc);
        Assert.Equal(SourceFreshness.Delayed, row.Fields["name"].Freshness);
        Assert.Equal(TimeSpan.FromSeconds(123), row.Device.Uptime);
        Assert.Equal("{\"$history\":\"run\"}", row.RawSourceValues!["raw"]);
        var later = Observation("100", ApState.Up, "stale replacement");
        later = later with
        {
            Device = later.Device with { Uptime = TimeSpan.FromSeconds(456) },
            Fields = later.Fields.SetItem("name", field with { SourceAtUtc = field.SourceAtUtc!.Value.AddMinutes(-1) })
                .Add("snmp_uptime", later.Fields["is_up"]).Add("raw", later.Fields["is_up"]),
            RawSourceValues = ImmutableDictionary<string, string>.Empty.Add("raw", "changed raw")
        };
        await Apply(later, Observation("200", ApState.Up));
        var current = (await Dashboard()).Rows.Single(r => r.ApId == row.ApId);
        Assert.Equal("처음 이름", current.Device.Name); Assert.Equal(row.Fields["name"], current.Fields["name"]);
        Assert.Equal(TimeSpan.FromSeconds(456), current.Device.Uptime); Assert.Equal("changed raw", current.RawSourceValues!["raw"]);
        Assert.Equal(oldJson, JsonSerializer.Serialize(await Dashboard(1, target: NewStore())));
    }

    [Fact]
    public async Task CompactHistory_SchemaFiveUpgradeKeepsLegacyBytesAndCombinesNewNormalObservations()
    {
        LoadSchemaFiveHistory();
        string ReadLegacy()
        {
            using var connection = Connect(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT group_concat(facts_json,'|') FROM (SELECT facts_json FROM run_rows ORDER BY run_id,ap_id)";
            return (string)command.ExecuteScalar()!;
        }
        var legacy = ReadLegacy();
        var old = await Dashboard(1);
        Assert.Equal(SchemaMigrator.CurrentVersion, Count("schema_migrations"));
        Assert.Equal(0, Count("run_history_frames")); Assert.Equal(4, Count("run_rows"));
        Assert.Equal(legacy, ReadLegacy());
        Assert.Equal(ApState.Down, old.Rows.Single(r => r.SourceApId == "100").CurrentState);
        Assert.Equal("AP-100", old.Rows.Single(r => r.SourceApId == "100").Device.Name);
        var saved = JsonSerializer.Serialize(old);
        await Apply(Observation("100", ApState.Up, "third name"), Observation("200", ApState.Up));
        Assert.Equal(4, Count("run_rows")); Assert.Equal(1, Count("run_history_frames"));
        Assert.Equal(legacy, ReadLegacy()); Assert.Equal(saved, JsonSerializer.Serialize(await Dashboard(1)));
        var detail = await store.GetApDetailAsync(new(location.DatasetId, 1, 3, old.Rows.Single(r => r.SourceApId == "100").ApId), default);
        Assert.Equal(new long[] { 3, 2, 1 }, detail.Observations.Select(o => o.RunId));
        Assert.Equal(new[] { ObservationClassification.Up, ObservationClassification.Recovered, ObservationClassification.InitialDown }, detail.Observations.Select(o => o.Classification));
    }

    [Fact]
    public async Task CompactHistory_FailedTransactionAndReceiptReplayDoNotLeaveExtraVersionsOrFrames()
    {
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        var before = JsonSerializer.Serialize(await Dashboard()); var fail = true;
        var target = NewStore(boundary => { if (fail && boundary == StorageTransactionBoundary.BeforeFinalCommit) throw new IOException("controlled compact rollback"); });
        var run = await Begin(target); var batch = Batch(Observation("100", ApState.Up, "changed"), Observation("200", ApState.Up));
        await Assert.ThrowsAsync<IOException>(() => target.ApplyApBatchAsync(run, batch, default));
        Assert.Equal(2, Count("ap_history_versions")); Assert.Equal(1, Count("ap_history_sets"));
        Assert.Equal(2, Count("ap_history_members")); Assert.Equal(1, Count("run_history_frames"));
        Assert.Equal(before, JsonSerializer.Serialize(await Dashboard()));
        fail = false;
        var committed = await target.ApplyApBatchAsync(run, batch, default);
        var replayed = await target.ApplyApBatchAsync(run, batch, default);
        Assert.False(committed.AlreadyApplied); Assert.True(replayed.AlreadyApplied);
        Assert.Equal(3, Count("ap_history_versions")); Assert.Equal(2, Count("run_history_frames"));
        Assert.Equal(0, Count("run_rows"));
    }

    [Fact]
    public async Task CompactHistory_RetentionBackupRestorePreserveOldFramesAndVersionHighwater()
    {
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        var old = JsonSerializer.Serialize(await Dashboard(1));
        var backupService = new BackupService(location, gate, clock); var backup = await backupService.CreateAsync(default);
        await Apply(Observation("100", ApState.Up, "after backup"), Observation("200", ApState.Up));
        var high = Scalar("SELECT MAX(version_id) FROM ap_history_versions");
        var setHigh = Scalar("SELECT MAX(set_id) FROM ap_history_sets");
        await backupService.RestoreAsync(backup.BackupPath, default);
        var reopened = NewStore(); await reopened.InitializeAsync(default);
        Assert.Equal(old, JsonSerializer.Serialize(await Dashboard(1, target: reopened)));
        var run = await Begin(reopened);
        await reopened.ApplyApBatchAsync(run, Batch(Observation("100", ApState.Up, "after restore"), Observation("200", ApState.Up)), default);
        Assert.True(Scalar("SELECT MAX(version_id) FROM ap_history_versions") > high);
        Assert.True(Scalar("SELECT MAX(set_id) FROM ap_history_sets") > setHigh);
        await new RetentionService(location, gate, reopened).PruneAsync(clock.GetUtcNow().AddDays(100), default);
        Assert.Equal(old, JsonSerializer.Serialize(await Dashboard(1, target: reopened)));
        Assert.Equal(0, Count("run_execution_details"));
        // Restore itself commits an Unknown AP run; it remains an immutable frame too.
        Assert.Equal(3, Count("run_history_frames"));
    }

    [Fact]
    public async Task CompactHistory_AllDownAndUnknownKeepEveryDetailedRowAndRecoveryIsStillUp()
    {
        var ids = Enumerable.Range(1, 32).Select(i => i.ToString()).ToArray();
        var profile = Profile(ids: ids);
        var down = await Begin(profile: profile);
        await store.ApplyApBatchAsync(down, Batch(ids.Select(id => Observation(id, ApState.Down)).ToArray()), default); clock.Advance();
        var unknown = await Begin(profile: profile);
        var failed = await store.ApplyApBatchAsync(unknown, Batch(ids.Select(id => Observation(id, null)).ToArray()), default); clock.Advance();
        Assert.Equal(32, failed.Summary.Unknown); Assert.Null(failed.Summary.ConfirmedDown);
        var up = await Begin(profile: profile);
        var recovered = await store.ApplyApBatchAsync(up, Batch(ids.Select(id => Observation(id, ApState.Up)).ToArray()), default);
        Assert.Equal(32, recovered.Summary.Recovered);
        Assert.Equal(64, Count("run_rows")); Assert.Equal(96, Count("historical_ap_rows"));
        Assert.Equal(32, (await Dashboard(query: new(location.DatasetId, 1, up.RunId, Filter: DashboardFilter.Recovered))).TotalRows);
        Assert.Equal(32, (await Dashboard(query: new(location.DatasetId, 1, up.RunId, Filter: DashboardFilter.Up))).TotalRows);
        Assert.Equal(32, (await Dashboard(query: new(location.DatasetId, 1, unknown.RunId, Filter: DashboardFilter.Unknown))).TotalRows);
    }

    [Fact]
    public async Task CompactHistory_ManualPlacementAndGenerationChangesCannotRewriteNormalSnapshots()
    {
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        var first = await Dashboard(1); var row = first.Rows.Single(r => r.SourceApId == "100");
        var before = JsonSerializer.Serialize(first);
        await store.SaveInventoryAsync(Edit(row), default);
        var point = Guid.NewGuid();
        await store.SavePointAsync(new(Guid.NewGuid(), point, 0, "point", "compact-point", new()), default);
        await store.MovePlacementAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 0, point, 1, "installed"), default);
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        var edited = await Dashboard(2); var editedJson = JsonSerializer.Serialize(edited);
        Assert.Equal("수동 이름", edited.Rows.Single(r => r.ApId == row.ApId).ManualInventory!.Values[InventoryField.Name]);
        Assert.Equal(point, edited.Rows.Single(r => r.ApId == row.ApId).ManualInventory!.Placement!.InstallationPointId);
        await Apply(Observation("100", ApState.Up, "new physical device", "REPLACED"), Observation("200", ApState.Up));
        Assert.NotEqual(row.DeviceGenerationId, (await Dashboard()).Rows.Single(r => r.ApId == row.ApId).DeviceGenerationId);
        Assert.Equal(before, JsonSerializer.Serialize(await Dashboard(1)));
        Assert.Equal(editedJson, JsonSerializer.Serialize(await Dashboard(2)));
        Assert.Equal(0, Count("run_rows"));
    }

    [Fact]
    public async Task CompactHistory_MalformedTemplateIsReportedAsStoredDataFailure()
    {
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        using (var connection = Connect())
        using (var command = connection.CreateCommand())
        {
            // Deliberate isolated corruption; normal SQL cannot mutate an immutable version.
            command.CommandText = "DROP TRIGGER ap_history_versions_immutable; PRAGMA ignore_check_constraints=ON; UPDATE ap_history_versions SET facts_template_json='{private-corrupt-template'";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => Dashboard());
    }

    [Fact]
    public async Task CompactHistory_RepeatedParserRowsReuseVersionsAndRetainAbsentFieldEvidenceTimes()
    {
        const string xml = """
            <amp:amp_ap_list xmlns:amp="http://www.airwave.com" version="1">
            <ap id="100"><device_category>thin_ap</device_category><is_up>true</is_up><name>AP-100</name><serial_number>ONE</serial_number><last_contacted>1527739948</last_contacted></ap>
            <ap id="200"><device_category>thin_ap</device_category><is_up>true</is_up><name>AP-200</name><serial_number>TWO</serial_number><last_contacted>1527739948</last_contacted></ap>
            </amp:amp_ap_list>
            """;
        var firstAt = clock.GetUtcNow();
        for (var i = 0; i < 3; i++)
        {
            var run = await Begin();
            var batch = new ApListParser(EvidenceSource.Demo).Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), clock.GetUtcNow());
            Assert.Equal(2, batch.Observations.Length);
            await store.ApplyApBatchAsync(run, batch, default); clock.Advance();
        }
        var rows = (await Dashboard()).Rows;
        Assert.All(rows, row =>
        {
            Assert.Equal(FieldAvailability.NotProvided, row.Fields["model"].Availability);
            Assert.Equal(firstAt, row.Fields["model"].CollectedAtUtc);
            Assert.Equal(firstAt.AddMinutes(10), row.Fields["name"].CollectedAtUtc);
            Assert.Null(row.LastContactedAtUtc);
            Assert.Equal("1527739948", row.RawSourceValues!["last_contacted"]);
            Assert.Equal(firstAt.AddMinutes(10), row.Fields["last_contacted"].CollectedAtUtc);
        });
        using var diagnosticConnection = Connect(); using var diagnostic = diagnosticConnection.CreateCommand();
        diagnostic.CommandText = "SELECT group_concat(version_id||':'||metadata_version_id||':'||json_extract(facts_template_json,'$.Fields.device_category.CollectedAtUtc'),' | ') FROM ap_history_versions";
        Assert.True(Count("ap_history_versions") == 2, "Version/metadata/category-evidence: " + diagnostic.ExecuteScalar());
        Assert.Equal(1, Count("ap_history_sets")); Assert.Equal(2, Count("ap_history_members")); Assert.Equal(0, Count("run_rows"));
    }

    private void LoadSchemaFiveHistory()
    {
        using var connection = Connect(); using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=OFF;\n" + File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Storage", "schema5-history.sql"))
            .Replace("__DATASET__", location.DatasetId.ToString()).Replace("__INSTANCE__", instance.ToString()) + "\nPRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery(); clock.Set(DateTimeOffset.Parse("2026-11-15T01:10:00Z"));
    }

    private static void RemoveEmptyCompactSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM run_history_frames"; Assert.Equal(0L, (long)command.ExecuteScalar()!);
        command.CommandText = "DROP TRIGGER run_rows_finalized_no_insert; DROP TRIGGER run_rows_no_delete; DROP VIEW historical_ap_rows; DROP TABLE run_history_frames; DROP TABLE ap_history_members; DROP TABLE ap_history_sets; DROP TABLE ap_history_versions";
        command.ExecuteNonQuery();
    }
}
