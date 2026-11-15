using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Enrichment;

namespace AirWaveApMonitor.Tests.Enrichment;

public sealed class PowerStateAndCompatibilityTests
{
    [Fact]
    public void ValidSupplyState_PreservesZeroCounterAndSeparateNegotiationSupport()
    {
        var result = PowerStatePolicy.Evaluate(State() with { ErrorCount = 0 });
        Assert.Equal(PowerSupplyState.On, result.SupplyState);
        Assert.Equal(PowerSupplyMethod.Poe, result.SupplyMethod);
        Assert.Equal(0L, result.ErrorCount);
        Assert.True(result.LldpNegotiationSupported);
    }

    [Fact]
    public void MissingSupplyOrCounter_IsUnknownNotOffOrZero()
    {
        var state = State() with { SupplyEvidence = Samples.Evidence with { Availability = FieldAvailability.NotProvided }, ErrorCount = null };
        var result = PowerStatePolicy.Evaluate(state);
        Assert.Equal(PowerSupplyState.Unknown, result.SupplyState);
        Assert.Null(result.ErrorCount);
        Assert.Equal(PowerSupplyMethod.Poe, result.SupplyMethod);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("invalid")]
    [InlineData("source")]
    public void InvalidErrorCounter_RemainsUnknown(string fault)
    {
        var state = State();
        state = fault switch
        {
            "negative" => state with { ErrorCount = -1 },
            "invalid" => state with { ErrorEvidence = Samples.Evidence with { Validity = FieldValidity.Invalid } },
            _ => state with { ErrorEvidence = Samples.Evidence with { Source = EvidenceSource.Demo } }
        };
        Assert.Null(PowerStatePolicy.Evaluate(state).ErrorCount);
    }

    [Fact]
    public void SupplyOnAndNegotiationSupport_DoNotReplaceNeighborEvidence()
    {
        var payload = new EnrichmentPayload([Links.Query(Links.Connection() with { Protocol = DiscoveryProtocol.Unknown })],
            [new(Guid.NewGuid(), 1, Samples.Profile, Links.Port, PortAdministrativeState.Up, PortOperationalState.Up, Samples.Evidence, Samples.Evidence)],
            [Power.Sample()], [State()]);
        Assert.True(PowerStatePolicy.Evaluate(payload.PowerStates[0]).LldpNegotiationSupported);
        Assert.Equal(PowerSupplyState.On, PowerStatePolicy.Evaluate(payload.PowerStates[0]).SupplyState);
        Assert.False(ConnectionEvidencePolicy.Evaluate(payload.Neighbors[0], Samples.Time).HasCurrentLldpObservation);
    }

    [Fact]
    public void OldBatchAndReceiptJson_DoNotPretendTypedDataOrDispositionExists()
    {
        const string batchJson = """{"Kind":1,"Outcome":0,"Observations":[],"ExcludedRecords":[],"Completeness":0,"CollectedAtUtc":"2026-11-15T12:00:00Z","Errors":[],"SchemaVersion":1}""";
        const string receiptJson = """{"DatasetId":"10000000-0000-0000-0000-000000000001","Kind":2,"RunId":9,"AlreadyApplied":false,"Applied":true,"Outcome":0,"Completeness":0,"Errors":[],"SchemaVersion":1}""";
        var batch = JsonSerializer.Deserialize<EnrichmentBatch>(batchJson)!;
        var receipt = JsonSerializer.Deserialize<EnrichmentCommitReceipt>(receiptJson)!;
        Assert.Equal(CollectionKind.ConnectionNeighbor, batch.Kind);
        Assert.Null(batch.Detail);
        Assert.Equal(CollectionKind.Power, receipt.Kind);
        Assert.Null(receipt.Disposition);
        Assert.True(receipt.Applied);
        const string healthJson = """{"RecordedAtUtc":"2026-11-15T12:00:00Z","Phase":1,"LastAttemptRunId":8,"LastFinalRunId":7}""";
        var health = JsonSerializer.Deserialize<CollectorHealthSnapshot>(healthJson)!;
        Assert.Null(health.EnrichmentKinds);
        Assert.Equal(8, health.LastAttemptRunId);
        Assert.Equal(7, health.LastFinalRunId);
    }

