using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Enrichment;

namespace AirWaveApMonitor.Tests.Enrichment;

public sealed class EnrichmentGuardTests
{
    [Theory]
    [InlineData("dataset")]
    [InlineData("version")]
    [InlineData("origin")]
    [InlineData("source")]
    [InlineData("field")]
    public void MalformedCapabilityBinding_CannotEnableCollection(string fault)
    {
        var key = Samples.Key();
        key = fault switch
        {
            "dataset" => key with { Profile = key.Profile with { DatasetId = Guid.Empty } },
            "version" => key with { Profile = key.Profile with { CredentialsVersion = 0 } },
            "origin" => key with { Profile = key.Profile with { Origin = new Uri("https://user:secret@airwave.invalid/") } },
            "source" => key with { Profile = key.Profile with { Source = EvidenceSource.Manual } },
            _ => key with { Field = " " }
        };
        var result = CapabilityStatus.Evaluate(Samples.Capability() with { Key = key }, key, true);
        Assert.False(result.CanCollect);
        Assert.Equal(CapabilityAvailability.Unverified, result.Availability);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("remote")]
    [InlineData("rowId")]
    public void UnidentifiedNeighbor_CannotBeCurrentLldp(string fault)
    {
        var query = Links.Query(Links.Connection());
        if (fault == "profile")
        {
            var profile = Samples.Profile with { DatasetId = Guid.Empty };
            query = query with { Scope = query.Scope with { Profile = profile }, Neighbors = [query.Neighbors[0] with { Profile = profile }] };
        }
        else
        {
            var row = query.Neighbors[0];
            row = fault == "rowId" ? row with { Id = Guid.Empty } : row with { Remote = row.Remote with { SourceDeviceId = " " } };
            query = query with { Scope = query.Scope with { ExpectedRemote = row.Remote }, Neighbors = [row] };
        }
        Assert.False(ConnectionEvidencePolicy.Evaluate(query, Samples.Time).HasCurrentLldpObservation);
    }

    [Fact]
    public void UnknownArray_IsNotAConfirmedEmptyResponse()
    {
        var result = ConnectionEvidencePolicy.Evaluate(Links.Query() with { Neighbors = default }, Samples.Time);
        Assert.False(result.MayClosePreviousConnection);
    }

    [Fact]
    public void UnidentifiedSourceSample_DoesNotProduceDedupKey()
    {
        var sample = Power.Sample() with { SourceIdentity = new("verified", true), Endpoint = Links.Port with { SourceDeviceId = "" } };
        Assert.Null(PowerSampleIdentityPolicy.GetKey(sample));
    }

    [Fact]
    public void UnidentifiedConnection_CannotAttributePower()
        => Assert.Null(PowerAttributionPolicy.Resolve(Power.Sample(), [Power.Connection() with { Id = Guid.Empty }]).Target);

    [Fact]
    public void InvalidPlacementPointVersion_IsNotAttributed()
    {
        var row = Power.Connection();
        row = row with { Target = row.Target! with { Placement = Power.Placement() with { PointVersion = 0 } } };
        Assert.Null(PowerAttributionPolicy.Resolve(Power.Sample(), [row]).Target);
    }

    [Fact]
    public void SameApTwoDirections_SelectsStableEvidenceWithoutAmbiguity()
    {
        var first = Power.Connection();
        var reverse = first with
        {
            Id = Guid.Parse("30000000-0000-0000-0000-000000000002"),
            Local = first.Remote,
            Remote = first.Local,
            Direction = ObservationDirection.RemoteToLocal
        };
        var result = PowerAttributionPolicy.Resolve(Power.Sample(), [reverse, first]);
        Assert.Equal(PowerAttributionStatus.Attributed, result.Status);
        Assert.Equal(first.Id, result.Connection!.Id);
    }

    [Fact]
    public void MultipleDifferentPorts_DoNotTransferTheOtherPortsAp()
    {
        var right = Power.Connection();
        var other = right with
        {
            Id = Guid.NewGuid(),
            Local = right.Local with { SourceInterfaceId = "35" },
            Target = right.Target! with { ApId = Guid.NewGuid() }
        };
        Assert.Equal(right.Target, PowerAttributionPolicy.Resolve(Power.Sample(), [other, right]).Target);
    }
}
