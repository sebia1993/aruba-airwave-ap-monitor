using System.Collections.Immutable;
using System.Text;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Operations;
using static AirWaveApMonitor.Tests.Operations.OperationsFixture;
namespace AirWaveApMonitor.Tests.Operations;
public sealed class ManualImportValidatorTests
{
    private readonly ManualImportValidator validator = new();
    private ImportPreview Preview(string csv, ImportMapping? mapping = null) => validator.Preview(Encoding.UTF8.GetBytes(csv), mapping ?? Mapping(Inventory()));
    [Fact]
    public void UniqueSerialAndMacAndApId_AllAgree()
    {
        var row = Assert.Single(Preview($"ap_id,serial_number,wired_mac,name\r\n{Ap}, S1 ,00:11:22:33:44:55,new name\r\n").Rows);
        Assert.True(row.IsValid); Assert.Equal(Ap, row.Target!.ApId); Assert.Equal(Generation, row.Target.DeviceGenerationId);
        Assert.Equal("new name", row.Mutation.SetValues[InventoryField.Name]);
    }
    [Fact]
    public void Utf8BomQuotedCommaQuoteAndNewline_ArePreserved()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("serial_number,name,notes\r\nS1,\"건물, AP\",\"첫 줄\r\n두 번째 \"\"인용\"\"\"\r\n")).ToArray();
        var row = Assert.Single(validator.Preview(bytes, Mapping(Inventory())).Rows);
        Assert.True(row.IsValid); Assert.Equal("건물, AP", row.Mutation.SetValues[InventoryField.Name]);
        Assert.Equal("첫 줄\r\n두 번째 \"인용\"", row.Mutation.SetValues[InventoryField.Notes]);
    }
    [Theory]
    [InlineData("serial_number,name\nS1,\"unclosed")]
    [InlineData("serial_number,name\nS1,a\"b")]
    [InlineData("serial_number,name\nS1,\"a\"tail")]
    [InlineData("serial_number,serial_number\nS1,S1")]
    [InlineData("serial_number,state\nS1,Up")]
    [InlineData("serial_number,lldp\nS1,healthy")]
    [InlineData("serial_number,power\nS1,0")]
    [InlineData("serial_number,unknown\nS1,value")]
    public void InvalidDocument_IsRejectedWithoutPartialRows(string csv)
    {
        var preview = Preview(csv); Assert.False(preview.IsValidDocument); Assert.Empty(preview.Rows);
    }
    [Fact]
    public void MalformedUtf8_IsRejected()
    {
        var preview = validator.Preview(new byte[] { 0xC3, 0x28 }, Mapping(Inventory()));
        Assert.False(preview.IsValidDocument); Assert.Contains(preview.Errors, e => e.Code == "invalid_utf8");
    }
    [Fact]
    public void ByteRowAndCellLimits_RejectRatherThanTruncate()
    {
        var input = "serial_number,name\nS1,12345";
        Assert.False(Preview(input, Mapping(Inventory()) with { Limits = new(MaxBytes: 5) }).IsValidDocument);
        Assert.False(Preview(input, Mapping(Inventory()) with { Limits = new(MaxCellCharacters: 4) }).IsValidDocument);
        Assert.False(Preview("serial_number,name\nS1,a\nS2,b", Mapping(Inventory()) with { Limits = new(MaxRows: 1) }).IsValidDocument);
    }
    [Fact]
    public void AmbiguousSerialCsvRow_IsRejected()
    {
        var other = Inventory(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "S1", "00:11:22:33:44:66");
        var row = Assert.Single(Preview($"ap_id,serial_number,name\n{Ap},S1,new", Mapping(Inventory(), other)).Rows);
        Assert.False(row.IsValid); Assert.Contains(row.Errors, e => e.Code == "ambiguous_identity");
    }
    [Fact]
    public void ConflictingSelectors_AreNotResolvedByFirstMatch()
    {
        var other = Inventory(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), serial: "S2", mac: "00:11:22:33:44:66");
        var row = Assert.Single(Preview("serial_number,wired_mac,name\nS1,00:11:22:33:44:66,new", Mapping(Inventory(), other)).Rows);
        Assert.False(row.IsValid); Assert.Contains(row.Errors, e => e.Code == "conflicting_identity");
    }
    [Fact]
    public void NameOrIpAlone_NeverIdentifiesAnAp()
    {
        var row = Assert.Single(Preview("name,ip_address\nAP 1,192.0.2.1").Rows);
        Assert.False(row.IsValid); Assert.Contains(row.Errors, e => e.Code == "identity_required");
    }
    [Fact]
    public void DuplicateRows_AllRemainInvalid()
    {
        var rows = Preview($"ap_id,serial_number,name\n{Ap},,one\n,S1,two").Rows;
        Assert.Equal(2, rows.Length); Assert.All(rows, r => { Assert.False(r.IsValid); Assert.Contains(r.Errors, e => e.Code == "duplicate_target"); });
    }
    [Fact]
    public void UnknownAndOtherDatasetAndInvalidEvidence_AreNotMatched()
    {
        var foreign = Inventory() with { DatasetId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa") };
        var invalid = Inventory() with { IdentityEvidence = Fields("serial_number").SetItem("serial_number", Field() with { Validity = FieldValidity.Invalid }) };
        Assert.False(Assert.Single(Preview("serial_number,name\nS1,new", Mapping(foreign)).Rows).IsValid);
        Assert.False(Assert.Single(Preview("serial_number,name\nS1,new", Mapping(invalid)).Rows).IsValid);
        Assert.False(Assert.Single(Preview("serial_number,name\nmissing,new").Rows).IsValid);
    }
    [Fact]
    public void EmptyCellsMeanNoChange_ExplicitClearNeedsReason()
    {
        var entry = Inventory() with { Inventory = new(4, Generation, EvidenceSource.Manual, At,
            ImmutableDictionary<InventoryField, string>.Empty.Add(InventoryField.Name, "saved"), [InventoryField.Name]) };
        var blank = Assert.Single(Preview("serial_number,name\nS1,", Mapping(entry)).Rows);
        Assert.True(blank.IsValid); Assert.False(blank.HasChanges); Assert.Empty(blank.Mutation.ClearFields);
        var denied = Assert.Single(Preview("serial_number,clear_fields\nS1,name", Mapping(entry)).Rows);
        Assert.False(denied.IsValid);
        var cleared = Assert.Single(Preview("serial_number,clear_fields,clear_reason\nS1,name,outdated", Mapping(entry)).Rows);
        Assert.True(cleared.IsValid); Assert.Contains(InventoryField.Name, cleared.Mutation.ClearFields);
        var diff = Assert.Single(cleared.Differences, d => d.Field == "name"); Assert.Equal("saved", diff.BeforeValue); Assert.Null(diff.AfterValue); Assert.True(diff.IsExplicitClear);
    }
    [Fact]
    public void SetAndClearSameField_IsRejected()
    {
        var row = Assert.Single(Preview("serial_number,name,clear_fields,clear_reason\nS1,new,name,reason").Rows);
        Assert.False(row.IsValid); Assert.Contains(row.Errors, e => e.Code == "set_clear_conflict");
    }
    [Fact]
    public void ManualPreferenceAndTextPayload_AreInertAndSeparate()
    {
        var row = Assert.Single(Preview("serial_number,name,notes,manual_preferred_fields\nS1,=1+1,<script>alert(1)</script>,name").Rows);
        Assert.True(row.IsValid); Assert.Equal("=1+1", row.Mutation.SetValues[InventoryField.Name]);
        Assert.Equal("<script>alert(1)</script>", row.Mutation.SetValues[InventoryField.Notes]);
        Assert.Contains(InventoryField.Name, row.Mutation.ManualPreferredFields!);
        Assert.Equal("S1", row.Selector.SerialNumber);
    }
    [Fact]
    public void PointMustAlreadyExist_AndItsVersionIsFrozen()
    {
        var point = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var csv = $"serial_number,installation_point_id\nS1,{point}";
        Assert.False(Assert.Single(Preview(csv).Rows).IsValid);
        var mapping = Mapping(Inventory()) with { InstallationPoints = [new(point, 7, "P1")] };
        var row = Assert.Single(Preview(csv, mapping).Rows);
        Assert.True(row.IsValid); Assert.Equal(point, row.Target!.RequestedPointId); Assert.Equal(7, row.Target.RequestedPointVersion);
    }
    [Theory]
    [InlineData("MonitoringExcluded", "", false)]
    [InlineData("Decommissioned", "retired", true)]
    [InlineData("PlannedWork", "planned", true)]
    [InlineData("1", "reason", false)]
    [InlineData("Up", "reason", false)]
    public void ManagementIsManualPolicy_NotAnAutomaticState(string status, string reason, bool valid)
    {
        var row = Assert.Single(Preview($"serial_number,management_status,management_reason\nS1,{status},{reason}").Rows);
        Assert.Equal(valid, row.IsValid);
    }
    [Fact]
    public void Revalidate_RejectsAllBindingChangesAndRestoreEpoch()
    {
        var mapping = Mapping(Inventory()); var preview = Preview("serial_number,name\nS1,new", mapping);
        Assert.Empty(validator.Revalidate(preview, [1], mapping));
        Assert.NotEmpty(validator.Revalidate(preview, [1], mapping with { Version = mapping.Version with { ScopeVersion = 4 } }));
        Assert.NotEmpty(validator.Revalidate(preview, [1], mapping with { Version = mapping.Version with { ConfigVersion = 6 } }));
        Assert.NotEmpty(validator.Revalidate(preview, [1], mapping with { Version = mapping.Version with { RestoreEpoch = 1 } }));
        Assert.NotEmpty(validator.Revalidate(preview, [1], mapping with { Inventory = [Inventory(generation: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))] }));
        var revised = Inventory() with { Inventory = new(2, Generation, EvidenceSource.Manual, At, [], []) };
        Assert.NotEmpty(validator.Revalidate(preview, [1], mapping with { Inventory = [revised] }));
    }
    [Fact]
    public void Revalidate_RejectsNewIdentityAmbiguityAndPointChange()
    {
        var point = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var mapping = Mapping(Inventory()) with { InstallationPoints = [new(point, 1, "P1")] };
        var preview = Preview($"serial_number,installation_point_id\nS1,{point}", mapping);
        Assert.NotEmpty(validator.Revalidate(preview, [1], mapping with { InstallationPoints = [new(point, 2, "renamed")] }));
        Assert.NotEmpty(validator.Revalidate(preview, [1], mapping with { Inventory = [Inventory(), Inventory(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))] }));
    }
    [Fact]
    public void Revalidate_AllowsOnlyExplicitValidSubset()
    {
        var mapping = Mapping(Inventory()); var preview = Preview("serial_number,name\nS1,new\nunknown,bad", mapping);
        Assert.Empty(validator.Revalidate(preview, [1], mapping));
        Assert.NotEmpty(validator.Revalidate(preview, [1, 2], mapping));
        Assert.NotEmpty(validator.Revalidate(preview, [1, 1], mapping));
        Assert.NotEmpty(validator.Revalidate(preview, [99], mapping));
        Assert.NotEmpty(validator.Revalidate(preview, [], mapping));
    }
    [Fact]
    public void Revalidate_CurrentPointRegistrationChangeInvalidatesUnrelatedInventoryPreview()
    {
        var point = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var placement = new PlacementPeriod(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), Dataset, Ap, Generation, point, 1, At, null, EvidenceSource.Manual);
        var mapping = Mapping(Inventory() with { Placement = placement }) with { InstallationPoints = [new(point, 2, "P1")] };
        var preview = Preview("serial_number,name\nS1,new", mapping);
        Assert.Equal(2, Assert.Single(preview.Rows).Target!.CurrentPointVersion);
        Assert.NotEmpty(validator.Revalidate(preview, [1], mapping with { InstallationPoints = [new(point, 3, "new point version")] }));
    }
    [Theory]
    [InlineData("serial_number,wired_mac,name\nS1,00-11-22-33-44-55,new")]
    [InlineData("serial_number,ip_address\nS1,not-an-ip")]
    [InlineData("serial_number,clear_fields,clear_reason\nS1,state,reason")]
    [InlineData("serial_number,manual_preferred_fields\nS1,power")]
    public void InvalidManualFieldsOrUnverifiedMacFormat_AreRejected(string csv)
    {
        Assert.False(Assert.Single(Preview(csv).Rows).IsValid);
    }
    [Fact]
    public void ApplyValues_PreservesAbsentFieldsAndOldSnapshot()
    {
        var previous = new ManualInventorySnapshot(2, Generation, EvidenceSource.Manual, At,
            ImmutableDictionary<InventoryField, string>.Empty.Add(InventoryField.Name, "old").Add(InventoryField.Site, "site"), []);
        var mutation = new ImportMutation(ImmutableDictionary<InventoryField, string>.Empty.Add(InventoryField.Name, "new"), [], null, null, null, false, null, null);
        var next = ManualImportValidator.ApplyValues(previous, mutation);
        Assert.Equal("site", next[InventoryField.Site]); Assert.Equal("new", next[InventoryField.Name]);
        Assert.Equal("old", previous.Values[InventoryField.Name]);
    }
    [Fact]
    public void DirectMutation_RejectsInvalidPlacementAndUnknownEnums()
    {
        var badPoint = new ImportMutation([], [], null, null, null, false, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), null);
        Assert.NotEmpty(ManualImportValidator.ValidateMutation(badPoint));
        Assert.NotEmpty(ManualImportValidator.ValidateMutation(badPoint with { ChangesPlacement = true, InstallationPointId = Guid.Empty }));
        Assert.NotEmpty(ManualImportValidator.ValidateMutation(badPoint with { InstallationPointId = null, ClearFields = [(InventoryField)999], ClearReason = "reason" }));
    }

    [Fact]
    public void CustomCellLimit_IsConsistentThroughPreviewRevalidationAndValueApplication()
    {
        var limits = new ImportLimits(MaxCellCharacters: 5000);
        var mapping = Mapping(Inventory()) with { Limits = limits };
        var preview = Preview("serial_number,notes\nS1," + new string('n', 4500), mapping);
        var row = Assert.Single(preview.Rows);
        Assert.True(row.IsValid);
        Assert.Empty(validator.Revalidate(preview, [1], mapping));
        Assert.Equal(4500, ManualImportValidator.ApplyValues(null, row.Mutation, limits)[InventoryField.Notes].Length);
        Assert.NotEmpty(validator.Revalidate(preview, [1], mapping with { Limits = new() }));
    }
    [Fact]
    public void FiveThousandRows_ArePreservedAndTheNextIsRejected()
    {
        var entries = Enumerable.Range(1, 5000).Select(i => Inventory(new Guid(i, 0, 0, new byte[8]), serial: "S" + i)).ToArray();
        var csv = "serial_number,name\n" + string.Join('\n', Enumerable.Range(1, 5000).Select(i => "S" + i + ",name " + i));
        var mapping = Mapping(entries);
        var preview = Preview(csv, mapping);
        Assert.True(preview.IsValidDocument); Assert.Equal(5000, preview.Rows.Length);
        Assert.All(preview.Rows, row => Assert.True(row.IsValid));
        Assert.False(Preview(csv + "\nS5001,too many", mapping).IsValidDocument);
    }

}
