using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.App.Maintenance;

try
{
    if (await MaintenanceCommands.TryHandleAsync(args) is { } result) return result;
    await using var app = AppBootstrap.Build(args);
    if (app.Services.GetRequiredService<RuntimeOptions>().Desktop)
    {
        if (await DesktopStartup.StartAsync(app, new WindowsDesktopShell())) await app.WaitForShutdownAsync();
        return Environment.ExitCode;
    }
    await app.RunAsync();
    return Environment.ExitCode;
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
{
    DesktopStartup.ReportFailure(args, error, new WindowsDesktopShell());
    return 1;
}
