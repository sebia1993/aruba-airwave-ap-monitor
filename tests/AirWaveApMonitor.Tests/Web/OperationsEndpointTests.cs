using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AirWaveApMonitor.App.Endpoints;
using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Tests.Settings;
using AirWaveApMonitor.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AirWaveApMonitor.Tests.Web;

[Collection("Host environment")]
public sealed class OperationsEndpointTests
{
    [Fact]
    public async Task RestoreEpochIsReadWithoutMutationAndPassedUnchangedWithDraft()
    {
        await using var host = await Host.Start();
        var version = await host.Client.GetAsync("/api/operations/version");
        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        Assert.Equal("no-store", version.Headers.CacheControl!.ToString());
        Assert.Equal(7, (await version.Content.ReadFromJsonAsync<OperationsVersionSnapshot>())!.RestoreEpoch);
        Assert.Equal(0, host.Store.Writes);
        await SettingsEndpointTests.Token(host.Client);
        var edit = ValidInventory() with { ExpectedRestoreEpoch = 7 };
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/api/operations/inventory", edit)).StatusCode);
        Assert.Equal(7, host.Store.Inventory!.ExpectedRestoreEpoch);
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("tracking")]
    [InlineData("points")]
    [InlineData("placement")]
    [InlineData("work")]
    [InlineData("replacement/confirm")]
    [InlineData("import/preview")]
    [InlineData("import/apply")]
    public async Task OperationsWrites_KeepRealHostOriginAndCsrfGuard(string path)
    {
        await using var host = await Host.Start(); var route = "/api/operations/" + path;
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync(route, new { })).StatusCode);
        await SettingsEndpointTests.Token(host.Client);
        host.Client.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PostAsJsonAsync(route, new { })).StatusCode);
        host.Client.DefaultRequestHeaders.Add("Origin", "https://untrusted.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PostAsJsonAsync(route, new { })).StatusCode);
        host.Client.DefaultRequestHeaders.Remove("Origin");
        host.Client.DefaultRequestHeaders.Add("Origin", host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        host.Client.DefaultRequestHeaders.Host = "untrusted.invalid";
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync(route, new { })).StatusCode);
        Assert.Equal(0, host.Store.Writes);
    }

    [Fact]
    public async Task ImportPreviewAcceptsRawBoundedCsvAndUsesCurrentServerProfile()
    {
        await using var host = await Host.Start(); await SettingsEndpointTests.Token(host.Client);
        var csv = Encoding.UTF8.GetBytes("ap_id,notes\r\n" + Guid.NewGuid() + "," + new string('x', 40 * 1024));
        using var content = new ByteArrayContent(csv); content.Headers.ContentType = new("text/csv");
        var response = await host.Client.PostAsync("/api/operations/import/preview", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(csv, host.Store.LastCsv);
        Assert.Same(host.Profiles.Current, host.Store.LastProfile);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(1, host.Store.Writes);
    }

    [Fact]
    public async Task ImportLimitDoesNotRelaxOrdinaryMutationLimit()
    {
        await using var host = await Host.Start(); await SettingsEndpointTests.Token(host.Client);
        using var oversizedCsv = new ByteArrayContent(new byte[OperationsEndpoints.MaximumImportBytes + 1]);
        oversizedCsv.Headers.ContentType = new("text/csv");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/operations/import/preview") { Content = oversizedCsv };
        request.Headers.ExpectContinue = true;
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await host.Client.SendAsync(request)).StatusCode);
        var json = new { commandId = Guid.NewGuid(), apId = Guid.NewGuid(), expectedCurrentGenerationId = Guid.NewGuid(), expectedVersion = 0,
            setValues = new { Notes = new string('x', 40 * 1024) }, clearFields = Array.Empty<string>() };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await host.Client.PostAsJsonAsync("/api/operations/inventory", json)).StatusCode);
        Assert.Equal(0, host.Store.Writes);
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("tracking")]
    [InlineData("points")]
    [InlineData("placement")]
    [InlineData("work")]
    [InlineData("replacement/confirm")]
    [InlineData("import/preview")]
    [InlineData("import/apply")]
    public async Task BusyCollectionRejectsAllMutationBeforeStoreAccessAndReleasesGateAfterFailure(string route)
    {
        await using var host = await Host.Start(); await SettingsEndpointTests.Token(host.Client);
        await host.Profiles.OperationGate.WaitAsync();
        try
        {
            Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsJsonAsync("/api/operations/" + route, new { })).StatusCode);
            Assert.Equal(0, host.Store.Writes);
        }
        finally { host.Profiles.OperationGate.Release(); }
        host.Store.Failure = new OperationsConflictException("secret-key-must-not-leak");
        var edit = ValidInventory();
        var response = await host.Client.PostAsJsonAsync("/api/operations/inventory", edit);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("secret-key", await response.Content.ReadAsStringAsync());
        Assert.True(await host.Profiles.OperationGate.WaitAsync(0)); host.Profiles.OperationGate.Release();
        host.Store.Failure = null;
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/api/operations/inventory", edit)).StatusCode);
        Assert.Equal(edit.CommandId, host.Store.Inventory!.CommandId);
        Assert.Equal(edit.ExpectedCurrentGenerationId, host.Store.Inventory.ExpectedCurrentGenerationId);
        Assert.Equal(edit.ExpectedVersion, host.Store.Inventory.ExpectedVersion);
    }

    [Theory]
    [InlineData("{\"status\":0}")]
    [InlineData("{\"state\":\"Up\"}")]
    [InlineData("not-json")]
    public async Task InvalidJsonOrAutomaticColumnsAreNotSilentlyAccepted(string json)
    {
        await using var host = await Host.Start(); await SettingsEndpointTests.Token(host.Client);
        var response = await host.Client.PostAsync("/api/operations/tracking", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, host.Store.Writes);
    }

    [Theory]
    [InlineData("?apId=not-guid")]
    [InlineData("?kind=123")]
    [InlineData("?pageSize=501")]
    [InlineData("?throughEventId=-1")]
    public async Task TimelineQueryRejectsInvalidSelectors(string query)
    {
        await using var host = await Host.Start();
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/api/operations/timeline" + query)).StatusCode);
    }

    [Fact]
    public async Task TimelineCarriesStableSelectionAndReturnsStringEnums()
    {
        await using var host = await Host.Start(); var id = Guid.NewGuid();
        var response = await host.Client.GetAsync($"/api/operations/timeline?apId={id}&kind=WorkAction&throughEventId=12&page=2&pageSize=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(id, host.Store.Query!.ApId); Assert.Equal(12, host.Store.Query.ThroughEventId); Assert.Equal(2, host.Store.Query.Page);
        Assert.Contains("\"kind\":\"WorkAction\"", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("invalid", HttpStatusCode.BadRequest)]
    [InlineData("storage", HttpStatusCode.ServiceUnavailable)]
    public async Task ControlledErrorsDoNotLeakStoreException(string kind, HttpStatusCode expected)
    {
        await using var host = await Host.Start();
        host.Store.Failure = kind switch { "missing" => new KeyNotFoundException("private path"), "invalid" => new ArgumentException("private path"), _ => new InvalidDataException("private path") };
        var response = await host.Client.GetAsync("/api/operations/timeline");
        Assert.Equal(expected, response.StatusCode); Assert.DoesNotContain("private path", await response.Content.ReadAsStringAsync());
    }

    private static InventoryEdit ValidInventory() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3,
        ImmutableDictionary<InventoryField, string>.Empty.Add(InventoryField.Name, "수동 이름"), []);

    [Fact]
    public async Task ApplyAndReplacementPassExactVersionsAndUseOnlyServerProfile()
    {
        await using var host = await Host.Start(); await SettingsEndpointTests.Token(host.Client);
        var profile = host.Profiles.Current!;
        var apply = new ImportApply(Guid.NewGuid(), Guid.NewGuid(), new(profile.DatasetId, profile.InstanceId, 11, 22, 33), [2, 5]);
        var response = await host.Client.PostAsJsonAsync("/api/operations/import/apply", apply);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Same(profile, host.Store.LastProfile);
        Assert.Equal(apply.ExpectedVersion, host.Store.Apply!.ExpectedVersion);
        Assert.Equal(apply.RowNumbers.ToArray(), host.Store.Apply.RowNumbers.ToArray());
        var edit = new ReplacementConfirmationEdit(Guid.NewGuid(), 31, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 4, "confirmed", DateTimeOffset.UtcNow);
        response = await host.Client.PostAsJsonAsync("/api/operations/replacement/confirm", edit);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(edit, host.Store.Confirmation);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("multipart/form-data")]
    public async Task PreviewRejectsNonRawCsvContentTypeBeforeStoreAccess(string contentType)
    {
        await using var host = await Host.Start(); await SettingsEndpointTests.Token(host.Client);
        using var body = new StringContent("ap_id,notes", Encoding.UTF8, contentType);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsync("/api/operations/import/preview", body)).StatusCode);
        Assert.Equal(0, host.Store.Writes);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly TemporaryDirectory directory; private readonly WebApplication app;
        public HttpClient Client { get; } public FakeStore Store { get; } public ProfileStore Profiles { get; }
        private Host(TemporaryDirectory directory, WebApplication app, HttpClient client, FakeStore store)
        { this.directory = directory; this.app = app; Client = client; Store = store; Profiles = app.Services.GetRequiredService<ProfileStore>(); }
        public static async Task<Host> Start()
        {
            var directory = new TemporaryDirectory(); var port = SettingsEndpointTests.Port(); var store = new FakeStore();
            var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", directory.Path, "--port", port.ToString()]);
            await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
            // Keep the real production middleware and host settings. Only the unimplemented operations store is substituted.
            app.Use(async (http, next) => { var original = http.RequestServices; http.RequestServices = new Overlay(original, store); try { await next(http); } finally { http.RequestServices = original; } });
            if (!((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
                .Any(endpoint => endpoint.RoutePattern.RawText == "/api/operations/inventory")) app.MapOperations();
            await app.StartAsync(); return new(directory, app, SettingsEndpointTests.Client(port), store);
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); directory.Dispose(); }
    }
    private sealed class Overlay(IServiceProvider original, FakeStore store) : IServiceProvider
    { public object? GetService(Type type) => type == typeof(IOperationsStore) ? store : original.GetService(type); }
    private sealed class FakeStore : IOperationsStore
    {
        public Task<OperationsVersionSnapshot> GetVersionAsync(CancellationToken ct) => Task.FromResult(Check(new OperationsVersionSnapshot(7)));
        public int Writes; public Exception? Failure; public byte[]? LastCsv; public ProfileSnapshot? LastProfile;
        public InventoryEdit? Inventory; public OperationsTimelineQuery? Query;
        public ImportApply? Apply; public ReplacementConfirmationEdit? Confirmation;
        private T Check<T>(T result) { if (Failure is { } error) throw error; return result; }
        private Task<OperationCommandReceipt> Saved(Guid command) { Writes++; return Task.FromResult(Check(new OperationCommandReceipt(command, 4, false))); }
        public Task<OperationCommandReceipt> SaveInventoryAsync(InventoryEdit edit, CancellationToken ct) { Inventory = edit; return Saved(edit.CommandId); }
        public Task<OperationCommandReceipt> SaveTrackingPolicyAsync(TrackingPolicyEdit edit, CancellationToken ct) => Saved(edit.CommandId);
        public Task<OperationCommandReceipt> SavePointAsync(PointEdit edit, CancellationToken ct) => Saved(edit.CommandId);
        public Task<OperationCommandReceipt> MovePlacementAsync(PlacementEdit edit, CancellationToken ct) => Saved(edit.CommandId);
        public Task<OperationCommandReceipt> ConfirmReplacementAsync(ReplacementConfirmationEdit edit, CancellationToken ct) { Confirmation = edit; return Saved(edit.CommandId); }
        public Task<OperationCommandReceipt> SaveWorkAsync(WorkEdit edit, CancellationToken ct) => Saved(edit.CommandId);
        public Task<RegistrationSnapshot> GetRegistrationAsync(Guid apId, CancellationToken ct) => throw new KeyNotFoundException();
        public Task<PointPage> GetPointsAsync(PointListQuery query, CancellationToken ct) => Task.FromResult(Check(new PointPage([], 0, query.Page, query.PageSize)));
        public Task<PointSnapshot> GetPointAsync(Guid pointId, CancellationToken ct) => throw new KeyNotFoundException();
        public Task<WorkSnapshot> GetWorkAsync(Guid workId, CancellationToken ct) => throw new KeyNotFoundException();
        public Task<OperationsTimelinePage> GetTimelineAsync(OperationsTimelineQuery query, CancellationToken ct)
        {
            Query = query; return Task.FromResult(Check(new OperationsTimelinePage([new(1, OperationHistoryKind.WorkAction, DateTimeOffset.UtcNow,
                EvidenceSource.Manual, "Recorded", "notes", null, "내용", null, null, [])], 1, query.ThroughEventId ?? 1, query.Page, query.PageSize)));
        }
        public Task<ImportPreview> PreviewImportAsync(ReadOnlyMemory<byte> csv, ProfileSnapshot profile, CancellationToken ct)
        {
            Writes++; LastCsv = csv.ToArray(); LastProfile = profile;
            return Task.FromResult(Check(new ImportPreview(Guid.NewGuid(), new(profile.DatasetId, profile.InstanceId, profile.ScopeVersion, profile.ConfigurationVersion, 0), DateTimeOffset.UtcNow, [], [])));
        }
        public Task<ImportApplyReceipt> ApplyImportAsync(ImportApply apply, ProfileSnapshot profile, CancellationToken ct)
        { Writes++; LastProfile = profile; Apply = apply; return Task.FromResult(Check(new ImportApplyReceipt(apply.CommandId, apply.PreviewId, false, []))); }
    }
}
