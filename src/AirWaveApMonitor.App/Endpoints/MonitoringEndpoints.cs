using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace AirWaveApMonitor.App.Endpoints;

public static class MonitoringEndpoints
{
    public static void MapMonitoring(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/dashboard", (HttpContext http, ICollectionStore store, IMonitoringReadStore reads,
            StorageLocation location, ProfileStore profiles, CancellationToken ct) => MonitoringHttp.Execute(http, async () =>
        {
            var query = await MonitoringHttp.DashboardQuery(http, location, profiles, reads, ct);
            return MonitoringHttp.Json(await store.GetDashboardAsync(query, ct));
        }, noFinalRun: !http.Request.Query.ContainsKey("runId")));

        routes.MapGet("/api/runs", (HttpContext http, IMonitoringReadStore reads, StorageLocation location,
            CancellationToken ct) => MonitoringHttp.Execute(http, async () =>
        {
            var query = new RunListQuery(location.DatasetId, MonitoringHttp.Long(http, "scopeVersion"),
                MonitoringHttp.EnumValue<CollectionKind>(http, "kind"), MonitoringHttp.Time(http, "fromUtc"),
                MonitoringHttp.Time(http, "toUtc"), MonitoringHttp.Int(http, "page", 1),
                MonitoringHttp.Int(http, "pageSize", 50, 500), MonitoringHttp.Long(http, "throughRunId"),
                MonitoringHttp.Boolean(http, "enrichmentOnly", false));
            return MonitoringHttp.Json(await reads.GetRunsAsync(query, ct));
        }));

        routes.MapGet("/api/runs/{runId:long}", (long runId, HttpContext http, ICollectionStore store,
            IMonitoringReadStore reads, StorageLocation location, ProfileStore profiles, CancellationToken ct) => MonitoringHttp.Execute(http, async () =>
        {
            var query = await MonitoringHttp.DashboardQuery(http, location, profiles, reads, ct, runId);
            return MonitoringHttp.Json(await store.GetDashboardAsync(query, ct));
        }));

        routes.MapGet("/api/aps/{apId:guid}", (Guid apId, HttpContext http, IMonitoringReadStore reads,
            StorageLocation location, CancellationToken ct) => MonitoringHttp.Execute(http, async () =>
        {
            var run = MonitoringHttp.RequiredLong(http, "runId");
            var scope = await MonitoringHttp.Scope(http, reads, location.DatasetId, run, ct);
            return MonitoringHttp.Json(await reads.GetApDetailAsync(new(location.DatasetId, scope, run, apId,
                MonitoringHttp.Int(http, "historyLimit", 50, 200)), ct));
        }));

        routes.MapGet("/api/incidents", (HttpContext http, IMonitoringReadStore reads, StorageLocation location,
            CancellationToken ct) => MonitoringHttp.Execute(http, async () =>
                MonitoringHttp.Json(await reads.GetIncidentsAsync(await MonitoringHttp.IncidentQuery(http, reads, location, ct), ct))));

        routes.MapGet("/api/incidents/{incidentId:guid}", (Guid incidentId, HttpContext http, IMonitoringReadStore reads,
            StorageLocation location, CancellationToken ct) => MonitoringHttp.Execute(http, async () =>
                MonitoringHttp.Json(await reads.GetIncidentAsync(await MonitoringHttp.IncidentQuery(http, reads, location, ct), incidentId, ct))));
    }
}

