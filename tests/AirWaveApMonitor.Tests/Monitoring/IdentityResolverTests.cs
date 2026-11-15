using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Monitoring;
using static AirWaveApMonitor.Tests.Monitoring.MonitoringFixture;

namespace AirWaveApMonitor.Tests.Monitoring;

public sealed class IdentityResolverTests
{
    private readonly IdentityResolver resolver = new();

    [Theory]
    [InlineData(Serial, Mac, "SERIAL-B", Mac, IdentityChange.Replaced)]
    [InlineData(Serial, Mac, Serial, "02:00:00:00:00:65", IdentityChange.Unchanged)]
    [InlineData(null, Mac, null, "02:00:00:00:00:65", IdentityChange.Replaced)]
    [InlineData(Serial, Mac, null, Mac, IdentityChange.Unchanged)]
    [InlineData(Serial, Mac, null, null, IdentityChange.Unchanged)]
    [InlineData(null, Mac, Serial, Mac, IdentityChange.Enriched)]
    [InlineData(null, null, Serial, Mac, IdentityChange.Enriched)]
    [InlineData(Serial, Mac, null, "02:00:00:00:00:65", IdentityChange.Replaced)]
    public void IdentityPriority_UsesComparableValidSerialBeforeMac(string? oldSerial, string? oldMac,
        string? serial, string? mac, IdentityChange expected)
    {
        var result = resolver.Resolve(Identity(oldSerial, oldMac), Observation(serial: serial, mac: mac));
        Assert.Equal(expected, result.Change);
        Assert.Equal(expected == IdentityChange.Replaced, result.ReplacementNeedsConfirmation);
    }

    [Fact]
    public void IdentityFieldMissing_DoesNotReplace()
    {
        var result = resolver.Resolve(Identity(), Observation(serial: null, mac: null));
        Assert.Equal(IdentityChange.Unchanged, result.Change);
        Assert.Equal(new DeviceIdentity(Serial, Mac), result.Identity);
        Assert.Equal(T0, result.Fields[MonitoringFieldNames.SerialNumber].CollectedAtUtc);
    }

    [Fact]
    public void InvalidIdentityField_DoesNotReplaceOrErasePreviousEvidence()
    {
        var current = Observation(serial: "INVALID");
        current = current with { Fields = current.Fields.SetItem(MonitoringFieldNames.SerialNumber,
            current.Fields[MonitoringFieldNames.SerialNumber] with { Validity = FieldValidity.Invalid }) };
        var result = resolver.Resolve(Identity(), current);
        Assert.Equal(IdentityChange.Unchanged, result.Change);
        Assert.Equal(Serial, result.Identity.SerialNumber);
    }

    [Fact]
    public void MissingEvidence_DoesNotMakeAValueValid()
    {
        var current = Observation(serial: "DIFFERENT") with { Fields = [] };
        var result = resolver.Resolve(Identity(), current);
        Assert.Equal(IdentityChange.Unchanged, result.Change);
        Assert.Equal(Serial, result.Identity.SerialNumber);
    }

    [Fact]
    public void Replacement_DoesNotInheritMissingSerialFromOldGeneration()
    {
        var result = resolver.Resolve(Identity(), Observation(serial: null, mac: "02:00:00:00:00:65"));
        Assert.Null(result.Identity.SerialNumber);
        Assert.False(result.Fields.ContainsKey(MonitoringFieldNames.SerialNumber));
    }

    [Fact]
    public void MacCaseDifference_DoesNotReplace()
    {
        var result = resolver.Resolve(Identity(null, "02:AA:00:00:00:64"),
            Observation(serial: null, mac: "02:aa:00:00:00:64"));
        Assert.Equal(IdentityChange.Unchanged, result.Change);
    }

    [Fact]
    public void SameNameAndIp_DoesNotMerge()
    {
        var first = resolver.Resolve(null, Observation(id: "100"));
        var second = resolver.Resolve(null, Observation(id: "101"));
        Assert.Equal(IdentityChange.FirstGeneration, first.Change);
        Assert.Equal(IdentityChange.FirstGeneration, second.Change);
        Assert.Throws<ArgumentException>(() => resolver.Resolve(Identity(id: "100"), Observation(id: "101")));
    }

    [Fact]
    public void SerialCollision_DoesNotMergeDevices()
    {
        var observations = new[] { Observation(id: "100"), Observation(id: "101") };
        var conflicts = resolver.FindBatchConflicts(observations);
        Assert.Equal(new[] { "100", "101" }, conflicts.SerialCollisions[Serial]);
        Assert.Empty(conflicts.DuplicateApIds);
        Assert.All(observations, item => Assert.Equal(IdentityChange.FirstGeneration, resolver.Resolve(null, item).Change));
    }

    [Fact]
    public void BatchConflicts_ExcludeAllDuplicateIdsAndIgnoreInvalidSerials()
    {
        var invalid = Observation(id: "102");
        invalid = invalid with { Fields = invalid.Fields.SetItem(MonitoringFieldNames.SerialNumber,
            invalid.Fields[MonitoringFieldNames.SerialNumber] with { Validity = FieldValidity.Invalid }) };
        var result = resolver.FindBatchConflicts([Observation(), Observation(serial: "OTHER"), invalid]);
        Assert.Equal(new[] { "100" }, result.DuplicateApIds);
        Assert.Empty(result.SerialCollisions);
    }
}
