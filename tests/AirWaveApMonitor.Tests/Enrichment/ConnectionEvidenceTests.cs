using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Enrichment;

namespace AirWaveApMonitor.Tests.Enrichment;

public sealed class ConnectionEvidenceTests
{
    [Theory]
    [InlineData(DiscoveryProtocol.Unknown)]
    [InlineData(DiscoveryProtocol.ApListReference)]
    [InlineData(DiscoveryProtocol.Cdp)]
    [InlineData(DiscoveryProtocol.OtherVerified)]
    public void UnknownProtocol_IsNotLldpHealthy(DiscoveryProtocol protocol)
    {
        var observation = Links.Query(Links.Connection() with { Protocol = protocol });
        var result = ConnectionEvidencePolicy.Evaluate(observation, Samples.Time);
        Assert.Equal(NeighborPresence.Observed, result.Presence);
        Assert.False(result.HasCurrentLldpObservation);
        Assert.False(result.MayClosePreviousConnection);
    }

    [Fact]
    public void UnknownRowTime_CannotBorrowQueryFreshness()
    {
        var row = Links.Connection() with { Timing = Samples.Timing with { SourceAtUtc = null } };
        Assert.Equal(SourceFreshness.Unknown, ConnectionEvidencePolicy.Evaluate(Links.Query(row), Samples.Time).Freshness);
    }

    [Fact]
    public void DistinctIdsForSameSourceNeighbor_DoNotHideDuplicateRows()
    {
        var row = Links.Connection();
        var duplicate = row with { Id = Guid.NewGuid() };
        var result = ConnectionEvidencePolicy.Evaluate(Links.Query(row, duplicate), Samples.Time);
        Assert.Equal(NeighborPresence.Unknown, result.Presence);
        Assert.False(result.HasCurrentLldpObservation);
    }

    [Fact]
    public void DifferentNeighbor_DoesNotCountAsExpectedNeighbor()
    {
        var row = Links.Connection() with { Remote = Links.Ap with { SourceDeviceId = "ap-2" } };
        var result = ConnectionEvidencePolicy.Evaluate(Links.Query(row), Samples.Time);
        Assert.Equal(NeighborPresence.ExpectedNeighborNotObserved, result.Presence);
        Assert.False(result.HasCurrentLldpObservation);
        Assert.Single(result.Observations);
    }

    [Fact]
    public void ExpectedApWithoutRemoteInterface_StillRecognizesItsObservedLink()
    {
        var query = Links.Query(Links.Connection());
        query = query with { Scope = query.Scope with { ExpectedRemote = Links.Ap with { SourceInterfaceId = null } } };
        var result = ConnectionEvidencePolicy.Evaluate(query, Samples.Time);
        Assert.Equal(NeighborPresence.Observed, result.Presence);
        Assert.True(result.HasCurrentLldpObservation);
        Assert.False(result.MayClosePreviousConnection);
    }

    [Fact]
    public void UnknownResponseInterface_CannotProveExpectedInterfaceAbsent()
    {
        var row = Links.Connection() with { Remote = Links.Ap with { SourceInterfaceId = null } };
        var result = ConnectionEvidencePolicy.Evaluate(Links.Query(row), Samples.Time);
        Assert.False(result.MayClosePreviousConnection);
        Assert.Equal(NeighborPresence.Unknown, result.Presence);
    }

    [Fact]
    public void ValidLldp_IsAnObservationNotContinuousHealth()
    {
        var result = ConnectionEvidencePolicy.Evaluate(Links.Query(Links.Connection()), Samples.Time);
        Assert.True(result.HasCurrentLldpObservation);
        Assert.False(result.MayClosePreviousConnection);
        Assert.Single(result.Observations);
    }

    [Fact]
    public void RawSourceLldpObservation_DoesNotRequireOrProveInternalApMapping()
    {
        var row = Power.Connection() with { IdentityMappingVerified = false };
        Assert.True(ConnectionEvidencePolicy.Evaluate(Links.Query(row), Samples.Time).HasCurrentLldpObservation);
        Assert.Null(PowerAttributionPolicy.Resolve(Power.Sample(), [row]).Target);
    }

