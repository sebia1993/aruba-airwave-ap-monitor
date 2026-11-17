using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Demo;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Settings;
using AirWaveApMonitor.Infrastructure.Storage;
using AirWaveApMonitor.Tests.Support;

namespace AirWaveApMonitor.Tests.Storage;

public sealed class ScopedRecordQualityTests
{
    [Theory]
    [InlineData("<ap id='200'><device_category>thin_ap</device_category><is_up>bad</is_up></ap>", 1, 0)]
    [InlineData("<ap id='200'><device_category>thin_ap</device_category><is_up>true</is_up></ap><ap id='200'><device_category>thin_ap</device_category><is_up>false</is_up></ap>", 0, 2)]
    [InlineData("<ap id='200'><device_category>switch</device_category><is_up>true</is_up></ap>", 0, 1)]
    [InlineData("<ap id='200'><device_category>thin_ap</device_category><is_up>true</is_up><name>a</name><name>b</name></ap>", 1, 0)]
    public async Task AttributedOutsideErrors_DoNotDowngradeSelectedAp_AndSourceQualityRemainsVisible(
        string outside, int outsideObservations, int outsideExcluded)
    {
        using var f = await Fixture.Create();
        var batch = f.Parse(Ap("100") + outside);
        Assert.Equal(RunOutcome.Partial, batch.Outcome);
        var result = await f.Apply(batch);
        Assert.Equal(RunOutcome.Succeeded, result.Receipt.Outcome);
        Assert.Equal(1, result.View.Summary.ConfirmedUp); Assert.Equal(0, result.View.Summary.Unknown);
        Assert.Empty(result.View.Errors); Assert.Empty(result.View.SourceEvidence!.ExcludedRecords);
        var source = JsonSerializer.SerializeToElement(result.View.SourceEvidence);
        Assert.Equal((int)RunOutcome.Partial, source.GetProperty("SourceOutcome").GetInt32());
        Assert.Equal((int)ObservationCompleteness.Partial, source.GetProperty("SourceCompleteness").GetInt32());
        Assert.Equal(outsideObservations, source.GetProperty("ScopeExcludedApCount").GetInt32());
        Assert.Equal(outsideExcluded, source.GetProperty("ScopeExcludedRecordCount").GetInt32());
        Assert.Equal(1 + outsideObservations, result.View.SourceEvidence.ParsedApCount);
        using var connection = f.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT received_records,excluded_records,error_counts_json FROM run_execution_details WHERE run_id=$run";
        command.Parameters.AddWithValue("$run", result.Run.RunId);
        using (var reader = command.ExecuteReader())
        {
            Assert.True(reader.Read());
            Assert.Equal(1 + outsideObservations, reader.GetInt32(0));
            Assert.Equal(outsideExcluded, reader.GetInt32(1));
            var storedErrors = JsonSerializer.Deserialize<JsonElement>(reader.GetString(2));
            Assert.Equal(batch.Errors.Sum(error => error.Count), storedErrors.EnumerateArray().Sum(error => error.GetProperty("Count").GetInt32()));
        }
        Assert.Equal(result.Run.RunId, (await f.Store.GetSuccessTimesAsync(f.Location.DatasetId, 2, default)).LastCompleteRunId);
    }

    [Theory]
    [InlineData("<ap><device_category>thin_ap</device_category><is_up>true</is_up></ap>")]
    [InlineData("<ap id='bad'><device_category>thin_ap</device_category><is_up>true</is_up></ap>")]
    [InlineData("<ap id=' '><device_category>thin_ap</device_category><is_up>true</is_up></ap>")]
    public async Task UnattributableInvalidIds_RemainGlobalErrors(string invalid)
    {
        using var f = await Fixture.Create();
        var result = await f.Apply(f.Parse(Ap("100") + invalid));
        Assert.Equal(RunOutcome.Partial, result.Receipt.Outcome);
        Assert.NotEmpty(result.View.Errors);
        Assert.Single(result.View.SourceEvidence!.ExcludedRecords);
        Assert.Null((await f.Store.GetSuccessTimesAsync(f.Location.DatasetId, 2, default)).LastCompleteRunId);
    }

