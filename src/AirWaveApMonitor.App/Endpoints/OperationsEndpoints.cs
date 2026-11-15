using System.Text.Json;
using System.Text.Json.Serialization;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Settings;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;

namespace AirWaveApMonitor.App.Endpoints;

public static class OperationsEndpoints
{
    public const int MaximumImportBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    public static void MapOperations(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/operations/version", (Func<HttpContext, Task<IResult>>)(http => Read(http,
            async () => await Store(http).GetVersionAsync(http.RequestAborted))));
        routes.MapGet("/api/operations/aps/{apId:guid}/registration", (Guid apId, HttpContext http) => Read(http,
            async () => await Store(http).GetRegistrationAsync(apId, http.RequestAborted)));
        routes.MapGet("/api/operations/points", (Func<HttpContext, Task<IResult>>)(http => Read(http, async () =>
            await Store(http).GetPointsAsync(new(MonitoringHttp.Text(http, "search", 512),
                MonitoringHttp.Int(http, "page", 1), MonitoringHttp.Int(http, "pageSize", 50, 500)), http.RequestAborted))));
        routes.MapGet("/api/operations/points/{pointId:guid}", (Guid pointId, HttpContext http) => Read(http,
            async () => await Store(http).GetPointAsync(pointId, http.RequestAborted)));
        routes.MapGet("/api/operations/work/{workId:guid}", (Guid workId, HttpContext http) => Read(http,
            async () => await Store(http).GetWorkAsync(workId, http.RequestAborted)));
        routes.MapGet("/api/operations/timeline", (Func<HttpContext, Task<IResult>>)(http => Read(http, async () =>
            await Store(http).GetTimelineAsync(new(GuidQuery(http, "apId"), GuidQuery(http, "deviceGenerationId"),
                GuidQuery(http, "installationPointId"), MonitoringHttp.EnumValue<OperationHistoryKind>(http, "kind"),
                MonitoringHttp.Time(http, "fromUtc"), MonitoringHttp.Time(http, "toUtc"),
                MonitoringHttp.Long(http, "throughEventId"), MonitoringHttp.Int(http, "page", 1),
                MonitoringHttp.Int(http, "pageSize", 50, 500)), http.RequestAborted))));
        routes.MapPost("/api/operations/inventory", (HttpContext http, ProfileStore profiles) => Write(http, profiles,
            async _ => await Store(http).SaveInventoryAsync(await Body<InventoryEdit>(http), http.RequestAborted)));
        routes.MapPost("/api/operations/tracking", (HttpContext http, ProfileStore profiles) => Write(http, profiles,
            async _ => await Store(http).SaveTrackingPolicyAsync(await Body<TrackingPolicyEdit>(http), http.RequestAborted)));
        routes.MapPost("/api/operations/points", (HttpContext http, ProfileStore profiles) => Write(http, profiles,
            async _ => await Store(http).SavePointAsync(await Body<PointEdit>(http), http.RequestAborted)));
        routes.MapPost("/api/operations/placement", (HttpContext http, ProfileStore profiles) => Write(http, profiles,
            async _ => await Store(http).MovePlacementAsync(await Body<PlacementEdit>(http), http.RequestAborted)));
        routes.MapPost("/api/operations/work", (HttpContext http, ProfileStore profiles) => Write(http, profiles,
            async _ => await Store(http).SaveWorkAsync(await Body<WorkEdit>(http), http.RequestAborted)));
        routes.MapPost("/api/operations/replacement/confirm", (HttpContext http, ProfileStore profiles) => Write(http, profiles,
            async _ => await Store(http).ConfirmReplacementAsync(await Body<ReplacementConfirmationEdit>(http), http.RequestAborted)));
        routes.MapPost("/api/operations/import/preview", (HttpContext http, ProfileStore profiles) => Write(http, profiles,
            async profile => await Store(http).PreviewImportAsync(await CsvBody(http), profile, http.RequestAborted)));
        routes.MapPost("/api/operations/import/apply", (HttpContext http, ProfileStore profiles) => Write(http, profiles,
            async profile => await Store(http).ApplyImportAsync(await Body<ImportApply>(http), profile, http.RequestAborted)));
    }

    private static IOperationsStore Store(HttpContext http) => http.RequestServices.GetRequiredService<IOperationsStore>();
    private static async Task<T> Body<T>(HttpContext http)
    {
        if (!http.Request.HasJsonContentType()) throw new ArgumentException();
        return await http.Request.ReadFromJsonAsync<T>(Json, http.RequestAborted) ?? throw new ArgumentException();
    }
    private static Guid? GuidQuery(HttpContext http, string key)
    {
        var value = MonitoringHttp.Text(http, key);
        return value is null ? null : Guid.TryParse(value, out var id) && id != Guid.Empty ? id : throw new ArgumentException();
    }
    private static Task<IResult> Read<T>(HttpContext http, Func<Task<T>> action) => Execute(http, async () => Results.Json(await action(), Json));
    private static Task<IResult> Write<T>(HttpContext http, ProfileStore profiles, Func<ProfileSnapshot, Task<T>> action)
        => Execute(http, async () =>
        {
            if (!await profiles.OperationGate.WaitAsync(0, http.RequestAborted))
                return Error(409, "collector_busy", "수집 또는 설정 변경 중입니다. 완료된 뒤 다시 시도하세요.");
            try
            {
                var profile = profiles.Current ?? throw new OperationsConflictException("profile-required");
                return Results.Json(await action(profile), Json);
            }
            finally { profiles.OperationGate.Release(); }
        });
    private static async Task<byte[]> CsvBody(HttpContext http)
    {
        var mediaType = http.Request.ContentType?.Split(';', 2)[0].Trim();
        if (mediaType is not ("text/csv" or "application/octet-stream")) throw new ArgumentException();
        if (http.Request.ContentLength > MaximumImportBytes) throw new BadHttpRequestException("body-too-large", 413);
        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MaximumImportBytes;
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await http.Request.Body.ReadAsync(buffer, http.RequestAborted);
            if (read == 0) break;
            if (data.Length + read > MaximumImportBytes) throw new BadHttpRequestException("body-too-large", 413);
            await data.WriteAsync(buffer.AsMemory(0, read), http.RequestAborted);
        }
        return data.ToArray();
    }
    private static async Task<IResult> Execute(HttpContext http, Func<Task<IResult>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (OperationsConflictException) { return Error(409, "operations_conflict", "대상·등록 버전 또는 미리보기가 변경됐습니다. 현재 정보를 다시 읽고 검토하세요."); }
        catch (Exception error) when (error is ArgumentException or JsonException or NotSupportedException)
        { return Error(400, "invalid_operation", "입력 형식·식별자·필수 항목을 확인하세요. CSV 미리보기에는 CSV 파일 원문을 보내세요."); }
        catch (KeyNotFoundException) { return Error(404, "not_found", "선택한 운영 기록이나 대상이 없습니다."); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return Error(413, "body_too_large", "요청 크기 제한을 초과했습니다. CSV는 4 MiB까지 지원합니다."); }
        catch (Exception error) when (error is SqliteException or IOException or InvalidDataException or InvalidOperationException or FormatException or OverflowException)
        { return Error(503, "storage_unavailable", "운영 기록을 저장하거나 읽지 못했습니다. 수집기 저장 상태를 확인하고 같은 요청을 다시 확인하세요."); }
    }
    private static IResult Error(int code, string key, string message) => Results.Json(new { code = key, message }, statusCode: code);
}
