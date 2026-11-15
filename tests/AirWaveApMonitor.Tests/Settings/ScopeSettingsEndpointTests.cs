using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace AirWaveApMonitor.Tests.Settings;

[Collection("Host environment")]
public sealed class ScopeSettingsEndpointTests
{
    [Fact]
    public async Task SelectedScopeExcludesReturnedOtherApAndPreservesEarlierRun()
    {
        using var temp = new TemporaryDirectory(); var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", temp.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync(); using var client = SettingsEndpointTests.Client(port); await SettingsEndpointTests.Token(client);
        var profiles = app.Services.GetRequiredService<ProfileStore>(); var store = app.Services.GetRequiredService<ICollectionStore>();
        var first = await Apply(store, profiles.Current!, ApState.Up, ApState.Down);
        var before = await store.GetDashboardAsync(new(first.Profile.DatasetId, 1, first.RunId), default);
        var response = await client.GetAsync("/api/scope");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var save = await client.PostAsJsonAsync("/api/scope", new { expectedConfigurationVersion = 1,
            mode = "SelectedApIds", sourceApIds = new[] { "100" }, reason = "합성 원천 ID 100만 감시" });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.Equal(2, profiles.Current!.ScopeVersion);
        var next = await Apply(store, profiles.Current!, ApState.Up, ApState.Up);
        var after = await store.GetDashboardAsync(new(next.Profile.DatasetId, 2, next.RunId), default);
        Assert.Equal(1, after.TotalRows); Assert.Equal("100", Assert.Single(after.Rows).SourceApId);
        Assert.Equal(BaselineStatus.Unverified, after.Summary.BaselineStatus);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await store.GetDashboardAsync(new(first.Profile.DatasetId, 1, first.RunId), default)));
        var reopened = new ProfileStore(AirWaveApMonitor.Infrastructure.Maintenance.StoragePaths.Open(AppMode.Demo, temp.Path));
        Assert.Equal(profiles.Current.ConfigurationVersion, reopened.Current!.ConfigurationVersion);
        Assert.Equal(MonitoringScopeMode.SelectedApIds, reopened.Current.MonitoringScope!.Mode);
        Assert.Equal("100", Assert.Single(reopened.Current.MonitoringScope.SourceApIds));
        Assert.Equal("ScopeChanged", Assert.Single(reopened.Current.ScopeRevisions!.Value).Action);
        var restoreAll = await client.PostAsJsonAsync("/api/scope", new { expectedConfigurationVersion = 2,
            mode = "AllDiscovered", sourceApIds = Array.Empty<string>(), reason = "전체 발견 대상으로 재편입" });
        Assert.Equal(HttpStatusCode.OK, restoreAll.StatusCode);
        var reentry = await Apply(store, profiles.Current!, ApState.Up, ApState.Down);
        var reentered = await store.GetDashboardAsync(new(reentry.Profile.DatasetId, 3, reentry.RunId), default);
        Assert.Equal(2, reentered.TotalRows);
        Assert.Equal(before.Rows.Single(row => row.SourceApId == "200").OpenIncidentId,
            reentered.Rows.Single(row => row.SourceApId == "200").OpenIncidentId);
        Assert.Equal(0, reentered.Summary.NewDown); Assert.Equal(0, reentered.Summary.Recovered);
        await app.StopAsync();
    }

    [Fact]
    public async Task BaselineConfirmationIsPinnedAuditedAndPreservedByOtherSettings()
    {
        using var temp = new TemporaryDirectory(); var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", temp.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync(); using var client = SettingsEndpointTests.Client(port); await SettingsEndpointTests.Token(client);
        var profiles = app.Services.GetRequiredService<ProfileStore>(); var store = app.Services.GetRequiredService<ICollectionStore>();
        var first = await Apply(store, profiles.Current!);
        var second = await Apply(store, profiles.Current!);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/scope/confirm", new {
            expectedConfigurationVersion = 1, runId = first.RunId, comparedTargetCount = 2, reason = "이전 회차" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/scope/confirm", new {
            expectedConfigurationVersion = 1, runId = second.RunId, comparedTargetCount = 1, reason = "대수 불일치" })).StatusCode);
        Assert.Equal(1, profiles.Current!.ConfigurationVersion);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/scope/confirm", new {
            expectedConfigurationVersion = 1, runId = second.RunId, comparedTargetCount = 2, reason = "합성 관리 대장 두 ID 대조" })).StatusCode);
        Assert.Equal(2, profiles.Current.ConfigurationVersion); Assert.Equal(1, profiles.Current.ScopeVersion);
        Assert.Equal(BaselineStatus.Confirmed, profiles.Current.BaselineStatus);
        var revision = Assert.Single(profiles.Current.ScopeRevisions!.Value);
        Assert.Equal("BaselineConfirmed", revision.Action); Assert.Equal(second.RunId, revision.ComparedRunId);
        Assert.Equal("합성 관리 대장 두 ID 대조", revision.Reason);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/scope/confirm", new {
            expectedConfigurationVersion = 1, runId = second.RunId, comparedTargetCount = 2, reason = "중복 요청" })).StatusCode);
        await profiles.UpdateAsync(profiles.Current.Origin, 6, new(ProxyMode.Direct), 2, default);
        await profiles.UpdateEnrichmentAsync(16, true, false, 3, default);
        Assert.Equal(revision, Assert.Single(profiles.Current.ScopeRevisions!.Value));
        Assert.Equal(new[] { "100", "200" }, profiles.Current.BaselineConfirmedSourceApIds!.Value);
        var confirmed = await Apply(store, profiles.Current!);
        Assert.Equal(BaselineStatus.Confirmed, (await store.GetDashboardAsync(new(confirmed.Profile.DatasetId, 1, confirmed.RunId), default)).Summary.BaselineStatus);
        Assert.Equal(BaselineStatus.Unverified, (await store.GetDashboardAsync(new(first.Profile.DatasetId, 1, first.RunId), default)).Summary.BaselineStatus);
        var reopened = new ProfileStore(AirWaveApMonitor.Infrastructure.Maintenance.StoragePaths.Open(AppMode.Demo, temp.Path));
        Assert.Equal(profiles.Current.ScopeRevisions, reopened.Current!.ScopeRevisions);
        await profiles.CredentialsChangedAsync(default);
        Assert.Equal(BaselineStatus.Unverified, profiles.Current!.BaselineStatus);
        Assert.Equal(MonitoringScopeMode.AllDiscovered, profiles.Current.MonitoringScope!.Mode);
        Assert.Equal(revision, Assert.Single(profiles.Current.ScopeRevisions!.Value));
        await app.StopAsync();
    }

    [Theory]
    [InlineData("SelectedApIds", "[]", "합성 범위")]
    [InlineData("SelectedApIds", "[\"unknown-id\"]", "합성 범위")]
    [InlineData("SelectedApIds", "[\"100\",\"100\"]", "합성 범위")]
    [InlineData("SelectedApIds", "[\"100\"]", "")]
    [InlineData("AllDiscovered", "[\"100\"]", "합성 범위")]
    [InlineData("AllDiscovered", "[]", "변경 없음")]
    [InlineData("1", "[\"100\"]", "숫자 열거 거부")]
    public async Task InvalidScopeDoesNotChangeProfileOrHistory(string mode, string ids, string reason)
    {
        using var temp = new TemporaryDirectory(); var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", temp.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync(); using var client = SettingsEndpointTests.Client(port); await SettingsEndpointTests.Token(client);
        var profiles = app.Services.GetRequiredService<ProfileStore>(); var store = app.Services.GetRequiredService<ICollectionStore>();
        await profiles.EnsureInitializedAsync(default); await Apply(store, profiles.Current!);
        var before = File.ReadAllBytes(Path.Combine(temp.Path, "profile.json"));
        var response = await client.PostAsJsonAsync("/api/scope", new { expectedConfigurationVersion = 1, mode,
            sourceApIds = JsonSerializer.Deserialize<string[]>(ids), reason });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(temp.Path, "profile.json")));
        Assert.Equal(1, profiles.Current!.ScopeVersion);
        await app.StopAsync();
    }

    [Fact]
    public async Task NewDiscoveryInvalidatesComparedInventoryAndFrozenRunKeepsItsScope()
    {
        using var temp = new TemporaryDirectory(); var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", temp.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync(); using var client = SettingsEndpointTests.Client(port); await SettingsEndpointTests.Token(client);
        var profiles = app.Services.GetRequiredService<ProfileStore>(); var store = app.Services.GetRequiredService<ICollectionStore>();
        var first = await Apply(store, profiles.Current!);
        var view = await app.Services.GetRequiredService<AirWaveApMonitor.Infrastructure.Storage.SqliteCollectionStore>().GetScopeSettingsAsync(profiles.Current!, default);
        await profiles.ConfirmBaselineAsync(view.ComparisonRun!, 2, "합성 대장 대조", 1, default);
        var frozen = await store.BeginRunAsync(profiles.Current!, CollectionKind.ApStatus, RunTrigger.Manual, default);
        // A later configuration cannot change the already frozen run or its complete returned inventory.
        await profiles.UpdateScopeAsync(new(MonitoringScopeMode.SelectedApIds, ["100"]), ["100", "200"], "합성 선택", 2, default);
        var now = DateTimeOffset.UtcNow;
        await store.ApplyApBatchAsync(frozen, new(RunOutcome.Succeeded,
            [Observation("100", ApState.Up, now), Observation("200", ApState.Up, now), Observation("300", ApState.Up, now)], [], now,
            ObservationCompleteness.Complete, []), default);
        var expanded = await store.GetDashboardAsync(new(first.Profile.DatasetId, 1, frozen.RunId), default);
        Assert.Equal(3, expanded.TotalRows); Assert.Equal(BaselineStatus.Unverified, expanded.Summary.BaselineStatus);
        var selected = await Apply(store, profiles.Current!);
        Assert.Equal(1, (await store.GetDashboardAsync(new(first.Profile.DatasetId, 2, selected.RunId), default)).TotalRows);
        await app.StopAsync();
    }

    [Fact]
    public async Task ScopeReadWaitsForConcurrentSettingsReadWithoutReportingAWriteConflict()
    {
        using var temp = new TemporaryDirectory(); var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", temp.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync(); using var client = SettingsEndpointTests.Client(port);
        var profiles = app.Services.GetRequiredService<ProfileStore>();
        await profiles.OperationGate.WaitAsync();
        Task<HttpResponseMessage>? read = null;
        try
        {
            read = client.GetAsync("/api/scope");
            Assert.NotSame(read, await Task.WhenAny(read, Task.Delay(100)));
        }
        finally { profiles.OperationGate.Release(); }
        Assert.Equal(HttpStatusCode.OK, (await read!).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task NoObservationFailedRunAndStaleConfigurationCannotConfirmBaseline()
    {
        using var temp = new TemporaryDirectory(); var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", temp.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync(); using var client = SettingsEndpointTests.Client(port); await SettingsEndpointTests.Token(client);
        var profiles = app.Services.GetRequiredService<ProfileStore>(); var store = app.Services.GetRequiredService<ICollectionStore>();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/scope/confirm", new {
            expectedConfigurationVersion = 1, runId = 1, comparedTargetCount = 2, reason = "관측 전" })).StatusCode);
        await Apply(store, profiles.Current!);
        var failed = await Apply(store, profiles.Current!, null, null);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/scope/confirm", new {
            expectedConfigurationVersion = 1, runId = failed.RunId, comparedTargetCount = 2, reason = "실패 회차" })).StatusCode);
        var valid = await Apply(store, profiles.Current!);
        await profiles.UpdateAsync(profiles.Current!.Origin, 6, new(), 1, default);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/scope/confirm", new {
            expectedConfigurationVersion = 2, runId = valid.RunId, comparedTargetCount = 2, reason = "설정 변경 전 관측" })).StatusCode);
        await profiles.OperationGate.WaitAsync();
        try
        {
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/scope", new {
                expectedConfigurationVersion = 2, mode = "SelectedApIds", sourceApIds = new[] { "100" }, reason = "수집 중" })).StatusCode);
        }
        finally { profiles.OperationGate.Release(); }
        Assert.Equal(2, profiles.Current.ConfigurationVersion); Assert.Equal(BaselineStatus.Unverified, profiles.Current.BaselineStatus);
        await app.StopAsync();
    }

    internal static async Task<CollectionContext> Apply(ICollectionStore store, ProfileSnapshot profile,
        ApState? first = ApState.Up, ApState? second = ApState.Up)
    {
        var context = await store.BeginRunAsync(profile, CollectionKind.ApStatus, RunTrigger.Manual, default);
        var now = DateTimeOffset.UtcNow;
        await store.ApplyApBatchAsync(context, new(RunOutcome.Succeeded, [Observation("100", first, now), Observation("200", second, now)], [], now,
            ObservationCompleteness.Complete, []), default);
        return context;
    }
    private static ApObservation Observation(string id, ApState? state, DateTimeOffset now)
    {
        var evidence = new FieldEvidence(FieldAvailability.Provided, state is null ? FieldValidity.Unknown : FieldValidity.Valid, EvidenceSource.Demo, now);
        return new(id, state, new("SYN-" + id, null), new("Synthetic AP " + id, "192.0.2." + (id == "100" ? "1" : "2"), null, null),
            new(), new(), now, null, null, ImmutableDictionary<string, FieldEvidence>.Empty.Add("is_up", evidence).Add("name", evidence));
    }
}