    [Theory]
    [InlineData("<ap id='100'><device_category>thin_ap</device_category><is_up>bad</is_up></ap>", RunOutcome.Failed)]
    [InlineData("<ap id='100'><device_category>switch</device_category><is_up>true</is_up></ap>", RunOutcome.Failed)]
    [InlineData("<ap id='100'><device_category>thin_ap</device_category><is_up>true</is_up><name>a</name><name>b</name></ap>", RunOutcome.Partial)]
    [InlineData("<ap id='100'><device_category>thin_ap</device_category><is_up>true</is_up></ap><ap id='100'><device_category>thin_ap</device_category><is_up>false</is_up></ap>", RunOutcome.Failed)]
    public async Task SelectedRecordErrors_AreNotRemoved(string selected, RunOutcome expected)
    {
        using var f = await Fixture.Create();
        var result = await f.Apply(f.Parse(selected + Ap("200")));
        Assert.Equal(expected, result.Receipt.Outcome); Assert.NotEmpty(result.View.Errors);
        Assert.Equal(1, result.View.TotalRows);
        Assert.Null((await f.Store.GetSuccessTimesAsync(f.Location.DatasetId, 2, default)).LastCompleteRunId);
    }

    [Theory]
    [InlineData(ObservationCompleteness.Partial)]
    [InlineData(ObservationCompleteness.Unknown)]
    public async Task LegacyUnexplainedPartial_IsNeverPromoted(ObservationCompleteness completeness)
    {
        using var f = await Fixture.Create(); var parsed = f.Parse(Ap("100") + Ap("200"));
        var legacy = new ApCollectionBatch(RunOutcome.Partial, parsed.Observations, [], parsed.CollectedAtUtc, completeness, []);
        var result = await f.Apply(legacy);
        Assert.Equal(RunOutcome.Partial, result.Receipt.Outcome);
        Assert.Null((await f.Store.GetSuccessTimesAsync(f.Location.DatasetId, 2, default)).LastCompleteRunId);
    }

    [Theory]
    [InlineData(RunOutcome.Partial, ObservationCompleteness.Partial, false)]
    [InlineData(RunOutcome.Partial, ObservationCompleteness.Unknown, true)]
    [InlineData(RunOutcome.Succeeded, ObservationCompleteness.Partial, false)]
    public async Task AttributedButUnexplainedOrUnknownBatch_IsNeverPromoted(RunOutcome outcome, ObservationCompleteness completeness, bool outsideError)
    {
        using var f = await Fixture.Create();
        var parsed = f.Parse(Ap("100") + Ap("200"));
        var ambiguous = parsed with
        {
            Outcome = outcome, Completeness = completeness,
            Errors = outsideError ? [new(CollectionErrorCategory.InvalidField, 1, "outside") { SourceApId = "200" }] : []
        };
        Assert.True(ambiguous.RecordErrorAttributionComplete);
        var result = await f.Apply(ambiguous);
        Assert.Equal(RunOutcome.Partial, result.Receipt.Outcome);
        Assert.Null((await f.Store.GetSuccessTimesAsync(f.Location.DatasetId, 2, default)).LastCompleteRunId);
    }

    [Fact]
    public async Task GlobalErrorInOtherwiseAttributedPartialBatch_IsNeverRemoved()
    {
        using var f = await Fixture.Create();
        var parsed = f.Parse(Ap("100") + "<ap id='200'><device_category>thin_ap</device_category><is_up>bad</is_up></ap>");
        var result = await f.Apply(parsed with { Errors = parsed.Errors.Add(new(CollectionErrorCategory.Transport, 1, "global")) });
        Assert.Equal(RunOutcome.Partial, result.Receipt.Outcome);
        var error = Assert.Single(result.View.Errors);
        Assert.Equal(CollectionErrorCategory.Transport, error.Category); Assert.Null(error.SourceApId);
    }

    [Theory]
    [InlineData(CollectionErrorCategory.Transport)]
    [InlineData(CollectionErrorCategory.Timeout)]
    [InlineData(CollectionErrorCategory.ResponseTooLarge)]
    [InlineData(CollectionErrorCategory.InvalidResponse)]
    public async Task GlobalFailedBatch_CannotBecomeHealthyEvenWithAnOtherwiseValidSelectedObservation(CollectionErrorCategory category)
    {
        using var f = await Fixture.Create(); var parsed = f.Parse(Ap("100") + Ap("200"));
        var failed = new ApCollectionBatch(RunOutcome.Failed, parsed.Observations, [], parsed.CollectedAtUtc,
            ObservationCompleteness.Unknown, [new(category, 1, "global failure")]);
        var result = await f.Apply(failed);
        Assert.Equal(RunOutcome.Failed, result.Receipt.Outcome); Assert.Null(result.View.Summary.ConfirmedUp);
        Assert.Equal(1, result.View.Summary.Unknown); Assert.Contains(result.View.Errors, error => error.Category == category);
    }

