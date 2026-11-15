using System.Text.Json;
using System.Text.Json.Serialization;
using AirWaveApMonitor.App.Collection;
using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.App.Endpoints;

public static class CollectorEndpoints
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public static void MapCollector(this WebApplication app)
    {
        app.MapGet("/api/collector/health", (CollectorHealth health) => Results.Json(health.GetSnapshot(), Json));
        app.MapPost("/api/collector/requests", async (HttpContext context, ICollectorControl collector) =>
        {
            try
            {
                var input = await context.Request.ReadFromJsonAsync<CollectionRequest>(Json, context.RequestAborted)
                    ?? throw new ArgumentException();
                if (input.Trigger != RunTrigger.Manual) throw new ArgumentException();
                var receipt = await collector.RequestAsync(input.Kind, RunTrigger.Manual, context.RequestAborted);
                var status = receipt.Status switch
                {
                    RunRequestStatus.Accepted or RunRequestStatus.Queued => 202,
                    RunRequestStatus.Unsupported => 501,
                    RunRequestStatus.StorageBlocked or RunRequestStatus.Stopping => 503,
                    _ => 409
                };
                return Results.Json(receipt, Json, statusCode: status);
            }
            catch (Exception error) when (error is JsonException or ArgumentException)
            {
                return Results.BadRequest(new { message = "유효한 수집 종류와 Manual 시작 사유가 필요합니다." });
            }
        });
        app.MapPost("/api/collector/pause", async (HttpContext context, ICollectorControl collector, CollectorHealth health) =>
        {
            try
            {
                var input = await context.Request.ReadFromJsonAsync<PauseRequest>(Json, context.RequestAborted)
                    ?? throw new ArgumentException();
                await collector.SetPausedAsync(input.Paused, context.RequestAborted);
                return Results.Json(health.GetSnapshot(), Json);
            }
            catch (Exception error) when (error is JsonException or ArgumentException)
            {
                return Results.BadRequest(new { message = "중지 상태를 확인하세요." });
            }
        });
    }
}