    [Theory]
    [InlineData(ObservationCompleteness.Partial)]
    [InlineData(ObservationCompleteness.Unknown)]
    public void PartialNeighborResponse_DoesNotCloseConnection(ObservationCompleteness completeness)
    {
        var result = ConnectionEvidencePolicy.Evaluate(Links.Query() with { Completeness = completeness }, Samples.Time);
        Assert.False(result.MayClosePreviousConnection);
        Assert.Equal(NeighborPresence.Unknown, result.Presence);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("partialOutcome")]
    [InlineData("uncovered")]
    [InlineData("exclusions")]
    [InlineData("noExpected")]
    [InlineData("noPort")]
    [InlineData("old")]
    [InlineData("unknownTime")]
    public void MissingEvidence_CannotProveAbsence(string missing)
    {
        var query = Links.Query();
        query = missing switch
        {
            "failed" => query with { Outcome = RunOutcome.Failed },
            "partialOutcome" => query with { Outcome = RunOutcome.Partial },
            "uncovered" => query with { ScopeCovered = false },
            "exclusions" => query with { HasExclusionsOrConflicts = true },
            "noExpected" => query with { Scope = query.Scope with { ExpectedRemote = null } },
            "noPort" => query with { Scope = query.Scope with { Local = query.Scope.Local with { SourceInterfaceId = null } } },
            "old" => query with { Timing = query.Timing with { SourceAtUtc = Samples.Time.AddHours(-1) } },
            _ => query with { Timing = query.Timing with { SourceAtUtc = null } }
        };
        var result = ConnectionEvidencePolicy.Evaluate(query, Samples.Time);
        Assert.False(result.MayClosePreviousConnection);
        Assert.Equal(NeighborPresence.Unknown, result.Presence);
    }

    [Fact]
    public void CompleteCurrentExpectedPortQuery_CanRecordOnlyObservedAbsence()
    {
        var result = ConnectionEvidencePolicy.Evaluate(Links.Query(), Samples.Time);
        Assert.Equal(NeighborPresence.ExpectedNeighborNotObserved, result.Presence);
        Assert.True(result.MayClosePreviousConnection);
        Assert.False(result.HasCurrentLldpObservation);
    }

    [Fact]
    public void RepeatedOldRowWithNewQueryTime_DoesNotBecomeCurrent()
    {
        var row = Links.Connection() with { Timing = Samples.Timing with { SourceAtUtc = Samples.Time.AddHours(-1) } };
        var result = ConnectionEvidencePolicy.Evaluate(Links.Query(row), Samples.Time);
        Assert.Equal(NeighborPresence.Observed, result.Presence);
        Assert.False(result.HasCurrentLldpObservation);
        Assert.False(result.MayClosePreviousConnection);
        Assert.Equal(SourceFreshness.Delayed, result.Freshness);
    }

    [Fact]
    public void MultipleDirectionalLinks_AreNotCollapsedToOne()
    {
        var first = Links.Connection();
        var second = first with { Id = Guid.Parse("30000000-0000-0000-0000-000000000002"), Remote = Links.Ap with { SourceInterfaceId = "eth1" }, Direction = ObservationDirection.RemoteToLocal };
        var result = ConnectionEvidencePolicy.Evaluate(Links.Query(first, second), Samples.Time);
        Assert.Equal(2, result.Observations.Length);
        Assert.Equal(ObservationDirection.RemoteToLocal, result.Observations[1].Direction);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("profile")]
    [InlineData("port")]
    [InlineData("invalid")]
    [InlineData("source")]
    public void ConflictingOrOutOfScopeRows_BlockAbsenceAndLldp(string fault)
    {
        var row = Links.Connection();
        var query = Links.Query(row);
        query = fault switch
        {
            "duplicate" => Links.Query(row, row),
            "profile" => Links.Query(row with { Profile = row.Profile with { ScopeVersion = 2 } }),
            "port" => Links.Query(row with { Local = row.Local with { SourceInterfaceId = "another-port" } }),
            "invalid" => Links.Query(row with { Evidence = row.Evidence with { Validity = FieldValidity.Invalid } }),
            _ => Links.Query(row with { Evidence = row.Evidence with { Source = EvidenceSource.Demo } })
        };
        var result = ConnectionEvidencePolicy.Evaluate(query, Samples.Time);
        Assert.False(result.MayClosePreviousConnection);
        Assert.False(result.HasCurrentLldpObservation);
        Assert.Equal(NeighborPresence.Unknown, result.Presence);
    }
}

internal static class Links
{
    internal static ConnectionEndpoint Port => new("switch-1", "34", EndpointRole.Switch);
    internal static ConnectionEndpoint Ap => new("ap-1", "eth0", EndpointRole.AccessPoint);
    internal static ConnectionObservation Connection() => new(Guid.Parse("30000000-0000-0000-0000-000000000001"), 1,
        Samples.Profile, Port, Ap, DiscoveryProtocol.Lldp, ObservationDirection.LocalToRemote,
        Samples.Timing, TimeSpan.FromMinutes(5), Samples.Evidence, true,
        new(Samples.Time.AddMinutes(-1), Samples.Time.AddMinutes(1), true));
    internal static NeighborObservation Query(params ConnectionObservation[] rows) => new(new(Samples.Profile, Port, Ap),
        RunOutcome.Succeeded, ObservationCompleteness.Complete, true, false, Samples.Timing, TimeSpan.FromMinutes(5), [.. rows]);
}
