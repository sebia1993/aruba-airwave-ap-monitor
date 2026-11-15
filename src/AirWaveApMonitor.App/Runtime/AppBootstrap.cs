using System.Net;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Demo;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Security;

namespace AirWaveApMonitor.App.Runtime;

public static class AppBootstrap
{
    public static WebApplication Build(string[] args)
    {
        var options = RuntimeOptions.Parse(args);
        var location = StoragePaths.Open(options.Mode, options.DataDirectory);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [], ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Production"
        });
        // Runtime configuration comes only from the supported CLI. In particular, environment
        // URL/Kestrel overrides must never create another listener or broaden the loopback boundary.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Configure(new ConfigurationBuilder().Build());
            server.Listen(IPAddress.Loopback, options.Port);
        });
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(location);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ICredentialStore>(_ => CredentialStoreSelection.Create(options.Mode,
            () => new UnconfiguredLiveCredentialStore()));
        if (options.Mode == AppMode.Demo) builder.Services.AddSingleton<IApSource, ScenarioApSource>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var host = context.Request.Host;
            if ((host.Host != "127.0.0.1" && !host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) ||
                (host.Port ?? 80) != options.Port)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && !string.Equals(origin, $"http://{host.Value}", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await next(context);
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapGet("/api/health", () => new
        {
            mode = options.Mode.ToString().ToLowerInvariant(), status = "bootstrap",
            collection = "not-implemented", persistence = "not-implemented",
            fixtureApCount = options.Mode == AppMode.Demo ? (int?)ScenarioApSource.DeviceCount : null,
            liveConfigured = false
        });
        app.MapGet("/api/dashboard", () => Results.Problem(statusCode: 503,
            title: "저장된 AP 현황이 아직 없습니다.", detail: "현재 단계에는 수집·상태 반영·SQLite 저장이 구현되지 않았습니다."));
        app.MapGet("/api/capabilities/windows", () => new
        {
            supported = OperatingSystem.IsWindows(), implemented = false,
            status = OperatingSystem.IsWindows() ? "not-implemented" : "unsupported-platform",
            message = OperatingSystem.IsWindows() ? "Windows 자격 증명·자동 실행은 후속 구현 단계입니다." : "Windows DPAPI·서비스·예약 작업은 이 OS에서 지원하지 않습니다."
        });
        return app;
    }

    private sealed class UnconfiguredLiveCredentialStore : ICredentialStore
    {
        public Task SaveAsync(Guid datasetId, Uri origin, AirWaveCredentials credentials, CancellationToken ct)
            => throw new NotSupportedException(OperatingSystem.IsWindows()
                ? "실제 자격 증명 저장은 아직 구현되지 않았습니다."
                : "실제 자격 증명은 Windows DPAPI가 필요하며 이 OS에서는 지원하지 않습니다.");
        public Task<AirWaveCredentials?> ReadAsync(Guid datasetId, Uri origin, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<AirWaveCredentials?>(null);
        }
        public Task ClearAsync(Guid datasetId, Uri origin, CancellationToken ct)
            => throw new NotSupportedException("실제 자격 증명 저장소는 아직 구현되지 않았습니다.");
    }
}
