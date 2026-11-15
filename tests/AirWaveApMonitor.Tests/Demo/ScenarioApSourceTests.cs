using System.Collections.Immutable;
using System.Xml.Linq;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Demo;

namespace AirWaveApMonitor.Tests.Demo;

public sealed class ScenarioApSourceTests
{
    [Fact]
    public async Task Timeline_Provides2048DistinctDevicesAndRealPrehistoryForVisibleTransitions()
    {
        var source = new ScenarioApSource(new FixedTimeProvider());
        var baseline = await source.CollectAsync(Context(1), default);
        var previous = await source.CollectAsync(Context(2), default);
        var normal = await source.CollectAsync(Context(3), default);
        Assert.Equal(2048, normal.Observations.Length);
        Assert.Equal(2048, normal.Observations.Select(ap => ap.SourceApId).Distinct().Count());
        Assert.Equal(8, normal.Observations.Count(ap => ap.State == ApState.Down));
        Assert.Null(Find(previous, "6").State); // AP 6 is first-confirmed Down at the visible run.
        Assert.Equal(ApState.Down, Find(normal, "6").State);
        foreach (var id in new[] { "7", "8" })
        {
            Assert.Equal(ApState.Up, Find(previous, id).State);
            Assert.Equal(ApState.Down, Find(normal, id).State);
        }
        foreach (var id in new[] { "9", "10" })
        {
            Assert.Equal(ApState.Up, Find(baseline, id).State);
            Assert.Equal(ApState.Down, Find(previous, id).State);
            Assert.Equal(ApState.Up, Find(normal, id).State);
        }
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 2, 30, 0, TimeSpan.Zero), normal.CollectedAtUtc);
    }

    [Fact]
    public async Task FiveVisibleScenarios_KeepFailureAndOmissionDifferentFromUp()
    {
        var source = new ScenarioApSource(new FixedTimeProvider());
        var batches = new List<ApCollectionBatch>();
        for (var run = 1; run <= 7; run++) batches.Add(await source.CollectAsync(Context(run), default));
        Assert.Equal(10, batches[3].Observations.Count(ap => ap.State == ApState.Down));
        Assert.Equal(ApState.Up, Find(batches[4], "2").State);
        Assert.Equal(ApState.Up, Find(batches[4], "4").State);
        Assert.Equal(RunOutcome.Failed, batches[5].Outcome);
        Assert.Empty(batches[5].Observations);
        Assert.Contains(batches[5].Errors, error => error.Category == CollectionErrorCategory.Timeout);
        Assert.Equal(RunOutcome.Partial, batches[6].Outcome);
        Assert.Equal(2045, batches[6].Observations.Length);
        Assert.DoesNotContain(batches[6].Observations, ap => ap.SourceApId is "3" or "20" or "21");
        Assert.Equal(7, batches[6].Observations.Count(ap => ap.State == ApState.Down));
        Assert.Equal(["3", "20", "21"], batches[6].ExcludedRecords.Select(record => record.SourceApId));
    }

    [Fact]
    public void DemoXml_PreservesIdsAndDocumentedSyntheticFieldShape()
    {
        var normal = ScenarioApSource.Frames.Single(frame => frame.Name == "정상");
        var root = XDocument.Parse(ScenarioApSource.CreateXml(normal)).Root!;
        Assert.Equal(XName.Get("amp_ap_list", "http://www.airwave.com"), root.Name);
        var devices = root.Elements("ap").ToArray();
        Assert.Equal(2048, devices.Length);
        var first = devices.Single(ap => (string?)ap.Attribute("id") == "1");
        Assert.Equal("thin_ap", (string?)first.Element("device_category"));
        Assert.Equal("false", (string?)first.Element("is_up"));
        Assert.Equal("192.0.2.1", (string?)first.Element("lan_ip"));
        Assert.Equal("DEMO-000001", (string?)first.Element("serial_number"));
    }

    [Fact]
    public async Task CancelledCollection_DoesNotConsumeScenario()
    {
        var source = new ScenarioApSource(new FixedTimeProvider());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.CollectAsync(Context(1), new CancellationToken(true)));
        var first = await source.CollectAsync(Context(2), default);
        Assert.Equal(ApState.Up, Find(first, "9").State);
    }

    [Fact]
    public async Task DemoSource_RejectsLiveOrigin()
    {
        var source = new ScenarioApSource(new FixedTimeProvider());
        var profile = new ProfileSnapshot(Guid.NewGuid(), Guid.NewGuid(), new Uri("https://live.example.invalid"),
            1, 1, [], BaselineStatus.Unverified, new CollectionSettings());
        var context = Context(1) with { Profile = profile };
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.CollectAsync(context, default));
    }

    private static ApObservation Find(ApCollectionBatch batch, string id) => batch.Observations.Single(ap => ap.SourceApId == id);
    private static CollectionContext Context(long runId)
    {
        var profile = new ProfileSnapshot(Guid.Parse("221abec4-1807-4428-a4c6-a1cd594f5ef2"),
            Guid.Parse("f17d34c1-d405-4a09-b1a2-e94aff94b21c"), new Uri("https://demo.example.invalid"),
            1, 1, Enumerable.Range(1, 2048).Select(id => new ExpectedTarget(id.ToString())), BaselineStatus.Confirmed, new CollectionSettings());
        return new CollectionContext(runId, profile, CollectionKind.ApStatus, RunTrigger.Manual,
            DateTimeOffset.Parse("2026-11-15T02:30:00Z"), DateTimeOffset.Parse("2026-11-15T02:32:00Z"));
    }
    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 15, 2, 30, 0, TimeSpan.Zero);
    }
}
