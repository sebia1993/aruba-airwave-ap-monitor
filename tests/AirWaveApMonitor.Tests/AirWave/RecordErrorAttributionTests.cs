using System.Collections.Immutable;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using static AirWaveApMonitor.Tests.AirWave.ApListParserTests;

namespace AirWaveApMonitor.Tests.AirWave;

public sealed class RecordErrorAttributionTests
{
    [Fact]
    public void CompleteParsedDocument_AttributesFieldAndExcludedErrorsWithoutMergingApIds()
    {
        var batch = Parse(Xml(Ap("100", "true") + Ap("200", "bad") + Ap("201", "true", "switch") +
            Ap("300") + Ap("300") + Ap("301") + Ap("301") + Ap("bad") + Ap("invalid")));
        Assert.True(batch.RecordErrorAttributionComplete);
        Assert.Equal(2, batch.Errors.Where(e => e.SourceApId == "200").Sum(e => e.Count));
        Assert.Equal(CollectionErrorCategory.Unverified, Assert.Single(batch.Errors.Where(e => e.SourceApId == "201")).Category);
        Assert.Equal(2, Assert.Single(batch.Errors.Where(e => e.SourceApId == "300")).Count);
        Assert.Equal(2, Assert.Single(batch.Errors.Where(e => e.SourceApId == "301")).Count);
        var invalidIds = Assert.Single(batch.Errors.Where(e => e.SourceApId is null));
        Assert.Equal(CollectionErrorCategory.InvalidField, invalidIds.Category); Assert.Equal(2, invalidIds.Count);
        Assert.Equal(9, batch.Errors.Sum(e => e.Count));
    }

    [Theory]
    [InlineData("<broken>")]
    [InlineData("<html>login</html>")]
    [InlineData("<!DOCTYPE amp_ap_list [<!ENTITY x 'fake'>]><amp:amp_ap_list xmlns:amp='http://www.airwave.com'/>")]
    public void WholeDocumentFailure_HasNoCompleteRecordAttribution(string xml)
    {
        var batch = Parse(xml);
        Assert.Equal(RunOutcome.Failed, batch.Outcome); Assert.False(batch.RecordErrorAttributionComplete);
        Assert.All(batch.Errors, error => Assert.Null(error.SourceApId));
    }

    [Fact]
    public void ResponseLimitFailure_HasNoCompleteRecordAttribution()
    {
        var batch = Parse(Xml(Ap(extra: "<syslocation>" + new string('x', 64 * 1024 * 1024) + "</syslocation>")));
        Assert.Equal(RunOutcome.Failed, batch.Outcome); Assert.False(batch.RecordErrorAttributionComplete);
        Assert.Contains(batch.Errors, e => e.Category == CollectionErrorCategory.ResponseTooLarge && e.SourceApId is null);
    }

    [Fact]
    public void NewOptionalMetadata_PreservesLegacyJsonAndDefaultsOnDeserialization()
    {
        var error = new CollectionError(CollectionErrorCategory.InvalidField, 2, "safe");
        var originalError = JsonSerializer.SerializeToElement(error);
        Assert.False(originalError.TryGetProperty("SourceApId", out _));
        Assert.Null(JsonSerializer.Deserialize<CollectionError>(JsonSerializer.Serialize(error))!.SourceApId);
        var at = DateTimeOffset.Parse("2026-11-15T01:00:00Z");
        var batch = new ApCollectionBatch(RunOutcome.Partial, [], [], at, ObservationCompleteness.Partial, [error]);
        var batchJson = JsonSerializer.SerializeToElement(batch);
        Assert.False(batchJson.TryGetProperty("RecordErrorAttributionComplete", out _));
        Assert.False(JsonSerializer.Deserialize<ApCollectionBatch>(batchJson.GetRawText())!.RecordErrorAttributionComplete);
        var evidence = new ApRunSourceEvidence(1, 1, 1, [], new(ImmutableHashSet<string>.Empty, ImmutableDictionary<string, ImmutableArray<string>>.Empty));
        var evidenceJson = JsonSerializer.SerializeToElement(evidence);
        Assert.Equal(5, evidenceJson.EnumerateObject().Count());
        Assert.Null(JsonSerializer.Deserialize<ApRunSourceEvidence>(evidenceJson.GetRawText())!.SourceOutcome);
        Assert.Null(JsonSerializer.Deserialize<ApRunSourceEvidence>(evidenceJson.GetRawText())!.ScopeExcludedApCount);
    }
}
