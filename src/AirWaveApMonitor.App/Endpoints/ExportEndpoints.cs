using System.Globalization;
using System.Text;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Settings;

namespace AirWaveApMonitor.App.Endpoints;

public static class ExportEndpoints
{
    private static readonly string[] Headers = ["데이터 모드", "데이터셋", "범위 버전", "수집 회차", "수집 시작 UTC", "확정 UTC",
        "회차 수명주기", "회차 결과", "관측 완전성", "기준 목록 상태", "원천 AP ID", "내부 AP ID", "장비 세대", "감시 구간",
        "AP 이름", "AP IP", "시리얼", "유선 MAC", "모델", "펌웨어", "사업장", "건물", "층", "구역", "상세 위치", "AirWave 위치",
        "폴더", "그룹", "컨트롤러 ID", "컨트롤러 이름", "컨트롤러 IP", "원천 SW ID", "포트 인덱스", "포트명", "AP 인터페이스",
        "이번 상태", "마지막 유효 상태", "마지막 유효 확인 UTC", "첫 Down UTC", "열린 사건 ID", "관측 분류", "미확인 사유",
        "시간 확인 필요", "수동 관리 상태", "수동 관리 사유", "원천 유지보수 모드", "필드별 근거 JSON", "미검증 원문 JSON", "등록 SW 이름", "등록 SW IP", "자산 번호", "대장 메모",
        "자동 정보 JSON", "수동 등록 JSON", "대장 버전", "대장 출처", "대장 기록 UTC", "수동 우선 필드 JSON", "관측 당시 설치 지점 JSON", "RecordType", "QueryConditions"];

