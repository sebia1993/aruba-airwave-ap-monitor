using System.Collections.Immutable;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AirWaveApMonitor.Core.Contracts;
namespace AirWaveApMonitor.Infrastructure.AirWave;

public sealed class ApListParser(EvidenceSource source = EvidenceSource.AirWave)
{
    public const long MaximumBytes = 64L * 1024 * 1024;
    private static readonly string[] Fields = ["device_category", "is_up", "name", "lan_ip", "lan_mac", "serial_number", "model", "firmware", "folder", "group", "controller_id", "syslocation", "syscontact", "mfgr", "planned_maintenance_mode", "monitor_only", "operating_mode", "is_remote_ap", "reboot_count", "snmp_uptime", "last_contacted", "last_reboot", "upstream_device_id", "upstream_port_index"];
    public ApCollectionBatch Parse(Stream xml, DateTimeOffset observedAt) => Parse(xml, observedAt, CancellationToken.None);
    public ApCollectionBatch Parse(Stream xml, DateTimeOffset observedAt, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var bounded = new BoundedReadStream(xml, MaximumBytes, ct);
            using var reader = XmlReader.Create(bounded, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes, CloseInput = false });
            var root = XDocument.Load(reader).Root;
            if (root?.Name != XName.Get("amp_ap_list", "http://www.airwave.com"))
                return Failure(CollectionErrorCategory.InvalidResponse, observedAt);
            var entries = root.Elements("ap").ToArray();
            if (root.Elements().Any(e => e.Name != "ap"))
                return Failure(CollectionErrorCategory.InvalidResponse, observedAt);
            var duplicateIds = entries.Select(e => (string?)e.Attribute("id")).Where(id => id is not null).GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key!).ToHashSet(StringComparer.Ordinal);
            var observations = ImmutableArray.CreateBuilder<ApObservation>();
            var excluded = ImmutableArray.CreateBuilder<ExcludedRecord>();
            var errors = ImmutableArray.CreateBuilder<CollectionError>();
            for (var index = 0; index < entries.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[index];
                var id = (string?)entry.Attribute("id");
                if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !Regex.IsMatch(id, "^[0-9]+$", RegexOptions.CultureInvariant))
                {
                    excluded.Add(new(index, id?.Length <= 128 ? id : null, CollectionErrorCategory.InvalidField, "AP ID 유효성 확인 실패"));
                    continue;
                }
                if (duplicateIds.Contains(id))
                {
                    excluded.Add(new(index, id, CollectionErrorCategory.DuplicateIdentity, "중복 ID 전체 제외"));
                    continue;
                }
                var categories = entry.Elements("device_category").ToArray();
                if (categories.Length != 1 || categories[0].Value != "thin_ap")
                {
                    excluded.Add(new(index, id, CollectionErrorCategory.Unverified, "AP 분류 미검증: 대상에서 제외"));
                    continue;
                }
                var evidence = ImmutableDictionary.CreateBuilder<string, FieldEvidence>();
                var values = new Dictionary<string, string?>();
                foreach (var field in Fields)
                {
                    var elements = entry.Elements(field).ToArray();
                    var value = elements.Length == 1 ? elements[0].Value.Trim() : null;
                    var provided = !string.IsNullOrEmpty(value);
                    var valid = elements.Length <= 1 && (!provided || value!.Length <= 4096);
                    if (field == "lan_ip" && provided)
                        valid &= IPAddress.TryParse(value, out _);
                    if (field == "lan_mac" && provided)
                        valid &= Regex.IsMatch(value!, "^(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$", RegexOptions.CultureInvariant);
                    if (field == "is_up" && provided)
                        valid &= value is "true" or "false";
                    if (field == "planned_maintenance_mode" && provided)
                        valid &= value is "true" or "false";
                    var unverified = field is "last_contacted" or "last_reboot" or "snmp_uptime";
                    evidence[field] = new(provided ? unverified ? FieldAvailability.Unverified : FieldAvailability.Provided : FieldAvailability.NotProvided,
                        valid ? provided ? unverified ? FieldValidity.Unknown : FieldValidity.Valid : FieldValidity.Unknown : FieldValidity.Invalid, source, observedAt);
                    values[field] = valid && provided ? value : null;
                    if (!valid)
                        errors.Add(new(CollectionErrorCategory.InvalidField, 1, "AP 필드 유효성 확인 실패"));
                }
                foreach (var field in entry.Elements().Select(e => e.Name.ToString()).Distinct(StringComparer.Ordinal).Where(name => !evidence.ContainsKey(name)))
                    evidence[field] = new(FieldAvailability.Unverified, FieldValidity.Unknown, source, observedAt);
                string? V(string key) => values[key];
                ApState? state = V("is_up") switch
                {
                    "true" => ApState.Up,
                    "false" => ApState.Down,
                    _ => null
                };
                if (state is null)
                    errors.Add(new(CollectionErrorCategory.InvalidField, 1, "AP 상태 미확인"));
                var raw = new[] { "last_contacted", "last_reboot", "snmp_uptime" }.Where(f => V(f) != null).ToImmutableDictionary(f => f, f => V(f)!);
                observations.Add(new(id, state, new(V("serial_number"), V("lan_mac")), new(V("name"), V("lan_ip"), V("model"), V("firmware")),
                    new(AirWaveRawLocation: V("syslocation"), Folder: V("folder"), Group: V("group")), new(V("controller_id")), observedAt, null, null, evidence.ToImmutable(),
                    new(V("planned_maintenance_mode") switch
                    {
                        "true" => true,
                        "false" => false,
                        _ => null
                    }, V("operating_mode")),
                    new(V("upstream_device_id"), V("upstream_port_index"), null, null, evidence["upstream_device_id"]), raw));
            }
            foreach (var group in excluded.GroupBy(e => e.Reason))
                errors.Add(new(group.Key, group.Count(), "제외된 레코드가 있습니다."));
            if (observations.Count == 0)
                errors.Add(new(CollectionErrorCategory.Unverified, 1, "AP 0대: 대상·권한·응답 확인이 필요합니다."));
            var partial = errors.Count > 0;
            return new(partial ? RunOutcome.Partial : RunOutcome.Succeeded, observations.ToImmutable(), excluded.ToImmutable(), observedAt,
                observations.Count == 0 ? ObservationCompleteness.Unknown : partial ? ObservationCompleteness.Partial : ObservationCompleteness.Complete, errors.ToImmutable());
        }
        catch (ResponseLimitException) { return Failure(CollectionErrorCategory.ResponseTooLarge, observedAt); }
        catch (XmlException) { return Failure(CollectionErrorCategory.InvalidResponse, observedAt); }
    }
    internal static ApCollectionBatch Failure(CollectionErrorCategory category, DateTimeOffset at) => new(RunOutcome.Failed, [], [], at, ObservationCompleteness.Unknown, [new(category, 1, SafeMessages.For(category))]);
}

