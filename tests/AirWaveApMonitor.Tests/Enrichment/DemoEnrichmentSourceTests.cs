using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Enrichment;
using AirWaveApMonitor.Infrastructure.Demo;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Tests.Support;
namespace AirWaveApMonitor.Tests.Enrichment;
public sealed class DemoEnrichmentSourceTests
{
    internal static ImmutableArray<EnrichmentTargetBinding> Targets(CollectionContext c, int count = 1) => Enumerable.Range(1, count).Select(i => new EnrichmentTargetBinding(EnrichmentProfileBinding.From(c.Profile, EvidenceSource.Demo), Guid.NewGuid(), i.ToString(), Guid.NewGuid(), Guid.NewGuid())).ToImmutableArray();
    [Fact]
    public async Task ExplicitDemoEnable_ProducesSyntheticTypedConnectionsForEveryFrozenTarget()
    {
        var clock = new ManualTimeProvider(); var c = BoundedEnrichmentSourceTests.Context(clock); var source = new DemoEnrichmentSource(clock); var targets = Targets(c, 2048);
        Assert.True(source.Capability(c.Profile, c.Kind).CanCollect);
        var batch = await source.CollectAsync(c, targets, () => false, default);
        Assert.Equal(RunOutcome.Succeeded, batch.Outcome); Assert.Equal(2048, batch.Detail!.Neighbors.Length);
        Assert.All(batch.Detail.Neighbors, n => { var row = Assert.Single(n.Neighbors); Assert.Equal(EvidenceSource.Demo, row.Profile.Source); Assert.Contains(row.Target!, targets); Assert.Equal(DiscoveryProtocol.Lldp, row.Protocol); });
    }
    [Fact]
    public async Task DefaultsOrWrongOriginOrChangedFrozenBinding_CannotCollect()
    {
        var clock = new ManualTimeProvider(); var c = BoundedEnrichmentSourceTests.Context(clock); var source = new DemoEnrichmentSource(clock);
        var disabled = new ProfileSnapshot(c.Profile.InstanceId, c.Profile.DatasetId, c.Profile.Origin, 1, 1, [], BaselineStatus.Unverified, new());
        Assert.False(source.Capability(disabled, c.Kind).CanCollect);
        var wrong = new ProfileSnapshot(c.Profile.InstanceId, c.Profile.DatasetId, new Uri("https://live.invalid"), 1, 1, [], BaselineStatus.Unverified, c.Profile.Settings);
        Assert.False(source.Capability(wrong, c.Kind).CanCollect);
        var targets = Targets(c).Select(t => t with { Profile = t.Profile with { ScopeVersion = 2 } }).ToImmutableArray();
        Assert.Equal(RunOutcome.Failed, (await source.CollectAsync(c, targets, () => false, default)).Outcome);
    }
    [Fact]
    public async Task MissingPower_IsRetainedAsNullAndDoesNotBecomeZero()
    {
        var clock = new ManualTimeProvider(); var c = BoundedEnrichmentSourceTests.Context(clock) with { Kind = CollectionKind.Power }; var source = new DemoEnrichmentSource(clock) { Scenario = DemoEnrichmentScenario.MissingPower };
        var batch = await source.CollectAsync(c, Targets(c), () => false, default);
        Assert.Equal(RunOutcome.Partial, batch.Outcome); Assert.Contains(batch.Detail!.PowerSamples, p => p.Metric == PowerMetric.PortActual && p.Value is null);
        Assert.Contains(batch.Detail.PowerSamples, p => p.Metric == PowerMetric.Allocated && p.Value == 15.4m);
    }
    [Fact]
    public async Task DirectSample_PreservesFrozenIdentityWindow_AndDoesNotInventOne()
    {
        var clock = new ManualTimeProvider(); var c = BoundedEnrichmentSourceTests.Context(clock) with { Kind = CollectionKind.Power };
        var source = new DemoEnrichmentSource(clock); var target = Targets(c)[0];
        var unknown = await source.CollectAsync(c, [target], () => false, default);
        var unknownSample = unknown.Detail!.PowerSamples.Single(p => p.Metric == PowerMetric.ApConsumption);
        Assert.Null(unknownSample.DirectTarget!.IdentityWindow);
        Assert.Equal(PowerAttributionStatus.Unknown, PowerAttributionPolicy.Resolve(unknownSample, []).Status);
        target = target with { IdentityWindow = new(c.StartedAtUtc.AddMinutes(-1), c.StartedAtUtc.AddMinutes(1), true) };
        var valid = await source.CollectAsync(c, [target], () => false, default);
        var sample = valid.Detail!.PowerSamples.Single(p => p.Metric == PowerMetric.ApConsumption);
        Assert.Equal(target, sample.DirectTarget);
        Assert.Equal(PowerAttributionStatus.Attributed, PowerAttributionPolicy.Resolve(sample, []).Status);
    }
    [Theory]
    [InlineData(CollectionKind.ConnectionNeighbor, 3)]
    [InlineData(CollectionKind.Power, 10)]
    public void DemoCatalog_ListsAuthoredFieldsWithExactBinding_IndependentlyOfEnableFlags(CollectionKind kind, int count)
    {
        var clock = new ManualTimeProvider(); var current = BoundedEnrichmentSourceTests.Context(clock).Profile;
        var disabled = new ProfileSnapshot(current.InstanceId, current.DatasetId, current.Origin, 7, 9, [], BaselineStatus.Unverified, new()) { CredentialsVersion = 3 };
        IBoundedEnrichmentSource source = new DemoEnrichmentSource(clock);
        var fields = source.DescribeCapabilities(disabled, kind);
        Assert.Equal(count, fields.Length);
        Assert.Equal(ExpectedFields(kind), fields.Select(f => f.Key.Field).ToArray());
        Assert.All(fields, field =>
        {
            Assert.Equal(EnrichmentProfileBinding.From(disabled, EvidenceSource.Demo), field.Key.Profile);
            Assert.Equal("synthetic-demo", field.Key.DeviceFamily); Assert.Equal("demo-v1", field.Key.ContractRevision);
            Assert.True(field.Implemented); Assert.True(field.ContractVerified);
            Assert.Equal(CapabilityVerification.DemoFixture, field.Verification); Assert.Equal(CapabilityAvailability.AvailableConfirmed, field.Availability);
            Assert.NotNull(field.VerifiedAtUtc);
            Assert.Equal(CapabilityDecisionReason.Disabled, CapabilityStatus.Evaluate(field, field.Key, false).Reason);
            Assert.True(CapabilityStatus.Evaluate(field, field.Key, true).CanCollect);
        });
        Assert.False(source.Capability(disabled, kind).CanCollect);
    }
    [Theory]
    [InlineData(CollectionKind.ConnectionNeighbor)]
    [InlineData(CollectionKind.Power)]
    public void LiveCatalog_NeverConfirmsFieldOrModelEvenWithEnabledFlags(CollectionKind kind)
    {
        var clock = new ManualTimeProvider(); var current = BoundedEnrichmentSourceTests.Context(clock).Profile;
        var live = new ProfileSnapshot(current.InstanceId, current.DatasetId, new Uri("https://airwave.invalid"), 7, 9, [], BaselineStatus.Unverified, current.Settings) { CredentialsVersion = 3 };
        IBoundedEnrichmentSource source = new EnrichmentSource(clock); var fields = source.DescribeCapabilities(live, kind);
        Assert.Equal(ExpectedFields(kind), fields.Select(f => f.Key.Field).ToArray());
        Assert.All(fields, field =>
        {
            Assert.Equal(EnrichmentProfileBinding.From(live, EvidenceSource.AirWave), field.Key.Profile);
            Assert.Equal("unverified", field.Key.DeviceFamily); Assert.False(field.Implemented);
            Assert.False(field.ContractVerified); Assert.Equal(CapabilityVerification.None, field.Verification);
            Assert.Equal(CapabilityAvailability.Unverified, field.Availability); Assert.Null(field.VerifiedAtUtc);
            Assert.False(CapabilityStatus.Evaluate(field, field.Key, true).CanCollect); Assert.NotNull(field.SafeReason);
        });
        Assert.False(source.Capability(live, kind).CanCollect);
    }
    [Fact]
    public void CatalogRejectsUnknownKind_CrossModeOrigin_AndInvalidCredentialBinding()
    {
        var clock = new ManualTimeProvider(); var demo = BoundedEnrichmentSourceTests.Context(clock).Profile;
        var live = new ProfileSnapshot(demo.InstanceId, demo.DatasetId, new Uri("https://airwave.invalid"), 1, 1, [], BaselineStatus.Unverified, demo.Settings);
        IBoundedEnrichmentSource demoSource = new DemoEnrichmentSource(clock), liveSource = new EnrichmentSource(clock);
        Assert.Empty(demoSource.DescribeCapabilities(live, CollectionKind.Power)); Assert.Empty(liveSource.DescribeCapabilities(demo, CollectionKind.Power));
        foreach (var (source, profile) in new[] { (demoSource, demo), (liveSource, live) })
        {
            Assert.Empty(source.DescribeCapabilities(profile, CollectionKind.ApStatus)); Assert.Empty(source.DescribeCapabilities(profile, (CollectionKind)999));
            Assert.Empty(source.DescribeCapabilities(profile with { CredentialsVersion = 0 }, CollectionKind.Power));
        }
    }
    [Fact]
    public void CatalogEvidenceCannotBeReusedAcrossProfileVersion_AndMissingValueDoesNotRevokeSupport()
    {
        var clock = new ManualTimeProvider(); var profile = BoundedEnrichmentSourceTests.Context(clock).Profile;
        var demoSource = new DemoEnrichmentSource(clock); IBoundedEnrichmentSource source = demoSource;
        var first = source.DescribeCapabilities(profile, CollectionKind.Power).First();
        var changed = new ProfileSnapshot(profile.InstanceId, profile.DatasetId, profile.Origin, 2, 2, [], BaselineStatus.Unverified, profile.Settings) { CredentialsVersion = 2 };
        var requested = source.DescribeCapabilities(changed, CollectionKind.Power).First();
        Assert.Equal(CapabilityDecisionReason.BindingChanged, CapabilityStatus.Evaluate(first, requested.Key, true).Reason);
        demoSource.Scenario = DemoEnrichmentScenario.MissingPower;
        Assert.All(source.DescribeCapabilities(profile, CollectionKind.Power), field => Assert.Equal(CapabilityAvailability.AvailableConfirmed, field.Availability));
    }
    private static string[] ExpectedFields(CollectionKind kind) => kind == CollectionKind.ConnectionNeighbor
        ? ["neighbor", "port.admin", "port.oper"]
        : ["power.ApConsumption", "power.PortActual", "power.Requested", "power.Allocated", "power.Reserved", "power.Maximum",
            "power.supplyState", "power.supplyMethod", "power.errorCount", "power.lldpNegotiationSupported"];
    [Theory]
    [InlineData(DemoEnrichmentScenario.StaleNeighbor)]
    [InlineData(DemoEnrichmentScenario.UnknownProtocol)]
    public async Task UncertainScenario_DoesNotReportCurrentLldp(DemoEnrichmentScenario scenario)
    {
        var clock = new ManualTimeProvider(); var c = BoundedEnrichmentSourceTests.Context(clock); var source = new DemoEnrichmentSource(clock) { Scenario = scenario };
        var batch = await source.CollectAsync(c, Targets(c), () => false, default);
        var row = Assert.Single(batch.Detail!.Neighbors);
        Assert.False(ConnectionEvidencePolicy.Evaluate(row, clock.GetUtcNow()).HasCurrentLldpObservation);
    }
}
