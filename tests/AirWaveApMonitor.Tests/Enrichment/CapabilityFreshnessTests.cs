using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Enrichment;

namespace AirWaveApMonitor.Tests.Enrichment;

public sealed class CapabilityFreshnessTests
{
    [Fact]
    public void VerifiedProfileResponse_OnlyRunsWhenExplicitlyEnabled()
    {
        var evidence = Samples.Capability();
        Assert.True(CapabilityStatus.Evaluate(evidence, evidence.Key, true).CanCollect);
        Assert.False(CapabilityStatus.Evaluate(evidence, evidence.Key, false).CanCollect);
    }

    [Theory]
    [InlineData(CapabilityVerification.None)]
    [InlineData(CapabilityVerification.OfficialContract)]
    [InlineData(CapabilityVerification.DemoFixture)]
    public void FixtureOrDocumentation_CannotConfirmLiveField(CapabilityVerification verification)
    {
        var evidence = Samples.Capability() with { Verification = verification };
        var result = CapabilityStatus.Evaluate(evidence, evidence.Key, true);
        Assert.Equal(CapabilityAvailability.Unverified, result.Availability);
        Assert.False(result.CanCollect);
    }

    [Fact]
    public void DemoVerification_RemainsBoundToDemoDataset()
    {
        var key = Samples.Key() with { Profile = Samples.Profile with { Source = EvidenceSource.Demo } };
        var evidence = Samples.Capability() with { Key = key, Verification = CapabilityVerification.DemoFixture };
        Assert.True(CapabilityStatus.Evaluate(evidence, key, true).CanCollect);
        Assert.False(CapabilityStatus.Evaluate(evidence, Samples.Key(), true).CanCollect);
    }

    [Theory]
    [InlineData("dataset")]
    [InlineData("instance")]
    [InlineData("origin")]
    [InlineData("configuration")]
    [InlineData("scope")]
    [InlineData("credentials")]
    [InlineData("model")]
    [InlineData("field")]
    [InlineData("contract")]
    public void ChangedBinding_DoesNotInheritCapability(string change)
    {
        var evidence = Samples.Capability();
        var key = evidence.Key;
        key = change switch
        {
            "dataset" => key with { Profile = key.Profile with { DatasetId = Guid.Parse("10000000-0000-0000-0000-000000000002") } },
            "instance" => key with { Profile = key.Profile with { InstanceId = Guid.Parse("20000000-0000-0000-0000-000000000002") } },
            "origin" => key with { Profile = key.Profile with { Origin = new Uri("https://other.invalid/") } },
            "configuration" => key with { Profile = key.Profile with { ConfigurationVersion = 2 } },
            "scope" => key with { Profile = key.Profile with { ScopeVersion = 2 } },
            "credentials" => key with { Profile = key.Profile with { CredentialsVersion = 2 } },
            "model" => key with { DeviceFamily = "different-model" },
            "field" => key with { Field = "power.actual" },
            _ => key with { ContractRevision = "v2" }
        };
        Assert.Equal(CapabilityAvailability.Unverified, CapabilityStatus.Evaluate(evidence, key, true).Availability);
    }

    [Fact]
    public void MissingEvidence_IsUnverifiedNotUnsupported()
        => Assert.Equal(CapabilityAvailability.Unverified, CapabilityStatus.Evaluate(null, Samples.Key(), true).Availability);

