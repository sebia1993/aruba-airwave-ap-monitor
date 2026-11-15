using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Security;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Infrastructure.Storage;
using AirWaveApMonitor.Tests.Support;
using Microsoft.Data.Sqlite;

namespace AirWaveApMonitor.Tests.Maintenance;

public sealed class BackupServiceTests
{
    [Fact]
    public async Task OnlineBackup_IncludesCommittedWalAndHasVerifiedChecksum()
    {
        using var f = await Fixture.Create();
        await f.Observe(ApState.Down);
        using var reader = f.Open();
        using var readTransaction = reader.BeginTransaction(deferred: true);
        using (var pin = reader.CreateCommand()) { pin.Transaction = readTransaction; pin.CommandText = "SELECT COUNT(*) FROM runs"; pin.ExecuteScalar(); }
        await f.Observe(ApState.Up);
        var backup = await f.Backup.CreateAsync(default);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(backup.BackupPath))), backup.Sha256);
        Assert.Equal(2, Fixture.Count(backup.BackupPath, "run_results"));
        Assert.Equal(f.Location.DatasetId, backup.DatasetId);
        Assert.Equal(SchemaMigrator.CurrentVersion, backup.SchemaVersion);
        Assert.Single(await f.Backup.ListVerifiedAsync(default));
    }

    [Fact]
    public async Task FailedNewBackup_KeepsSevenGoodBackups()
    {
        using var f = await Fixture.Create();
        await f.Observe(ApState.Down);
        var paths = new List<string>();
        for (var i = 0; i < 7; i++) { paths.Add((await f.Backup.CreateAsync(default)).BackupPath); f.Clock.Advance(TimeSpan.FromDays(1)); }
        var failing = new BackupService(f.Location, f.Gate, f.Clock, phase => { if (phase == BackupBoundary.BeforePublish) throw new IOException("controlled test failure"); });
        await Assert.ThrowsAsync<IOException>(() => failing.CreateAsync(default));
        Assert.All(paths, path => Assert.True(File.Exists(path)));
        Assert.Equal(7, (await f.Backup.ListVerifiedAsync(default)).Count);
        var eighth = await f.Backup.CreateAsync(default);
        var kept = await f.Backup.ListVerifiedAsync(default);
        Assert.Equal(7, kept.Count);
        Assert.Contains(kept, item => item.BackupPath == eighth.BackupPath);
        Assert.False(File.Exists(paths[0]));
    }

    [Fact]
    public async Task Update_PreservesDatabaseAndSettingsAndSeparatePreUpdateBackup()
    {
        using var f = await Fixture.Create();
        await f.Observe(ApState.Down);
        var profileBytes = await File.ReadAllBytesAsync(Path.Combine(f.Directory.Path, "profile.json"));
        var previous = await f.Backup.CreateAsync(BackupKind.PreUpdate, default);
        for (var i = 0; i < 8; i++) { f.Clock.Advance(TimeSpan.FromDays(1)); await f.Backup.CreateAsync(default); }
        Assert.True(File.Exists(previous.BackupPath));
        Assert.Equal(profileBytes, await File.ReadAllBytesAsync(Path.Combine(f.Directory.Path, "profile.json")));
        Assert.Equal(1, Fixture.Count(f.DatabasePath, "incidents"));
    }

    [Fact]
    public async Task Restore_RefusesRunningCollectorBeforeChangingData()
    {
        using var f = await Fixture.Create();
        await f.Observe(ApState.Down);
        var backup = await f.Backup.CreateAsync(default);
        await f.Observe(ApState.Up);
        using var owner = new InstanceLock(f.Location);
        await Assert.ThrowsAsync<IOException>(() => f.Backup.RestoreAsync(backup.BackupPath, default));
        Assert.Equal(2, Fixture.Count(f.DatabasePath, "run_results"));
    }

    [Fact]
    public async Task IncompatibleDatabase_IsPreservedAndRejected()
    {
        using var f = await Fixture.Create();
        using var other = await Fixture.Create();
        await f.Observe(ApState.Down);
        await other.Observe(ApState.Up);
        var foreign = await other.Backup.CreateAsync(default);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Backup.RestoreAsync(foreign.BackupPath, default));
        Assert.Equal(ApState.Down, (await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1), default)).Rows.Single().CurrentState);
        Assert.True(File.Exists(foreign.BackupPath));
    }

    [Fact]
    public async Task RestoreFailureBeforeReplace_PreservesCurrentHistoryAndProfile()
    {
        using var f = await Fixture.Create();
        await f.Observe(ApState.Down);
        var backup = await f.Backup.CreateAsync(default);
        await f.Observe(ApState.Up);
        var profile = await File.ReadAllBytesAsync(Path.Combine(f.Directory.Path, "profile.json"));
        var failing = new BackupService(f.Location, f.Gate, f.Clock, phase => { if (phase == BackupBoundary.BeforeRestoreReplace) throw new IOException("controlled restore failure"); });
        await Assert.ThrowsAsync<IOException>(() => failing.RestoreAsync(backup.BackupPath, default));
        Assert.Equal(2, Fixture.Count(f.DatabasePath, "run_results"));
        Assert.Equal(profile, await File.ReadAllBytesAsync(Path.Combine(f.Directory.Path, "profile.json")));
    }

    [Fact]
    public async Task CorruptBackup_IsRejectedWithoutChangingCurrentDatabase()
    {
        using var f = await Fixture.Create();
        await f.Observe(ApState.Down);
        var backup = await f.Backup.CreateAsync(default);
        await File.AppendAllTextAsync(backup.BackupPath, "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Backup.RestoreAsync(backup.BackupPath, default));
        Assert.Equal(1, Fixture.Count(f.DatabasePath, "run_results"));
    }

    [Fact]
    public async Task Restore_RecordsOneNewGapRunAndPreservesHistoricalSnapshots()
    {
        using var f = await Fixture.Create();
        var oldRun = await f.Observe(ApState.Down);
        var oldView = await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 1, oldRun), default);
        var backup = await f.Backup.CreateAsync(default);
        await f.Observe(ApState.Up);
        f.Clock.Advance(TimeSpan.FromDays(2));
        var result = await f.Backup.RestoreAsync(backup.BackupPath, default);
        Assert.True(File.Exists(result.PreservedDatabasePath));
        Assert.Equal(2, Fixture.Count(result.PreservedDatabasePath, "run_results"));
        var reopened = new SqliteCollectionStore(f.Location, f.Gate, f.Clock);
        await reopened.InitializeAsync(default);
        var gapView = await reopened.GetDashboardAsync(new(f.Location.DatasetId, 1), default);
        Assert.True(gapView.RunId > oldRun);
        Assert.Equal(RunLifecycle.Interrupted, gapView.Lifecycle);
        Assert.Null(gapView.Summary.ConfirmedDown);
        Assert.Equal(1, gapView.Summary.OpenIncidents);
        Assert.Equal(oldView.Rows.Single().OpenIncidentId, gapView.Rows.Single().OpenIncidentId);
        Assert.Equal(JsonSerializer.Serialize(oldView.Rows.Single()), JsonSerializer.Serialize((await reopened.GetDashboardAsync(new(f.Location.DatasetId, 1, oldRun), default)).Rows.Single()));
        Assert.Equal(1, Fixture.Count(f.DatabasePath, "observation_gaps"));
        await new SqliteCollectionStore(f.Location, f.Gate, f.Clock).InitializeAsync(default);
        Assert.Equal(2, Fixture.Count(f.DatabasePath, "runs"));
        using var c = f.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT consumed_run_id FROM restore_events WHERE restore_event_id=$id";
        cmd.Parameters.AddWithValue("$id", result.RestoreEventId.ToString("D"));
        Assert.Equal(gapView.RunId, cmd.ExecuteScalar());
    }

    [Fact]
    public async Task Restore_OldLiveBackupCannotClearCurrentAuthenticationStopOrRegistrationGuard()
    {
        using var f = await Fixture.Create(AppMode.Live);
        await f.Observe(ApState.Down);
        var backup = await f.Backup.CreateAsync(default);
        var binding = AuthenticationBinding.From(f.Profile);
        var auth = new SqliteAuthenticationStateStore(f.Location, f.Gate);
        await auth.SaveStopAsync(new(binding, AuthenticationStopReason.LoginRejected, f.Clock.GetUtcNow()), default);
        var profileBytes = await File.ReadAllBytesAsync(Path.Combine(f.Directory.Path, "profile.json"));
        var result = await f.Backup.RestoreAsync(backup.BackupPath, default);
        var state = await auth.ReadAsync(binding, default);
        Assert.True(result.RequiresCredentialRegistration);
        Assert.True(state.CredentialRegistrationPending);
        Assert.Equal(AuthenticationStopReason.LoginRejected, state.Stop!.Reason);
        await auth.ClearStopAsync(binding, default);
        Assert.True((await auth.ReadAsync(binding, default)).CredentialRegistrationPending);
        Assert.Equal(profileBytes, await File.ReadAllBytesAsync(Path.Combine(f.Directory.Path, "profile.json")));
    }

    [Fact]
    public async Task Restore_CurrentUnknownSchemaIsPreservedRatherThanDiscardingWal()
    {
        using var f = await Fixture.Create();
        await f.Observe(ApState.Down);
        var backup = await f.Backup.CreateAsync(default);
        using (var c = f.Open()) { using var command = c.CreateCommand(); command.CommandText = "PRAGMA user_version=999"; command.ExecuteNonQuery(); }
        var before = await File.ReadAllBytesAsync(f.DatabasePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Backup.RestoreAsync(backup.BackupPath, default));
        Assert.Equal(before, await File.ReadAllBytesAsync(f.DatabasePath));
        Assert.True(File.Exists(backup.BackupPath));
    }

    [Fact]
    public async Task Restore_DoesNotReuseRunIdsThatExistedAfterBackup()
    {
        using var f = await Fixture.Create();
        await f.Observe(ApState.Down);
        var backup = await f.Backup.CreateAsync(default);
        await f.Observe(ApState.Up);
        var lostRunId = await f.Observe(ApState.Down);
        await f.Backup.RestoreAsync(backup.BackupPath, default);
        var reopened = new SqliteCollectionStore(f.Location, f.Gate, f.Clock);
        await reopened.InitializeAsync(default);
        var view = await reopened.GetDashboardAsync(new(f.Location.DatasetId, 1), default);
        Assert.True(view.RunId > lostRunId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => reopened.GetDashboardAsync(new(f.Location.DatasetId, 1, lostRunId), default));
    }

    [Fact]
    public async Task RestoreGapTransactionFailure_RetriesExactlyOnce()
    {
        using var f = await Fixture.Create();
        await f.Observe(ApState.Down);
        var backup = await f.Backup.CreateAsync(default);
        await f.Backup.RestoreAsync(backup.BackupPath, default);
        using (var c = f.Open()) { using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TRIGGER refuse_restore_result BEFORE INSERT ON run_results BEGIN SELECT RAISE(ABORT,'test failure'); END"; cmd.ExecuteNonQuery(); }
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteCollectionStore(f.Location, f.Gate, f.Clock).InitializeAsync(default));
        Assert.Equal(1, Fixture.Count(f.DatabasePath, "runs"));
        using (var c = f.Open()) { using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT consumed FROM restore_events"; Assert.Equal(0L, cmd.ExecuteScalar()); cmd.CommandText = "DROP TRIGGER refuse_restore_result"; cmd.ExecuteNonQuery(); }
        await new SqliteCollectionStore(f.Location, f.Gate, f.Clock).InitializeAsync(default);
        await new SqliteCollectionStore(f.Location, f.Gate, f.Clock).InitializeAsync(default);
        Assert.Equal(2, Fixture.Count(f.DatabasePath, "runs"));
        Assert.Equal(1, Fixture.Count(f.DatabasePath, "observation_gaps"));
    }

    [Fact]
    public async Task RestoreWithRunningRun_OpensGapAtBackupBoundaryAndDoesNotRepeatRecovery()
    {
        using var f = await Fixture.Create();
        await f.Observe(ApState.Down);
        var unfinished = await f.Store.BeginRunAsync(f.Profile, CollectionKind.ApStatus, RunTrigger.Manual, default);
        var backup = await f.Backup.CreateAsync(default);
        f.Clock.Advance(TimeSpan.FromDays(2));
        await f.Backup.RestoreAsync(backup.BackupPath, default);
        await new SqliteCollectionStore(f.Location, f.Gate, f.Clock).InitializeAsync(default);
        using (var connection = f.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT gap_json FROM observation_gaps";
            var gap = JsonSerializer.Deserialize<ObservationGap>((string)command.ExecuteScalar()!)!;
            Assert.Equal(backup.CreatedAtUtc, gap.StartedAtUtc);
            Assert.Equal(unfinished.RunId, gap.FirstRunId);
            Assert.Equal(MissingObservationReason.Interrupted, gap.Reason);
            Assert.Null(gap.ResumedRunId);
        }
        Assert.Equal(3, Fixture.Count(f.DatabasePath, "runs"));
        Assert.Equal(1, Fixture.Count(f.DatabasePath, "observation_gaps"));
        await new SqliteCollectionStore(f.Location, f.Gate, f.Clock).InitializeAsync(default);
        Assert.Equal(3, Fixture.Count(f.DatabasePath, "runs"));
        Assert.Equal(1, Fixture.Count(f.DatabasePath, "observation_gaps"));
    }

    [Fact]
    public async Task RestoreWithRunningRun_PreservesEarlierExistingGap()
    {
        using var f = await Fixture.Create();
        await f.Observe(ApState.Down);
        var failure = await f.Store.BeginRunAsync(f.Profile, CollectionKind.ApStatus, RunTrigger.Manual, default);
        await f.Store.ApplyApBatchAsync(failure, new(RunOutcome.Failed, [], [], f.Clock.GetUtcNow(), ObservationCompleteness.Unknown, []), default);
        string original;
        using (var connection = f.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT gap_json FROM observation_gaps";
            original = (string)command.ExecuteScalar()!;
        }
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        await f.Store.BeginRunAsync(f.Profile, CollectionKind.ApStatus, RunTrigger.Manual, default);
        var backup = await f.Backup.CreateAsync(default);
        f.Clock.Advance(TimeSpan.FromDays(2));
        await f.Backup.RestoreAsync(backup.BackupPath, default);
        await new SqliteCollectionStore(f.Location, f.Gate, f.Clock).InitializeAsync(default);
        using (var connection = f.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT gap_json FROM observation_gaps";
            Assert.Equal(original, (string)command.ExecuteScalar()!);
        }
        Assert.Equal(4, Fixture.Count(f.DatabasePath, "runs"));
        await new SqliteCollectionStore(f.Location, f.Gate, f.Clock).InitializeAsync(default);
        Assert.Equal(4, Fixture.Count(f.DatabasePath, "runs"));
        Assert.Equal(1, Fixture.Count(f.DatabasePath, "observation_gaps"));
    }

    [Fact]
    public async Task CancelledBackupAndRestore_PreserveCommittedDataAndOldBackup()
    {
        using var f = await Fixture.Create();
        await f.Observe(ApState.Down);
        var backup = await f.Backup.CreateAsync(default);
        using var cancelBackup = new CancellationTokenSource();
        var first = new BackupService(f.Location, f.Gate, f.Clock, phase => { if (phase == BackupBoundary.AfterSnapshot) cancelBackup.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.CreateAsync(cancelBackup.Token));
        Assert.Single(await f.Backup.ListVerifiedAsync(default));
        await f.Observe(ApState.Up);
        using var cancelRestore = new CancellationTokenSource();
        var second = new BackupService(f.Location, f.Gate, f.Clock, phase => { if (phase == BackupBoundary.BeforeRestoreReplace) cancelRestore.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.RestoreAsync(backup.BackupPath, cancelRestore.Token));
        Assert.Equal(2, Fixture.Count(f.DatabasePath, "run_results"));
        Assert.True(File.Exists(backup.BackupPath));
        Assert.Empty(System.IO.Directory.EnumerateFiles(f.Directory.Path, "*.staging*"));
    }

    [Fact]
    public async Task ClockRollback_DoesNotPruneJustCreatedVerifiedBackup()
    {
        using var f = await Fixture.Create();
        await f.Observe(ApState.Down);
        for (var i = 0; i < 7; i++) { await f.Backup.CreateAsync(default); f.Clock.Advance(TimeSpan.FromDays(1)); }
        f.Clock.Advance(TimeSpan.FromDays(-10));
        var current = await f.Backup.CreateAsync(default);
        Assert.True(File.Exists(current.BackupPath));
        Assert.Contains(await f.Backup.ListVerifiedAsync(default), item => item.BackupPath == current.BackupPath);
        Assert.Equal(7, (await f.Backup.ListVerifiedAsync(default)).Count);
    }

    internal sealed class Fixture : IDisposable
    {
        public TemporaryDirectory Directory { get; } = new();
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ManualTimeProvider Clock { get; } = new();
        public StorageLocation Location { get; }
        public SqliteCollectionStore Store { get; }
        public BackupService Backup { get; }
        public ProfileSnapshot Profile { get; private set; } = null!;
        public string DatabasePath => Path.Combine(Directory.Path, "monitor.db");
        private Fixture(AppMode mode) { Location = StoragePaths.Open(mode, Directory.Path); Store = new(Location, Gate, Clock); Backup = new(Location, Gate, Clock); }
        public static async Task<Fixture> Create(AppMode mode = AppMode.Demo)
        {
            var f = new Fixture(mode);
            var profiles = new ProfileStore(f.Location);
            if (mode == AppMode.Live) await profiles.UpdateAsync(new("https://example.invalid/"), 5, new ProxySettings(), 0, default);
            else await profiles.EnsureInitializedAsync(default);
            f.Profile = profiles.Current!;
            await f.Store.InitializeAsync(default);
            return f;
        }
        public async Task<long> Observe(ApState state)
        {
            var context = await Store.BeginRunAsync(Profile, CollectionKind.ApStatus, RunTrigger.Manual, default);
            var evidence = new FieldEvidence(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.Demo, Clock.GetUtcNow());
            var observation = new ApObservation("1", state, new("S1", "02:00:00:00:00:01"), new("AP1", "192.0.2.1", "model", null), new(), new(), Clock.GetUtcNow(), null, null,
                ImmutableDictionary<string, FieldEvidence>.Empty.Add("is_up", evidence).Add("serial_number", evidence));
            await Store.ApplyApBatchAsync(context, new(RunOutcome.Succeeded, [observation], [], Clock.GetUtcNow(), ObservationCompleteness.Complete, []), default);
            Clock.Advance(TimeSpan.FromMinutes(5));
            return context.RunId;
        }
        public SqliteConnection Open() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false, ForeignKeys = true }.ToString()); c.Open(); return c; }
        public static long Count(string path, string table) { using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM " + table; return (long)cmd.ExecuteScalar()!; }
        public void Dispose() { Gate.Dispose(); Directory.Dispose(); }
    }
}
