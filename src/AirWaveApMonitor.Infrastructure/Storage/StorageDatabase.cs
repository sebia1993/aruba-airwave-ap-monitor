using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace AirWaveApMonitor.Infrastructure.Storage;

internal static class StorageDatabase
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new UtcConverter() }
    };

    public static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value switch
            {
                null => DBNull.Value,
                Guid id => id.ToString("D"),
                DateTimeOffset time => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                Enum enumeration => Convert.ToInt32(enumeration, CultureInfo.InvariantCulture),
                _ => value
            });
        }
        return command;
    }

    public static int Execute(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        var result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T Decode<T>(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(value, Json) ?? throw new InvalidDataException("저장된 JSON이 비어 있습니다.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("저장된 자료의 형식을 확인할 수 없습니다.", error);
        }
    }

    public static DateTimeOffset? Time(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : DateTimeOffset.ParseExact(reader.GetString(ordinal), "O", CultureInfo.InvariantCulture).ToUniversalTime();

    private sealed class UtcConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetDateTimeOffset().ToUniversalTime();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToUniversalTime());
    }
}
