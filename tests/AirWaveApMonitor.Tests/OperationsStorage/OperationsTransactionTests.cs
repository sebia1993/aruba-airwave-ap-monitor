using System.Text;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Storage;

namespace AirWaveApMonitor.Tests.Storage;

public sealed partial class SqliteCollectionStoreTests
{
    [Fact]
    public async Task OperationLostReceipt_RetryReturnsPersistedReceiptWithoutAnotherCommit()
    {
        var row = await SeedOperations(); var edit = Edit(row);
        var failing = NewStore(boundary => { if (boundary == StorageTransactionBoundary.AfterFinalCommit) throw new IOException("lost response"); });
        await failing.InitializeAsync(default);
        await Assert.ThrowsAsync<IOException>(() => failing.SaveInventoryAsync(edit, default));
        Assert.True((await failing.SaveInventoryAsync(edit, default)).AlreadyApplied);
        Assert.Equal(1, Count("manual_inventory_versions")); Assert.Equal(1, Count("operation_commands"));
    }

    [Fact]
    public async Task PointPagination_HugePageNeverWrapsToFirstPage()
    {
        await SeedOperations();
        await store.SavePointAsync(new(Guid.NewGuid(), Guid.NewGuid(), 0, "설치점", null, new()), default);
        Assert.Empty((await store.GetPointsAsync(new(Page: int.MaxValue, PageSize: 2), default)).Rows);
    }

    [Fact]
    public async Task EndedTracking_CannotBecomePausedWithoutExplicitOperatingReentry()
    {
        var row = await SeedOperations();
        await store.SaveTrackingPolicyAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 0, OperationalStatus.MonitoringExcluded, "추적 종료", MonitoringDirective.EndTracking), default);
        await Apply(Observation("200", ApState.Up));
        await Assert.ThrowsAsync<OperationsConflictException>(() => store.SaveTrackingPolicyAsync(new(Guid.NewGuid(), row.ApId, row.DeviceGenerationId, 1, OperationalStatus.MonitoringExcluded, "일시 제외"), default));
        Assert.Equal(MonitoringDirective.EndTracking, (await store.GetRegistrationAsync(row.ApId, default)).Registration.TrackingPolicy!.Directive);
    }

    [Fact]
    public async Task ImportedTrackingEnd_HistoryRetainsImportProvenance()
    {
        var row = await SeedOperations();
        var preview = await store.PreviewImportAsync(Encoding.UTF8.GetBytes($"ap_id,management_status,management_reason\n{row.ApId},Decommissioned,철거\n"), Profile(), default);
        Assert.True(preview.Rows.Single().IsValid, string.Join(";", preview.Rows.Single().Errors.Select(e => e.Message)));
        await store.ApplyImportAsync(new(Guid.NewGuid(), preview.PreviewId, preview.Version, [1]), Profile(), default);
        await Apply(Observation("200", ApState.Up));
        var ended = (await store.GetTimelineAsync(new(ApId: row.ApId, Kind: OperationHistoryKind.Incident), default)).Rows.Single(e => e.Action == "TrackingEnded");
        Assert.Equal(EvidenceSource.Import, ended.Source);
    }
}
