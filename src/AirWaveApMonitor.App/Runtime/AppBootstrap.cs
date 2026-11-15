using System.Net;
using AirWaveApMonitor.App.Collection;
using AirWaveApMonitor.App.Maintenance;
using AirWaveApMonitor.App.Endpoints;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Settings;
using Microsoft.AspNetCore.Antiforgery;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Demo;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Security;
using AirWaveApMonitor.Infrastructure.Storage;

namespace AirWaveApMonitor.App.Runtime;

public static class AppBootstrap
{
    public static WebApplication Build(string[] args)
    {
        var options = RuntimeOptions.Parse(args);
        var location = StoragePaths.Open(options.Mode, options.DataDirectory);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Production"
        });
        // Runtime configuration comes only from the supported CLI. In particular, environment
        // URL/Kestrel overrides must never create another listener or broaden the loopback boundary.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Configure(new ConfigurationBuilder().Build());
            server.Listen(IPAddress.Loopback, options.Port);
            server.Limits.MaxRequestBodySize = 32 * 1024;
        });
        builder.Services.AddAirWaveWindowsService(options.ServiceName);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(location);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ICredentialStore>(_ => CredentialStoreSelection.Create(options.Mode,
            () => new DpapiCredentialStore(location.DataDirectory)));
        if (options.Mode == AppMode.Demo)
        {
            builder.Services.AddSingleton<IApSource, ScenarioApSource>();
            builder.Services.AddSingleton<DemoPrehistoryInitializer>();
        }

        builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = "AirWave-CSRF"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict; });
        builder.Services.AddSingleton<ProfileStore>();
        builder.Services.AddSingleton(_ => new SemaphoreSlim(1, 1));
        builder.Services.AddSingleton<SqliteCollectionStore>();
        builder.Services.AddSingleton<InstanceLock>();
        builder.Services.AddSingleton<ICollectionStore>(sp => sp.GetRequiredService<SqliteCollectionStore>());
        builder.Services.AddSingleton<IMonitoringReadStore>(sp => sp.GetRequiredService<SqliteCollectionStore>());
        builder.Services.AddSingleton<ISourceCheckpointStore>(sp => sp.GetRequiredService<SqliteCollectionStore>());
        builder.Services.AddHostedService<StorageInitialization>();
        builder.Services.AddSingleton<IAuthenticationStateStore>(sp => options.Mode == AppMode.Live
            ? new SqliteAuthenticationStateStore(location, sp.GetRequiredService<SemaphoreSlim>())
            : new MemoryAuthenticationStateStore());
        builder.Services.AddSingleton(sp => new AirWaveSession(authenticationStates: sp.GetRequiredService<IAuthenticationStateStore>()));
        builder.Services.AddSingleton<AirWaveClient>();
        builder.Services.AddSingleton<CredentialRegistration>();
        builder.Services.AddSingleton(sp => new ConnectionDiagnostics(options.Mode, sp.GetRequiredService<AirWaveClient>(), sp.GetRequiredService<TimeProvider>()));
        if (options.Mode == AppMode.Live)
            builder.Services.AddSingleton<IApSource>(sp => sp.GetRequiredService<AirWaveClient>());
        builder.Services.AddSingleton<CollectorHealth>();
        builder.Services.AddSingleton<CollectionCoordinator>();
        builder.Services.AddSingleton<ICollectorControl>(sp => sp.GetRequiredService<CollectionCoordinator>());
        builder.Services.AddSingleton(sp => new BackgroundFailurePolicy(
            sp.GetRequiredService<CollectionCoordinator>().StopAsync,
            Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService(),
            code => Environment.ExitCode = code, Environment.Exit));
        builder.Services.AddHostedService<CollectionWorker>();
        builder.Services.AddSingleton<BackupService>();
        builder.Services.AddSingleton<RetentionService>();
        builder.Services.AddSingleton<MaintenanceHealth>();
        builder.Services.AddSingleton<MaintenanceCoordinator>();
        builder.Services.AddHostedService<MaintenanceWorker>();
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
            var mutation = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method);
            var origin = context.Request.Headers.Origin.ToString();
            if (mutation && origin.Length == 0)
            {
                context.Response.StatusCode = 403;
                return;
            }
            if (origin.Length > 0 && !string.Equals(origin, $"http://{host.Value}", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (mutation)
            {
                try
                {
                    await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
                }
                catch (AntiforgeryValidationException) { context.Response.StatusCode = 400; return; }
            }
            await next(context);
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapGet("/api/health", (ProfileStore profiles, CollectorHealth health, MaintenanceHealth maintenanceHealth) =>
        {
            var collector = health.GetSnapshot();
            return Results.Json(new
            {
                mode = options.Mode.ToString().ToLowerInvariant(),
                status = collector.Paused ? "paused" : collector.BlockedReason ?? collector.Phase.ToString().ToLowerInvariant(),
                collection = "automatic",
                persistence = "sqlite-ready",
                fixtureApCount = options.Mode == AppMode.Demo ? (int?)ScenarioApSource.DeviceCount : null,
                liveConfigured = options.Mode == AppMode.Live && collector.Configured,
                serverConfigured = profiles.Current is not null,
                collector,
                maintenance = maintenanceHealth.GetSnapshot()
            }, CollectorEndpoints.Json);
        });
        app.MapGet("/api/capabilities/windows", () => new
        {
            supported = OperatingSystem.IsWindows(),
            implemented = OperatingSystem.IsWindows(),
            status = OperatingSystem.IsWindows() ? "dpapi-available" : "unsupported-platform",
            message = OperatingSystem.IsWindows() ? "현재 실행 계정의 DPAPI 사용 가능. 서비스·예약 작업 등록은 패키지의 Windows 운영 스크립트를 사용하세요." : "Windows DPAPI·서비스·예약 작업은 이 OS에서 지원하지 않습니다."
        });
        app.MapSettings();
        app.MapCollector();
        app.MapMonitoring();
        app.MapExport();
        return app;
    }

}
