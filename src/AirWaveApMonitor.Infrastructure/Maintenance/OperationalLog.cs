using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
namespace AirWaveApMonitor.Infrastructure.Maintenance;

/// <summary>Only controlled UTC/category/run IDs. Shared path gate covers append, rotation and expiry.</summary>
public sealed class OperationalLog(StorageLocation location, TimeProvider clock)
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string path = Path.Combine(location.DataDirectory, "collector-diagnostics.log");
    private object Gate => Gates.GetOrAdd(path, static _ => new object());
    public bool Record(CollectionErrorCategory category, long? runId)
    {
        if (!Enum.IsDefined(category)) throw new ArgumentOutOfRangeException(nameof(category));
        lock (Gate)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length >= 64 * 1024) File.Move(path, path + ".previous", true);
                var line = JsonSerializer.Serialize(new { utc = clock.GetUtcNow().ToUniversalTime(), category = category.ToString(), runId });
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    public int Prune(DateTimeOffset beforeUtc)
    {
        lock (Gate)
        {
            var removed = 0;
            foreach (var candidate in new[] { path, path + ".previous" })
            {
                if (!File.Exists(candidate)) continue;
                if (new FileInfo(candidate).Length > 1024 * 1024) throw new InvalidDataException("운영 로그 크기가 예상 범위를 벗어났습니다. 원본을 보존합니다.");
                var lines = File.ReadAllLines(candidate);
                var retained = lines.Where(line => !CanExpire(line, beforeUtc)).ToArray();
                if (retained.Length == lines.Length) continue;
                var temporary = candidate + "." + Guid.NewGuid().ToString("N") + ".staging";
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
                        foreach (var line in retained) writer.WriteLine(line);
                        writer.Flush(); output.Flush(true);
                    }
                    File.Move(temporary, candidate, true);
                    removed += lines.Length - retained.Length;
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return removed;
        }
    }

    private static bool CanExpire(string line, DateTimeOffset beforeUtc)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            var value = json.RootElement;
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 3 ||
                !value.TryGetProperty("utc", out var at) || !value.TryGetProperty("category", out var category) || !value.TryGetProperty("runId", out var runId) ||
                !DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp) ||
                !Enum.TryParse<CollectionErrorCategory>(category.GetString(), out var parsed) || !Enum.IsDefined(parsed) ||
                runId.ValueKind is not (JsonValueKind.Null or JsonValueKind.Number) ||
                (runId.ValueKind == JsonValueKind.Number && !runId.TryGetInt64(out _))) return false;
            return timestamp < beforeUtc;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { return false; }
    }
}
