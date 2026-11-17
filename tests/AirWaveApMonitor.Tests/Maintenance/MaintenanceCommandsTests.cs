using System.Text.Json;
using AirWaveApMonitor.App.Maintenance;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Tests.Support;
using static AirWaveApMonitor.Tests.Maintenance.BackupServiceTests;

namespace AirWaveApMonitor.Tests.Maintenance;

public sealed class MaintenanceCommandsTests
{
    [Fact]
    public async Task OrdinaryArgumentsAreLeftToNormalHost()
        => Assert.Null(await MaintenanceCommands.TryHandleAsync(["--mode", "demo"]));

    [Theory]
    [InlineData("restore", false)]
    [InlineData("unknown", false)]
    [InlineData("backup", true)]
    public async Task InvalidArgumentsFailWithoutCreatingData(string operation, bool extra)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "not-created");
        var args = new List<string> { "--maintenance", operation, "--mode", "demo", "--data-dir", path };
        if (extra) args.AddRange(["--backup-file", "not-accepted-for-backup"]);
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(1, await MaintenanceCommands.TryHandleAsync(args.ToArray(), output: output, error: error));
        Assert.False(Directory.Exists(path));
        Assert.Equal("", output.ToString());
        Assert.NotEmpty(error.ToString());
    }

    [Fact]
    public async Task CliBackup_ReturnsRealVerifiedPathAndRefusesActiveCollector()
    {
        using var f = await Fixture.Create();
        await f.Observe(ApState.Down);
        string[] args = ["--maintenance", "pre-update-backup", "--mode", "demo", "--data-dir", f.Directory.Path];
        using var output = new StringWriter(); using var error = new StringWriter();
        using (var owner = new InstanceLock(f.Location)) Assert.Equal(1, await MaintenanceCommands.TryHandleAsync(args, output: output, error: error));
        Assert.Empty(await f.Backup.ListVerifiedAsync(default));
        Assert.Equal(0, await MaintenanceCommands.TryHandleAsync(args, output: output, error: error));
        using var document = JsonDocument.Parse(output.ToString());
        var result = document.RootElement;
        Assert.True(File.Exists(result.GetProperty("backupPath").GetString()));
        Assert.Equal("pre-update-backup", result.GetProperty("operation").GetString());
        Assert.Equal(64, result.GetProperty("sha256").GetString()!.Length);
    }
}