internal static class MonitoringHttp
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    internal static IResult Json<T>(T value) => Results.Json(value, JsonOptions);
    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    internal static async Task<IResult> Execute(HttpContext http, Func<Task<IResult>> action, bool noFinalRun = false)
    {
        http.Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (ArgumentException)
        { return Results.Json(new { code = "invalid_query", message = "조회 조건과 회차를 확인하세요." }, statusCode: 400); }
        catch (KeyNotFoundException)
        { return Results.Json(new { code = noFinalRun ? "no_final_run" : "not_found", message = "선택한 범위와 시점에 저장된 자료가 없습니다." }, statusCode: 404); }
        catch (Exception error) when (error is SqliteException or IOException or InvalidDataException or FormatException or OverflowException or InvalidOperationException)
        { return Results.Json(new { code = "storage_unavailable", message = "저장된 자료를 읽지 못했습니다. 수집기 저장 상태를 확인하세요." }, statusCode: 503); }
    }
    internal static async Task<DashboardQuery> DashboardQuery(HttpContext http, StorageLocation location,
        ProfileStore profiles, IMonitoringReadStore reads, CancellationToken ct, long? forcedRunId = null)
    {
        var supplied = Long(http, "runId");
        if (forcedRunId is <= 0 || (forcedRunId is not null && supplied is not null && supplied != forcedRunId))
            throw new ArgumentException("회차가 일치하지 않습니다.");
        var run = forcedRunId ?? supplied;
        var scope = run is { } id ? await Scope(http, reads, location.DatasetId, id, ct)
            : Long(http, "scopeVersion") ?? profiles.Current?.ScopeVersion ?? throw new KeyNotFoundException();
        return new(location.DatasetId, scope, run, Text(http, "search", 512),
            EnumValue<DashboardFilter>(http, "filter") ?? DashboardFilter.OpenIncidents,
            EnumValue<DashboardSort>(http, "sort") ?? DashboardSort.FirstDownAt,
            Boolean(http, "descending", true), Int(http, "page", 1), Int(http, "pageSize", 50, 500),
            EnumValue<OperationalStatus>(http, "managementStatus"), Boolean(http, "hidePlanned", false),
            Long(http, "minimumOpenElapsedSeconds"), Int(http, "repeatThreshold", 3, 720),
            Int(http, "priorityThreshold", 5, 720), IncludeStatusHistory: true);
    }
    internal static async Task<long> Scope(HttpContext http, IMonitoringReadStore reads, Guid dataset, long run, CancellationToken ct)
    {
        var requested = Long(http, "scopeVersion");
        var saved = await reads.GetRunAsync(dataset, run, ct);
        if (requested is not null && requested != saved.ScopeVersion) throw new KeyNotFoundException();
        if (saved.Kind != CollectionKind.ApStatus) throw new KeyNotFoundException();
        return saved.ScopeVersion;
    }
    internal static async Task<IncidentQuery> IncidentQuery(HttpContext http, IMonitoringReadStore reads,
        StorageLocation location, CancellationToken ct)
    {
        var run = RequiredLong(http, "asOfRunId"); var scope = await Scope(http, reads, location.DatasetId, run, ct);
        return new(location.DatasetId, scope, run, GuidValue(http, "apId"), EnumValue<IncidentStatus>(http, "status"),
            Text(http, "search", 512), Time(http, "fromUtc"), Time(http, "toUtc"), Int(http, "page", 1), Int(http, "pageSize", 50, 500),
            DeviceGenerationId: GuidValue(http, "deviceGenerationId"), MinimumOpenElapsedSeconds: Long(http, "minimumOpenElapsedSeconds"));
    }
    internal static string? Text(HttpContext http, string key, int maximum = 128)
    {
        if (!http.Request.Query.TryGetValue(key, out var values)) return null;
        if (values.Count != 1 || values[0] is not { } value || value.Length > maximum) throw new ArgumentException("조회 조건이 올바르지 않습니다.");
        return value;
    }
    internal static long? Long(HttpContext http, string key)
    {
        var value = Text(http, key);
        if (value is null) return null;
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0
            ? result : throw new ArgumentException("양의 회차/범위 값이 필요합니다.");
    }
    internal static long RequiredLong(HttpContext http, string key) => Long(http, key) ?? throw new ArgumentException("명시적 회차가 필요합니다.");
    internal static int Int(HttpContext http, string key, int fallback, int maximum = int.MaxValue)
    {
        var value = Long(http, key) ?? fallback;
        return value <= maximum ? (int)value : throw new ArgumentOutOfRangeException(key);
    }
    internal static T? EnumValue<T>(HttpContext http, string key) where T : struct, Enum
    {
        var value = Text(http, key);
        if (value is null) return null;
        value = value.Trim();
        return Enum.GetNames<T>().Any(name => name.Equals(value, StringComparison.OrdinalIgnoreCase)) &&
            Enum.TryParse<T>(value, true, out var parsed)
            ? parsed : throw new ArgumentException("지원하지 않는 조회 분류입니다.");
    }
    internal static bool Boolean(HttpContext http, string key, bool fallback)
    {
        var value = Text(http, key);
        return value is null ? fallback : bool.TryParse(value, out var parsed) ? parsed : throw new ArgumentException("참/거짓 값이 필요합니다.");
    }
    private static Guid? GuidValue(HttpContext http, string key)
    {
        var value = Text(http, key);
        if (value is null) return null;
        return Guid.TryParse(value, out var parsed) && parsed != Guid.Empty ? parsed : throw new ArgumentException("식별자를 확인하세요.");
    }
    internal static DateTimeOffset? Time(HttpContext http, string key)
    {
        var value = Text(http, key, 64);
        if (value is null) return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed : throw new ArgumentException("UTC 시각 범위를 확인하세요.");
    }
}
