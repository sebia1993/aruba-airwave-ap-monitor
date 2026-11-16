using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using AirWaveApMonitor.Infrastructure.Maintenance;

namespace AirWaveApMonitor.App.Runtime;

public interface IDesktopShell
{
    void OpenBrowser(Uri address);
    void ShowError(string message);
}

public static class DesktopStartup
{
    public const string Product = "AirWaveAPMonitor";

    /// <returns>True when this host started; false when the already-running host was reused.</returns>
    public static async Task<bool> StartAsync(WebApplication app, IDesktopShell shell, CancellationToken ct = default)
    {
        var options = app.Services.GetRequiredService<RuntimeOptions>();
        if (!options.Desktop) throw new InvalidOperationException("desktop 시작 옵션이 필요합니다.");
        var location = app.Services.GetRequiredService<StorageLocation>();
        var address = new Uri($"http://127.0.0.1:{options.Port}/");
        var existing = await ProbeAsync(address, location, ct);
        if (existing == Existing.Match)
        {
            Console.WriteLine($"이미 실행 중인 동일 데이터의 수집기를 사용합니다: {address}");
            OpenBrowser(shell, options, address);
            return false;
        }
        if (existing == Existing.Conflict) throw PortConflict(options.Port);
        try { await app.StartAsync(ct); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            // Another double-click may have won the data lock/listener after our first probe.
            // Reuse only its verified identity; never terminate or adopt an unrelated process.
            if (await ProbeAsync(address, location, ct) != Existing.Match) throw;
            Console.WriteLine($"동시에 시작된 동일 데이터의 수집기를 사용합니다: {address}");
            OpenBrowser(shell, options, address);
            return false;
        }
        if (OperatingSystem.IsWindows())
        {
            try { Console.Title = $"AirWave AP Monitor — {options.Mode} — {address}"; }
            catch (IOException) { /* A redirected or detached console must not stop the ready host. */ }
        }
        Console.WriteLine($"브라우저 주소: {address}");
        Console.WriteLine("브라우저를 닫아도 수집은 계속됩니다. 이 콘솔 창을 닫거나 Ctrl+C를 누르면 수집기가 종료됩니다.");
        Console.WriteLine("서비스·예약 작업 등록과 기존 데이터 변경은 별도 작업입니다.");
        OpenBrowser(shell, options, address);
        return true;
    }

    public static void ReportFailure(string[] args, Exception error, IDesktopShell shell)
    {
        var message = "시작 실패: " + error.Message;
        Console.Error.WriteLine(message);
        if (HasTrueFlag(args, "--desktop") && !HasTrueFlag(args, "--no-browser")) shell.ShowError(message);
    }

    private static bool HasTrueFlag(string[] args, string name)
    {
        // This is only error presentation, including malformed argument lists that
        // RuntimeOptions rejected before it could return a parsed suppression flag.
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i] == name && args[i + 1] == "true") return true;
        return false;
    }

    private static void OpenBrowser(IDesktopShell shell, RuntimeOptions options, Uri address)
    {
        if (options.NoBrowser) return;
        try { shell.OpenBrowser(address); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var message = $"수집기는 실행 중입니다. 기본 브라우저를 열지 못했습니다. 브라우저에서 다음 주소를 직접 여세요: {address}";
            Console.Error.WriteLine(message);
            shell.ShowError(message);
        }
    }

    private enum Existing { Absent, Match, Conflict }

    private static async Task<Existing> ProbeAsync(Uri address, StorageLocation location, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2), MaxResponseContentBufferSize = 64 * 1024 };
        try
        {
            using var response = await client.GetAsync(new Uri(address, "api/health"), ct);
            if (!response.IsSuccessStatusCode) return Existing.Conflict;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("product", out var product) && product.ValueKind == JsonValueKind.String && product.GetString() == Product &&
                root.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String && mode.GetString() == location.Mode.ToString().ToLowerInvariant() &&
                root.TryGetProperty("datasetId", out var dataset) && dataset.ValueKind == JsonValueKind.String && dataset.TryGetGuid(out var id) && id == location.DatasetId
                ? Existing.Match : Existing.Conflict;
        }
        catch (HttpRequestException error)
        {
            for (Exception? inner = error; inner is not null; inner = inner.InnerException)
                if (inner is SocketException socket && socket.SocketErrorCode == SocketError.ConnectionRefused) return Existing.Absent;
            return Existing.Conflict;
        }
        catch (JsonException) { return Existing.Conflict; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Existing.Conflict; }
    }

    private static InvalidOperationException PortConflict(int port)
        => new($"포트 {port}의 응답이 이 앱·모드·데이터셋과 일치하지 않거나 확인할 수 없습니다. 기존 프로그램을 임의로 종료하지 않았습니다. 기존 수집기와 포트 설정을 확인하세요.");
}

public sealed class WindowsDesktopShell : IDesktopShell
{
    public void OpenBrowser(Uri address) => Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });

    public void ShowError(string message)
    {
        if (OperatingSystem.IsWindows()) _ = MessageBox(IntPtr.Zero, message, "AirWave AP Monitor", 0x10);
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
