using System.Text;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
namespace AirWaveApMonitor.Tests.Enrichment;
public sealed class DocumentedInterfaceParserTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-16T00:00:00Z");
    private static readonly ProfileSnapshot Profile = new(Guid.NewGuid(), Guid.NewGuid(), new Uri("https://example.invalid"), 1, 1, [], BaselineStatus.Unverified, new());
    [Fact]
    public void OfficialShape_PreservesIndependentStatesAndPortIndex_WithoutInventingLldpOrPower()
    {
        using var xml = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures/ap-detail-interface-documented.xml"));
        var result = new DocumentedInterfaceParser().Parse(xml, new(17, Profile, CollectionKind.ConnectionNeighbor, RunTrigger.Manual, At, At.AddMinutes(2)), At);
        var row = Assert.Single(result.Interfaces);
        Assert.Equal("79", row.State.Endpoint.SourceDeviceId);
        Assert.Equal("88", row.State.Endpoint.SourceInterfaceId);
        Assert.Equal("34", row.PortIndex);
        Assert.Equal(EndpointRole.Other, row.State.Endpoint.Role);
        Assert.Equal(PortAdministrativeState.Up, row.State.AdministrativeState);
        Assert.Equal(PortOperationalState.Dormant, row.State.OperationalState);
        Assert.Null(row.Timing.SourceAtUtc);
        Assert.False(row.Timing.MeaningVerified);
        Assert.Equal(ObservationCompleteness.Unknown, result.Completeness);
        Assert.Empty(result.Errors);
    }
    [Theory]
    [InlineData("<html/>")]
    [InlineData("<!DOCTYPE r [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><r>&x;</r>")]
    [InlineData("<amp:amp_ap_detail xmlns:amp='http://www.airwave.com'><ap id='1'><interface id='2'/><interface id='2'/></ap></amp:amp_ap_detail>")]
    public void InvalidOrDuplicateIdentity_IsRejected(string input)
    {
        using var xml = new MemoryStream(Encoding.UTF8.GetBytes(input));
        var result = new DocumentedInterfaceParser().Parse(xml, new(17, Profile, CollectionKind.ConnectionNeighbor, RunTrigger.Manual, At, At.AddMinutes(2)), At);
        Assert.Empty(result.Interfaces);
        Assert.NotEmpty(result.Errors);
    }
    [Fact]
    public void InvalidAdmin_DoesNotDiscardValidOper()
    {
        using var xml = new MemoryStream(Encoding.UTF8.GetBytes("<amp:amp_ap_detail xmlns:amp='http://www.airwave.com'><ap id='1'><interface id='2'><admin_status>bad</admin_status><oper_status>LowerLayerDown</oper_status></interface></ap></amp:amp_ap_detail>"));
        var row = Assert.Single(new DocumentedInterfaceParser().Parse(xml, new(17, Profile, CollectionKind.ConnectionNeighbor, RunTrigger.Manual, At, At.AddMinutes(2)), At).Interfaces);
        Assert.Equal(PortAdministrativeState.Unknown, row.State.AdministrativeState);
        Assert.Equal(FieldValidity.Invalid, row.State.AdministrativeEvidence.Validity);
        Assert.Equal(PortOperationalState.LowerLayerDown, row.State.OperationalState);
        Assert.Equal(FieldValidity.Valid, row.State.OperationalEvidence.Validity);
    }    [Fact]
    public void Cancellation_DoesNotReturnPartialOrFabricatedSuccess()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); using var xml = new MemoryStream();
        Assert.Throws<OperationCanceledException>(() => new DocumentedInterfaceParser().Parse(xml,
            new(17, Profile, CollectionKind.ConnectionNeighbor, RunTrigger.Manual, At, At.AddMinutes(2)), At, cancel.Token));
    }
    [Fact]
    public void DecompressedResponseOver64MiB_IsRejected()
    {
        using var xml = new RepeatedXmlStream();
        var result = new DocumentedInterfaceParser().Parse(xml,
            new(17, Profile, CollectionKind.ConnectionNeighbor, RunTrigger.Manual, At, At.AddMinutes(2)), At);
        Assert.Empty(result.Interfaces); Assert.Contains(result.Errors, e => e.Category == CollectionErrorCategory.ResponseTooLarge);
    }
    private sealed class RepeatedXmlStream : Stream
    {
        private readonly byte[] prefix = Encoding.UTF8.GetBytes("<amp:amp_ap_detail xmlns:amp='http://www.airwave.com'><ap id='1'><name>");
        private long read;
        public override int Read(byte[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++, read++) buffer[offset + i] = read < prefix.Length ? prefix[read] : (byte)'x';
            return count;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => read; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

}