    [Theory]
    [InlineData("<html><form>login</form></html>")]
    [InlineData("<amp:amp_ap_list xmlns:amp='http://www.airwave.com'><ap id='100'>")]
    [InlineData("<!DOCTYPE amp_ap_list [<!ENTITY x 'fake'>]><amp:amp_ap_list xmlns:amp='http://www.airwave.com'/>")]
    public async Task WholeDocumentFailures_AreNeverPromoted(string xml)
    {
        using var f = await Fixture.Create();
        var parsed = new ApListParser().Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), f.Clock.GetUtcNow());
        var result = await f.Apply(parsed);
        Assert.Equal(RunOutcome.Failed, result.Receipt.Outcome); Assert.Equal(1, result.View.Summary.Unknown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DemoCheckpointedFrames_KeepGlobalFailureAndSelectedMissingOnly(bool includeMissingAp)
    {
        using var f = await Fixture.Create(includeMissingAp ? ["3", "100"] : ["100"]);
        var source = new ScenarioApSource(f.Clock, f.Store);
        for (var index = 0; index < ScenarioApSource.Frames.Length; index++)
        {
            var run = await f.Store.BeginRunAsync(f.Profiles.Current!, CollectionKind.ApStatus, RunTrigger.Manual, default);
            var batch = await source.CollectAsync(run, default);
            var receipt = await f.Store.ApplyApBatchAsync(run, batch, default);
            var expected = index == 5 ? RunOutcome.Failed : index == 6 && includeMissingAp ? RunOutcome.Partial : RunOutcome.Succeeded;
            Assert.Equal(expected, receipt.Outcome);
            Assert.Equal(index, (await f.Store.GetSourceCheckpointAsync(f.Location.DatasetId, ScenarioApSource.CheckpointSource, default))!.Position);
            var view = await f.Store.GetDashboardAsync(new(f.Location.DatasetId, 2, run.RunId), default);
            Assert.Equal(includeMissingAp ? 2 : 1, view.TotalRows);
            if (index == 6 && includeMissingAp) Assert.Equal(1, view.Summary.Unknown);
            f.Clock.Advance(TimeSpan.FromMinutes(5));
        }
    }

    private static string Ap(string id) => $"<ap id='{id}'><device_category>thin_ap</device_category><name>AP-{id}</name><is_up>true</is_up></ap>";
    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory directory = new();
        private readonly SemaphoreSlim gate = new(1, 1);
        public StorageLocation Location { get; }
        public ManualTimeProvider Clock { get; } = new();
        public SqliteCollectionStore Store { get; }
        public ProfileStore Profiles { get; }
        private Fixture() { Location = StoragePaths.Open(AppMode.Demo, directory.Path); Store = new(Location, gate, Clock); Profiles = new(Location); }
        public static async Task<Fixture> Create(string[]? selected = null)
        {
            var fixture = new Fixture(); await fixture.Profiles.EnsureInitializedAsync(default);
            await fixture.Apply(fixture.Parse(Ap("3") + Ap("100") + Ap("200")));
            await fixture.Profiles.UpdateScopeAsync(new(MonitoringScopeMode.SelectedApIds, (selected ?? ["100"]).ToImmutableArray()),
                ["3", "100", "200"], "synthetic selected scope", 1, default);
            return fixture;
        }
        public ApCollectionBatch Parse(string aps) => new ApListParser(EvidenceSource.Demo).Parse(new MemoryStream(Encoding.UTF8.GetBytes(
            "<amp:amp_ap_list xmlns:amp='http://www.airwave.com'>" + aps + "</amp:amp_ap_list>")), Clock.GetUtcNow());
        public async Task<(CollectionContext Run, CommitReceipt Receipt, DashboardSnapshot View)> Apply(ApCollectionBatch batch)
        {
            var run = await Store.BeginRunAsync(Profiles.Current!, CollectionKind.ApStatus, RunTrigger.Manual, default);
            var receipt = await Store.ApplyApBatchAsync(run, batch, default);
            var view = await Store.GetDashboardAsync(new(Location.DatasetId, Profiles.Current!.ScopeVersion, run.RunId), default);
            Clock.Advance(TimeSpan.FromMinutes(5)); return (run, receipt, view);
        }
        public Microsoft.Data.Sqlite.SqliteConnection Open()
        {
            var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = Path.Combine(Location.DataDirectory, "monitor.db"), Pooling = false }.ToString());
            connection.Open(); return connection;
        }
        public void Dispose() { gate.Dispose(); directory.Dispose(); }
    }
}
