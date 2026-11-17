using System.Collections.Immutable;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Security;
using AirWaveApMonitor.Infrastructure.AirWave;
using System.Text;
using AirWaveApMonitor.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace AirWaveApMonitor.Tests.Storage;

public sealed partial class SqliteCollectionStoreTests
{
    [Fact]
    public async Task FreshMigration_InitializesAuthenticationInSameDatabase()
    {
        await store.InitializeAsync(default);
        Assert.Equal(0, Count("authentication_state"));
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", command.ExecuteScalar());
    }

    [Fact]
    public async Task ExistingAuthenticationOnlyDatabase_MigratesWithoutDroppingBlock()
    {
        var binding = new AuthenticationBinding(location.DatasetId, new Uri("https://airwave.example.invalid/"), 1);
        var authentication = new SqliteAuthenticationStateStore(location, gate);
        var stop = new AuthenticationStop(binding, AuthenticationStopReason.LoginRejected, clock.GetUtcNow());
        await authentication.SaveStopAsync(stop, default);
        await store.InitializeAsync(default);
        Assert.Equal(stop, (await authentication.ReadAsync(binding, default)).Stop);
        Assert.Equal(SchemaMigrator.CurrentVersion, Scalar("PRAGMA user_version"));
        Assert.Equal(0, Count("runs"));
    }

