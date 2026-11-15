using System.Globalization;
using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Enrichment;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Settings;

namespace AirWaveApMonitor.App.Endpoints;

public static class EnrichmentEndpoints
{
    public static void MapEnrichment(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/enrichment/capabilities", (HttpContext http, ProfileStore profiles,
            RuntimeOptions runtime, IBoundedEnrichmentSource source, CancellationToken ct) => MonitoringHttp.Execute(http, async () =>
        {
            await profiles.OperationGate.WaitAsync(ct);
            try
            {
                var profile = profiles.Current;
                var kinds = new[] { CollectionKind.ConnectionNeighbor, CollectionKind.Power }.Select(kind =>
                {
                    var enabled = profile is not null && (kind == CollectionKind.ConnectionNeighbor
                        ? profile.Settings.ConnectionCollectionEnabled : profile.Settings.PowerCollectionEnabled);
                    var decision = profile is null ? new(CapabilityAvailability.Unverified, false, CapabilityDecisionReason.Unverified)
                        : source.Capability(profile, kind);
                    var fields = profile is null ? Array.Empty<FieldCapabilityView>() : source.DescribeCapabilities(profile, kind)
                        .Select(evidence => new FieldCapabilityView(evidence, CapabilityStatus.Evaluate(evidence,
                            evidence.Key with { Profile = EnrichmentProfileBinding.From(profile,
                                runtime.Mode == AppMode.Demo ? EvidenceSource.Demo : EvidenceSource.AirWave) }, enabled))).ToArray();
                    return new KindCapabilityView(kind, enabled, decision, fields);
                }).ToArray();
                return MonitoringHttp.Json(new { mode = runtime.Mode.ToString().ToLowerInvariant(), configurationVersion = profile?.ConfigurationVersion, kinds });
            }
            finally { profiles.OperationGate.Release(); }
        }));

        routes.MapGet("/api/enrichment/runs", (HttpContext http, IEnrichmentReadStore reads,
            StorageLocation location, CancellationToken ct) => MonitoringHttp.Execute(http, async () =>
        {
            var kind = MonitoringHttp.EnumValue<CollectionKind>(http, "kind");
            if (kind == CollectionKind.ApStatus) throw new ArgumentException();
            var query = new EnrichmentRunQuery(location.DatasetId, MonitoringHttp.Long(http, "scopeVersion"), kind,
                MonitoringHttp.Long(http, "throughRunId"), MonitoringHttp.Time(http, "fromUtc"), MonitoringHttp.Time(http, "toUtc"),
                MonitoringHttp.Int(http, "page", 1), MonitoringHttp.Int(http, "pageSize", 50, 500));
            ValidateTimes(query.FromUtc, query.ToUtc);
            return MonitoringHttp.Json(await reads.GetEnrichmentRunsAsync(query, ct));
        }));

        routes.MapGet("/api/enrichment/runs/{runId:long}", (long runId, HttpContext http,
            IEnrichmentReadStore reads, StorageLocation location, CancellationToken ct) => MonitoringHttp.Execute(http, async () =>
        {
            if (runId <= 0) throw new ArgumentException();
            return MonitoringHttp.Json(await reads.GetEnrichmentRunAsync(location.DatasetId, runId, ct));
        }));

        routes.MapGet("/api/enrichment/power", (HttpContext http, IEnrichmentReadStore reads,
            IMonitoringReadStore runs, StorageLocation location, ProfileStore profiles, CancellationToken ct) => MonitoringHttp.Execute(http, async () =>
        {
            var through = MonitoringHttp.Long(http, "throughRunId");
            var scope = MonitoringHttp.Long(http, "scopeVersion");
            if (through is { } id)
            {
                var saved = await runs.GetRunAsync(location.DatasetId, id, ct);
                if (scope is not null && scope != saved.ScopeVersion) throw new KeyNotFoundException();
                scope = saved.ScopeVersion;
            }
            scope ??= profiles.Current?.ScopeVersion ?? throw new KeyNotFoundException();
            var query = new PowerSampleQuery(location.DatasetId, scope.Value, through,
                GuidValue(http, "apId"), GuidValue(http, "deviceGenerationId"), GuidValue(http, "installationPointId"),
                MonitoringHttp.EnumValue<PowerMetric>(http, "metric"), MonitoringHttp.Text(http, "sourceDeviceId", 256),
                MonitoringHttp.Text(http, "sourceInterfaceId", 256), MonitoringHttp.EnumValue<EndpointRole>(http, "endpointRole"),
                MonitoringHttp.Time(http, "fromUtc"), MonitoringHttp.Time(http, "toUtc"), MonitoringHttp.Int(http, "page", 1),
                MonitoringHttp.Int(http, "pageSize", 50, 500), CommitSequence(http));
            ValidateTimes(query.FromUtc, query.ToUtc);
            return MonitoringHttp.Json(await reads.GetPowerSamplesAsync(query, ct));
        }));
    }
    private static long? CommitSequence(HttpContext http)
    {
        var value = MonitoringHttp.Text(http, "throughCommitSequence");
        if (value is null) return null;
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed : throw new ArgumentException();
    }
    private static Guid? GuidValue(HttpContext http, string key)
    {
        var text = MonitoringHttp.Text(http, key, 36);
        if (text is null) return null;
        return Guid.TryParseExact(text, "D", out var parsed) && parsed != Guid.Empty ? parsed : throw new ArgumentException();
    }
    private static void ValidateTimes(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is not null && to is not null && from > to) throw new ArgumentException();
    }
    private sealed record FieldCapabilityView(CapabilityEvidence Evidence, CapabilityDecision Decision);
    private sealed record KindCapabilityView(CollectionKind Kind, bool Enabled, CapabilityDecision Decision, FieldCapabilityView[] Fields);
}
