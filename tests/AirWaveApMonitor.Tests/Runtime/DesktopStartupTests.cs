using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AirWaveApMonitor.Tests.Runtime;

[Collection("Host environment")]
public sealed class DesktopStartupTests
{
    [Fact]
    public void DesktopWithoutMode_UsesLiveAndExistingDefaultPort()
    {
        var options = RuntimeOptions.Parse(["--desktop", "true"]);
        Assert.Equal(AppMode.Live, options.Mode);
        Assert.Equal(8765, options.Port);
        Assert.Null(options.DataDirectory);
    }

    [Fact]
    public void DesktopExplicitDemo_PreservesDataDirectoryAndPort()
    {
        var options = RuntimeOptions.Parse(["--desktop", "true", "--mode", "demo", "--data-dir", "/synthetic/한글 폴더", "--port", "18765", "--no-browser", "true"]);
        Assert.Equal(AppMode.Demo, options.Mode);
        Assert.Equal("/synthetic/한글 폴더", options.DataDirectory);
        Assert.Equal(18765, options.Port);
    }

    [Fact]
    public async Task Health_IdentifiesTheExactProductDatasetModeAndProcess()
    {
        using var directory = new TemporaryDirectory();
        var port = FreePort();
        await using var app = AppBootstrap.Build(["--mode", "live", "--data-dir", directory.Path, "--port", port.ToString()]);
        await app.StartAsync();
        using var client = new HttpClient();
        using var json = JsonDocument.Parse(await client.GetStringAsync($"http://127.0.0.1:{port}/api/health"));
        var health = json.RootElement;
        Assert.True(health.TryGetProperty("product", out var product), "Health must identify this product before a launcher can reuse its port.");
        Assert.Equal("AirWaveAPMonitor", product.GetString());
        Assert.Equal(app.Services.GetRequiredService<StorageLocation>().DatasetId, health.GetProperty("datasetId").GetGuid());
        Assert.Equal("live", health.GetProperty("mode").GetString());
        Assert.Equal(Environment.ProcessId, health.GetProperty("processId").GetInt32());
        Assert.False(health.GetProperty("liveConfigured").GetBoolean());
    }

    [Theory]
    [InlineData("--desktop", "TRUE")]
    [InlineData("--no-browser", "yes")]
    public void DesktopFlags_RejectNonBooleanValues(string flag, string value)
        => Assert.Throws<ArgumentException>(() => RuntimeOptions.Parse(["--mode", "live", flag, value]));

    [Fact]
    public void DesktopFlags_DoNotRelaxServiceOrHeadlessModeContracts()
    {
        Assert.Throws<ArgumentException>(() => RuntimeOptions.Parse([]));
        Assert.Throws<ArgumentException>(() => RuntimeOptions.Parse(["--desktop", "false"]));
        Assert.Throws<ArgumentException>(() => RuntimeOptions.Parse(["--mode", "live", "--no-browser", "true"]));
        Assert.Throws<ArgumentException>(() => RuntimeOptions.Parse(["--desktop", "true", "--service-name", "service"]));
        Assert.Throws<ArgumentException>(() => RuntimeOptions.Parse(["--desktop", "true", "--desktop", "false"]));
    }

