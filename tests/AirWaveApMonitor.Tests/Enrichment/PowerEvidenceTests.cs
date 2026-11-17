using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Enrichment;

namespace AirWaveApMonitor.Tests.Enrichment;

public sealed class PowerEvidenceTests
{
    [Fact]
    public void MissingPower_IsNotZero()
    {
        Assert.Null(PowerValuePolicy.Normalize(Power.Sample() with { Value = null }).Watts);
        Assert.Equal(0m, PowerValuePolicy.Normalize(Power.Sample() with { Value = 0 }).Watts);
    }

    [Theory]
    [InlineData("W", 12.5, 12.5)]
    [InlineData("mW", 12500, 12.5)]
    public void VerifiedPowerUnit_NormalizesWithoutLosingMeaning(string unit, double value, double watts)
    {
        var sample = Power.Sample() with { Unit = unit, Value = (decimal)value };
        Assert.Equal((decimal)watts, PowerValuePolicy.Normalize(sample).Watts);
    }

    [Theory]
    [InlineData("dBm")]
    [InlineData("Wh")]
    [InlineData("kWh")]
    public void RfAndEnergy_AreNotPoePower(string unit)
    {
        var result = PowerValuePolicy.Normalize(Power.Sample() with { Unit = unit });
        Assert.Null(result.Watts);
        Assert.Equal(PowerValueReason.DifferentQuantity, result.Reason);
    }

    [Theory]
    [InlineData("unverified")]
    [InlineData("unit")]
    [InlineData("negative")]
    [InlineData("field")]
    [InlineData("source")]
    public void InvalidMeasurement_IsNotAUsableNumber(string fault)
    {
        var sample = Power.Sample();
        sample = fault switch
        {
            "unverified" => sample with { UnitVerified = false },
            "unit" => sample with { Unit = "unknown" },
            "negative" => sample with { Value = -1 },
            "field" => sample with { Evidence = sample.Evidence with { Availability = FieldAvailability.NotProvided } },
            _ => sample with { Evidence = sample.Evidence with { Source = EvidenceSource.Demo } }
        };
        Assert.Null(PowerValuePolicy.Normalize(sample).Watts);
    }

    [Fact]
    public void EqualValueOrTime_IsNotDeduplicationEvidence()
    {
        Assert.Null(PowerSampleIdentityPolicy.GetKey(Power.Sample()));
        Assert.Null(PowerSampleIdentityPolicy.GetKey(Power.Sample() with { SourceIdentity = new("tick", false) }));
    }

    [Fact]
    public void VerifiedUniqueSourceIdentity_DeduplicatesOnlySameMetricEndpointAndProfile()
    {
        var sample = Power.Sample() with { SourceIdentity = new("sample-1", true) };
        var key = PowerSampleIdentityPolicy.GetKey(sample);
        Assert.NotNull(key);
        Assert.Equal(key, PowerSampleIdentityPolicy.GetKey(sample with { Id = Guid.NewGuid(), RunId = 9, Timing = sample.Timing with { ReadAtUtc = Samples.Time.AddMinutes(5) } }));
        Assert.NotEqual(key, PowerSampleIdentityPolicy.GetKey(sample with { Metric = PowerMetric.Allocated }));
        Assert.NotEqual(key, PowerSampleIdentityPolicy.GetKey(sample with { Endpoint = sample.Endpoint with { SourceInterfaceId = "35" } }));
        Assert.NotEqual(key, PowerSampleIdentityPolicy.GetKey(sample with { Profile = sample.Profile with { ScopeVersion = 2 } }));
    }