internal sealed class ResponseLimitException : IOException;
internal sealed class BoundedReadStream(Stream inner, long limit, CancellationToken ct) : Stream
{
    private long read;
    public override int Read(byte[] buffer, int offset, int count)
    {
        ct.ThrowIfCancellationRequested();
        var n = inner.Read(buffer, offset, count);
        Check(n);
        return n;
    }
    public override int Read(Span<byte> buffer)
    {
        ct.ThrowIfCancellationRequested();
        var n = inner.Read(buffer);
        Check(n);
        return n;
    }
    private void Check(int n)
    {
        read += n;
        if (read > limit)
            throw new ResponseLimitException();
    }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException(); public override long Position
    {
        get => read; set => throw new NotSupportedException();
    }
    public override void Flush() => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}
public static class SafeMessages
{
    public static string For(CollectionErrorCategory category) => category switch
    {
        CollectionErrorCategory.AuthenticationRejected => "인증이 거절되었습니다. 자격 증명 확인 후 명시적으로 재시도하세요.",
        CollectionErrorCategory.AuthenticationBlocked => "자동 로그인이 중단되었습니다. 자격 증명 재등록 또는 명시적 재시도가 필요합니다.",
        CollectionErrorCategory.Permission => "권한 또는 토큰 오류입니다. HTTP 403은 자동 재로그인하지 않습니다.",
        CollectionErrorCategory.Tls => "TLS 인증서 검증 실패. HTTP_STATUS=미수신",
        CollectionErrorCategory.Timeout => "요청 또는 전체 진단 제한 시간을 초과했습니다.",
        CollectionErrorCategory.ResponseTooLarge => "압축 해제 후 응답이 64 MiB를 초과했습니다.",
        CollectionErrorCategory.InvalidResponse => "지원하는 AP XML 또는 로그인 응답이 아닙니다.",
        CollectionErrorCategory.Configuration => "서버 설정 또는 자격 증명을 확인하세요.",
        CollectionErrorCategory.Unsupported => "이 플랫폼에서는 Windows DPAPI를 지원하지 않습니다.",
        CollectionErrorCategory.SessionExpired => "세션 재인증 후에도 인증이 유효하지 않습니다.",
        CollectionErrorCategory.CredentialUnavailable => "실행 계정에서 자격 증명을 복호화하지 못했습니다. 재등록이 필요합니다.",
        _ => "연결 또는 읽기에 실패했습니다. 진단 단계와 오류 분류를 확인하세요."
    };
}