    [Fact]
    public void PerKindHealth_RetainsApAndEnrichmentClocksIndependently()
    {
        var health = new CollectorHealthSnapshot(Samples.Time, CollectorPhase.Idle,
            LastAttemptAtUtc: Samples.Time, LastAttemptRunId: 7, LastFinalRunId: 7)
        {
            EnrichmentKinds = [new(CollectionKind.Power, LastAttemptAtUtc: Samples.Time.AddMinutes(-1),
                LastAttemptRunId: 6, LastFinalRunId: 6, LastOutcome: RunOutcome.Failed,
                HasQualityGap: true, LastError: CollectionErrorCategory.Permission) { CapabilityReason = "Unverified" }]
        };
        var copy = JsonSerializer.Deserialize<CollectorHealthSnapshot>(JsonSerializer.Serialize(health))!;
        Assert.Equal(7, copy.LastAttemptRunId);
        Assert.Equal(Samples.Time, copy.LastAttemptAtUtc);
        var power = Assert.Single(copy.EnrichmentKinds!.Value);
        Assert.Equal(6, power.LastFinalRunId);
        Assert.Equal(Samples.Time.AddMinutes(-1), power.LastAttemptAtUtc);
        Assert.True(power.HasQualityGap);
        Assert.Null(power.NextAtUtc);
        Assert.Equal("Unverified", power.CapabilityReason);
    }

    [Fact]
    public void TypedPayload_RoundTripsOriginalClocksRawStateAndHistoricalBinding()
    {
        var batch = new EnrichmentBatch(CollectionKind.Power, RunOutcome.Succeeded, [], [],
            ObservationCompleteness.Complete, Samples.Time, [])
        {
            Detail = new([Links.Query(Power.Connection())], [], [Power.Sample()], [State()])
        };
        var copy = JsonSerializer.Deserialize<EnrichmentBatch>(JsonSerializer.Serialize(batch))!;
        Assert.Equal(Power.Target(), copy.Detail!.Neighbors[0].Neighbors[0].Target);
        Assert.Equal("source-on", copy.Detail.PowerStates[0].RawSupplyState);
        Assert.Equal(Samples.Time, copy.Detail.PowerSamples[0].Timing.SourceAtUtc);
        Assert.Equal(PowerMetric.PortActual, copy.Detail.PowerSamples[0].Metric);
    }

    private static PowerStateObservation State() => new(Guid.Parse("90000000-0000-0000-0000-000000000001"), 2,
        Samples.Profile, Links.Port, PowerSupplyState.On, "source-on", PowerSupplyMethod.Poe, "802.3at", 4, true,
        Samples.Evidence, Samples.Evidence, Samples.Evidence, Samples.Evidence);

    [Fact]
    public void OptionalPowerStateArray_SerializesAsEmpty()
    {
        var copy = JsonSerializer.Deserialize<EnrichmentPayload>(JsonSerializer.Serialize(new EnrichmentPayload([], [], [])))!;
        Assert.Empty(copy.PowerStates);
    }

    [Fact]
    public void OldTargetJson_MissingIdentityWindowRemainsUnknown()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Power.Target()))!.AsObject();
        json.Remove("IdentityWindow");
        var old = JsonSerializer.Deserialize<EnrichmentTargetBinding>(json.ToJsonString())!;
        Assert.Null(old.IdentityWindow);
        var sample = Power.Sample() with { Metric = PowerMetric.ApConsumption, Endpoint = Links.Ap, DirectTarget = old };
        Assert.Null(PowerAttributionPolicy.Resolve(sample, []).Target);
    }

    [Fact]
    public void PortAdministrativeAndOperationalStates_KeepIndependentValidity()
    {
        var port = new PortStateObservation(Guid.NewGuid(), 1, Samples.Profile, Links.Port,
            PortAdministrativeState.Down, PortOperationalState.Up, Samples.Evidence,
            Samples.Evidence with { Availability = FieldAvailability.NotProvided });
        var result = PortStatePolicy.Evaluate(port);
        Assert.Equal(PortAdministrativeState.Down, result.AdministrativeState);
        Assert.Equal(PortOperationalState.Unknown, result.OperationalState);
        result = PortStatePolicy.Evaluate(port with { AdministrativeEvidence = Samples.Evidence with { Validity = FieldValidity.Invalid }, OperationalEvidence = Samples.Evidence });
        Assert.Equal(PortAdministrativeState.Unknown, result.AdministrativeState);
        Assert.Equal(PortOperationalState.Up, result.OperationalState);
    }

    [Theory]
    [InlineData(PortOperationalState.Dormant)]
    [InlineData(PortOperationalState.NotPresent)]
    [InlineData(PortOperationalState.LowerLayerDown)]
    public void DistinctPortStates_AreNotCollapsedToDown(PortOperationalState state)
    {
        var port = new PortStateObservation(Guid.NewGuid(), 1, Samples.Profile, Links.Port,
            PortAdministrativeState.Up, state, Samples.Evidence, Samples.Evidence);
        Assert.Equal(state, PortStatePolicy.Evaluate(port).OperationalState);
    }
}
