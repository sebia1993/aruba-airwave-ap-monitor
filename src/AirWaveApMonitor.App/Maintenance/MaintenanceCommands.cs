using System.Text.Json;
using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using Microsoft.Data.Sqlite;

namespace AirWaveApMonitor.App.Maintenance;

/// <summary>Intercept before building the HTTP host. A null result means ordinary app execution.</summary>
public static class MaintenanceCommands
{
    public static async Task<int?> TryHandleAsync(string[] args, CancellationToken ct = default, TextWriter? output = null, TextWriter? error = null)
    {
        if (!args.Contains("--maintenance", StringComparer.Ordinal)) return null;
        output ??= Console.Out; error ??= Console.Error;
        try
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index += 2)
                if (args[index] is not ("--maintenance" or "--mode" or "--data-dir" or "--backup-file") || index + 1 >= args.Length ||
                    string.IsNullOrWhiteSpace(args[index + 1]) || !values.TryAdd(args[index], args[index + 1])) throw new ArgumentException();
            var operation = values["--maintenance"];
            if (operation is not ("backup" or "pre-update-backup" or "restore") ||
                (operation == "restore") != values.ContainsKey("--backup-file") || !values.ContainsKey("--mode")) throw new ArgumentException();
            var runtime = new List<string> { "--mode", values["--mode"] };
            if (values.TryGetValue("--data-dir", out var folder)) runtime.AddRange(["--data-dir", folder]);
            var options = RuntimeOptions.Parse(runtime.ToArray());
            var directory = StoragePaths.Resolve(options.Mode, options.DataDirectory);
            if (!File.Exists(Path.Combine(directory, "runtime-mode.json")) || !File.Exists(Path.Combine(directory, "monitor.db")))
                throw new InvalidDataException("기존 모드 표시자와 DB가 필요합니다.");
            var location = StoragePaths.Open(options.Mode, directory);
            using var gate = new SemaphoreSlim(1, 1);
            var service = new BackupService(location, gate);
            if (operation == "restore")
            {
                var result = await service.RestoreAsync(values["--backup-file"], ct);
                await output.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    operation,
                    restoredFrom = result.RestoredFrom,
                    datasetId = result.DatasetId,
                    schemaVersion = result.SchemaVersion,
                    occurredAtUtc = result.RestoredAtUtc,
                    restoreEventId = result.RestoreEventId,
                    preservedDatabasePath = result.PreservedDatabasePath,
                    requiresCredentialRegistration = result.RequiresCredentialRegistration
                }));
            }
            else
            {
                using var ownership = new InstanceLock(location);
                var result = await service.CreateAsync(operation == "backup" ? BackupKind.Daily : BackupKind.PreUpdate, ct);
                await output.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    operation,
                    backupPath = result.BackupPath,
                    datasetId = result.DatasetId,
                    schemaVersion = result.SchemaVersion,
                    sha256 = result.Sha256,
                    occurredAtUtc = result.CreatedAtUtc,
                    bytes = result.Bytes,
                    pruningError = result.PruningError
                }));
            }
            return 0;
        }
        catch (ArgumentException)
        {
            await error.WriteLineAsync("유지보수 옵션: --maintenance backup|pre-update-backup|restore --mode demo|live [--data-dir 경로]. restore에만 --backup-file 경로가 필요합니다.");
            return 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { await error.WriteLineAsync("유지보수 작업이 취소되었습니다. 기존 DB와 검증된 백업을 확인하세요."); return 130; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or SqliteException or JsonException)
        {
            await error.WriteLineAsync("유지보수를 완료하지 못했습니다. 수집기 종료, 폴더 권한·공간, 백업 SHA256, DB 스키마·모드·데이터셋을 확인하세요. 알 수 없는 현재 DB/WAL은 자동 대체하지 않습니다. 폴더 전체를 보존하세요.");
            return 1;
        }
    }
}