    [Fact]
    public void PowerSample_UsesHistoricalConnection()
    {
        var old = Power.Connection();
        var current = old with
        {
            Id = Guid.NewGuid(),
            Timing = Samples.Timing with { SourceAtUtc = Samples.Time.AddMinutes(1), ReadAtUtc = Samples.Time.AddMinutes(1) },
            Window = new(Samples.Time.AddMinutes(1), null, true),
            Target = old.Target! with { DeviceGenerationId = Guid.NewGuid() }
        };
        var sample = Power.Sample() with { Timing = Power.Sample().Timing with { ReadAtUtc = Samples.Time.AddHours(1) } };
        var result = PowerAttributionPolicy.Resolve(sample, [current, old]);
        Assert.Equal(PowerAttributionStatus.Attributed, result.Status);
        Assert.Equal(Power.Target(), result.Target);
        Assert.Equal(old, result.Connection);
        Assert.Equal(Samples.Time, result.MeasuredAtUtc);
    }

    [Fact]
    public void DifferentValidHistoricalTargets_AreAmbiguous()
    {
        var old = Power.Connection();
        var conflict = old with { Id = Guid.NewGuid(), Target = old.Target! with { ApId = Guid.NewGuid() } };
        var result = PowerAttributionPolicy.Resolve(Power.Sample(), [old, conflict]);
        Assert.Equal(PowerAttributionStatus.Ambiguous, result.Status);
        Assert.Null(result.Target);
    }

    [Theory]
    [InlineData("timestamp")]
    [InlineData("meaning")]
    [InlineData("future")]
    public void UnknownMeasurementTime_DoesNotUseCurrentAp(string fault)
    {
        var timing = Power.Sample().Timing;
        timing = fault switch
        {
            "timestamp" => timing with { SourceAtUtc = null },
            "meaning" => timing with { Meaning = SourceTimeMeaning.LastObserved },
            _ => timing with { SourceAtUtc = Samples.Time.AddMinutes(1) }
        };
        var result = PowerAttributionPolicy.Resolve(Power.Sample() with { Timing = timing }, [Power.Connection()]);
        Assert.Equal(PowerAttributionStatus.Unknown, result.Status);
        Assert.Null(result.Target);
        Assert.Null(result.MeasuredAtUtc);
    }

    [Theory]
    [InlineData("mapping")]
    [InlineData("apId")]
    [InlineData("generation")]
    [InlineData("interval")]
    [InlineData("sourceAp")]
    [InlineData("window")]
    [InlineData("continuity")]
    [InlineData("upstream")]
    [InlineData("port")]
    public void UnprovenConnection_NeverBorrowsCurrentAp(string fault)
    {
        var row = Power.Connection();
        row = fault switch
        {
            "mapping" => row with { IdentityMappingVerified = false },
            "apId" => row with { Target = row.Target! with { ApId = Guid.Empty } },
            "generation" => row with { Target = row.Target! with { DeviceGenerationId = Guid.Empty } },
            "interval" => row with { Target = row.Target! with { MonitoringIntervalId = Guid.Empty } },
            "sourceAp" => row with { Target = row.Target! with { SourceApId = "ap-other" } },
            "window" => row with { Window = null },
            "continuity" => row with { Window = row.Window! with { ContinuityVerified = false } },
            "upstream" => row with { Protocol = DiscoveryProtocol.ApListReference },
            _ => row with { Local = row.Local with { SourceInterfaceId = "35" } }
        };
        Assert.Null(PowerAttributionPolicy.Resolve(Power.Sample(), [row]).Target);
    }

    [Fact]
    public void StaleConnection_HasNoAttribution()
    {
        var row = Power.Connection() with { Timing = Samples.Timing with { SourceAtUtc = Samples.Time.AddHours(-1) } };
        var result = PowerAttributionPolicy.Resolve(Power.Sample(), [row]);
        Assert.Equal(PowerAttributionStatus.Stale, result.Status);
        Assert.Null(result.Target);
    }

    [Fact]
    public void HistoricalWindow_EndIsExclusive()
    {
        var row = Power.Connection() with { Window = new(Samples.Time.AddMinutes(-1), Samples.Time, true) };
        Assert.Null(PowerAttributionPolicy.Resolve(Power.Sample(), [row]).Target);
    }

