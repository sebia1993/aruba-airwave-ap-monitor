using System.Globalization;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using Microsoft.Data.Sqlite;

namespace AirWaveApMonitor.Infrastructure.Security;

/// <summary>Short operations against the common monitor.db; callers share the database write gate.</summary>
public sealed class SqliteAuthenticationStateStore(StorageLocation location, SemaphoreSlim writeGate) : IAuthenticationStateStore
{
    // The 001 migrator calls EnsureSchema using its connection/transaction and this same definition.
    // This helper deliberately does not set PRAGMA user_version or claim a database schema version.
    public static void EnsureSchema(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS authentication_state (
                dataset_id TEXT NOT NULL,
                normalized_origin TEXT NOT NULL,
                credential_version INTEGER NOT NULL CHECK (credential_version > 0),
                blocked_reason TEXT NULL CHECK (blocked_reason IN ('LoginRejected', 'RepeatedSessionExpiry')),
                blocked_at_utc TEXT NULL,
                credential_registration_pending INTEGER NOT NULL DEFAULT 0 CHECK (credential_registration_pending IN (0, 1)),
                PRIMARY KEY (dataset_id, normalized_origin, credential_version),
                CHECK ((blocked_reason IS NULL) = (blocked_at_utc IS NULL))
            );
            """;
        command.ExecuteNonQuery();
    }

    public Task<AuthenticationState> ReadAsync(AuthenticationBinding binding, CancellationToken ct) => ExecuteAsync(binding, (connection, transaction) =>
    {
        using var command = Command(connection, transaction, binding, """
            SELECT blocked_reason, blocked_at_utc, credential_registration_pending
            FROM authentication_state
            WHERE dataset_id = $dataset AND normalized_origin = $origin AND credential_version = $version;
            """);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new AuthenticationState(null, false);
        AuthenticationStop? stop = null;
        if (!reader.IsDBNull(0))
        {
            if (!Enum.TryParse<AuthenticationStopReason>(reader.GetString(0), out var reason) || !Enum.IsDefined(reason) ||
                !DateTimeOffset.TryParseExact(reader.GetString(1), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var occurred))
                throw new AuthenticationStateStoreException();
            stop = new(binding, reason, occurred.ToUniversalTime());
        }
        return new AuthenticationState(stop, reader.GetInt64(2) != 0);
    }, ct);

    public Task SaveStopAsync(AuthenticationStop stop, CancellationToken ct)
    {
        if (!Enum.IsDefined(stop.Reason)) throw new ArgumentOutOfRangeException(nameof(stop));
        return ExecuteAsync(stop.Binding, (connection, transaction) =>
        {
            using var command = Command(connection, transaction, stop.Binding, """
                INSERT INTO authentication_state (dataset_id, normalized_origin, credential_version, blocked_reason, blocked_at_utc)
                VALUES ($dataset, $origin, $version, $reason, $occurred)
                ON CONFLICT (dataset_id, normalized_origin, credential_version) DO UPDATE SET
                    blocked_reason = excluded.blocked_reason, blocked_at_utc = excluded.blocked_at_utc;
                """);
            command.Parameters.AddWithValue("$reason", stop.Reason.ToString());
            command.Parameters.AddWithValue("$occurred", stop.OccurredAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            return command.ExecuteNonQuery();
        }, ct);
    }

    public Task ClearStopAsync(AuthenticationBinding binding, CancellationToken ct) => ExecuteAsync(binding, (connection, transaction) =>
    {
        using var command = Command(connection, transaction, binding, """
            UPDATE authentication_state SET blocked_reason = NULL, blocked_at_utc = NULL
            WHERE dataset_id = $dataset AND normalized_origin = $origin AND credential_version = $version;
            """);
        return command.ExecuteNonQuery();
    }, ct);

    public Task MarkCredentialRegistrationPendingAsync(AuthenticationBinding binding, CancellationToken ct) => ExecuteAsync(binding, (connection, transaction) =>
    {
        using var command = Command(connection, transaction, binding, """
            INSERT INTO authentication_state (dataset_id, normalized_origin, credential_version, credential_registration_pending)
            VALUES ($dataset, $origin, $version, 1)
            ON CONFLICT (dataset_id, normalized_origin, credential_version) DO UPDATE SET credential_registration_pending = 1;
            """);
        return command.ExecuteNonQuery();
    }, ct);

    private async Task<T> ExecuteAsync<T>(AuthenticationBinding binding, Func<SqliteConnection, SqliteTransaction, T> action, CancellationToken ct)
    {
        if (binding.DatasetId != location.DatasetId) throw new ArgumentException("다른 데이터셋의 인증 상태에 접근할 수 없습니다.");
        await writeGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(location.DataDirectory, "monitor.db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString());
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
                pragma.ExecuteNonQuery();
            }
            // Microsoft.Data.Sqlite executes SQL synchronously. Cancellation is checked at
            // operation boundaries; SQLite busy waits are capped by DefaultTimeout.
            ct.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction();
            EnsureSchema(connection, transaction);
            var result = action(connection, transaction);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return result; // A completed durable commit is not reported as cancelled.
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new AuthenticationStateStoreException();
        }
        finally { writeGate.Release(); }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, AuthenticationBinding binding, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$dataset", binding.DatasetId.ToString("D"));
        command.Parameters.AddWithValue("$origin", binding.NormalizedOrigin);
        command.Parameters.AddWithValue("$version", binding.CredentialVersion);
        return command;
    }
}
