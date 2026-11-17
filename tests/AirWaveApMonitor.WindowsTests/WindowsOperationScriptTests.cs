using System.Diagnostics;

namespace AirWaveApMonitor.WindowsTests;

public sealed class WindowsOperationScriptTests
{
    [WindowsFact]
    public Task PowerShell51_ParsesOperationScriptsWithKoreanContent()
        => RunAsync("ScriptSyntax.Tests.ps1");

    [WindowsFact]
    public Task PowerShell51_ValidatesOwnershipAndDataDirectoryBoundaries()
        => RunAsync("ScriptBehavior.Tests.ps1");

    [WindowsFact]
    public Task PowerShell51_NativeArgumentsPreserveQuotesBackslashesAndKorean()
        => RunAsync("NativeArguments.Tests.ps1");

    [WindowsFact]
    public Task PowerShell51_RegistersThreeRestartsThenTerminalNoneInActualScm()
        => RunAsync("ServiceRecovery.Tests.ps1");

    private static async Task RunAsync(string script)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        Assert.NotNull(root);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root.FullName
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", Path.Combine(root.FullName, "tests", "Windows", script), "-RepositoryRoot", root.FullName })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, $"{script}: {await output}\n{await error}");
    }
}
