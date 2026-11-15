using System.Net;
using AirWaveApMonitor.App.Endpoints;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Settings;
using Microsoft.AspNetCore.Antiforgery;
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
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(location);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ICredentialStore>(_ => CredentialStoreSelection.Create(options.Mode,
            () => new DpapiCredentialStore(location.DataDirectory)));
        if (options.Mode == AppMode.Demo)
            builder.Services.AddSingleton<IApSource, ScenarioApSource>();

        builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = "AirWave-CSRF"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict; });
        builder.Services.AddSingleton<ProfileStore>();
        builder.Services.AddSingleton<AirWaveSession>();
        builder.Services.AddSingleton<AirWaveClient>();
        builder.Services.AddSingleton<CredentialRegistration>();
        builder.Services.AddSingleton(sp => new ConnectionDiagnostics(options.Mode, sp.GetRequiredService<AirWaveClient>(), sp.GetRequiredService<TimeProvider>()));
        if (options.Mode == AppMode.Live)
            builder.Services.AddSingleton<IApSource>(sp => sp.GetRequiredService<AirWaveClient>());
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
        app.MapGet("/api/health", async (ProfileStore profiles, ICredentialStore credentials, CancellationToken ct) =>
        {
            var configured = false;
            if (options.Mode == AppMode.Live && OperatingSystem.IsWindows() && profiles.Current is { } profile)
            {
                try
                {
                    configured = await credentials.ReadAsync(profile.DatasetId, profile.Origin, ct) is not null;
                }
                catch (CredentialUnavailableException) { /* Status remains unconfigured; settings explain re-registration. */ }
            }
            return new
            {
                mode = options.Mode.ToString().ToLowerInvariant(),
                status = "connection-diagnostics",
                collection = "not-implemented",
                persistence = "not-implemented",
                fixtureApCount = options.Mode == AppMode.Demo ? (int?)ScenarioApSource.DeviceCount : null,
                liveConfigured = configured,
                serverConfigured = profiles.Current is not null
            };
        });
        app.MapGet("/api/dashboard", () => Results.Problem(statusCode: 503,
            title: "저장된 AP 현황이 아직 없습니다.", detail: "초기 연결·단일 읽기 진단은 제공하며 자동 수집·상태 반영·이력 SQLite 저장은 아직 구현되지 않았습니다."));
        app.MapGet("/api/capabilities/windows", () => new
        {
            supported = OperatingSystem.IsWindows(),
            implemented = OperatingSystem.IsWindows(),
            status = OperatingSystem.IsWindows() ? "dpapi-available" : "unsupported-platform",
            message = OperatingSystem.IsWindows() ? "현재 실행 계정의 DPAPI 사용 가능. 서비스·예약 작업은 아직 구현되지 않았습니다." : "Windows DPAPI·서비스·예약 작업은 이 OS에서 지원하지 않습니다."
        });
        app.MapSettings();
        return app;
    }

}
