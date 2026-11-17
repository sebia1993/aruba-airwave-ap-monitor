using System.Text;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
namespace AirWaveApMonitor.Tests.AirWave;

public sealed class ApListParserTests
{
    internal const string Prefix = "<amp:amp_ap_list xmlns:amp='http://www.airwave.com' version='1'>";
    internal static string Ap(string id = "7", string state = "false", string category = "thin_ap", string extra = "") => $"<ap id='{id}'><device_category>{category}</device_category><is_up>{state}</is_up><name>가상 AP</name><lan_ip>192.0.2.7</lan_ip><lan_mac>00:11:22:33:44:55</lan_mac><serial_number>TEST-7</serial_number>{extra}</ap>";
    internal static string Xml(string aps) => Prefix + aps + "</amp:amp_ap_list>";
    internal static ApCollectionBatch Parse(string xml) => new ApListParser().Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), DateTimeOffset.Parse("2026-11-15T01:00:00Z"));
    [Fact]
    public void DocumentedShape_ParsesStatusAndIdentityWithoutInventingTimes()
    {
        var batch = Parse(Xml(Ap(extra: "<last_contacted>1527739948</last_contacted><last_reboot>0</last_reboot><radio><name>wrong</name></radio>")));
        Assert.Equal(RunOutcome.Succeeded, batch.Outcome);
        var ap = Assert.Single(batch.Observations);
        Assert.Equal(ApState.Down, ap.State);
        Assert.Equal("가상 AP", ap.Device.Name);
        Assert.Null(ap.SourceAtUtc);
        Assert.Null(ap.LastContactedAtUtc);
        Assert.Null(ap.Device.RebootedAtUtc);
        Assert.Equal(FieldAvailability.Unverified, ap.Fields["last_contacted"].Availability);
    }
    [Theory]
    [InlineData("<html><form>Login</form></html>")]
    [InlineData("<amp_ap_list/>")]
    [InlineData("<x:amp_ap_list xmlns:x='urn:wrong'/>")]
    [InlineData("<broken>")]
    public void InvalidRoot_IsFailure(string xml)
    {
        var b = Parse(xml);
        Assert.Equal(RunOutcome.Failed, b.Outcome);
        Assert.Empty(b.Observations);
        Assert.Contains(b.Errors, e => e.Category == CollectionErrorCategory.InvalidResponse);
    }
    [Fact]
    public void DuplicateIds_AreExcluded()
    {
        var b = Parse(Xml(Ap() + Ap(extra: "<name>changed</name>") + Ap("8", "true")));
        Assert.Equal(RunOutcome.Partial, b.Outcome);
        Assert.Equal("8", Assert.Single(b.Observations).SourceApId);
        Assert.Equal(2, b.ExcludedRecords.Length);
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("1")]
    public void UnknownState_IsNotDown(string value)
    {
        var b = Parse(Xml(Ap(state: value)));
        Assert.Null(Assert.Single(b.Observations).State);
        Assert.Equal(RunOutcome.Partial, b.Outcome);
    }
    [Theory]
    [InlineData("controller")]
    [InlineData("switch")]
    [InlineData("unseen")]
    public void UnknownCategory_IsExcludedAndUnverified(string category)
    {
        var b = Parse(Xml(Ap(category: category)));
        Assert.Empty(b.Observations);
        Assert.NotEqual(RunOutcome.Succeeded, b.Outcome);
        Assert.Single(b.ExcludedRecords);
    }
    [Fact]
    public void ConflictingCategoryFields_ExcludeEntireRecord()
    {
        var result = Parse(Xml(Ap(extra: "<device_category>controller</device_category>")));
        Assert.Empty(result.Observations);
        Assert.Single(result.ExcludedRecords);
    }
    [Fact]
    public void Dtd_IsFailure()
    {
        var b = Parse("<!DOCTYPE amp_ap_list [<!ENTITY secret SYSTEM 'file:///etc/passwd'>]>" + Xml(Ap(extra: "<syslocation>&secret;</syslocation>")));
        Assert.Equal(RunOutcome.Failed, b.Outcome);
        Assert.Empty(b.Observations);
    }
    [Fact]
    public void Oversize_IsFailure()
    {
        var b = Parse(Xml(Ap(extra: "<name>" + new string('x', 64 * 1024 * 1024) + "</name>")));
        Assert.Equal(RunOutcome.Failed, b.Outcome);
        Assert.Contains(b.Errors, e => e.Category == CollectionErrorCategory.ResponseTooLarge);
    }
    [Fact]
    public void EmptyList_RequiresVerification()
    {
        var b = Parse(Xml(""));
        Assert.Equal(RunOutcome.Partial, b.Outcome);
        Assert.Equal(ObservationCompleteness.Unknown, b.Completeness);
    }
    [Fact]
    public void DeclaredLatin1_IsDecodedFromBytes()
    {
        var xml = "<?xml version='1.0' encoding='ISO-8859-1'?>" + Xml(Ap(extra: "<syslocation>café</syslocation>"));
        var b = new ApListParser().Parse(new MemoryStream(Encoding.Latin1.GetBytes(xml)), DateTimeOffset.UtcNow);
        Assert.Equal("café", Assert.Single(b.Observations).Location.AirWaveRawLocation);
    }
    [Fact]
    public void OfficialExample_UsesDocumentedFields()
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ap-list-official-example.xml"));
        var b = new ApListParser().Parse(file, DateTimeOffset.UtcNow);
        Assert.NotEqual(RunOutcome.Failed, b.Outcome);
        Assert.NotEmpty(b.Observations);
    }
}