    [Fact]
    public async Task MalformedKnownVersion_IsRejectedBeforeBeginningOrRecoveringRun()
    {
        await store.InitializeAsync(default);
        using (var connection = Connect())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "ALTER TABLE ap_current RENAME COLUMN tracking_json TO broken_tracking";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => NewStore().InitializeAsync(default));
        Assert.Equal(0, Count("runs"));
    }

    [Fact]
    public async Task MalformedUnversionedDatabase_IsPreservedAndRejected()
    {
        using (var connection = Connect())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE private_data(value TEXT); INSERT INTO private_data VALUES('preserve');";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync(default));
        Assert.Equal(1, Count("private_data"));
        Assert.Equal(0, Scalar("PRAGMA user_version"));
    }

    [Fact]
    public async Task InterruptAfterCommitFailure_ReleasesRunWithoutInventingRecovery()
    {
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        var failure = NewStore(boundary =>
        {
            if (boundary == StorageTransactionBoundary.BeforeFinalCommit)
                throw new IOException("boundary failure");
        });
        var running = await Begin(failure);
        await Assert.ThrowsAsync<IOException>(() => failure.ApplyApBatchAsync(running,
            Batch(Observation("100", ApState.Up), Observation("200", ApState.Up)), default));
        var interrupted = await failure.InterruptRunAsync(running, default);
        Assert.Equal(RunOutcome.Failed, interrupted.Outcome);
        Assert.Null(interrupted.Summary.Recovered);
        Assert.Equal(1, interrupted.Summary.OpenIncidents);
        Assert.Equal(RunLifecycle.Interrupted, (await Dashboard(target: failure)).Lifecycle);
        Assert.Equal(2, interrupted.Summary.Unknown);
        Assert.True((await failure.InterruptRunAsync(running, default)).AlreadyApplied);
        Assert.True((await Begin(failure)).RunId > running.RunId);
    }

    [Fact]
    public async Task InterruptAfterCommittedResponseLoss_ReturnsOriginalSuccessfulReceipt()
    {
        var target = NewStore(boundary =>
        {
            if (boundary == StorageTransactionBoundary.AfterFinalCommit)
                throw new IOException("response lost");
        });
        var run = await Begin(target);
        await Assert.ThrowsAsync<IOException>(() => target.ApplyApBatchAsync(run,
            Batch(Observation("100", ApState.Down), Observation("200", ApState.Up)), default));
        var receipt = await target.InterruptRunAsync(run, default);
        Assert.True(receipt.AlreadyApplied);
        Assert.Equal(RunOutcome.Succeeded, receipt.Outcome);
        Assert.Equal(RunLifecycle.Finalized, (await Dashboard(target: target)).Lifecycle);
        Assert.Equal(1, Count("run_results"));
    }

    [Fact]
    public async Task RepeatedInitialize_DoesNotInterruptLiveRun()
    {
        var run = await Begin();
        await store.InitializeAsync(default);
        Assert.Equal(0, Count("run_results"));
        Assert.Equal(run.RunId, Scalar("SELECT run_id FROM runs WHERE lifecycle=0"));
    }

    [Fact]
    public async Task ActualSqliteWriteFailure_RollsBackMetadataIncidentAndSuccessClock()
    {
        await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        var previous = await store.GetSuccessTimesAsync(location.DatasetId, 1, default);
        var run = await Begin();
        using (var connection = Connect())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER simulate_storage_failure BEFORE INSERT ON run_results BEGIN SELECT RAISE(ABORT,'simulated storage failure'); END";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<SqliteException>(() => store.ApplyApBatchAsync(run,
            Batch(Observation("100", ApState.Down, "unsaved"), Observation("200", ApState.Up)), default));
        Assert.Equal(0, Count("incidents"));
        Assert.Equal(2, Count("metadata_versions"));
        Assert.Equal(previous, await store.GetSuccessTimesAsync(location.DatasetId, 1, default));
        Assert.Equal("AP-100", (await Dashboard()).Rows.Single(row => row.SourceApId == "100").Device.Name);
    }

    [Fact]
    public async Task CorruptRestoredRunReference_IsRejectedWithNoPartialApply()
    {
        await Apply(Observation("100", ApState.Down), Observation("200", ApState.Up));
        var run = await Begin();
        using (var connection = Connect())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE ap_current SET tracking_json=json_set(tracking_json,'$.LastValidRunId',999) WHERE ap_id=(SELECT ap_id FROM aps WHERE source_ap_id='100')";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ApplyApBatchAsync(run,
            Batch(Observation("100", ApState.Up), Observation("200", ApState.Up)), default));
        Assert.Equal(1, Count("run_results"));
        Assert.Equal(1, Count("incidents"));
    }

    [Fact]
    public async Task DetectionAndRecoverySnapshots_UseSameFrameMetadataAndStayImmutable()
    {
        await Apply(Observation("100", ApState.Up, "before"), Observation("200", ApState.Up));
        var oldConnectionTime = clock.GetUtcNow().AddMinutes(-5);
        var oldConnection = new SourceUpstreamReference("switch-1", "7", null, null,
            new(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.Demo, oldConnectionTime));
        var down = Observation("100", ApState.Down, "detected") with
        {
            Upstream = oldConnection,
            Fields = Observation("100", ApState.Down).Fields.Add("upstream_device_id", oldConnection.Evidence)
                .Add("upstream_port_index", oldConnection.Evidence)
        };
        var detection = await Apply(down, Observation("200", ApState.Up));
        await Apply(Observation("100", ApState.Up, "recovered"), Observation("200", ApState.Up));
        await Apply(Observation("100", ApState.Up, "future"), Observation("200", ApState.Up));
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM incident_snapshots ORDER BY snapshot_kind";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var first = JsonSerializer.Deserialize<IncidentSnapshot>(reader.GetString(0))!;
        Assert.Equal(detection.RunId, first.RunId);
        Assert.Equal("detected", first.Device.Metadata.Device.Name);
        Assert.Equal("switch-1", first.Device.Metadata.Upstream!.SourceSwitchId);
        Assert.Equal(oldConnectionTime, first.Device.Fields["upstream_device_id"].CollectedAtUtc);
        Assert.True(reader.Read());
        var recovery = JsonSerializer.Deserialize<IncidentSnapshot>(reader.GetString(0))!;
        Assert.Equal("recovered", recovery.Device.Metadata.Device.Name);
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task HistoricalSortFilterAndPage_UseOnlySelectedRunValues()
    {
        var first = await Apply(Observation("100", ApState.Down, "Zulu"), Observation("200", ApState.Up, "Alpha"));
        await Apply(Observation("100", ApState.Up, "Alpha"), Observation("200", ApState.Down, "Zulu"));
        var selected = await Dashboard(query: new(location.DatasetId, 1, first.RunId, Sort: DashboardSort.Name, PageSize: 1));
        Assert.Equal(2, selected.TotalRows);
        Assert.Equal("200", Assert.Single(selected.Rows).SourceApId);
        var page2 = await Dashboard(query: new(location.DatasetId, 1, first.RunId, Sort: DashboardSort.Name, Page: 2, PageSize: 1));
        Assert.Equal("100", Assert.Single(page2.Rows).SourceApId);
        var filtered = await Dashboard(query: new(location.DatasetId, 1, first.RunId, Filter: DashboardFilter.Down));
        Assert.Equal("100", Assert.Single(filtered.Rows).SourceApId);
        Assert.Equal(1, filtered.Summary.ConfirmedDown);
    }

    [Fact]
    public async Task FailedOutcomeWithValidLookingRows_PreservesAllSavedMetadata()
    {
        await Apply(Observation("100", ApState.Down, "before"), Observation("200", ApState.Up));
        var run = await Begin();
        var receipt = await store.ApplyApBatchAsync(run,
            Batch(Observation("100", ApState.Up, "invalid transport", "newserial"), Observation("200", ApState.Up)) with
            { Outcome = RunOutcome.Failed }, default);
        Assert.Equal(RunOutcome.Failed, receipt.Outcome);
        Assert.Equal(1, receipt.Summary.OpenIncidents);
        Assert.Equal("before", (await Dashboard()).Rows.Single(row => row.SourceApId == "100").Device.Name);
        Assert.Equal(1, Count("incidents"));
        Assert.Equal(2, Count("generations"));
    }

    [Fact]
    public async Task ValidBatch_NewUnknownApIsDiscoveredAndRetainedAsExpectedTarget()
    {
        var run = await Begin(profile: Profile(ids: []));
        var receipt = await store.ApplyApBatchAsync(run, Batch(Observation("100", ApState.Up), Observation("200", null)), default);
        Assert.Equal(RunOutcome.Partial, receipt.Outcome);
        Assert.Equal(2, receipt.Summary.TargetCount);
        Assert.Equal(1, receipt.Summary.Unknown);
        var unknown = (await Dashboard()).Rows.Single(row => row.SourceApId == "200");
        Assert.Equal(ObservationClassification.Unknown, unknown.Classification);
        Assert.NotEqual(Guid.Empty, unknown.DeviceGenerationId);
        var next = await Begin(profile: Profile(ids: []));
        Assert.Equal(2, next.Profile.ExpectedTargets.Length);
    }

    [Fact]
    public async Task SourceExclusionsAndSerialCollisions_AreRetainedWithTheRun()
    {
        var run = await Begin();
        var batch = Batch(Observation("100", ApState.Up, serial: "collision"), Observation("200", ApState.Up, serial: "collision")) with
        {
            ExcludedRecords = [new(3, "300", CollectionErrorCategory.Unverified, "category unverified")]
        };
        await store.ApplyApBatchAsync(run, batch, default);
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_evidence_json FROM run_results";
        var evidence = command.ExecuteScalar()!.ToString()!;
        using var parsed = JsonDocument.Parse(evidence);
        Assert.Equal(2, parsed.RootElement.GetProperty("ParsedApCount").GetInt32());
        Assert.Equal("300", parsed.RootElement.GetProperty("ExcludedRecords")[0].GetProperty("SourceApId").GetString());
        Assert.Equal(2, parsed.RootElement.GetProperty("IdentityConflicts").GetProperty("SerialCollisions").GetProperty("collision").GetArrayLength());
    }

    [Fact]
    public async Task CorruptFrozenRunId_IsRejectedDuringStartupRecovery()
    {
        var run = await Begin();
        using (var connection = Connect())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE runs SET context_json=json_set(context_json,'$.RunId',999) WHERE run_id=$run";
            command.Parameters.AddWithValue("$run", run.RunId);
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().InitializeAsync(default));
        Assert.Equal(0, Count("run_results"));
        Assert.Equal(0, Count("aps"));
    }

    [Fact]
    public async Task VolatileRawTimestamps_DoNotDuplicateStableDeviceMetadata()
    {
        var initial = Observation("100", ApState.Up) with
        {
            RawSourceValues = ImmutableDictionary<string, string>.Empty.Add("last_contacted", "old raw clock"),
            Fields = Observation("100", ApState.Up).Fields.Add("last_contacted",
                new(FieldAvailability.Unverified, FieldValidity.Unknown, EvidenceSource.Demo, clock.GetUtcNow()))
        };
        var first = await Apply(initial, Observation("200", ApState.Up));
        var changed = Observation("100", ApState.Down) with
        {
            RawSourceValues = ImmutableDictionary<string, string>.Empty.Add("last_contacted", "new raw clock"),
            Fields = Observation("100", ApState.Down).Fields.Add("last_contacted",
                new(FieldAvailability.Unverified, FieldValidity.Unknown, EvidenceSource.Demo, clock.GetUtcNow()))
        };
        await Apply(changed, Observation("200", ApState.Up));
        Assert.Equal(2, Count("metadata_versions"));
        Assert.Equal(2, Count("metadata_changes"));
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM incident_snapshots";
        var snapshot = JsonSerializer.Deserialize<IncidentSnapshot>(command.ExecuteScalar()!.ToString()!)!;
        Assert.Equal("new raw clock", snapshot.Device.Metadata.RawSourceValues!["last_contacted"]);
        Assert.Equal(changed.CollectedAtUtc, snapshot.Device.Fields["last_contacted"].CollectedAtUtc);
        Assert.Equal(initial.CollectedAtUtc, (await Dashboard(first.RunId)).Rows.Single(row => row.SourceApId == "100").Fields["last_contacted"].CollectedAtUtc);
    }

    [Fact]
    public async Task RealParserRawUptime_ChangesValueAndEvidenceTogetherInDetectionSnapshot()
    {
        ApCollectionBatch Parse(string uptime, bool up)
        {
            var xml = $"<amp:amp_ap_list xmlns:amp='http://www.airwave.com'><ap id='100'><device_category>thin_ap</device_category><is_up>{up.ToString().ToLowerInvariant()}</is_up><name>AP-100</name><snmp_uptime>{uptime}</snmp_uptime></ap></amp:amp_ap_list>";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            return new ApListParser(EvidenceSource.Demo).Parse(stream, clock.GetUtcNow());
        }
        var first = await Begin(profile: Profile(ids: ["100"]));
        await store.ApplyApBatchAsync(first, Parse("100", true), default);
        clock.Advance();
        var second = await Begin(profile: Profile(ids: ["100"]));
        var observedAt = clock.GetUtcNow();
        await store.ApplyApBatchAsync(second, Parse("200", false), default);
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM incident_snapshots";
        var snapshot = JsonSerializer.Deserialize<IncidentSnapshot>(command.ExecuteScalar()!.ToString()!)!;
        Assert.Equal("200", snapshot.Device.Metadata.RawSourceValues!["snmp_uptime"]);
        Assert.Equal(observedAt, snapshot.Device.Fields["snmp_uptime"].CollectedAtUtc);
        Assert.Equal(FieldAvailability.Unverified, snapshot.Device.Fields["snmp_uptime"].Availability);
        Assert.Equal(FieldValidity.Unknown, snapshot.Device.Fields["snmp_uptime"].Validity);
        Assert.Null(snapshot.Device.Metadata.Device.Uptime);
        Assert.Equal(1, Count("metadata_versions"));
    }

    [Fact]
    public async Task EnrichmentWithoutDurableBegin_DoesNotClaimApplicationOrChangeApRun()
    {
        var receipt = await Apply(Observation("100", ApState.Up), Observation("200", ApState.Up));
        var context = new CollectionContext(100, Profile(), CollectionKind.Power, RunTrigger.Manual, clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(2));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.ApplyEnrichmentBatchAsync(context,
            new(CollectionKind.Power, RunOutcome.Succeeded, [], [], ObservationCompleteness.Complete, clock.GetUtcNow(), []), default));
        Assert.Equal(receipt.RunId, (await Dashboard()).RunId);
        Assert.Equal(1, Count("run_results"));
    }
}
