using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Infrastructure.Storage;
using AirWaveApMonitor.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AirWaveApMonitor.Tests.Settings;

[Collection("Host environment")]
public sealed class ScopeIndependentReviewTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RestoreGap_PreservesTheBackedUpScopeDefinitionAndConfirmationEvidence()
    {
        using var f = new Fixture();
        await f.Apply(["100", "200"]);
        await f.Profiles.UpdateScopeAsync(new(MonitoringScopeMode.SelectedApIds, ["100"]), ["100", "200"], "selected", 1, default);
        await f.Apply(["100", "200"]);
        var view = await f.Store.GetScopeSettingsAsync(f.Profiles.Current!, default);
        await f.Profiles.ConfirmBaselineAsync(view.ComparisonRun!, 1, "compared one AP", 2, default);
        var backedUp = await f.Apply(["100", "200"]);
        var service = new BackupService(f.Location, f.Gate, f.Clock);
        var backup = await service.CreateAsync(BackupKind.PreUpdate, default);
        await f.Profiles.UpdateScopeAsync(new(MonitoringScopeMode.AllDiscovered, []), ["100", "200"], "return to all", 3, default);
        await f.Apply(["100", "200"]);
        var profileBefore = File.ReadAllBytes(Path.Combine(f.Location.DataDirectory, "profile.json"));
        await service.RestoreAsync(backup.BackupPath, default);
        var reopened = new SqliteCollectionStore(f.Location, f.Gate, f.Clock);
        await reopened.InitializeAsync(default);
        using var connection = f.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT context_json FROM runs WHERE kind=0 ORDER BY run_id DESC LIMIT 1";
        var restored = JsonSerializer.Deserialize<CollectionContext>((string)command.ExecuteScalar()!)!;
        Assert.Equal(RunTrigger.DatabaseRestore, restored.Trigger);
        Assert.Equal(backedUp.Profile.ScopeVersion, restored.Profile.ScopeVersion);
        Assert.Equal(profileBefore, File.ReadAllBytes(Path.Combine(f.Location.DataDirectory, "profile.json")));
        output.WriteLine("Restore source scope={0}, gap scope definition present={1}, runtime profile preserved=true",
            backedUp.Profile.ScopeVersion, restored.Profile.MonitoringScope is not null);
        Assert.Equal(JsonSerializer.Serialize(backedUp.Profile.MonitoringScope), JsonSerializer.Serialize(restored.Profile.MonitoringScope));
        Assert.Equal(JsonSerializer.Serialize(backedUp.Profile.ScopeRevisions), JsonSerializer.Serialize(restored.Profile.ScopeRevisions));
        Assert.Equal(backedUp.Profile.BaselineConfirmedSourceApIds, restored.Profile.BaselineConfirmedSourceApIds);
    }

    [Fact]
    public async Task SelectedScope_AcceptsFiveThousandKnownIdsThroughProtectedHttpRoute()
    {
        using var directory = new TemporaryDirectory(); var port = SettingsEndpointTests.Port();
        await using var app = AppBootstrap.Build(["--mode", "demo", "--data-dir", directory.Path, "--port", port.ToString()]);
        await app.Services.GetRequiredService<ICollectorControl>().SetPausedAsync(true, default);
        await app.StartAsync();
        using var client = SettingsEndpointTests.Client(port); await SettingsEndpointTests.Token(client);
        var profiles = app.Services.GetRequiredService<ProfileStore>();
        var store = app.Services.GetRequiredService<ICollectionStore>();
        var ids = Enumerable.Range(1, 5000).Select(i => i.ToString()).ToArray();
        var context = await store.BeginRunAsync(profiles.Current!, CollectionKind.ApStatus, RunTrigger.Manual, default);
        var now = DateTimeOffset.UtcNow;
        await store.ApplyApBatchAsync(context, new(RunOutcome.Succeeded, ids.Select(id => Observation(id, now)).ToImmutableArray(),
            [], now, ObservationCompleteness.Complete, []), default);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { expectedConfigurationVersion = 1, mode = "SelectedApIds",
            sourceApIds = ids, reason = "5000 known AP IDs" });
        Assert.True(bytes.Length > 32 * 1024);
        using var body = new ByteArrayContent(bytes); body.Headers.ContentType = new("application/json");
        var response = await client.PostAsync("/api/scope", body);
        output.WriteLine("Known IDs={0}, JSON bytes={1}, HTTP={2}", ids.Length, bytes.Length, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5000, profiles.Current!.MonitoringScope!.SourceApIds.Length);
        Assert.Equal(2, profiles.Current.ScopeVersion);
        await app.StopAsync();
    }

    [Fact]
    public async Task SelectedScope_DoesNotTreatAnOutsideInvalidRecordAsItsOwnPartialObservation()
    {
        using var f = new Fixture();
        await f.Apply(["100", "200"]);
        await f.Profiles.UpdateScopeAsync(new(MonitoringScopeMode.SelectedApIds, ["100"]), ["100", "200"], "select 100", 1, default);
        const string xml = "<amp:amp_ap_list xmlns:amp=\"http://www.airwave.com\"><ap id=\"100\"><device_category>thin_ap</device_category><is_up>true</is_up></ap><ap id=\"200\"><device_category>thin_ap</device_category><is_up>invalid-outside-scope</is_up></ap></amp:amp_ap_list>";
        var batch = new ApListParser(EvidenceSource.Demo).Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), f.Clock.GetUtcNow());
        Assert.Equal(RunOutcome.Partial, batch.Outcome); // Correct whole-response parsing result.
        var run = await f.Store.BeginRunAsync(f.Profiles.Current!, CollectionKind.ApStatus, RunTrigger.Manual, default);
        var receipt = await f.Store.ApplyApBatchAsync(run, batch, default);
        var view = await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 2, run.RunId), default);
        Assert.Equal(1, view.TotalRows); Assert.Equal(1, view.Summary.ConfirmedUp); Assert.Equal(0, view.Summary.Unknown);
        output.WriteLine("Selected APs={0}, Up={1}, Unknown={2}, scoped outcome={3}, errors={4}",
            view.TotalRows, view.Summary.ConfirmedUp, view.Summary.Unknown, receipt.Outcome, view.Errors.Length);
        Assert.Equal(RunOutcome.Succeeded, receipt.Outcome);
    }

    private static ApObservation Observation(string id, DateTimeOffset now)
    {
        var field = new FieldEvidence(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.Demo, now);
        return new(id, ApState.Up, new("serial-" + id, null), new("AP-" + id, null, null, null), new(), new(), now, null, null,
            ImmutableDictionary<string, FieldEvidence>.Empty.Add("is_up", field).Add("name", field));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory directory = new();
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ManualTimeProvider Clock { get; } = new();
        public StorageLocation Location { get; }
        public ProfileStore Profiles { get; }
        public SqliteCollectionStore Store { get; }
        public Fixture()
        {
            Location = StoragePaths.Open(AppMode.Demo, directory.Path);
            Profiles = new(Location); Store = new(Location, Gate, Clock);
        }
        public async Task<CollectionContext> Apply(string[] ids)
        {
            await Profiles.EnsureInitializedAsync(default);
            var context = await Store.BeginRunAsync(Profiles.Current!, CollectionKind.ApStatus, RunTrigger.Manual, default);
            await Store.ApplyApBatchAsync(context, new(RunOutcome.Succeeded,
                ids.Select(id => Observation(id, Clock.GetUtcNow())).ToImmutableArray(), [], Clock.GetUtcNow(), ObservationCompleteness.Complete, []), default);
            Clock.Advance(TimeSpan.FromMinutes(5)); return context;
        }
        public SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(Location.DataDirectory, "monitor.db"), Pooling = false }.ToString());
            connection.Open(); return connection;
        }
        public void Dispose() { Gate.Dispose(); directory.Dispose(); }
    }
}
