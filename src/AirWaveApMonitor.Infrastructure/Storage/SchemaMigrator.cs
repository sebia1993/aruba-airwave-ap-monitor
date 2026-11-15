using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Security;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed class SchemaMigrator
{
    public const int CurrentVersion = 5;
    private static readonly Lazy<Dictionary<string, string>> VersionOneDefinitions = new(() => BuildExpectedDefinitions(1));
    private static readonly Lazy<Dictionary<string, string>> VersionTwoDefinitions = new(() => BuildExpectedDefinitions(2));
    private static readonly Lazy<Dictionary<string, string>> VersionThreeDefinitions = new(() => BuildExpectedDefinitions(3));

    private static readonly Lazy<Dictionary<string, string>> VersionFourDefinitions = new(() => BuildExpectedDefinitions(4));

    private static readonly Lazy<Dictionary<string, string>> VersionFiveDefinitions = new(() => BuildExpectedDefinitions(5));

    public void Migrate(SqliteConnection connection, StorageLocation location, DateTimeOffset now)
    {
        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version"));
        if (version is < 0 or > CurrentVersion)
        {
            throw new InvalidOperationException("지원하지 않는 DB 스키마 버전입니다. 기존 파일을 보존합니다.");
        }
        if (!Equals(Scalar(connection, null, "PRAGMA quick_check"), "ok"))
        {
            throw new InvalidOperationException("SQLite 무결성 검사에 실패했습니다. 기존 파일을 보존합니다.");
        }
        var tables = Tables(connection);
        if (version == 0 && tables.Any(name => name != "authentication_state"))
        {
            throw new InvalidOperationException("알 수 없는 기존 DB를 자동 마이그레이션하지 않습니다.");
        }
        if (tables.Contains("authentication_state"))
        {
            ValidateAuthenticationSchema(connection, location);
        }
        if (version > 0)
        {
            ValidateCoreSchema(connection, location, version);
        }
        Execute(connection, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
        if (version == CurrentVersion)
        {
            return;
        }
        using var transaction = connection.BeginTransaction();
        if (version == 0)
        {
            Execute(connection, transaction, ReadMigration("001_core.sql"));
            SqliteAuthenticationStateStore.EnsureSchema(connection, transaction);
            Execute(connection, transaction, "INSERT INTO dataset VALUES($dataset,$mode,1)",
                ("$dataset", location.DatasetId), ("$mode", location.Mode));
            Execute(connection, transaction, "INSERT INTO schema_migrations VALUES(1,$at)", ("$at", now));
        }
        if (version < 2)
        {
            Execute(connection, transaction, ReadMigration("002_source_checkpoints.sql"));
            Execute(connection, transaction, "UPDATE dataset SET schema_version=2; INSERT INTO schema_migrations VALUES(2,$at); PRAGMA user_version=2", ("$at", now));
        }
        if (version < 3)
        {
            Execute(connection, transaction, ReadMigration("003_maintenance.sql"));
            Execute(connection, transaction, "UPDATE dataset SET schema_version=3; INSERT INTO schema_migrations VALUES(3,$at); PRAGMA user_version=3", ("$at", now));
        }
        if (version < 4)
        {
            Execute(connection, transaction, ReadMigration("004_operations.sql"));
            Execute(connection, transaction, "UPDATE dataset SET schema_version=4; INSERT INTO schema_migrations VALUES(4,$at); PRAGMA user_version=4", ("$at", now));
        }
        Execute(connection, transaction, ReadMigration("005_enrichment.sql"));
        Execute(connection, transaction, "UPDATE dataset SET schema_version=5; INSERT INTO schema_migrations VALUES(5,$at); PRAGMA user_version=5", ("$at", now));
        transaction.Commit();
    }

    /// <summary>Read-only compatibility and integrity validation; never creates, migrates or recovers runs.</summary>
    public int ValidateExisting(SqliteConnection connection, StorageLocation location)
    {
        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version"));
        if (version is < 1 or > CurrentVersion || !Equals(Scalar(connection, null, "PRAGMA quick_check"), "ok"))
            throw new InvalidDataException("지원하지 않거나 무결성이 확인되지 않은 DB입니다. 기존 파일을 보존합니다.");
        ValidateAuthenticationSchema(connection, location);
        ValidateCoreSchema(connection, location, version);
        return version;
    }

    private static HashSet<string> Tables(SqliteConnection connection)
    {
        using var command = Command(connection, null, "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%'");
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    private static void ValidateAuthenticationSchema(SqliteConnection connection, StorageLocation location)
    {
        var definitions = ReadDefinitions(connection);
        if (!definitions.TryGetValue("table:authentication_state", out var definition) ||
            definition != VersionOneDefinitions.Value["table:authentication_state"])
        {
            throw new InvalidOperationException("기존 인증 상태 DB 스키마를 확인할 수 없습니다.");
        }
        using var command = Command(connection, null, "PRAGMA table_info(authentication_state)");
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }
        reader.Close();
        string[] expected = ["dataset_id", "normalized_origin", "credential_version", "blocked_reason", "blocked_at_utc", "credential_registration_pending"];
        if (!columns.SequenceEqual(expected) || Convert.ToInt64(Scalar(connection, null,
            "SELECT COUNT(*) FROM authentication_state WHERE dataset_id != $dataset", ("$dataset", location.DatasetId))) != 0)
        {
            throw new InvalidOperationException("기존 인증 상태 DB 형식 또는 데이터셋을 확인할 수 없습니다.");
        }
    }

    private static void ValidateCoreSchema(SqliteConnection connection, StorageLocation location, int version)
    {
        try
        {
            if (Convert.ToInt64(Scalar(connection, null, "SELECT COUNT(*) FROM dataset")) != 1 ||
                Convert.ToInt64(Scalar(connection, null,
                    "SELECT COUNT(*) FROM dataset WHERE dataset_id=$dataset AND mode=$mode AND schema_version=$version",
                    ("$dataset", location.DatasetId), ("$mode", location.Mode), ("$version", version))) != 1)
            {
                throw new InvalidOperationException("DB와 데이터 폴더의 데이터셋/모드가 일치하지 않습니다.");
            }
            var actualDefinitions = ReadDefinitions(connection);
            var expected = version switch { 1 => VersionOneDefinitions.Value, 2 => VersionTwoDefinitions.Value, 3 => VersionThreeDefinitions.Value, 4 => VersionFourDefinitions.Value, 5 => VersionFiveDefinitions.Value, _ => throw new InvalidOperationException("지원하지 않는 DB 버전입니다.") };
            if (expected.Any(pair => !actualDefinitions.TryGetValue(pair.Key, out var actual) || actual != pair.Value))
            {
                throw new InvalidOperationException("필수 DB 열 또는 무결성 제약이 변경되었습니다.");
            }
            string[] required = ["schema_migrations", "profiles", "scopes", "expected_targets", "runs", "run_results", "aps",
                "generations", "monitoring_intervals", "metadata_versions", "ap_current", "incidents", "incident_snapshots",
                "observation_gaps", "run_rows", "run_transitions", "metadata_changes", "monitoring_changes", "success_times"];
            if (!Tables(connection).IsSupersetOf(required) ||
                Convert.ToInt64(Scalar(connection, null, "SELECT COUNT(*) FROM sqlite_schema WHERE type='index' AND name IN ('one_running_ap_run','one_open_incident_per_generation')")) != 2)
            {
                throw new InvalidOperationException("필수 DB 스키마 또는 제약이 없습니다.");
            }
            using var command = Command(connection, null, "PRAGMA foreign_key_check");
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                throw new InvalidOperationException("DB 참조 무결성 검사에 실패했습니다.");
            }
        }
        catch (SqliteException error)
        {
            throw new InvalidOperationException("DB 스키마를 확인할 수 없습니다. 기존 파일을 보존합니다.", error);
        }
    }
    private static Dictionary<string, string> BuildExpectedDefinitions(int version)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, null, ReadMigration("001_core.sql"));
        if (version >= 2) Execute(connection, null, ReadMigration("002_source_checkpoints.sql"));
        if (version >= 3) Execute(connection, null, ReadMigration("003_maintenance.sql"));
        if (version >= 4) Execute(connection, null, ReadMigration("004_operations.sql"));
        if (version >= 5) Execute(connection, null, ReadMigration("005_enrichment.sql"));
        SqliteAuthenticationStateStore.EnsureSchema(connection);
        return ReadDefinitions(connection);
    }

    private static string ReadMigration(string file)
    {
        using var stream = typeof(SchemaMigrator).Assembly.GetManifestResourceStream(
            "AirWaveApMonitor.Infrastructure.Storage.Migrations." + file) ?? throw new InvalidOperationException("마이그레이션 리소스가 없습니다.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Dictionary<string, string> ReadDefinitions(SqliteConnection connection)
    {
        using var command = Command(connection, null,
            "SELECT type,name,sql FROM sqlite_schema WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'");
        using var reader = command.ExecuteReader();
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            definitions.Add(reader.GetString(0) + ":" + reader.GetString(1),
                Regex.Replace(reader.GetString(2), @"\s+", " ").Trim());
        }
        return definitions;
    }

}
