using AirWaveApMonitor.App.Runtime;

try
{
    await using var app = AppBootstrap.Build(args);
    await app.RunAsync();
    return 0;
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"시작 실패: {error.Message}");
    return 1;
}
