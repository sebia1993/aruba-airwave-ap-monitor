using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Core.Operations;

/// <summary>Bounded UTF-8 CSV preview and pure apply-time checks. No database, clock, IDs or observation writes.</summary>
public sealed class ManualImportValidator
{
    public static ImmutableDictionary<string, InventoryField> InventoryColumns { get; } = new Dictionary<string, InventoryField>(StringComparer.Ordinal)
    {
        ["name"] = InventoryField.Name, ["ip_address"] = InventoryField.IpAddress, ["model"] = InventoryField.Model,
        ["firmware"] = InventoryField.Firmware, ["site"] = InventoryField.Site, ["building"] = InventoryField.Building,
        ["floor"] = InventoryField.Floor, ["zone"] = InventoryField.Zone, ["detail"] = InventoryField.Detail,
        ["raw_location"] = InventoryField.RawLocation, ["folder"] = InventoryField.Folder, ["group"] = InventoryField.Group,
        ["controller_id"] = InventoryField.ControllerId, ["controller_name"] = InventoryField.ControllerName,
        ["controller_ip"] = InventoryField.ControllerIp, ["switch_name"] = InventoryField.SwitchName,
        ["switch_ip"] = InventoryField.SwitchIp, ["port_name"] = InventoryField.PortName,
        ["ap_interface"] = InventoryField.ApInterface, ["asset_tag"] = InventoryField.AssetTag, ["notes"] = InventoryField.Notes
    }.ToImmutableDictionary(StringComparer.Ordinal);
    private static readonly ImmutableHashSet<string> ExtraColumns = ImmutableHashSet.Create(StringComparer.Ordinal,
        "ap_id", "serial_number", "wired_mac", "installation_point_id", "management_status", "management_reason",
        "manual_preferred_fields", "clear_fields", "clear_reason");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ImportPreview Preview(ReadOnlyMemory<byte> csv, ImportMapping mapping)
    {
        ValidateMapping(mapping);
        var limits = mapping.Limits ?? new();
        ImportPreview Error(string code, string message) => new(mapping.PreviewId, mapping.Version, mapping.CreatedAtUtc, [], [new(code, message)]);
        if (csv.Length > limits.MaxBytes) return Error("byte_limit", "CSV 크기 제한을 초과했습니다.");
        string input;
        try { input = StrictUtf8.GetString(csv.Span); }
        catch (DecoderFallbackException) { return Error("invalid_utf8", "UTF-8 CSV만 사용할 수 있습니다."); }
        if (input.StartsWith('\uFEFF')) input = input[1..];
        var parsed = Parse(input, limits);
        if (parsed.Error is { } parseError) return Error(parseError.Code, parseError.Message);
        var records = parsed.Rows;
        if (records.Count < 1) return Error("header_required", "CSV 머리글이 필요합니다.");
        var headers = records[0].Select(x => x.Trim()).ToArray();
        if (headers.Any(string.IsNullOrEmpty) || headers.Distinct(StringComparer.Ordinal).Count() != headers.Length)
            return Error("invalid_headers", "빈 머리글 또는 중복 머리글이 있습니다.");
        if (headers.Any(x => !InventoryColumns.ContainsKey(x) && !ExtraColumns.Contains(x)))
            return Error("unsupported_column", "허용되지 않은 열이 있습니다. 상태·LLDP·전력은 자동 관측으로 가져올 수 없습니다.");
        if (records.Count == 1) return Error("rows_required", "가져올 데이터 행이 없습니다.");
        var index = new InventoryIndex(mapping);
        var rows = ImmutableArray.CreateBuilder<ImportPreviewRow>();
        for (var i = 1; i < records.Count; i++)
        {
            var rowNumber = i;
            var errors = ImmutableArray.CreateBuilder<ImportProblem>();
            if (records[i].Length != headers.Length)
            {
                rows.Add(new(rowNumber, new(null, null, null), null, EmptyMutation(), [],
                    [new("column_count", "행의 열 개수가 머리글과 다릅니다.", rowNumber)]));
                continue;
            }
            var values = headers.Select((h, n) => (h, value: records[i][n].Trim())).ToDictionary(p => p.h, p => p.value, StringComparer.Ordinal);
            string? Value(string name) => values.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
            Guid? apId = null;
            if (Value("ap_id") is { } apText)
            {
                if (Guid.TryParseExact(apText, "D", out var parsedAp) && parsedAp != Guid.Empty) apId = parsedAp;
                else errors.Add(new("invalid_identity", "내부 AP ID 형식이 올바르지 않습니다.", rowNumber, "ap_id"));
            }
            var serial = Value("serial_number");
            var macText = Value("wired_mac");
            var mac = NormalizeMac(macText);
            if (macText is not null && mac is null) errors.Add(new("invalid_identity", "유선 MAC은 6개의 16진수 쌍을 콜론으로 구분해야 합니다.", rowNumber, "wired_mac"));
            var selector = new ImportSelector(apId, serial, mac);
            var target = Resolve(selector, index, rowNumber, errors);
            var set = ImmutableDictionary.CreateBuilder<InventoryField, string>();
            foreach (var (column, field) in InventoryColumns)
                if (Value(column) is { } value) set[field] = value;
            var clear = ImmutableHashSet.CreateBuilder<InventoryField>();
            var clearPoint = false;
            foreach (var field in SplitFields(Value("clear_fields")))
            {
                if (field == "installation_point_id") clearPoint = true;
                else if (InventoryColumns.TryGetValue(field, out var known)) clear.Add(known);
                else errors.Add(new("unsupported_clear", "해제할 수 없는 항목입니다.", rowNumber, "clear_fields"));
            }
            ImmutableHashSet<InventoryField>? preferences = null;
            if (Value("manual_preferred_fields") is { } preferredText)
            {
                var preferenceBuilder = ImmutableHashSet.CreateBuilder<InventoryField>();
                if (preferredText != "none")
                    foreach (var field in SplitFields(preferredText))
                    {
                        if (InventoryColumns.TryGetValue(field, out var known)) preferenceBuilder.Add(known);
                        else errors.Add(new("unsupported_preference", "수동 우선값 항목이 올바르지 않습니다.", rowNumber, "manual_preferred_fields"));
                    }
                preferences = preferenceBuilder.ToImmutable();
            }
            Guid? requestedPoint = null;
            ImportPointEntry? pointEntry = null;
            if (Value("installation_point_id") is { } pointText)
            {
                if (Guid.TryParseExact(pointText, "D", out var point) && point != Guid.Empty && index.Points.TryGetValue(point, out pointEntry)) requestedPoint = point;
                else errors.Add(new("unknown_point", "먼저 등록된 정확한 설치 지점 ID가 필요합니다.", rowNumber, "installation_point_id"));
            }
            if (clearPoint && requestedPoint is not null) errors.Add(new("set_clear_conflict", "설치 지점을 지정하면서 동시에 해제할 수 없습니다.", rowNumber, "installation_point_id"));
            OperationalStatus? status = null;
            if (Value("management_status") is { } statusText)
            {
                if (Enum.GetNames<OperationalStatus>().Contains(statusText, StringComparer.OrdinalIgnoreCase) &&
                    Enum.TryParse<OperationalStatus>(statusText, true, out var parsedStatus)) status = parsedStatus;
                else errors.Add(new("invalid_management_status", "지원하는 수동 관리 상태를 선택해야 합니다.", rowNumber, "management_status"));
            }
            var mutation = new ImportMutation(set.ToImmutable(), clear.ToImmutable(), preferences, status,
                Value("management_reason"), requestedPoint is not null || clearPoint, requestedPoint, Value("clear_reason"));
            errors.AddRange(ValidateMutation(mutation, limits).Select(e => e with { RowNumber = rowNumber }));
            var binding = target is null ? null : Bind(target, requestedPoint, pointEntry?.Version, index);
            var differences = target is null ? ImmutableArray<ImportDifference>.Empty : Differences(target, mutation);
            rows.Add(new(rowNumber, selector, binding, mutation, differences, errors.ToImmutable()));
        }
        var duplicates = rows.Where(x => x.Target is not null).GroupBy(x => x.Target!.ApId).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Target is { } target && duplicates.Contains(target.ApId))
                rows[i] = rows[i] with { Errors = rows[i].Errors.Add(new("duplicate_target", "같은 AP를 가리키는 모든 중복 행은 적용할 수 없습니다.", rows[i].RowNumber)) };
        return new(mapping.PreviewId, mapping.Version, mapping.CreatedAtUtc, rows.ToImmutable(), []);
    }

    /// <summary>Call inside the apply transaction. Any returned error must reject the whole selected subset.</summary>
    public ImmutableArray<ImportProblem> Revalidate(ImportPreview preview, IEnumerable<int> selectedRows, ImportMapping current)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(selectedRows);
        ValidateMapping(current);
        var errors = ImmutableArray.CreateBuilder<ImportProblem>();
        if (!preview.IsValidDocument) errors.Add(new("invalid_preview", "유효하지 않은 미리보기입니다."));
        if (preview.Version != current.Version) errors.Add(new("stale_preview", "범위·설정·복원 기준이 변경되었습니다. 다시 미리보기 하십시오."));
        var selected = selectedRows.ToArray();
        if (selected.Length == 0 || selected.Length > (current.Limits ?? new()).MaxRows || selected.Distinct().Count() != selected.Length)
            errors.Add(new("invalid_selection", "적용할 유효 행을 중복 없이 선택해야 합니다."));
        var index = new InventoryIndex(current);
        var previewRows = preview.Rows.ToDictionary(x => x.RowNumber);
        foreach (var number in selected.Distinct())
        {
            if (!previewRows.TryGetValue(number, out var row) || !row.IsValid)
            {
                errors.Add(new("invalid_selection", "적용할 수 없는 행입니다.", number));
                continue;
            }
            errors.AddRange(ValidateMutation(row.Mutation, current.Limits).Select(e => e with { RowNumber = number }));
            var target = Resolve(row.Selector, index, number, errors);
            if (target is null) continue;
            long? pointVersion = null;
            if (row.Target!.RequestedPointId is { } requested)
            {
                if (index.Points.TryGetValue(requested, out var point)) pointVersion = point.Version;
                else errors.Add(new("stale_point", "설치 지점이 변경되었습니다.", number));
            }
            if (Bind(target, row.Target.RequestedPointId, pointVersion, index) != row.Target)
                errors.Add(new("stale_target", "장비 세대·등록 정보·감시 정책·설치 지점이 변경되었습니다. 다시 미리보기 하십시오.", number));
        }
        return errors.ToImmutable();
    }

    /// <summary>Shared validation for direct edits/imports. Empty means no change; clearing is explicit and reasoned.</summary>
    public static ImmutableArray<ImportProblem> ValidateMutation(ImportMutation mutation, ImportLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        limits ??= new();
        var errors = ImmutableArray.CreateBuilder<ImportProblem>();
        foreach (var (field, value) in mutation.SetValues)
        {
            if (!Enum.IsDefined(field) || string.IsNullOrWhiteSpace(value) || value.Length > limits.MaxCellCharacters || HasInvalidControl(value))
                errors.Add(new("invalid_manual_value", "수동 값의 항목·길이·내용이 올바르지 않습니다.", Column: Column(field)));
            else if (field == InventoryField.FollowUp && value != "true")
                errors.Add(new("invalid_follow_up", "관리 대상 지정은 true 또는 명시 해제로만 변경할 수 있습니다.", Column: Column(field)));
            else if (field is InventoryField.IpAddress or InventoryField.ControllerIp or InventoryField.SwitchIp && !IPAddress.TryParse(value, out _))
                errors.Add(new("invalid_ip", "IP 주소 형식이 올바르지 않습니다.", Column: Column(field)));
        }
        if (mutation.ClearFields.Any(field => !Enum.IsDefined(field)) || mutation.ManualPreferredFields?.Any(field => !Enum.IsDefined(field)) == true)
            errors.Add(new("invalid_manual_field", "지원하지 않는 수동 항목입니다."));
        if (mutation.ManualPreferredFields?.Contains(InventoryField.FollowUp) == true)
            errors.Add(new("invalid_follow_up_preference", "관리 대상은 자동 관측값의 표시 출처가 아닙니다."));
        if (mutation.SetValues.Keys.Any(mutation.ClearFields.Contains)) errors.Add(new("set_clear_conflict", "같은 항목을 지정하면서 해제할 수 없습니다."));
        if (!mutation.ClearFields.IsEmpty && string.IsNullOrWhiteSpace(mutation.ClearReason)) errors.Add(new("clear_reason_required", "명시적 해제 사유가 필요합니다."));
        if (mutation.ManagementStatus is { } status && (!Enum.IsDefined(status) || string.IsNullOrWhiteSpace(mutation.ManagementReason)))
            errors.Add(new("management_reason_required", "관리 상태 변경 사유가 필요합니다."));
        if (mutation.ManagementStatus is null && !string.IsNullOrWhiteSpace(mutation.ManagementReason))
            errors.Add(new("management_status_required", "변경 사유에 해당하는 관리 상태가 필요합니다."));
        if ((!mutation.ChangesPlacement && mutation.InstallationPointId is not null) || mutation.InstallationPointId == Guid.Empty)
            errors.Add(new("invalid_placement", "설치 지점 변경 요청이 올바르지 않습니다."));
        if (mutation.ChangesPlacement && mutation.InstallationPointId is null && string.IsNullOrWhiteSpace(mutation.ClearReason))
            errors.Add(new("clear_reason_required", "설치 지점 해제 사유가 필요합니다."));
        foreach (var reason in new[] { mutation.ClearReason, mutation.ManagementReason })
            if (reason is not null && (reason.Length > limits.MaxCellCharacters || HasInvalidControl(reason)))
                errors.Add(new("invalid_reason", "변경 사유의 길이·내용이 올바르지 않습니다."));
        return errors.ToImmutable();
    }

    public static ImmutableDictionary<InventoryField, string> ApplyValues(ManualInventorySnapshot? previous, ImportMutation mutation, ImportLimits? limits = null)
    {
        if (!ValidateMutation(mutation, limits).IsEmpty) throw new ArgumentException("Invalid manual mutation.", nameof(mutation));
        var result = (previous?.Values ?? ImmutableDictionary<InventoryField, string>.Empty).ToBuilder();
        foreach (var field in mutation.ClearFields) result.Remove(field);
        foreach (var (field, value) in mutation.SetValues) result[field] = value.Trim();
        return result.ToImmutable();
    }

    public static string Column(InventoryField field) => InventoryColumns.FirstOrDefault(x => x.Value == field).Key ?? field.ToString();
    private static ImportMutation EmptyMutation() => new([], [], null, null, null, false, null, null);
    private static IEnumerable<string> SplitFields(string? value) => value?.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
    private static bool HasInvalidControl(string text) => text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));
    private static string? NormalizeMac(string? value) => value is not null && Regex.IsMatch(value.Trim(), "^(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$", RegexOptions.CultureInvariant) ? value.Trim().ToUpperInvariant() : null;
    private static ImportTargetBinding Bind(ImportInventoryEntry entry, Guid? point, long? pointVersion, InventoryIndex index) => new(entry.ApId,
        entry.DeviceGenerationId, entry.Inventory?.Version ?? 0, entry.TrackingPolicy?.Version ?? 0,
        entry.Placement?.PlacementId, entry.Placement?.InstallationPointId,
        entry.Placement is { } placement && index.Points.TryGetValue(placement.InstallationPointId, out var currentPoint) ? currentPoint.Version : null, point, pointVersion);
    private static ImmutableArray<ImportDifference> Differences(ImportInventoryEntry target, ImportMutation mutation)
    {
        var differences = ImmutableArray.CreateBuilder<ImportDifference>();
        var before = target.Inventory?.Values ?? ImmutableDictionary<InventoryField, string>.Empty;
        foreach (var (field, value) in mutation.SetValues.OrderBy(x => x.Key))
            if (before.GetValueOrDefault(field) != value) differences.Add(new(Column(field), before.GetValueOrDefault(field), value));
        foreach (var field in mutation.ClearFields.Order())
            if (before.TryGetValue(field, out var value)) differences.Add(new(Column(field), value, null, true));
        if (mutation.ManualPreferredFields is { } preference && !preference.SetEquals(target.Inventory?.ManualPreferredFields ?? []))
            differences.Add(new("manual_preferred_fields", string.Join(';', (target.Inventory?.ManualPreferredFields ?? []).Order().Select(Column)), string.Join(';', preference.Order().Select(Column))));
        if (mutation.ManagementStatus is { } status && (target.TrackingPolicy?.Status != status || target.TrackingPolicy.Reason != mutation.ManagementReason))
            differences.Add(new("management_status", target.TrackingPolicy?.Status.ToString(), status.ToString()));
        if (mutation.ChangesPlacement && target.Placement?.InstallationPointId != mutation.InstallationPointId)
            differences.Add(new("installation_point_id", target.Placement?.InstallationPointId.ToString("D"), mutation.InstallationPointId?.ToString("D"), mutation.InstallationPointId is null));
        return differences.ToImmutable();
    }

    private static ImportInventoryEntry? Resolve(ImportSelector selector, InventoryIndex index, int row, ImmutableArray<ImportProblem>.Builder errors)
    {
        var matches = new List<ImportInventoryEntry>();
        void Match(ImportInventoryEntry[] candidates, string column)
        {
            if (candidates.Length == 0) errors.Add(new("unknown_identity", "현재 데이터셋에서 정확한 장비를 찾지 못했습니다.", row, column));
            else if (candidates.Length > 1) errors.Add(new("ambiguous_identity", "동일 식별자가 여러 현재 AP에 있어 적용할 수 없습니다.", row, column));
            else matches.Add(candidates[0]);
        }
        if (selector.ApId is { } id) Match(index.ById.GetValueOrDefault(id) ?? [], "ap_id");
        if (selector.SerialNumber is { } serial) Match(index.BySerial.GetValueOrDefault(serial) ?? [], "serial_number");
        if (selector.WiredMac is { } mac) Match(index.ByMac.GetValueOrDefault(mac) ?? [], "wired_mac");
        if (selector.ApId is null && selector.SerialNumber is null && selector.WiredMac is null) errors.Add(new("identity_required", "내부 AP ID 또는 유일한 시리얼·유선 MAC이 필요합니다.", row));
        if (matches.Select(x => x.ApId).Distinct().Count() > 1)
        {
            errors.Add(new("conflicting_identity", "제공한 식별자들이 서로 다른 AP를 가리킵니다.", row));
            return null;
        }
        return matches.FirstOrDefault();
    }

    private static void ValidateMapping(ImportMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var limits = mapping.Limits ?? new();
        if (mapping.PreviewId == Guid.Empty || mapping.Version.DatasetId == Guid.Empty || mapping.Version.InstanceId == Guid.Empty ||
            mapping.Version.ScopeVersion <= 0 || mapping.Version.ConfigVersion <= 0 || mapping.Version.RestoreEpoch < 0 ||
            mapping.Inventory.IsDefault || mapping.InstallationPoints.IsDefault || limits.MaxBytes is <= 0 or > 16 * 1024 * 1024 ||
            limits.MaxRows is <= 0 or > 10000 || limits.MaxCellCharacters is <= 0 or > 16384)
            throw new ArgumentException("Invalid import context or limits.", nameof(mapping));
        if (mapping.InstallationPoints.Any(x => x.InstallationPointId == Guid.Empty || x.Version <= 0) ||
            mapping.InstallationPoints.Select(x => x.InstallationPointId).Distinct().Count() != mapping.InstallationPoints.Length)
            throw new ArgumentException("Point index must contain unique registered IDs and versions.", nameof(mapping));
    }

    private sealed class InventoryIndex
    {
        internal readonly Dictionary<Guid, ImportInventoryEntry[]> ById;
        internal readonly Dictionary<string, ImportInventoryEntry[]> BySerial;
        internal readonly Dictionary<string, ImportInventoryEntry[]> ByMac;
        internal readonly Dictionary<Guid, ImportPointEntry> Points;
        internal InventoryIndex(ImportMapping mapping)
        {
            var entries = mapping.Inventory.Where(e => e.DatasetId == mapping.Version.DatasetId && e.InstanceId == mapping.Version.InstanceId &&
                e.ApId != Guid.Empty && e.DeviceGenerationId != Guid.Empty).ToArray();
            ById = entries.GroupBy(e => e.ApId).ToDictionary(g => g.Key, g => g.ToArray());
            bool Valid(ImportInventoryEntry entry, string field) => entry.IdentityEvidence.TryGetValue(field, out var evidence) &&
                evidence.Availability == FieldAvailability.Provided && evidence.Validity == FieldValidity.Valid && evidence.Source is EvidenceSource.AirWave or EvidenceSource.Demo;
            BySerial = entries.Where(e => Valid(e, "serial_number") && !string.IsNullOrWhiteSpace(e.Identity.SerialNumber))
                .GroupBy(e => e.Identity.SerialNumber!.Trim(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            ByMac = entries.Where(e => Valid(e, "lan_mac") && NormalizeMac(e.Identity.WiredMac) is not null)
                .GroupBy(e => NormalizeMac(e.Identity.WiredMac)!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            Points = mapping.InstallationPoints.ToDictionary(p => p.InstallationPointId);
        }
    }

    private sealed record ParseResult(List<string[]> Rows, ImportProblem? Error = null);
    private static ParseResult Parse(string input, ImportLimits limits)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var closedQuote = false;
        var recordStarted = false;
        ParseResult Error(string code, string message) => new([], new(code, message));
        void EndCell() { fields.Add(cell.ToString()); cell.Clear(); closedQuote = false; }
        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (char.IsControl(ch) && ch is not ('\r' or '\n' or '\t')) return Error("invalid_control", "CSV에 지원하지 않는 제어 문자가 있습니다.");
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < input.Length && input[i + 1] == '"') { cell.Append('"'); i++; }
                    else { quoted = false; closedQuote = true; }
                }
                else cell.Append(ch);
            }
            else if (ch == ',') { EndCell(); recordStarted = true; }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && (i + 1 >= input.Length || input[i + 1] != '\n')) return Error("invalid_csv", "줄바꿈은 LF 또는 CRLF를 사용해야 합니다.");
                if (ch == '\r') i++;
                EndCell(); records.Add(fields.ToArray()); fields.Clear(); recordStarted = false;
                if (records.Count > limits.MaxRows + 1) return Error("row_limit", "CSV 행 수 제한을 초과했습니다.");
            }
            else if (ch == '"' && cell.Length == 0 && !closedQuote) { quoted = true; recordStarted = true; }
            else if (ch == '"' || closedQuote) return Error("invalid_csv", "CSV 따옴표 형식이 올바르지 않습니다.");
            else { cell.Append(ch); recordStarted = true; }
            if (cell.Length > limits.MaxCellCharacters) return Error("cell_limit", "CSV 셀 길이 제한을 초과했습니다.");
            if (fields.Count > 48) return Error("column_limit", "CSV 열 수 제한을 초과했습니다.");
        }
        if (quoted) return Error("invalid_csv", "닫히지 않은 CSV 따옴표가 있습니다.");
        if (recordStarted || fields.Count > 0 || cell.Length > 0) { EndCell(); records.Add(fields.ToArray()); }
        if (records.Count > limits.MaxRows + 1) return Error("row_limit", "CSV 행 수 제한을 초과했습니다.");
        return new(records);
    }
}
