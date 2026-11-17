using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
namespace AirWaveApMonitor.Tests.Operations;
internal static class OperationsFixture
{
    internal static readonly Guid Dataset = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid Instance = Guid.Parse("22222222-2222-2222-2222-222222222222");
    internal static readonly Guid Ap = Guid.Parse("33333333-3333-3333-3333-333333333333");
    internal static readonly Guid Generation = Guid.Parse("44444444-4444-4444-4444-444444444444");
    internal static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-16T01:00:00Z");
    internal static FieldEvidence Field(DateTimeOffset? at = null, DateTimeOffset? sourceAt = null) =>
        new(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.Demo, at ?? At, sourceAt);
    internal static ImmutableDictionary<string, FieldEvidence> Fields(params string[] names) =>
        names.ToImmutableDictionary(x => x, _ => Field());
    internal static DeviceMetadata Metadata(string? name = "AP 1", string? serial = "S1", string? mac = "00:11:22:33:44:55") =>
        new(new(serial, mac), new(name, "192.0.2.1", "AP-model", "1.0"), new(Folder: "old"), new("controller-1"));
    internal static ObservationEvidence Evidence(ImmutableDictionary<string, FieldEvidence> before,
        ImmutableDictionary<string, FieldEvidence> after) => new(Dataset, Instance, Ap, Generation, 42, 3, At, before, after, Generation);
    internal static ImportInventoryEntry Inventory(Guid? ap = null, Guid? generation = null, string serial = "S1", string mac = "00:11:22:33:44:55") =>
        new(Dataset, Instance, ap ?? Ap, generation ?? Generation, new(serial, mac), Fields("serial_number", "lan_mac"));
    internal static ImportMapping Mapping(params ImportInventoryEntry[] entries) => new(Guid.Parse("55555555-5555-5555-5555-555555555555"),
        new(Dataset, Instance, 3, 5, 0), At, entries.ToImmutableArray(), []);
}
