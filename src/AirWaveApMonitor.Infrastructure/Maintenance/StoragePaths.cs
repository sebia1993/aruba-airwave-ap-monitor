using AirWaveApMonitor.Core.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirWaveApMonitor.Infrastructure.Maintenance;

public sealed record StorageLocation(string DataDirectory, AppMode Mode, Guid DatasetId);

public static class StoragePaths
{
    private const string MarkerName = "runtime-mode.json";
    private const string LockName = "runtime-mode.lock";
    private static readonly JsonSerializerOptions MarkerJson = new()
    {
        Converters = { new JsonStringEnumConverter<AppMode>() }
    };

    public static string Resolve(AppMode mode, string? overridePath)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (overridePath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(overridePath);
            return Path.GetFullPath(overridePath);
        }

        var root = OperatingSystem.IsWindows() && mode == AppMode.Live
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.GetFullPath(Path.Combine(root, "AirWaveAPMonitor", mode.ToString().ToLowerInvariant()));
    }

    public static StorageLocation Open(AppMode mode, string? overridePath)
    {
        var directory = Resolve(mode, overridePath);
        Directory.CreateDirectory(directory);
        // The marker is attached to the physical directory, so relative paths, casing and
        // symlinks cannot make a Live directory into a Demo directory on a later launch.
        using var initializationLock = new FileStream(Path.Combine(directory, LockName),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var markerPath = Path.Combine(directory, MarkerName);
        if (File.Exists(markerPath))
        {
            ModeMarker? marker;
            try { marker = JsonSerializer.Deserialize<ModeMarker>(File.ReadAllText(markerPath), MarkerJson); }
            catch (JsonException error)
            {
                throw new InvalidOperationException("데이터 모드 표시자가 손상되었습니다. 기존 자료를 자동으로 채택하지 않습니다.", error);
            }
            if (marker is null || marker.SchemaVersion != 1 || marker.DatasetId == Guid.Empty || !Enum.IsDefined(marker.Mode))
                throw new InvalidOperationException("지원하지 않거나 유효하지 않은 데이터 모드 표시자입니다.");
            if (marker.Mode != mode)
                throw new InvalidOperationException($"이 데이터 폴더는 {marker.Mode} 전용입니다. {mode} 모드로 열 수 없습니다.");
            return new StorageLocation(directory, mode, marker.DatasetId);
        }

        if (Directory.EnumerateFileSystemEntries(directory).Any(path => Path.GetFileName(path) != LockName))
            throw new InvalidOperationException("모드 표시자 없이 기존 자료가 있는 폴더는 열 수 없습니다. 새 전용 폴더를 지정하세요.");

        var created = new ModeMarker(1, mode, Guid.NewGuid());
        // A crash during initialization leaves a nonempty unmarked directory: fail closed.
        var temporaryPath = markerPath + ".tmp";
        using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(output, created, MarkerJson);
            output.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, markerPath);
        return new StorageLocation(directory, mode, created.DatasetId);
    }

    private sealed record ModeMarker([property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] AppMode Mode, [property: JsonRequired] Guid DatasetId);
}
