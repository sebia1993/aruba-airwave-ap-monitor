using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Operations;
using static AirWaveApMonitor.Tests.Operations.OperationsFixture;
namespace AirWaveApMonitor.Tests.Operations;
public sealed class ChangeDetectorTests
{
    private readonly ChangeDetector detector = new();
    [Fact]
    public void InitialMetadata_IsBaselineNotChange()
    {
        var result = detector.Compare(null, Metadata(), Evidence([], Fields("name", "serial_number", "lan_mac")));
        Assert.Equal(3, result.Length);
        Assert.All(result, e => { Assert.Equal(ChangeSemantics.BaselineRegistered, e.Semantics); Assert.False(e.IsChangedValue); Assert.Null(e.BeforeValue); });
    }
    [Fact]
    public void MissingField_DoesNotDeletePreviousValue()
    {
        var result = detector.Compare(Metadata(), Metadata(null), Evidence(Fields("name"), Fields("name")));
        Assert.Empty(result);
    }
    [Theory]
    [InlineData(FieldAvailability.NotProvided, FieldValidity.Unknown)]
    [InlineData(FieldAvailability.Unsupported, FieldValidity.Unknown)]
    [InlineData(FieldAvailability.Unverified, FieldValidity.Unknown)]
    [InlineData(FieldAvailability.Provided, FieldValidity.Invalid)]
    public void InvalidOrUnsupportedField_DoesNotBecomeChange(FieldAvailability availability, FieldValidity validity)
    {
        var fields = Fields("name").SetItem("name", Field() with { Availability = availability, Validity = validity });
        Assert.Empty(detector.Compare(Metadata(), Metadata("changed"), Evidence(Fields("name"), fields)));
    }
    [Fact]
    public void ValidChange_PreservesBothFieldClocksScopeAndGap()
    {
        var before = Fields("name").SetItem("name", Field(At.AddMinutes(-5), At.AddMinutes(-7)));
        var after = Fields("name").SetItem("name", Field(At, At.AddMinutes(-2)));
        var change = Assert.Single(detector.Compare(Metadata(), Metadata("AP changed"), Evidence(before, after) with { HasObservationGap = true }));
        Assert.Equal("AP 1", change.BeforeValue); Assert.Equal("AP changed", change.AfterValue);
        Assert.Equal(before["name"], change.BeforeEvidence); Assert.Equal(after["name"], change.AfterEvidence);
        Assert.Equal(42, change.RunId); Assert.Equal(3, change.ScopeVersion); Assert.Equal(Dataset, change.DatasetId);
        Assert.True(change.HasObservationGap); Assert.Equal(ChangeSemantics.ValueChanged, change.Semantics);
    }
    [Fact]
    public void StaleSourceField_IsIgnoredEvenWithLaterReadTime()
    {
        var before = Fields("name").SetItem("name", Field(At.AddMinutes(-1), At));
        var after = Fields("name").SetItem("name", Field(At.AddMinutes(1), At.AddMinutes(-1)));
        Assert.Empty(detector.Compare(Metadata(), Metadata("stale"), Evidence(before, after)));
    }
    [Fact]
    public void MissingSourceTime_IsNotInventedFromReadTime()
    {
        var change = Assert.Single(detector.Compare(Metadata(), Metadata("new"), Evidence(Fields("name"), Fields("name"))));
        Assert.Null(change.AfterEvidence!.SourceAtUtc); Assert.Equal(SourceFreshness.Unknown, change.AfterEvidence.Freshness);
    }
    [Fact]
    public void MacCaseAndOuterWhitespace_DoNotCreateFalseChanges()
    {
        var previous = Metadata(" AP 1 ", " S1 ", "aa:bb:cc:dd:ee:ff");
        var current = Metadata("AP 1", "S1", "AA:BB:CC:DD:EE:FF");
        var fields = Fields("name", "serial_number", "lan_mac");
        Assert.Empty(detector.Compare(previous, current, Evidence(fields, fields)));
    }
    [Fact]
    public void FirmwareControllerAndUpstream_KeepDifferentMeanings()
    {
        var previous = Metadata() with { Upstream = new("sw1", "1", null, null) };
        var current = previous with { Device = previous.Device with { Firmware = "2.0" }, Controller = new("controller-2"), Upstream = new("sw1", "2", null, null) };
        var fields = Fields("firmware", "controller_id", "upstream_port_index");
        var changes = detector.Compare(previous, current, Evidence(fields, fields));
        Assert.Equal(3, changes.Length);
        Assert.Equal(OperationHistoryKind.FirmwareController, changes.Single(c => c.Field == "controller_id").Kind);
        Assert.Equal(OperationHistoryKind.FirmwareController, changes.Single(c => c.Field == "firmware").Kind);
        Assert.Equal(OperationHistoryKind.Connection, changes.Single(c => c.Field == "upstream_port_index").Kind);
        Assert.DoesNotContain(changes, c => c.Field.Contains("lldp", StringComparison.OrdinalIgnoreCase));
    }
    [Fact]
    public void Replacement_RequiresConfirmationAndNewFieldsAreBaselines()
    {
        var next = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var fields = Fields("serial_number", "name");
        var evidence = Evidence(fields, fields) with { DeviceGenerationId = next, IdentityChange = IdentityChange.Replaced };
        var changes = detector.Compare(Metadata(), Metadata("new device", "s1"), evidence);
        var replacement = Assert.Single(changes, x => x.Semantics == ChangeSemantics.ReplacementDetected);
        Assert.True(replacement.NeedsConfirmation); Assert.Equal(Generation, replacement.PreviousDeviceGenerationId);
        Assert.Equal(next, replacement.DeviceGenerationId);
        Assert.Equal("serial_number", replacement.Field); Assert.Equal("S1", replacement.BeforeValue); Assert.Equal("s1", replacement.AfterValue);
        Assert.All(changes.Where(x => x.Semantics != ChangeSemantics.ReplacementDetected), x => Assert.Equal(ChangeSemantics.BaselineRegistered, x.Semantics));
        Assert.DoesNotContain(changes, x => x.Semantics == ChangeSemantics.ReplacementConfirmed);
    }
    [Fact]
    public void ManualAndAutomaticValues_AreKeptSeparately()
    {
        var automatic = Metadata();
        var manual = new ManualInventorySnapshot(3, Generation, EvidenceSource.Manual, At,
            ImmutableDictionary<InventoryField, string>.Empty.Add(InventoryField.Name, "manual label"), [InventoryField.Name]);
        var current = automatic with { ManualManagement = new(OperationalStatus.PlannedWork, "work", At) };
        Assert.Empty(detector.Compare(automatic, current, Evidence(Fields("name"), Fields("name"))));
        Assert.Equal("AP 1", automatic.Device.Name); Assert.Equal("manual label", manual.Values[InventoryField.Name]);
        var eventAfterAutomaticChange = Assert.Single(detector.Compare(automatic, Metadata("AP 2"), Evidence(Fields("name"), Fields("name"))));
        Assert.Equal("AP 1", eventAfterAutomaticChange.BeforeValue);
    }
    [Fact]
    public void ManualEvidence_CannotCreateAutomaticChange()
    {
        var fields = Fields("name").SetItem("name", Field() with { Source = EvidenceSource.Manual });
        Assert.Empty(detector.Compare(Metadata(), Metadata("manual"), Evidence(Fields("name"), fields)));
    }
    [Fact]
    public void UptimeAndPowerRawMeasurements_DoNotBecomeMetadataEvents()
    {
        var old = Metadata(); var current = old with { Device = old.Device with { Uptime = TimeSpan.FromDays(1) }, RawSourceValues = ImmutableDictionary<string, string>.Empty.Add("poe", "12.5") };
        Assert.Empty(detector.Compare(old, current, Evidence(Fields("snmp_uptime", "poe"), Fields("snmp_uptime", "poe"))));
    }
    [Fact]
    public void BackwardReadClock_IsPreservedAndFlagged()
    {
        var before = Fields("name").SetItem("name", Field(At.AddMinutes(5)));
        var change = Assert.Single(detector.Compare(Metadata(), Metadata("new"), Evidence(before, Fields("name"))));
        Assert.True(change.TimeNeedsReview); Assert.Equal(At, change.ObservedAtUtc);
    }
    [Fact]
    public void DifferentSerialCannotBeRecordedAsSameGenerationEdit()
    {
        var fields = Fields("serial_number");
        Assert.Throws<ArgumentException>(() => detector.Compare(Metadata(), Metadata(serial: "S2"), Evidence(fields, fields)));
    }
    [Fact]
    public void ReplacementWithoutValidChangedIdentity_IsRejected()
    {
        var evidence = Evidence([], []) with { IdentityChange = IdentityChange.Replaced, DeviceGenerationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa") };
        Assert.Throws<ArgumentException>(() => detector.Compare(Metadata(), Metadata(serial: "S2"), evidence));
    }
    [Fact]
    public void UndefinedFreshness_IsNotAcceptedEvidence()
    {
        var invalid = Fields("name").SetItem("name", Field() with { Freshness = (SourceFreshness)999 });
        Assert.Empty(detector.Compare(Metadata(), Metadata("new"), Evidence(Fields("name"), invalid)));
    }

    [Fact]
    public void ReplacementWithRegressingSourceIdentityTime_IsRejectedBeforeHistoryCreation()
    {
        var before = Fields("serial_number").SetItem("serial_number", Field(At.AddMinutes(-1), At));
        var after = Fields("serial_number").SetItem("serial_number", Field(At.AddMinutes(1), At.AddMinutes(-1)));
        var evidence = Evidence(before, after) with { IdentityChange = IdentityChange.Replaced, DeviceGenerationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa") };
        Assert.Throws<ArgumentException>(() => detector.Compare(Metadata(), Metadata(serial: "S2"), evidence));
    }

    [Fact]
    public void ReplacementWithoutSerial_RecordsActualNormalizedMacValues()
    {
        var fields = Fields("lan_mac");
        var evidence = Evidence(fields, fields) with { IdentityChange = IdentityChange.Replaced, DeviceGenerationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa") };
        var result = detector.Compare(Metadata(serial: null, mac: "aa:bb:cc:dd:ee:ff"), Metadata(serial: null, mac: "11:22:33:44:55:66"), evidence);
        var replacement = Assert.Single(result, e => e.Semantics == ChangeSemantics.ReplacementDetected);
        Assert.Equal("lan_mac", replacement.Field); Assert.Equal("AA:BB:CC:DD:EE:FF", replacement.BeforeValue);
        Assert.Equal("11:22:33:44:55:66", replacement.AfterValue); Assert.Equal(fields["lan_mac"], replacement.AfterEvidence);
        Assert.Equal(Generation, replacement.PreviousDeviceGenerationId); Assert.Equal(evidence.DeviceGenerationId, replacement.DeviceGenerationId);
    }

}
