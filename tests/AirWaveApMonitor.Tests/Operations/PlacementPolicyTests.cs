using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Operations;
using static AirWaveApMonitor.Tests.Operations.OperationsFixture;
namespace AirWaveApMonitor.Tests.Operations;
public sealed class PlacementPolicyTests
{
    private static readonly Guid Point1 = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid Point2 = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static PlacementPeriod Old => new(Guid.Parse("99999999-9999-9999-9999-999999999999"), Dataset, Ap, Generation, Point1, 1, At.AddDays(-1), null, EvidenceSource.Manual);
    private static PlacementRequest Request => new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), Dataset, Ap, Generation, Point2, 2, At, At.AddHours(-1), EvidenceSource.Manual, "confirmed move");
    [Fact]
    public void PlacementChange_DoesNotRewriteOldIncident()
    {
        var original = Old; var result = new PlacementPolicy().Move(original, Request);
        Assert.True(result.Changed); Assert.Equal(At, result.Closed!.RecordedEndAtUtc);
        Assert.Equal(At.AddHours(-1), result.Closed.ActualEndAtUtc);
        Assert.Equal(Point2, result.Open!.InstallationPointId); Assert.Equal(Generation, result.Open.DeviceGenerationId);
        Assert.Null(original.RecordedEndAtUtc); Assert.Equal(Point1, original.InstallationPointId);
    }
    [Fact]
    public void DifferentDeviceAtSamePoint_RemainsSeparate()
    {
        var otherGeneration = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var first = new PlacementPolicy().Move(null, Request with { InstallationPointId = Point1 });
        var second = new PlacementPolicy().Move(null, Request with { InstallationPointId = Point1, DeviceGenerationId = otherGeneration, ApId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc") });
        Assert.Equal(first.Open!.InstallationPointId, second.Open!.InstallationPointId);
        Assert.NotEqual(first.Open.DeviceGenerationId, second.Open.DeviceGenerationId);
    }
    [Fact]
    public void UnchangedPoint_DoesNotRestartPlacement()
    {
        var result = new PlacementPolicy().Move(Old, Request with { InstallationPointId = Point1 });
        Assert.False(result.Changed); Assert.Equal(Old, result.Open); Assert.Null(result.Closed);
    }
    [Fact]
    public void ExplicitRemoval_ClosesOnlyTheSavedRelation()
    {
        var result = new PlacementPolicy().Move(Old, Request with { InstallationPointId = null, PointVersion = null });
        Assert.True(result.Changed); Assert.NotNull(result.Closed); Assert.Null(result.Open);
    }
    [Fact]
    public void OtherGenerationOrAlreadyClosedRelation_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new PlacementPolicy().Move(Old with { DeviceGenerationId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb") }, Request));
        Assert.Throws<ArgumentException>(() => new PlacementPolicy().Move(Old with { RecordedEndAtUtc = At }, Request));
    }
    [Fact]
    public void MissingReasonOrImplicitPoint_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new PlacementPolicy().Move(Old, Request with { Reason = " " }));
        Assert.Throws<ArgumentException>(() => new PlacementPolicy().Move(null, Request with { InstallationPointId = Guid.Empty }));
    }
    [Fact]
    public void BackwardClock_PreservesOriginalTimesAndMarksReview()
    {
        var request = Request with { RecordedAtUtc = At.AddDays(-2), ActualWorkAtUtc = At.AddDays(-3) };
        var result = new PlacementPolicy().Move(Old, request);
        Assert.Equal(request.RecordedAtUtc, result.Closed!.RecordedEndAtUtc); Assert.True(result.Closed.TimeNeedsReview);
        Assert.True(result.Open!.TimeNeedsReview); Assert.Equal(request.ActualWorkAtUtc, result.Open.ActualStartAtUtc);
    }
}