    [Fact]
    public void OtherProfileOrGenerationPlacement_IsNotInherited()
    {
        var row = Power.Connection();
        var wrongProfile = row with { Target = row.Target! with { Profile = Samples.Profile with { ConfigurationVersion = 2 } } };
        Assert.Null(PowerAttributionPolicy.Resolve(Power.Sample(), [wrongProfile]).Target);
        var wrongPlacement = row with { Target = row.Target! with { Placement = Power.Placement() with { DeviceGenerationId = Guid.NewGuid() } } };
        Assert.Null(PowerAttributionPolicy.Resolve(Power.Sample(), [wrongPlacement]).Target);
    }

    [Fact]
    public void PlacementMustApplyAtMeasurementAndBeKnownAtRead()
    {
        var row = Power.Connection();
        var futurePlacement = Power.Placement() with { EffectiveFromUtc = Samples.Time.AddMinutes(1) };
        Assert.Null(PowerAttributionPolicy.Resolve(Power.Sample(), [row with { Target = row.Target! with { Placement = futurePlacement } }]).Target);
        var futureRecord = Power.Placement() with { RecordedAtUtc = Samples.Time.AddMinutes(1) };
        Assert.Null(PowerAttributionPolicy.Resolve(Power.Sample(), [row with { Target = row.Target! with { Placement = futureRecord } }]).Target);
    }

    [Fact]
    public void DirectApConsumption_RequiresMatchingFrozenIdentity()
    {
        var sample = Power.Sample() with { Metric = PowerMetric.ApConsumption, Endpoint = Links.Ap, DirectTarget = Power.Target() };
        var result = PowerAttributionPolicy.Resolve(sample, []);
        Assert.Equal(PowerAttributionStatus.Attributed, result.Status);
        Assert.Equal(Power.Target(), result.Target);
        Assert.Null(result.Connection);
        Assert.Null(PowerAttributionPolicy.Resolve(sample with { DirectTarget = Power.Target() with { SourceApId = "ap-2" } }, []).Target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectApConsumption_UnknownIdentityWindowCannotUsePlacementAsSubstitute(bool hasPlacement)
    {
        var target = Power.Target() with { IdentityWindow = null, Placement = hasPlacement ? Power.Placement() : null };
        var sample = Power.Sample() with { Metric = PowerMetric.ApConsumption, Endpoint = Links.Ap, DirectTarget = target };
        var result = PowerAttributionPolicy.Resolve(sample, []);
        Assert.Equal(PowerAttributionStatus.Unknown, result.Status);
        Assert.Null(result.Target);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("monitoringInterval")]
    public void CachedDirectSample_BeforeKnownIdentityStartIsNotAssignedToNewIdentity(string changedIdentity)
    {
        var target = Power.Target() with { Placement = null, IdentityWindow = new(Samples.Time.AddHours(1), null, true) };
        target = changedIdentity == "generation" ? target with { DeviceGenerationId = Guid.NewGuid() }
            : target with { MonitoringIntervalId = Guid.NewGuid() };
        var sample = Power.Sample() with
        {
            Metric = PowerMetric.ApConsumption,
            Endpoint = Links.Ap,
            DirectTarget = target,
            Timing = Power.Sample().Timing with { ReadAtUtc = Samples.Time.AddDays(1) }
        };
        var result = PowerAttributionPolicy.Resolve(sample, []);
        Assert.Equal(PowerAttributionStatus.OutsideBinding, result.Status);
        Assert.Null(result.Target);
    }

    [Theory]
    [InlineData(0, 10, true)]
    [InlineData(-10, 1, true)]
    [InlineData(-10, 0, false)]
    [InlineData(-10, -1, false)]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, false)]
    public void DirectIdentityWindow_StartInclusiveAndEndExclusive(int fromSeconds, int untilSeconds, bool attributed)
    {
        var target = Power.Target() with
        {
            Placement = null,
            IdentityWindow = new(Samples.Time.AddSeconds(fromSeconds), Samples.Time.AddSeconds(untilSeconds), true)
        };
        var sample = Power.Sample() with { Metric = PowerMetric.ApConsumption, Endpoint = Links.Ap, DirectTarget = target };
        var result = PowerAttributionPolicy.Resolve(sample, []);
        Assert.Equal(attributed ? PowerAttributionStatus.Attributed : PowerAttributionStatus.OutsideBinding, result.Status);
        if (!attributed) Assert.Null(result.Target);
    }

