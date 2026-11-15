using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.App.Maintenance;

try
{
    if (await MaintenanceCommands.TryHandleAsync(args) is { } result) return result;
    await using var app = AppBootstrap.Build(args);
    await app.RunAsync();
    return Environment.ExitCode;
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"시작 실패: {error.Message}");
    return 1;
}
