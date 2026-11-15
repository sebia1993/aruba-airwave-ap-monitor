using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using System.Globalization;
using System.Xml.Linq;
using System.Text;
using AirWaveApMonitor.Infrastructure.AirWave;

namespace AirWaveApMonitor.Infrastructure.Demo;

public sealed record DemoScenarioFrame(string Name, bool IsPrehistory, DateTimeOffset SourceAtUtc,
    RunOutcome Outcome, ImmutableHashSet<int> DownIds, ImmutableHashSet<int> MissingIds, ImmutableHashSet<int> UnknownStateIds);

public sealed class ScenarioApSource : IApSource
{
    public const int DeviceCount = 2048;
    public const string CheckpointSource = "demo-ap-v1";
    public static readonly Uri DemoOrigin = new("https://demo.example.invalid/");
    private readonly TimeProvider timeProvider;
    private readonly ISourceCheckpointStore? checkpoints;
    private readonly object gate = new();
    private int nextFrame;

    public ScenarioApSource(TimeProvider timeProvider, ISourceCheckpointStore? checkpoints = null)
    {
        this.timeProvider = timeProvider;
        this.checkpoints = checkpoints;
    }

    // Two prehistory runs precede the five visible scenarios. They are observations, not UI totals.
    public static ImmutableArray<DemoScenarioFrame> Frames
    {
        get;
    } =
    [
        Frame("기준 관측", true, 55, RunOutcome.Partial, [], [], [6]),
        Frame("선행 Down", true, 60, RunOutcome.Partial, [1, 2, 3, 4, 5, 9, 10], [], [6]),
        Frame("정상", false, 65, RunOutcome.Succeeded, [1, 2, 3, 4, 5, 6, 7, 8]),
        Frame("신규 Down", false, 70, RunOutcome.Succeeded, [1, 2, 3, 4, 5, 6, 7, 8, 11, 12]),
        Frame("복구", false, 75, RunOutcome.Succeeded, [1, 3, 5, 6, 7, 8, 11, 12]),
        Frame("전체 실패", false, 80, RunOutcome.Failed, []),
        Frame("일부 누락", false, 85, RunOutcome.Partial, [1, 5, 6, 7, 8, 11, 12], [3, 20, 21])
    ];

    public async Task<ApCollectionBatch> CollectAsync(CollectionContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (context.Profile.Origin != DemoOrigin || context.Kind != CollectionKind.ApStatus)
            throw new InvalidOperationException("Demo AP 공급자는 가상 HTTPS 출처의 AP 상태 수집에만 사용할 수 있습니다.");
        int frameIndex;
        if (checkpoints is not null)
        {
            var committed = await checkpoints.GetSourceCheckpointAsync(context.Profile.DatasetId, CheckpointSource, ct);
            frameIndex = Math.Min((committed?.Position ?? -1) + 1, Frames.Length - 1);
        }
        else
        {
            // Standalone parser fixtures have no store. Runtime DI always supplies the committed checkpoint store.
            lock (gate)
            {
                frameIndex = Math.Min(nextFrame, Frames.Length - 1);
                if (nextFrame < Frames.Length - 1) nextFrame++;
            }
        }
        var frame = Frames[frameIndex];
        var checkpoint = new SourceCheckpoint(CheckpointSource, frameIndex);
        var collectedAt = timeProvider.GetUtcNow();
        if (frame.Outcome == RunOutcome.Failed)
            return new ApCollectionBatch(RunOutcome.Failed, [], [], collectedAt,
                ObservationCompleteness.Unknown, [new(CollectionErrorCategory.Timeout, 1, "가상 전체 수집 시간 초과")])
            { Checkpoint = checkpoint };

        var parsed = new ApListParser(EvidenceSource.Demo).Parse(new MemoryStream(Encoding.UTF8.GetBytes(CreateXml(frame))), collectedAt, ct);
        if (parsed.Outcome == RunOutcome.Failed)
            return parsed with { Checkpoint = checkpoint };
        var missing = frame.MissingIds.Order().Select(id => new ExcludedRecord(id, id.ToString(CultureInfo.InvariantCulture),
            CollectionErrorCategory.MissingRecord, "가상 응답에서 누락된 기대 대상")).ToImmutableArray();
        var errors = parsed.Errors.ToBuilder();
        foreach (var record in missing)
            errors.Add(new(CollectionErrorCategory.MissingRecord, 1, "일부 기대 대상 누락") { SourceApId = record.SourceApId });
        var partial = frame.Outcome == RunOutcome.Partial || parsed.Outcome == RunOutcome.Partial || errors.Count > 0;
        return parsed with
        {
            Outcome = partial ? RunOutcome.Partial : RunOutcome.Succeeded,
            ExcludedRecords = parsed.ExcludedRecords.AddRange(missing),
            Completeness = parsed.Completeness == ObservationCompleteness.Unknown ? ObservationCompleteness.Unknown
                : partial ? ObservationCompleteness.Partial : ObservationCompleteness.Complete,
            Errors = errors.ToImmutable(),
            Checkpoint = checkpoint,
            RecordErrorAttributionComplete = parsed.RecordErrorAttributionComplete &&
                (frame.Outcome != RunOutcome.Partial || errors.Count > 0)
        };
    }