    [Fact]
    public void DirectIdentityWindow_UnverifiedBoundsAreNotAttributionEvidence()
    {
        var target = Power.Target() with { Placement = null, IdentityWindow = new(Samples.Time.AddHours(-1), null, false) };
        var sample = Power.Sample() with { Metric = PowerMetric.ApConsumption, Endpoint = Links.Ap, DirectTarget = target };
        var result = PowerAttributionPolicy.Resolve(sample, []);
        Assert.Equal(PowerAttributionStatus.Unknown, result.Status);
        Assert.Null(result.Target);
    }

    [Fact]
    public void ValidHistoricalDirectBinding_RetainsOldGenerationEvenWhenReadAfterItsEnd()
    {
        var target = Power.Target() with
        {
            Placement = null,
            IdentityWindow = new(Samples.Time.AddHours(-1), Samples.Time.AddHours(1), true)
        };
        var sample = Power.Sample() with
        {
            Metric = PowerMetric.ApConsumption,
            Endpoint = Links.Ap,
            DirectTarget = target,
            Timing = Power.Sample().Timing with { ReadAtUtc = Samples.Time.AddDays(1) }
        };
        var result = PowerAttributionPolicy.Resolve(sample, []);
        Assert.Equal(PowerAttributionStatus.Attributed, result.Status);
        Assert.Equal(target, result.Target);
        Assert.Equal(Power.Generation, result.Target!.DeviceGenerationId);
    }

    [Theory]
    [InlineData("startsLater")]
    [InlineData("ended")]
    [InlineData("unverified")]
    public void PortConnection_CannotOverrideContradictoryTargetIdentityWindow(string fault)
    {
        var window = fault switch
        {
            "startsLater" => new EvidenceInterval(Samples.Time.AddHours(1), null, true),
            "ended" => new EvidenceInterval(Samples.Time.AddHours(-1), Samples.Time, true),
            _ => new EvidenceInterval(Samples.Time.AddHours(-1), null, false)
        };
        var row = Power.Connection() with { Target = Power.Target() with { Placement = null, IdentityWindow = window } };
        Assert.Null(PowerAttributionPolicy.Resolve(Power.Sample(), [row]).Target);
    }

    [Fact]
    public void PortConnection_ExplicitHistoricalMappingDoesNotRequireSeparateTargetWindow()
    {
        var row = Power.Connection() with { Target = Power.Target() with { IdentityWindow = null } };
        Assert.Equal(PowerAttributionStatus.Attributed, PowerAttributionPolicy.Resolve(Power.Sample(), [row]).Status);
    }
}

internal static class Power
{
    internal static readonly Guid Generation = Guid.Parse("40000000-0000-0000-0000-000000000001");
    internal static PlacementEvidenceSnapshot Placement() => new(Generation, 3,
        Guid.Parse("50000000-0000-0000-0000-000000000001"), 2, Samples.Time.AddHours(-1), null, Samples.Time.AddHours(-1));
    internal static EnrichmentTargetBinding Target() => new(Samples.Profile,
        Guid.Parse("60000000-0000-0000-0000-000000000001"), "ap-1", Generation,
        Guid.Parse("70000000-0000-0000-0000-000000000001"), Placement(), new(Samples.Time.AddHours(-1), null, true));
    internal static ConnectionObservation Connection() => Links.Connection() with { Target = Target() };
    internal static PowerSample Sample() => new(Guid.Parse("80000000-0000-0000-0000-000000000001"), 2,
        Samples.Profile, Links.Port, PowerMetric.PortActual, 12.5m, "W", true,
        Samples.Timing with { Meaning = SourceTimeMeaning.Measurement }, Samples.Evidence);
}
