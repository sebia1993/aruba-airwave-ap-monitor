using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Tests.Monitoring;

internal static class MonitoringFixture
{
    public static readonly Guid Instance = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Dataset = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid Generation = Guid.Parse("33333333-3333-3333-3333-333333333331");
    public static readonly Guid Interval = Guid.Parse("44444444-4444-4444-4444-444444444441");
    public static readonly Guid Incident = Guid.Parse("55555555-5555-5555-5555-555555555551");
    public static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-15T02:00:00Z");
    public const string Serial = "DEMO-SERIAL-100-A";
    public const string Mac = "02:00:00:00:00:64";

    public static CollectionContext Context(long run = 101, IEnumerable<string>? ids = null,
        BaselineStatus baseline = BaselineStatus.Unverified, long scope = 1)
        => new(run, new(Instance, Dataset, new("https://airwave.example.invalid/"), 1, scope,
            (ids ?? ["100"]).Select(id => new ExpectedTarget(id)).ToImmutableArray(), baseline, new()),
            CollectionKind.ApStatus, RunTrigger.Scheduled, At(run), At(run).AddMinutes(2));

    public static DateTimeOffset At(long run) => T0.AddMinutes((run - 100) * 5);

    public static ApObservation Observation(ApState? state = ApState.Up, long run = 101,
        string id = "100", string? serial = Serial, string? mac = Mac)
    {
        var at = At(run);
        var fields = ImmutableDictionary<string, FieldEvidence>.Empty
            .Add(MonitoringFieldNames.SerialNumber, Evidence(serial is not null, at))
            .Add(MonitoringFieldNames.WiredMac, Evidence(mac is not null, at))
            .Add(MonitoringFieldNames.State, Evidence(state is not null, at));
        return new(id, state, new(serial, mac), new("AP-A", "10.240.0.100", null, null),
            new(), new(), at, null, null, fields);
    }

    public static FieldEvidence Evidence(bool provided, DateTimeOffset at)
        => new(provided ? FieldAvailability.Provided : FieldAvailability.NotProvided,
            provided ? FieldValidity.Valid : FieldValidity.Unknown, EvidenceSource.Demo, at);

    public static ApIdentitySnapshot Identity(string? serial = Serial, string? mac = Mac, string id = "100")
    {
        var observation = Observation(run: 100, id: id, serial: serial, mac: mac);
        return new(Instance, Dataset, id, Generation, observation.Identity, observation.Fields);
    }

    public static ApTrackingSnapshot Previous(ApState? state = ApState.Down, string id = "100")
        => new(Identity(id: id), Interval, LastValidState: state,
            LastValidRunId: state is null ? null : 100,
            LastValidObservedAtUtc: state is null ? null : T0,
            LastUpAtUtc: state == ApState.Up ? T0 : null,
            OpenIncident: state == ApState.Down
                ? new(Incident, Generation, Interval, 100, T0, 100, T0, null, true)
                : null,
            LastProcessedRunId: 100, LastProcessedAtUtc: T0);
}