    /// <summary>Synthetic fixture for the later XML parser. This does not establish real AirWave compatibility.</summary>
    public static string CreateXml(DemoScenarioFrame frame)
    {
        if (frame.Outcome == RunOutcome.Failed)
            throw new InvalidOperationException("실패 시나리오에는 성공 XML이 없습니다.");
        XNamespace ns = "http://www.airwave.com";
        var root = new XElement(ns + "amp_ap_list", new XAttribute(XNamespace.Xmlns + "amp", ns), new XAttribute("version", "1"));
        foreach (var ap in CreateObservations(frame, frame.SourceAtUtc))
        {
            root.Add(new XElement("ap", new XAttribute("id", ap.SourceApId),
                new XElement("device_category", "thin_ap"), new XElement("name", ap.Device.Name),
                new XElement("lan_ip", ap.Device.IpAddress), new XElement("lan_mac", ap.Identity.WiredMac),
                new XElement("serial_number", ap.Identity.SerialNumber), new XElement("model", ap.Device.Model),
                ap.State is null ? null : new XElement("is_up", ap.State == ApState.Up ? "true" : "false")));
        }
        return new XDocument(root).ToString(SaveOptions.DisableFormatting);
    }

    private static ImmutableArray<ApObservation> CreateObservations(DemoScenarioFrame frame, DateTimeOffset collectedAt)
    {
        var result = ImmutableArray.CreateBuilder<ApObservation>(DeviceCount - frame.MissingIds.Count);
        for (var id = 1; id <= DeviceCount; id++)
        {
            if (frame.MissingIds.Contains(id))
                continue;
            ApState? state = frame.UnknownStateIds.Contains(id) ? null : frame.DownIds.Contains(id) ? ApState.Down : ApState.Up;
            var evidence = new FieldEvidence(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.Demo, collectedAt, frame.SourceAtUtc);
            var fields = new[] { "name", "lan_ip", "lan_mac", "serial_number", "model", "firmware", "location", "controller" }
                .ToImmutableDictionary(name => name, _ => evidence)
                .Add("is_up", state is null ? evidence with
                {
                    Availability = FieldAvailability.NotProvided,
                    Validity = FieldValidity.Unknown
                } : evidence);
            var ip = id <= 254 ? $"192.0.2.{id}" : $"198.18.{(id - 255) / 254}.{(id - 255) % 254 + 1}";
            result.Add(new ApObservation(id.ToString(CultureInfo.InvariantCulture), state,
                new DeviceIdentity($"DEMO-{id:D6}", $"02:00:00:{(id >> 16) & 255:X2}:{(id >> 8) & 255:X2}:{id & 255:X2}"),
                new DeviceDetails($"DEMO-AP-{id:D4}", ip, "Virtual-AP", "demo-1"),
                new LocationDetails(Site: "가상 사업장", Building: $"가상 건물 {(id - 1) / 256 + 1}", Floor: $"{(id - 1) / 64 % 4 + 1}층"),
                new ControllerDetails("demo-controller", "가상 컨트롤러", "192.0.2.254"),
                collectedAt, frame.SourceAtUtc, null, fields));
        }
        return result.MoveToImmutable();
    }

    private static DemoScenarioFrame Frame(string name, bool prehistory, int minutes, RunOutcome outcome,
        int[] downIds, int[]? missingIds = null, int[]? unknownStateIds = null)
        => new(name, prehistory, new DateTimeOffset(2026, 11, 15, 1, 0, 0, TimeSpan.Zero).AddMinutes(minutes), outcome,
            downIds.ToImmutableHashSet(), (missingIds ?? []).ToImmutableHashSet(), (unknownStateIds ?? []).ToImmutableHashSet());
}