    [Fact]
    public async Task FreshUnconfiguredLive_OpensBrowserOnlyAfterHttpIsReady()
    {
        using var directory = new TemporaryDirectory();
        var port = FreePort();
        await using var app = DesktopApp(directory.Path, port);
        var shell = new RecordingShell();
        Assert.True(await DesktopStartup.StartAsync(app, shell));
        Assert.Equal($"http://127.0.0.1:{port}/", Assert.Single(shell.Opened).AbsoluteUri);
        Assert.Equal(HttpStatusCode.OK, shell.StatusWhenOpened);
        Assert.Empty(shell.Errors);
        using var client = new HttpClient();
        using var health = JsonDocument.Parse(await client.GetStringAsync($"http://127.0.0.1:{port}/api/health"));
        Assert.False(health.RootElement.GetProperty("liveConfigured").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"http://127.0.0.1:{port}/api/dashboard")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameDataset_ReusesExistingServerWithoutStartingAnotherCollector(bool noBrowser)
    {
        using var directory = new TemporaryDirectory();
        var port = FreePort();
        await using var first = DesktopApp(directory.Path, port);
        await first.StartAsync();
        var marker = await File.ReadAllBytesAsync(Path.Combine(directory.Path, "runtime-mode.json"));
        await using var second = DesktopApp(directory.Path, port, noBrowser);
        var shell = new RecordingShell();
        Assert.False(await DesktopStartup.StartAsync(second, shell));
        Assert.Empty(second.Urls);
        Assert.Equal(noBrowser ? 0 : 1, shell.Opened.Count);
        Assert.Empty(shell.Errors);
        Assert.Equal(marker, await File.ReadAllBytesAsync(Path.Combine(directory.Path, "runtime-mode.json")));
        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"http://127.0.0.1:{port}/api/health")).StatusCode);
    }

    [Fact]
    public async Task AnotherDatasetOnPort_IsNotOpenedOrStopped()
    {
        using var firstDirectory = new TemporaryDirectory();
        using var secondDirectory = new TemporaryDirectory();
        var port = FreePort();
        await using var first = DesktopApp(firstDirectory.Path, port);
        await first.StartAsync();
        await using var second = DesktopApp(secondDirectory.Path, port);
        var shell = new RecordingShell();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopStartup.StartAsync(second, shell));
        Assert.Empty(shell.Opened);
        Assert.False(File.Exists(Path.Combine(secondDirectory.Path, "monitor.db")));
        using var client = new HttpClient();
        using var json = JsonDocument.Parse(await client.GetStringAsync($"http://127.0.0.1:{port}/api/health"));
        Assert.Equal(first.Services.GetRequiredService<StorageLocation>().DatasetId, json.RootElement.GetProperty("datasetId").GetGuid());
    }

    [Theory]
    [InlineData("product")]
    [InlineData("mode")]
    [InlineData("dataset")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("redirect")]
    public async Task UnverifiedHealth_IsNeverReused(string reason)
    {
        using var directory = new TemporaryDirectory();
        var port = FreePort();
        await using var candidate = DesktopApp(directory.Path, port);
        var dataset = candidate.Services.GetRequiredService<StorageLocation>().DatasetId;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, port));
        await using var foreign = builder.Build();
        foreign.MapGet("/api/health", () => reason switch
        {
            "malformed" => Results.Text("not json", "application/json"),
            "oversized" => Results.Text(new string('x', 65537), "application/json"),
            "redirect" => Results.Redirect("https://must-not-follow.invalid/"),
            _ => Results.Json(new { product = reason == "product" ? "OtherApp" : "AirWaveAPMonitor",
                mode = reason == "mode" ? "demo" : "live", datasetId = reason == "dataset" ? Guid.NewGuid() : dataset })
        });
        await foreign.StartAsync();
        var shell = new RecordingShell();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopStartup.StartAsync(candidate, shell));
        Assert.Empty(shell.Opened);
        Assert.Empty(candidate.Urls);
        Assert.False(File.Exists(Path.Combine(directory.Path, "monitor.db")));
    }

    [Fact]
    public async Task BrowserLaunchFailure_LeavesReadyCollectorAvailableAtPrintedAddress()
    {
        using var directory = new TemporaryDirectory();
        var port = FreePort();
        await using var app = DesktopApp(directory.Path, port);
        var shell = new RecordingShell { FailOpen = true };
        Assert.True(await DesktopStartup.StartAsync(app, shell));
        Assert.Contains($"http://127.0.0.1:{port}/", Assert.Single(shell.Errors));
        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"http://127.0.0.1:{port}/")).StatusCode);
    }

    [Fact]
    public async Task NoBrowser_StartsCollectorWithoutAnyDesktopInteraction()
    {
        using var directory = new TemporaryDirectory();
        var port = FreePort();
        await using var app = DesktopApp(directory.Path, port, noBrowser: true);
        var shell = new RecordingShell { FailOpen = true };
        Assert.True(await DesktopStartup.StartAsync(app, shell));
        Assert.Empty(shell.Opened); Assert.Empty(shell.Errors);
        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"http://127.0.0.1:{port}/")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupFailure_ReportsReasonAndHonorsNoBrowserEvenForMalformedArguments(bool noBrowser)
    {
        var shell = new RecordingShell();
        // An unpaired user option must not hide desktop error presentation or its suppression.
        DesktopStartup.ReportFailure(["--desktop", "true", "--bogus", "--no-browser", noBrowser ? "true" : "false"],
            new IOException("synthetic startup reason"), shell);
        Assert.Empty(shell.Opened);
        if (noBrowser) Assert.Empty(shell.Errors);
        else Assert.Contains("synthetic startup reason", Assert.Single(shell.Errors));
    }

    [Fact]
    public void MalformedBackendArguments_WithDesktopAtOddIndexStillShowStartupFailure()
    {
        string[] args = ["--port", "--desktop", "true"];
        var error = Assert.Throws<ArgumentException>(() => RuntimeOptions.Parse(args));
        var shell = new RecordingShell();
        DesktopStartup.ReportFailure(args, error, shell);
        Assert.Single(shell.Errors);
        Assert.Empty(shell.Opened);
    }

    [Fact]
    public async Task ExistingUnresponsivePort_IsNotAdoptedOrReplaced()
    {
        using var directory = new TemporaryDirectory();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var candidate = DesktopApp(directory.Path, port);
        var shell = new RecordingShell();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopStartup.StartAsync(candidate, shell));
        Assert.Empty(shell.Opened); Assert.Empty(candidate.Urls);
        Assert.False(File.Exists(Path.Combine(directory.Path, "monitor.db")));
        Assert.True(listener.Server.IsBound);
    }

    [Fact]
    public async Task StartupFailure_PreservesUnreadableDatabaseAndDoesNotOpenBrowser()
    {
        using var directory = new TemporaryDirectory();
        _ = StoragePaths.Open(AppMode.Live, directory.Path);
        var database = Path.Combine(directory.Path, "monitor.db");
        var original = "synthetic unreadable database"u8.ToArray();
        await File.WriteAllBytesAsync(database, original);
        await using var app = DesktopApp(directory.Path, FreePort());
        var shell = new RecordingShell();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DesktopStartup.StartAsync(app, shell));
        Assert.Empty(shell.Opened);
        Assert.Equal(original, await File.ReadAllBytesAsync(database));
    }

    private static WebApplication DesktopApp(string directory, int port, bool noBrowser = false)
        => AppBootstrap.Build(["--desktop", "true", "--data-dir", directory, "--port", port.ToString(), "--no-browser", noBrowser ? "true" : "false"]);

    private sealed class RecordingShell : IDesktopShell
    {
        public bool FailOpen { get; init; }
        public List<Uri> Opened { get; } = [];
        public List<string> Errors { get; } = [];
        public HttpStatusCode? StatusWhenOpened { get; private set; }
        public void OpenBrowser(Uri address)
        {
            Opened.Add(address);
            if (FailOpen) throw new InvalidOperationException("No browser association in synthetic desktop test.");
            using var client = new HttpClient();
            StatusWhenOpened = client.GetAsync(address).GetAwaiter().GetResult().StatusCode;
        }
        public void ShowError(string message) => Errors.Add(message);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