    public static void MapExport(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/export.csv", (HttpContext http, ICollectionStore store, IMonitoringReadStore reads,
            StorageLocation location, ProfileStore profiles, CancellationToken ct) => MonitoringHttp.Execute(http, async () =>
        {
            var runId = MonitoringHttp.RequiredLong(http, "runId");
            var query = await MonitoringHttp.DashboardQuery(http, location, profiles, reads, ct, runId);
            query = query with { Page = 1, PageSize = 500 };
            var snapshot = await store.GetDashboardAsync(query, ct);
            var builder = new StringBuilder(); Append(builder, Headers);
            Append(builder, ContextValues(location.Mode, snapshot, query));
            var written = 0;
            for (var page = 1; ; page++)
            {
                var current = page == 1 ? snapshot : await store.GetDashboardAsync(query with { Page = page }, ct);
                if (current.RunId != runId || current.ScopeVersion != snapshot.ScopeVersion || current.TotalRows != snapshot.TotalRows)
                    throw new InvalidDataException("내보내기 회차가 변경되었습니다.");
                foreach (var row in current.Rows)
                {
                    ct.ThrowIfCancellationRequested(); Append(builder, Values(location.Mode, snapshot, row)); written++;
                }
                if (written >= snapshot.TotalRows) break;
                if (current.Rows.Length == 0) throw new InvalidDataException("내보내기 목록이 불완전합니다.");
            }
            if (written != snapshot.TotalRows) throw new InvalidDataException("내보내기 목록 수가 일치하지 않습니다.");
            http.Response.Headers["X-AirWave-Run-Id"] = runId.ToString(CultureInfo.InvariantCulture);
            http.Response.Headers["X-AirWave-Scope-Version"] = snapshot.ScopeVersion.ToString(CultureInfo.InvariantCulture);
            http.Response.Headers["X-AirWave-Row-Count"] = written.ToString(CultureInfo.InvariantCulture);
            http.Response.Headers["X-AirWave-Mode"] = location.Mode.ToString();
            http.Response.Headers["X-AirWave-Observation-Time"] = Time(snapshot.FinalizedAtUtc);
            var body = Encoding.UTF8.GetBytes(builder.ToString());
            byte[] bytes = [0xEF, 0xBB, 0xBF, .. body];
            return Results.File(bytes, "text/csv; charset=utf-8", $"airwave-{location.Mode.ToString().ToLowerInvariant()}-run-{runId}.csv");
        }));
    }
    private static IEnumerable<string?> CommonValues(AppMode mode, DashboardSnapshot snapshot) =>
    [
        mode.ToString(), snapshot.DatasetId.ToString("D"), snapshot.ScopeVersion.ToString(CultureInfo.InvariantCulture),
        snapshot.RunId.ToString(CultureInfo.InvariantCulture), Time(snapshot.StartedAtUtc), Time(snapshot.FinalizedAtUtc),
        snapshot.Lifecycle.ToString(), snapshot.Outcome?.ToString(), snapshot.Completeness.ToString(), snapshot.Summary.BaselineStatus.ToString()
    ];
    private static string?[] ContextValues(AppMode mode, DashboardSnapshot snapshot, DashboardQuery query)
    {
        var values = new string?[Headers.Length];
        CommonValues(mode, snapshot).ToArray().CopyTo(values, 0);
        values[^2] = "QueryContext";
        values[^1] = MonitoringHttp.Serialize(new
        {
            snapshot.DatasetId, snapshot.ScopeVersion, snapshot.RunId, snapshot.StartedAtUtc, snapshot.FinalizedAtUtc,
            exportExtent = "AllMatchingRows", totalApRows = snapshot.TotalRows,
            query.Filter, query.Search, query.Sort, query.Descending, query.ManagementStatus, query.HidePlanned,
            query.MinimumOpenElapsedSeconds
        });
        return values;
    }
    private static IEnumerable<string?> Values(AppMode mode, DashboardSnapshot snapshot, ApSnapshotRow row) =>
    [
        .. CommonValues(mode, snapshot),
        row.SourceApId, row.ApId.ToString("D"), row.DeviceGenerationId.ToString("D"), row.MonitoringIntervalId.ToString("D"),
        Display(row, InventoryField.Name, row.Device.Name), Display(row, InventoryField.IpAddress, row.Device.IpAddress), row.Identity.SerialNumber, row.Identity.WiredMac, Display(row, InventoryField.Model, row.Device.Model), Display(row, InventoryField.Firmware, row.Device.Firmware),
        Display(row, InventoryField.Site, row.Location.Site), Display(row, InventoryField.Building, row.Location.Building), Display(row, InventoryField.Floor, row.Location.Floor), Display(row, InventoryField.Zone, row.Location.Zone), Display(row, InventoryField.Detail, row.Location.Detail), Display(row, InventoryField.RawLocation, row.Location.AirWaveRawLocation),
        Display(row, InventoryField.Folder, row.Location.Folder), Display(row, InventoryField.Group, row.Location.Group), Display(row, InventoryField.ControllerId, row.Controller.SourceId), Display(row, InventoryField.ControllerName, row.Controller.Name), Display(row, InventoryField.ControllerIp, row.Controller.IpAddress),
        row.Upstream?.SourceSwitchId, row.Upstream?.PortIndex, Display(row, InventoryField.PortName, row.Upstream?.PortName), Display(row, InventoryField.ApInterface, row.Upstream?.ApInterface),
        row.CurrentState?.ToString() ?? "Unknown", row.LastValidState?.ToString(), Time(row.LastValidObservedAtUtc), Time(row.FirstDownAtUtc),
        row.OpenIncidentId?.ToString("D"), row.Classification.ToString(), row.UnknownReason?.ToString(), row.TimeNeedsReview.ToString(),
        row.ManualManagement?.Status.ToString(), row.ManualManagement?.Reason, row.SourceManagement?.MaintenanceMode?.ToString(),
        MonitoringHttp.Serialize(row.Fields), row.RawSourceValues is null ? null : MonitoringHttp.Serialize(row.RawSourceValues),
        Registered(row, InventoryField.SwitchName), Registered(row, InventoryField.SwitchIp),
        Registered(row, InventoryField.AssetTag), Registered(row, InventoryField.Notes),
        MonitoringHttp.Serialize(new { row.Device, row.Location, row.Controller, row.Upstream }),
        row.ManualInventory is null ? null : MonitoringHttp.Serialize(row.ManualInventory.Values),
        row.ManualInventory?.Version.ToString(CultureInfo.InvariantCulture), row.ManualInventory?.Source.ToString(),
        Time(row.ManualInventory?.RecordedAtUtc),
        row.ManualInventory is null ? null : MonitoringHttp.Serialize(row.ManualInventory.ManualPreferredFields),
        row.ManualInventory?.Placement is null ? null : MonitoringHttp.Serialize(row.ManualInventory.Placement),
        "Ap", null
    ];
    private static string? Registered(ApSnapshotRow row, InventoryField field)
        => row.ManualInventory?.Values.GetValueOrDefault(field);
    private static string? Display(ApSnapshotRow row, InventoryField field, string? automatic)
        => row.ManualInventory?.ManualPreferredFields.Contains(field) == true && Registered(row, field) is { } manual ? manual : automatic;
    private static string? Time(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static void Append(StringBuilder builder, IEnumerable<string?> values)
    {
        builder.AppendJoin(',', values.Select(value =>
        {
            var text = value ?? ""; var trimmed = text.TrimStart();
            if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0])) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }));
        builder.Append("\r\n");
    }
}
