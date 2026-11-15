using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using AirWaveApMonitor.App.Endpoints;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.FileIO;

namespace AirWaveApMonitor.Tests.Web;

public sealed class OperationsExportTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CsvUsesOnlySelectedRunManualSnapshotAndPreservesAutomaticEvidence(bool preferManual)
    {
        using var fixture = new ReadFixture();
        var past = await fixture.Apply([fixture.Ap("100", ApState.Down, "past automatic")]);
        var current = await fixture.Apply([fixture.Ap("100", ApState.Up, "future automatic")]);
        var row = (await fixture.Dashboard(past.RunId)).Rows.Single(row => row.SourceApId == "100");
        var manual = new ManualInventorySnapshot(7, row.DeviceGenerationId, EvidenceSource.Import, fixture.Now,
            new Dictionary<InventoryField, string> { [InventoryField.Name] = "=manual formula", [InventoryField.IpAddress] = "192.0.2.77",
                [InventoryField.Site] = "registered site", [InventoryField.Model] = "registered model", [InventoryField.Notes] = "line1\r\nline2\"quoted\"" }.ToImmutableDictionary(),
            preferManual ? ImmutableHashSet.Create(InventoryField.Name, InventoryField.IpAddress, InventoryField.Site) : []);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
        builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await File.WriteAllTextAsync(Path.Combine(fixture.Location.DataDirectory, "profile.json"), JsonSerializer.Serialize(fixture.Profile()));
        var wrapper = new SnapshotStore(fixture.Store, past.RunId, manual);
        builder.Services.AddSingleton(fixture.Location);
        builder.Services.AddSingleton<ICollectionStore>(wrapper);
        builder.Services.AddSingleton<IMonitoringReadStore>(fixture.Store);
        builder.Services.AddSingleton(new ProfileStore(fixture.Location));
        await using var app = builder.Build(); app.MapExport(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.GetAsync($"/api/export.csv?runId={past.RunId}&filter=Down");
        response.EnsureSuccessStatusCode(); var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, bytes[..3]);
        using var parser = new TextFieldParser(new MemoryStream(bytes)); parser.SetDelimiters(","); parser.HasFieldsEnclosedInQuotes = true;
        var headers = parser.ReadFields()!; var values = parser.ReadFields()!;
        var columns = headers.Zip(values).ToDictionary(pair => pair.First, pair => pair.Second);
        Assert.True(parser.EndOfData);
        Assert.Equal(preferManual ? "'=manual formula" : "past automatic", columns["AP 이름"]);
        Assert.Equal(preferManual ? "192.0.2.77" : row.Device.IpAddress, columns["AP IP"]);
        Assert.Equal(preferManual ? "registered site" : row.Location.Site, columns["사업장"]);
        Assert.Equal(row.Device.Model ?? "", columns["모델"]); // Registered but not preferred remains a difference.
        Assert.Equal("7", columns["대장 버전"]); Assert.Equal("Import", columns["대장 출처"]);
        Assert.Equal("line1\r\nline2\"quoted\"", columns["대장 메모"]);
        Assert.Contains("past automatic", columns["자동 정보 JSON"]);
        Assert.Contains("=manual formula", columns["수동 등록 JSON"]);
        Assert.DoesNotContain("future automatic", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.All(wrapper.Reads, query => Assert.Equal(past.RunId, query.RunId));
        Assert.NotEqual(current.RunId, past.RunId); await app.StopAsync();
    }
    // Inject a read contract snapshot. Real SQLite persistence of this layer is separately owned and tested by the operations store.
    private sealed class SnapshotStore(ICollectionStore inner, long pastRun, ManualInventorySnapshot manual) : ICollectionStore
    {
        public List<DashboardQuery> Reads { get; } = [];
        public async Task<DashboardSnapshot> GetDashboardAsync(DashboardQuery query, CancellationToken ct)
        {
            Reads.Add(query); var value = await inner.GetDashboardAsync(query, ct);
            return value with { Rows = value.Rows.Select(row => row with { ManualInventory = value.RunId == pastRun ? manual : null }).ToImmutableArray() };
        }
        public Task<CollectionSuccessTimes> GetSuccessTimesAsync(Guid datasetId, long scopeVersion, CancellationToken ct) => inner.GetSuccessTimesAsync(datasetId, scopeVersion, ct);
        public Task<CollectionContext> BeginRunAsync(ProfileSnapshot profile, CollectionKind kind, RunTrigger trigger, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommitReceipt> ApplyApBatchAsync(CollectionContext context, ApCollectionBatch batch, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommitReceipt> InterruptRunAsync(CollectionContext context, CancellationToken ct) => throw new NotSupportedException();
        public Task<EnrichmentCommitReceipt> ApplyEnrichmentBatchAsync(CollectionContext context, EnrichmentBatch batch, CancellationToken ct) => throw new NotSupportedException();
    }
}
