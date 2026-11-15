using System.Security.Cryptography;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Maintenance;

public enum BackupBoundary { AfterSnapshot, BeforePublish, BeforeRestoreReplace }

/// <summary>DB-only backup. Runtime profile, mode marker and DPAPI files stay in the data directory.</summary>
public sealed class BackupService(StorageLocation location, SemaphoreSlim writeGate, TimeProvider? clock = null,
    Action<BackupBoundary>? boundary = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private string DatabasePath => Path.Combine(location.DataDirectory, "monitor.db");
    private string BackupRoot => Path.Combine(location.DataDirectory, "backups");

    public Task<VerifiedBackup> CreateAsync(CancellationToken ct) => CreateAsync(BackupKind.Daily, ct);

    public async Task<VerifiedBackup> CreateAsync(BackupKind kind, CancellationToken ct)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        await operationGate.WaitAsync(ct);
        string? stage = null, published = null, manifestStage = null;
        var committed = false;
        try
        {
            var directory = Path.Combine(BackupRoot, kind == BackupKind.Daily ? "daily" : "pre-update");
            Directory.CreateDirectory(directory);
            var created = time.GetUtcNow().ToUniversalTime();
            var basename = $"airwave-{(kind == BackupKind.Daily ? "daily" : "pre-update")}-{created:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}.db";
            published = Path.Combine(directory, basename);
            stage = published + ".staging";
            await writeGate.WaitAsync(ct);
            int version;
            try
            {
                using var source = Open(DatabasePath, SqliteOpenMode.ReadOnly);
                version = Validate(source);
                using var destination = Open(stage, SqliteOpenMode.ReadWriteCreate);
                // SQLite's backup API takes a consistent snapshot of the database including WAL.
                // The wrapper is synchronous; the shared write gate honestly blocks app writes here.
                source.BackupDatabase(destination);
                ct.ThrowIfCancellationRequested();
                if (Validate(destination) != version) throw new InvalidDataException("백업 스키마가 원본과 일치하지 않습니다.");
                Execute(destination, null, "PRAGMA journal_mode=DELETE;");
            }
            finally { writeGate.Release(); }
            Flush(stage);
            // The metadata timestamp is the completed snapshot boundary, after any gate wait.
            created = time.GetUtcNow().ToUniversalTime();
            boundary?.Invoke(BackupBoundary.AfterSnapshot);
            ct.ThrowIfCancellationRequested();
            var verified = new VerifiedBackup(published, location.DatasetId, location.Mode, version, kind, created,
                await HashAsync(stage, ct), new FileInfo(stage).Length);
            manifestStage = published + ".manifest.json.staging";
            await WriteJsonAsync(manifestStage, verified, ct);
            boundary?.Invoke(BackupBoundary.BeforePublish);
            ct.ThrowIfCancellationRequested();
            File.Move(stage, published);
            stage = null;
            File.Move(manifestStage, Manifest(published));
            manifestStage = null;
            committed = true;
            // A published backup remains successful even if subsequent housekeeping fails.
            // Pruning is also available separately to the maintenance worker, which reports its error.
            if (kind == BackupKind.Daily)
            {
                try { await PruneDailyAsync(published, CancellationToken.None); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                { verified = verified with { PruningError = "backup-prune-unavailable" }; }
            }
            return verified;
        }
        finally
        {
            try
            {
                DeleteOwnedStage(stage);
                DeleteOwnedStage(manifestStage);
                if (!committed && published is not null && File.Exists(published)) File.Delete(published);
            }
            finally { operationGate.Release(); }
        }
    }

    public async Task<IReadOnlyList<VerifiedBackup>> ListVerifiedAsync(CancellationToken ct)
    {
        var results = new List<VerifiedBackup>();
        foreach (var kind in new[] { BackupKind.Daily, BackupKind.PreUpdate })
        {
            var directory = Path.Combine(BackupRoot, kind == BackupKind.Daily ? "daily" : "pre-update");
            if (!Directory.Exists(directory)) continue;
            foreach (var manifest in Directory.EnumerateFiles(directory, "airwave-*.db.manifest.json"))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var backup = await VerifyAsync(manifest[..^".manifest.json".Length], ct);
                    if (backup.Kind == kind) results.Add(backup);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or InvalidOperationException or JsonException)
                {
                    // A damaged/orphaned candidate is preserved, not counted as a verified backup.
                }
            }
        }
        return results.OrderByDescending(b => b.CreatedAtUtc).ThenByDescending(b => b.BackupPath, StringComparer.Ordinal).ToArray();
    }

    private async Task PruneDailyAsync(string justCreated, CancellationToken ct)
    {
        var backups = (await ListVerifiedAsync(ct)).Where(b => b.Kind == BackupKind.Daily && b.BackupPath != justCreated).ToArray();
        // Preserve the new verified backup even when wall time moved backward.
        foreach (var expired in backups.Skip(6))
        {
            ct.ThrowIfCancellationRequested();
            File.Delete(expired.BackupPath);
            File.Delete(Manifest(expired.BackupPath));
        }
    }

    public async Task<RestoreReceipt> RestoreAsync(string backupPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        using var ownership = new InstanceLock(location); // paused collection still owns this lock and is refused.
        await operationGate.WaitAsync(ct);
        string? stage = null;
        try
        {
            var backup = await VerifyAsync(Path.GetFullPath(backupPath), ct);
            // Profile and DPAPI bytes are deliberately not restored from an older DB snapshot.
            var profile = new ProfileStore(location).Current;
            if (location.Mode == AppMode.Live && profile is null)
                throw new InvalidDataException("Live DB 복원에는 현재 데이터 폴더의 유효한 서버 설정이 필요합니다.");
            var restoredAt = time.GetUtcNow().ToUniversalTime();
            var eventId = Guid.NewGuid();
            stage = Path.Combine(location.DataDirectory, $"restore-{eventId:N}.staging");
            await CopyDurableAsync(backup.BackupPath, stage, ct);
            if (new FileInfo(stage).Length != backup.Bytes || await HashAsync(stage, ct) != backup.Sha256)
                throw new InvalidDataException("복사 중 백업 내용이 변경되었습니다. 원본 DB를 보존합니다.");
            await writeGate.WaitAsync(ct);
            try
            {
                using (var candidate = Open(stage, SqliteOpenMode.ReadWrite))
                {
                    // Migrations occur on the verified staging copy, never on the original backup.
                    new SchemaMigrator().Migrate(candidate, location, restoredAt);
                    using var transaction = candidate.BeginTransaction();
                    Execute(candidate, transaction, "INSERT INTO restore_events(restore_event_id,backup_sha256,backup_created_at_utc,restored_at_utc) VALUES($id,$hash,$backup,$restored)",
                        ("$id", eventId), ("$hash", backup.Sha256), ("$backup", backup.CreatedAtUtc), ("$restored", restoredAt));
                    if (location.Mode == AppMode.Live)
                    {
                        var binding = AuthenticationBinding.From(profile!);
                        Execute(candidate, transaction, """
                            INSERT INTO authentication_state(dataset_id,normalized_origin,credential_version,credential_registration_pending)
                            VALUES($dataset,$origin,$version,1)
                            ON CONFLICT(dataset_id,normalized_origin,credential_version) DO UPDATE SET credential_registration_pending=1
                            """, ("$dataset", binding.DatasetId), ("$origin", binding.NormalizedOrigin), ("$version", binding.CredentialVersion));
                    }
                    transaction.Commit();
                }
                // Validate current data BEFORE changing journal mode. An unknown/corrupt current
                // DB/hot WAL is preserved and requires a separate whole-folder recovery procedure.
                using (var currentCheck = Open(DatabasePath, SqliteOpenMode.ReadOnly)) Validate(currentCheck);
                using (var current = Open(DatabasePath, SqliteOpenMode.ReadWrite))
                {
                    Validate(current);
                    using var candidate = Open(stage, SqliteOpenMode.ReadWrite);
                    using var merge = candidate.BeginTransaction();
                    MergeAuthenticationStops(current, candidate, merge);
                    PreserveSequenceHighWaterMarks(current, candidate, merge);
                    merge.Commit();
                    Validate(candidate);
                    Execute(candidate, null, "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;");
                    using var checkpoint = Command(current, null, "PRAGMA wal_checkpoint(TRUNCATE)");
                    using var result = checkpoint.ExecuteReader();
                    if (!result.Read() || result.GetInt32(0) != 0) throw new IOException("기존 DB의 WAL을 안전하게 정리하지 못했습니다. 모든 DB 파일을 보존합니다.");
                    result.Close();
                    if (!string.Equals(Scalar(current, null, "PRAGMA journal_mode=DELETE") as string, "delete", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("기존 DB의 배타적 복원 준비를 완료하지 못했습니다.");
                }
                Flush(stage);
                boundary?.Invoke(BackupBoundary.BeforeRestoreReplace);
                ct.ThrowIfCancellationRequested();
                var preservedRoot = Path.Combine(BackupRoot, "pre-restore");
                Directory.CreateDirectory(preservedRoot);
                var preserved = Path.Combine(preservedRoot, $"before-restore-{eventId:N}.db");
                File.Replace(stage, DatabasePath, preserved);
                stage = null;
                // Never throw cancellation after the atomic replacement has committed.
                return new(backup.BackupPath, location.DatasetId, SchemaMigrator.CurrentVersion, restoredAt,
                    eventId, preserved, location.Mode == AppMode.Live);
            }
            finally { writeGate.Release(); }
        }
        finally { try { DeleteOwnedStage(stage); } finally { operationGate.Release(); } }
    }

    private static void PreserveSequenceHighWaterMarks(SqliteConnection current, SqliteConnection staged, SqliteTransaction transaction)
    {
        using var command = Command(current, null, "SELECT name,seq FROM sqlite_sequence");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (Scalar(staged, transaction, "SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$name", ("$name", name)) is null) continue;
            if (Execute(staged, transaction, "UPDATE sqlite_sequence SET seq=MAX(seq,$seq) WHERE name=$name", ("$name", name), ("$seq", reader.GetInt64(1))) == 0)
                Execute(staged, transaction, "INSERT INTO sqlite_sequence(name,seq) VALUES($name,$seq)", ("$name", name), ("$seq", reader.GetInt64(1)));
        }
    }

    private static void MergeAuthenticationStops(SqliteConnection current, SqliteConnection staged, SqliteTransaction transaction)
    {
        using var command = Command(current, null, "SELECT dataset_id,normalized_origin,credential_version,blocked_reason,blocked_at_utc,credential_registration_pending FROM authentication_state WHERE blocked_reason IS NOT NULL OR credential_registration_pending=1");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            Execute(staged, transaction, """
                INSERT INTO authentication_state VALUES($dataset,$origin,$version,$reason,$at,$pending)
                ON CONFLICT(dataset_id,normalized_origin,credential_version) DO UPDATE SET
                    blocked_reason=COALESCE(excluded.blocked_reason,authentication_state.blocked_reason),
                    blocked_at_utc=COALESCE(excluded.blocked_at_utc,authentication_state.blocked_at_utc),
                    credential_registration_pending=MAX(excluded.credential_registration_pending,authentication_state.credential_registration_pending)
                """, ("$dataset", reader.GetString(0)), ("$origin", reader.GetString(1)), ("$version", reader.GetInt64(2)),
                ("$reason", reader.IsDBNull(3) ? null : reader.GetString(3)), ("$at", reader.IsDBNull(4) ? null : reader.GetString(4)), ("$pending", reader.GetInt32(5)));
        }
    }

    private async Task<VerifiedBackup> VerifyAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path) || !File.Exists(Manifest(path))) throw new InvalidDataException("DB와 검증 명세가 함께 있는 백업이 필요합니다.");
        if (new FileInfo(Manifest(path)).Length > 16 * 1024) throw new InvalidDataException("백업 명세의 크기가 올바르지 않습니다.");
        VerifiedBackup backup;
        try { backup = JsonSerializer.Deserialize<VerifiedBackup>(await File.ReadAllTextAsync(Manifest(path), ct)) ?? throw new InvalidDataException("백업 명세가 비어 있습니다."); }
        catch (JsonException error) { throw new InvalidDataException("백업 명세를 읽을 수 없습니다.", error); }
        if (backup.DatasetId != location.DatasetId || backup.Mode != location.Mode || !Enum.IsDefined(backup.Kind) || backup.SchemaVersion is < 1 or > SchemaMigrator.CurrentVersion ||
            backup.Bytes != new FileInfo(path).Length || !string.Equals(backup.Sha256, await HashAsync(path, ct), StringComparison.Ordinal))
            throw new InvalidDataException("백업 데이터셋·모드·스키마·크기 또는 SHA256 검증에 실패했습니다.");
        using var connection = Open(path, SqliteOpenMode.ReadOnly);
        if (Validate(connection) != backup.SchemaVersion) throw new InvalidDataException("DB와 백업 명세의 스키마 버전이 다릅니다.");
        return backup with { BackupPath = Path.GetFullPath(path) };
    }

    private int Validate(SqliteConnection connection)
    {
        try { return new SchemaMigrator().ValidateExisting(connection, location); }
        catch (Exception error) when (error is InvalidOperationException or SqliteException)
        { throw new InvalidDataException("DB 스키마·데이터셋 또는 무결성을 확인할 수 없습니다. 기존 파일을 보존합니다.", error); }
    }
    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, ForeignKeys = true, DefaultTimeout = 5 }.ToString());
        try { connection.Open(); return connection; } catch { connection.Dispose(); throw; }
    }
    private static string Manifest(string path) => path + ".manifest.json";
    private static async Task<string> HashAsync(string path, CancellationToken ct)
    { await using var file = File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)); }
    private static void Flush(string path) { using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read); file.Flush(true); }
    private static async Task WriteJsonAsync(string path, VerifiedBackup backup, CancellationToken ct)
    { await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); await JsonSerializer.SerializeAsync(file, backup, cancellationToken: ct); await file.FlushAsync(ct); file.Flush(true); }
    private static async Task CopyDurableAsync(string source, string target, CancellationToken ct)
    { await using var input = File.OpenRead(source); await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None); await input.CopyToAsync(output, ct); await output.FlushAsync(ct); output.Flush(true); }
    private static void DeleteOwnedStage(string? path)
    {
        if (path is null) return;
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(candidate)) File.Delete(candidate);
    }
}