    [Fact]
    public void Unsupported_RequiresExplicitMatchingEvidence()
    {
        var evidence = Samples.Capability() with { Availability = CapabilityAvailability.UnsupportedConfirmed };
        Assert.Equal(CapabilityAvailability.Unverified, CapabilityStatus.Evaluate(evidence, evidence.Key, true).Availability);
        evidence = evidence with { Verification = CapabilityVerification.ExplicitUnsupported };
        var result = CapabilityStatus.Evaluate(evidence, evidence.Key, true);
        Assert.Equal(CapabilityAvailability.UnsupportedConfirmed, result.Availability);
        Assert.False(result.CanCollect);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void CodeOrContractMissing_DoesNotRun(bool implemented, bool contractVerified)
    {
        var evidence = Samples.Capability() with { Implemented = implemented, ContractVerified = contractVerified };
        Assert.False(CapabilityStatus.Evaluate(evidence, evidence.Key, true).CanCollect);
    }

    [Fact]
    public void RepeatedRead_DoesNotRefreshSourceTime()
    {
        var timing = Samples.Timing with { SourceAtUtc = Samples.Time.AddMinutes(-20) };
        var result = SourceFreshnessPolicy.Evaluate(timing, Samples.Time, TimeSpan.FromMinutes(5));
        var reread = SourceFreshnessPolicy.Evaluate(timing with { ReadAtUtc = Samples.Time.AddMinutes(2) }, Samples.Time.AddMinutes(2), TimeSpan.FromMinutes(5));
        Assert.Equal(SourceFreshness.Delayed, result.Freshness);
        Assert.Equal(SourceFreshness.Delayed, reread.Freshness);
        Assert.Equal(Samples.Time.AddMinutes(-20), reread.SourceAtUtc);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("meaning")]
    [InlineData("verified")]
    [InlineData("age")]
    public void MissingSourceContract_IsUnknown(string missing)
    {
        var timing = Samples.Timing;
        TimeSpan? age = TimeSpan.FromMinutes(5);
        if (missing == "source") timing = timing with { SourceAtUtc = null };
        if (missing == "meaning") timing = timing with { Meaning = SourceTimeMeaning.Unknown };
        if (missing == "verified") timing = timing with { MeaningVerified = false };
        if (missing == "age") age = null;
        Assert.Equal(SourceFreshness.Unknown, SourceFreshnessPolicy.Evaluate(timing, Samples.Time, age).Freshness);
    }

    [Fact]
    public void FreshnessAgeBoundary_IsInclusiveAndUsesViewTime()
    {
        Assert.Equal(SourceFreshness.Current, SourceFreshnessPolicy.Evaluate(Samples.Timing, Samples.Time.AddMinutes(5), TimeSpan.FromMinutes(5)).Freshness);
        Assert.Equal(SourceFreshness.Delayed, SourceFreshnessPolicy.Evaluate(Samples.Timing, Samples.Time.AddMinutes(5).AddTicks(1), TimeSpan.FromMinutes(5)).Freshness);
    }

    [Fact]
    public void FutureOrBackwardsSourceTime_IsUnknown()
    {
        var future = Samples.Timing with { SourceAtUtc = Samples.Time.AddSeconds(1) };
        Assert.Equal(SourceFreshness.Unknown, SourceFreshnessPolicy.Evaluate(future, Samples.Time, TimeSpan.FromMinutes(5)).Freshness);
        Assert.Equal(SourceFreshness.Unknown, SourceFreshnessPolicy.Evaluate(Samples.Timing, Samples.Time.AddMinutes(-1), TimeSpan.FromMinutes(5)).Freshness);
        Assert.Equal(SourceFreshness.Unknown, SourceFreshnessPolicy.Evaluate(Samples.Timing, Samples.Time, TimeSpan.FromMinutes(5), Samples.Time.AddMinutes(1)).Freshness);
    }

    [Fact]
    public void InvalidAllowedAge_RejectsContract()
        => Assert.Throws<ArgumentOutOfRangeException>(() => SourceFreshnessPolicy.Evaluate(Samples.Timing, Samples.Time, TimeSpan.FromTicks(-1)));
}

internal static class Samples
{
    internal static readonly DateTimeOffset Time = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    internal static readonly EnrichmentProfileBinding Profile = new(
        Guid.Parse("10000000-0000-0000-0000-000000000001"), Guid.Parse("20000000-0000-0000-0000-000000000001"),
        new Uri("https://airwave.invalid/"), 1, 1, 1, EvidenceSource.AirWave);
    internal static SourceTiming Timing => new(Time, Time, SourceTimeMeaning.LastObserved, true);
    internal static FieldEvidence Evidence => new(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.AirWave, Time, Time, SourceFreshness.Current);
    internal static CapabilityKey Key() => new(Profile, "switch-family", "wired.neighbor", "v1");
    internal static CapabilityEvidence Capability() => new(Key(), CapabilityAvailability.AvailableConfirmed, true, true, CapabilityVerification.ProfileResponse, Time, null);
}
