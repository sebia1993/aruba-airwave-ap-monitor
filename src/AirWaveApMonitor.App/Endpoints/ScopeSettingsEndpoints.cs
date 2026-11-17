using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Infrastructure.Storage;
using Microsoft.AspNetCore.Http.Features;
using System.Text.Json;

namespace AirWaveApMonitor.App.Endpoints;

public static class ScopeSettingsEndpoints
{
    private const long MaximumScopeBytes = 2 * 1024 * 1024;
    public static void MapScopeSettings(this WebApplication app)
    {
        app.MapGet("/api/scope", async (HttpContext http, ProfileStore profiles, SqliteCollectionStore store) =>
            await ScopeGuard(profiles, http, async () =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return MonitoringHttp.Json(await store.GetScopeSettingsAsync(profiles.Current ?? throw new ArgumentException(), http.RequestAborted));
            }, waitForGate: true));
        app.MapPost("/api/scope", async (HttpContext http, ProfileStore profiles, SqliteCollectionStore store) =>
            await ScopeGuard(profiles, http, async () =>
            {
                if (http.Request.ContentLength > MaximumScopeBytes)
                    throw new BadHttpRequestException("body-too-large", 413);
                var size = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (size is { IsReadOnly: false }) size.MaxRequestBodySize = MaximumScopeBytes;
                var request = await http.Request.ReadFromJsonAsync<ScopeInput>(http.RequestAborted) ?? throw new ArgumentException();
                if (!Enum.GetNames<MonitoringScopeMode>().Contains(request.Mode, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException("감시 범위 모드를 확인하세요.");
                var definition = new MonitoringScopeDefinition(Enum.Parse<MonitoringScopeMode>(request.Mode!, true),
                    (request.SourceApIds ?? []).ToImmutableArray());
                var view = await store.GetScopeSettingsAsync(profiles.Current ?? throw new ArgumentException(), http.RequestAborted);
                await profiles.UpdateScopeAsync(definition, view.KnownTargets.Select(target => target.SourceApId).ToImmutableArray(),
                    request.Reason ?? "", request.ExpectedConfigurationVersion, http.RequestAborted);
                return MonitoringHttp.Json(await store.GetScopeSettingsAsync(profiles.Current!, http.RequestAborted));
            }));
        app.MapPost("/api/scope/confirm", async (HttpContext http, ProfileStore profiles, SqliteCollectionStore store) =>
            await ScopeGuard(profiles, http, async () =>
            {
                var request = await http.Request.ReadFromJsonAsync<BaselineInput>(http.RequestAborted) ?? throw new ArgumentException();
                var view = await store.GetScopeSettingsAsync(profiles.Current ?? throw new ArgumentException(), http.RequestAborted);
                if (view.ComparisonRun is not { } compared || compared.RunId != request.RunId)
                    throw new ProfileConflictException("대조 회차가 바뀌었습니다. 최신 회차의 대수와 목록을 다시 확인하세요.");
                await profiles.ConfirmBaselineAsync(compared, request.ComparedTargetCount, request.Reason ?? "",
                    request.ExpectedConfigurationVersion, http.RequestAborted);
                return MonitoringHttp.Json(await store.GetScopeSettingsAsync(profiles.Current!, http.RequestAborted));
            }));
    }
    private static Task<IResult> ScopeGuard(ProfileStore profiles, HttpContext http, Func<Task<IResult>> action, bool waitForGate = false)
        => SettingsEndpoints.Guard(profiles, http, async () =>
        {
            try { return await action(); }
            catch (BadHttpRequestException error) when (error.StatusCode == 413)
            { return Results.Json(new { message = "감시 범위 요청은 2 MiB 이하로 지정하세요." }, statusCode: 413); }
            catch (Exception error) when (error is ArgumentException or JsonException)
            { return Results.BadRequest(new { message = "범위 모드·알려진 AP ID·대조 대수와 사유를 확인하세요. 중복 ID와 빈 선택 범위는 저장할 수 없습니다." }); }
        }, waitForGate);
    public sealed record ScopeInput(long ExpectedConfigurationVersion, string? Mode, string[]? SourceApIds, string? Reason);
    public sealed record BaselineInput(long ExpectedConfigurationVersion, long RunId, int ComparedTargetCount, string? Reason);
}
