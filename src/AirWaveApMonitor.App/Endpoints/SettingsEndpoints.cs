using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Security;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.App.Runtime;
using Microsoft.AspNetCore.Antiforgery;
namespace AirWaveApMonitor.App.Endpoints;

public static class SettingsEndpoints
{
    public static void MapSettings(this WebApplication app)
    {
        app.MapGet("/api/antiforgery", (HttpContext context, IAntiforgery antiforgery) => { context.Response.Headers.CacheControl = "no-store"; return Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }); });
        app.MapGet("/api/settings", async (HttpContext context, ProfileStore profiles, ICredentialStore credentials, RuntimeOptions runtime, AirWaveSession session) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await profiles.OperationGate.WaitAsync(context.RequestAborted);
            try
            {
                return Results.Ok(await View(profiles, credentials, runtime, session, context.RequestAborted));
            }
            catch (AuthenticationStateStoreException) { return Results.Problem(statusCode: 503, title: "인증 중단 상태를 확인하지 못했습니다. 데이터 폴더 권한과 저장 공간을 확인하세요."); }
            finally { profiles.OperationGate.Release(); }
        });
        app.MapPost("/api/settings", async (HttpContext context, ProfileStore profiles, ICredentialStore credentials, RuntimeOptions runtime, AirWaveSession session) => await Guard(profiles, context, async () =>
        {
            var request = await context.Request.ReadFromJsonAsync<SettingsInput>(context.RequestAborted) ?? throw new ArgumentException();
            if (!Enum.TryParse<ProxyMode>(request.ProxyMode, true, out var mode))
                throw new ArgumentException();
            var origin = new Uri(request.Origin, UriKind.Absolute);
            var proxy = new ProxySettings(mode, string.IsNullOrWhiteSpace(request.ProxyAddress) ? null : new Uri(request.ProxyAddress, UriKind.Absolute));
            await profiles.UpdateAsync(origin, request.ApIntervalMinutes, proxy, request.ExpectedConfigurationVersion, context.RequestAborted);
            return Results.Ok(await View(profiles, credentials, runtime, session, context.RequestAborted));
        }));
        app.MapPost("/api/credentials", async (HttpContext context, ProfileStore profiles, ICredentialStore credentials, RuntimeOptions runtime, AirWaveSession session, CredentialRegistration registration) => await Guard(profiles, context, async () =>
        {
            if (runtime.Mode == AppMode.Demo)
                return Results.BadRequest(new
                {
                    message = "Demo 모드에서는 실제 계정을 받지 않습니다. 가상 메모리 자격 증명만 사용합니다."
                });
            // Deliberately avoid a printable password-bearing record or response DTO.
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var username = body.RootElement.GetProperty("username").GetString();
            var password = body.RootElement.GetProperty("password").GetString();
            if (username is null || password is null)
                throw new ArgumentException();
            await registration.RegisterAsync(new(username, password), context.RequestAborted);
            return Results.Ok(await View(profiles, credentials, runtime, session, context.RequestAborted));
        }));
        app.MapPost("/api/authentication/retry", async (HttpContext context, ProfileStore profiles, AirWaveSession session) => await Guard(profiles, context, async () =>
        {
            var profile = profiles.Current ?? throw new ArgumentException();
            await session.RetryAuthenticationAsync(profile, context.RequestAborted);
            return Results.Ok(new { authenticationBlocked = false, message = "명시적 재시도 허용. 연결 진단을 실행하세요." });
        }));
        app.MapPost("/api/diagnostics", async (HttpContext context, ProfileStore profiles, ConnectionDiagnostics diagnostics) => await Guard(profiles, context, async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var profile = profiles.Current ?? throw new ArgumentException();
            return Results.Ok(await diagnostics.RunAsync(profile, context.RequestAborted));
        }));
    }
    private static async Task<IResult> Guard(ProfileStore profiles, HttpContext context, Func<Task<IResult>> action)
    {
        if (!await profiles.OperationGate.WaitAsync(0, context.RequestAborted))
            return Results.Conflict(new
            {
                message = "진단 또는 설정 변경이 진행 중입니다. 완료 후 다시 시도하세요."
            });
        try
        {
            return await action();
        }
        catch (ProfileConflictException error) { return Results.Conflict(new { message = error.Message }); }
        catch (PlatformNotSupportedException) { return Results.Problem(statusCode: 501, title: SafeMessages.For(CollectionErrorCategory.Unsupported)); }
        catch (AuthenticationStateStoreException) { return Results.Problem(statusCode: 503, title: "인증 중단 상태를 저장하거나 확인하지 못했습니다. 데이터 폴더 권한과 저장 공간을 확인하세요."); }
        catch (CredentialUnavailableException) { return Results.Problem(statusCode: 409, title: SafeMessages.For(CollectionErrorCategory.CredentialUnavailable)); }
        catch (Exception error) when (error is ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return Results.BadRequest(new { message = "입력값 또는 서버 설정을 확인하세요. HTTPS 출처와 고정 프록시 서버 주소만 허용하며 PAC URL은 지원하지 않습니다." }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { return Results.Problem(statusCode: 503, title: "실행 계정의 설정·자격 증명 저장에 실패했습니다. 재등록과 데이터 폴더 권한 확인이 필요합니다."); }
        finally { profiles.OperationGate.Release(); }
    }
    private static async Task<object> View(ProfileStore profiles, ICredentialStore credentials, RuntimeOptions runtime, AirWaveSession session, CancellationToken ct)
    {
        if (profiles.Current is { } current)
            await session.RestoreAuthenticationStateAsync(current, ct);
        var status = runtime.Mode == AppMode.Demo ? "synthetic-memory" : !OperatingSystem.IsWindows() ? "unsupported-platform" : "missing";
        if (runtime.Mode == AppMode.Live && OperatingSystem.IsWindows() && profiles.Current is { } profile)
        {
            try
            {
                status = await credentials.ReadAsync(profile.DatasetId, profile.Origin, ct) is null ? "missing" : "registered";
            }
            catch (CredentialUnavailableException) { status = "reregistration-required"; }
        }
        if (session.CredentialRegistrationPending)
            status = "reregistration-required";
        return new
        {
            mode = runtime.Mode.ToString().ToLowerInvariant(),
            profile = profiles.Current,
            executionAccount = ExecutionIdentity.Current,
            credentialStatus = status,
            authenticationBlocked = session.AuthenticationBlocked,
            authenticationStop = session.AuthenticationStop,
            credentialRegistrationPending = session.CredentialRegistrationPending,
            persistence = "sqlite-ready",
            diagnosticScope = "connection-and-single-read-only"
        };
    }
    public sealed record SettingsInput(string Origin, int ApIntervalMinutes, string ProxyMode, string? ProxyAddress, long ExpectedConfigurationVersion);
}
