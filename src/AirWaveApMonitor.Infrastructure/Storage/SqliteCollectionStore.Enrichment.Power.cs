using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Enrichment;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    private bool StorePower(SqliteConnection connection, SqliteTransaction transaction, CollectionContext context,
        string profile, PowerSample sample, int ordinal, IReadOnlyList<ConnectionObservation> connections, StoredTargetBoundsCache targetBounds, bool current)
    {
        var normalized = PowerValuePolicy.Normalize(sample);
        var eligible = sample.Timing.SourceAtUtc is { } measured
            ? connections.Where(c => c.Target is not null && targetBounds.Allows(c.Target, measured)).ToArray()
            : connections.ToArray();
        var attribution = PowerAttributionPolicy.Resolve(sample, eligible);
        if (attribution.Target is { } targetBinding && attribution.MeasuredAtUtc is { } measuredAt &&
            !targetBounds.Allows(targetBinding, measuredAt))
            attribution = new(sample.Id, PowerAttributionStatus.OutsideBinding, PowerAttributionReason.BindingMismatch, measuredAt, null, null);
        var key = PowerSampleIdentityPolicy.GetKey(sample);
        var keyHash = key is null ? null : EnrichmentHash(key);
        // Read/run clocks and operator placement are not a source measurement identity. A repeated
        // source sample keeps its original attribution; a reused unique key with a new value is a conflict.
        var measurementHash = EnrichmentHash(new { sample.Profile, sample.Endpoint, sample.Metric, sample.Value, sample.Unit,
            sample.UnitVerified, sample.Timing.SourceAtUtc, sample.Timing.Meaning, sample.Timing.MeaningVerified,
            sample.Evidence.Availability, sample.Evidence.Validity, sample.Evidence.Source });
        long? sequence = null;
        if (keyHash is not null)
        {
            using var command = Command(connection, transaction, "SELECT measurement_hash,sample_sequence FROM power_sample_keys WHERE key_hash=$key", ("$key", keyHash));
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(0) != measurementHash) throw new InvalidDataException("원천 전력 표본의 고유 ID에 다른 측정값이 있습니다.");
                if (!reader.IsDBNull(1)) sequence = reader.GetInt64(1);
                // An expired source sample remains expired. A repeated read cannot revive its numeric
                // history or claim a new measurement, even though the read receipt remains visible.
                else
                {
                    reader.Close();
                    Execute(connection, transaction, "INSERT INTO enrichment_power_reads VALUES($run,$ordinal,NULL,$at)", ("$run", context.RunId), ("$ordinal", ordinal), ("$at", sample.Timing.ReadAtUtc));
                    return false;
                }
            }
        }
        if (sequence is null)
        {
            if (sample.Id == Guid.Empty) throw new ArgumentException("전력 표본 ID가 비어 있습니다.", nameof(sample));
            var target = attribution.Target;
            Execute(connection, transaction, """
                INSERT INTO power_samples(sample_id,run_id,profile_key,scope_version,endpoint_role,source_device_id,source_interface_id,metric,
                    ap_id,generation_id,interval_id,point_id,read_at_utc,source_at_utc,sample_json,normalized_json,attribution_json)
                VALUES($id,$run,$profile,$scope,$role,$device,$interface,$metric,$ap,$gen,$interval,$point,$read,$source,$sample,$normalized,$attribution)
                """, ("$id", sample.Id), ("$run", context.RunId), ("$profile", profile), ("$scope", context.Profile.ScopeVersion), ("$role", sample.Endpoint.Role),
                ("$device", sample.Endpoint.SourceDeviceId), ("$interface", sample.Endpoint.SourceInterfaceId), ("$metric", sample.Metric), ("$ap", target?.ApId),
                ("$gen", target?.DeviceGenerationId), ("$interval", target?.MonitoringIntervalId), ("$point", target?.Placement?.InstallationPointId),
                ("$read", sample.Timing.ReadAtUtc), ("$source", sample.Timing.SourceAtUtc), ("$sample", Encode(sample)), ("$normalized", Encode(normalized)), ("$attribution", Encode(attribution)));
            sequence = Convert.ToInt64(Scalar(connection, transaction, "SELECT last_insert_rowid()"));
            if (keyHash is not null) Execute(connection, transaction, "INSERT INTO power_sample_keys VALUES($key,$hash,$sequence)", ("$key", keyHash), ("$hash", measurementHash), ("$sequence", sequence));
        }
        Execute(connection, transaction, "INSERT INTO enrichment_power_reads VALUES($run,$ordinal,$sequence,$at)", ("$run", context.RunId), ("$ordinal", ordinal), ("$sequence", sequence), ("$at", sample.Timing.ReadAtUtc));
        var entity = EnrichmentHash(new { sample.Endpoint, sample.Metric });
        var previous = Optional<PowerSample>(connection, transaction, "SELECT p.sample_json FROM enrichment_power_current c JOIN power_samples p USING(sample_sequence) WHERE c.profile_key=$profile AND c.entity_key=$key", ("$profile", profile), ("$key", entity));
        var regressed = sample.Timing.MeaningVerified && previous?.Timing.MeaningVerified == true && sample.Timing.SourceAtUtc is { } at && previous.Timing.SourceAtUtc is { } before && at < before;
        var valid = normalized.Watts is not null && !regressed;
        if (current && valid)
        {
            Execute(connection, transaction, "INSERT INTO enrichment_power_current VALUES($profile,$key,$sequence) ON CONFLICT(profile_key,entity_key) DO UPDATE SET sample_sequence=excluded.sample_sequence", ("$profile", profile), ("$key", entity), ("$sequence", sequence));
            // Deduplicated reads retain the original sample attribution, not this read's current placement.
            var storedTarget = ReadPowerEvidence(connection, transaction, sequence.Value)!.Attribution.Target;
            Execute(connection, transaction, "INSERT INTO enrichment_accepted_power(profile_key,entity_key,run_id,sample_sequence,ap_id,generation_id,interval_id) VALUES($profile,$key,$run,$sequence,$ap,$gen,$interval)",
                ("$profile", profile), ("$key", entity), ("$run", context.RunId), ("$sequence", sequence), ("$ap", storedTarget?.ApId),
                ("$gen", storedTarget?.DeviceGenerationId), ("$interval", storedTarget?.MonitoringIntervalId));
        }
        return valid;
    }
    private static Dictionary<ConnectionEndpoint, List<ConnectionObservation>> IndexConnections(IEnumerable<ConnectionObservation> rows)
    {
        var result = new Dictionary<ConnectionEndpoint, List<ConnectionObservation>>();
        void Add(ConnectionEndpoint? endpoint, ConnectionObservation row)
        {
            if (endpoint is null) return;
            if (!result.TryGetValue(endpoint, out var values)) result.Add(endpoint, values = []);
            values.Add(row);
        }
        foreach (var row in rows)
        {
            Add(row.Local, row);
            if (row.Remote != row.Local) Add(row.Remote, row);
        }
        return result;
    }
    private static IReadOnlyList<ConnectionObservation> EndpointConnections(
        Dictionary<ConnectionEndpoint, List<ConnectionObservation>> index, ConnectionEndpoint endpoint)
        => index.TryGetValue(endpoint, out var values) ? values : [];

    private sealed record StoredTargetTimeBounds(bool Exists, DateTimeOffset FromUtc, DateTimeOffset? UntilUtc)
    {
        public bool Allows(DateTimeOffset at) => Exists && at >= FromUtc && (UntilUtc is null || at < UntilUtc);
        public static StoredTargetTimeBounds Rejected { get; } = new(false, default, null);
    }
    // One cache per finalization transaction. Only immutable bounds are cached: each sample's
    // measurement time is checked independently, and the original connection candidates remain intact.
    private sealed class StoredTargetBoundsCache(SqliteConnection connection, SqliteTransaction transaction)
    {
        private readonly Dictionary<EnrichmentTargetBinding, StoredTargetTimeBounds> targets = [];
        private readonly Dictionary<long, DateTimeOffset> runStarts = [];
        private DateTimeOffset Start(long run)
        {
            if (!runStarts.TryGetValue(run, out var at)) runStarts.Add(run, at = ReadContext(connection, transaction, run).StartedAtUtc);
            return at;
        }
        public bool Allows(EnrichmentTargetBinding target, DateTimeOffset at)
        {
            if (!targets.TryGetValue(target, out var bounds)) targets.Add(target, bounds = ReadStoredTargetBounds(connection, transaction, target, Start));
            return bounds.Allows(at);
        }
    }
    private static StoredTargetTimeBounds ReadStoredTargetBounds(SqliteConnection connection, SqliteTransaction transaction,
        EnrichmentTargetBinding target, Func<long, DateTimeOffset> start)
    {
        long generationStart, intervalStart; long? generationEnd, intervalEnd;
        using (var command = Command(connection, transaction, """
            SELECT g.started_run_id,g.ended_run_id,i.started_run_id,i.ended_run_id
            FROM aps a JOIN generations g USING(ap_id) JOIN monitoring_intervals i ON i.generation_id=g.generation_id
            WHERE a.ap_id=$ap AND a.dataset_id=$dataset AND a.instance_id=$instance AND a.source_ap_id=$source
                AND g.generation_id=$gen AND i.interval_id=$interval
            """, ("$ap", target.ApId), ("$dataset", target.Profile.DatasetId), ("$instance", target.Profile.InstanceId),
            ("$source", target.SourceApId), ("$gen", target.DeviceGenerationId), ("$interval", target.MonitoringIntervalId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return StoredTargetTimeBounds.Rejected;
            generationStart = reader.GetInt64(0); generationEnd = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            intervalStart = reader.GetInt64(2); intervalEnd = reader.IsDBNull(3) ? null : reader.GetInt64(3);
        }
        // Recorded logical registration bounds conservatively exclude an old identity once its
        // ending run begins. These clocks never assert a physical replacement time on Live equipment.
        var from = new[] { start(generationStart), start(intervalStart) }.Max();
        var until = new[] { generationEnd, intervalEnd }.Where(r => r is not null).Select(r => (DateTimeOffset?)start(r!.Value)).Min();
        if (target.Placement is not { InstallationPointId: { } point } placement) return new(true, from, until);
        var periods = ReadList<PlacementPeriod>(connection, transaction, """
            SELECT v.version_json FROM placements p JOIN placement_versions v ON v.placement_id=p.placement_id AND v.version=p.current_version
            WHERE p.ap_id=$ap AND p.generation_id=$gen AND p.point_id=$point
            """, ("$ap", target.ApId), ("$gen", target.DeviceGenerationId), ("$point", point));
        var matching = periods.Where(p => p.RecordedStartAtUtc == placement.RecordedAtUtc &&
            (p.ActualStartAtUtc ?? p.RecordedStartAtUtc) == placement.EffectiveFromUtc).ToArray();
        if (matching.Length != 1 || matching[0].TimeNeedsReview) return StoredTargetTimeBounds.Rejected;
        var period = matching[0]; var endAt = period.ActualEndAtUtc ?? period.RecordedEndAtUtc;
        from = new[] { from, period.ActualStartAtUtc ?? period.RecordedStartAtUtc }.Max();
        until = new[] { until, endAt }.Min();
        return new(true, from, until);
    }
    private static PowerEvidenceView? ReadPowerEvidence(SqliteConnection connection, SqliteTransaction transaction, long sequence)
    {
        using var command = Command(connection, transaction, "SELECT sample_json,normalized_json,attribution_json FROM power_samples WHERE sample_sequence=$id", ("$id", sequence));
        using var reader = command.ExecuteReader(); return !reader.Read() ? null : new(sequence, Decode<PowerSample>(reader.GetString(0)), Decode<NormalizedPower>(reader.GetString(1)), Decode<PowerAttribution>(reader.GetString(2)));
    }
}
