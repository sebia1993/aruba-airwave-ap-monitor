using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public sealed partial class SqliteCollectionStore
{
    public async Task<ScopeSettingsView> GetScopeSettingsAsync(ProfileSnapshot profile, CancellationToken ct)
    {
        ValidateDataset(profile.DatasetId);
        await InitializeAsync(ct);
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: true);
        var known = ImmutableArray.CreateBuilder<KnownScopeTarget>();
        using (var command = Command(connection, transaction, """
            SELECT a.source_ap_id,c.tracking_json,m.metadata_json FROM aps a
            JOIN ap_current c ON c.ap_id=a.ap_id JOIN metadata_versions m ON m.metadata_version_id=c.metadata_version_id
            WHERE a.dataset_id=$dataset AND a.instance_id=$instance ORDER BY a.source_ap_id
            """, ("$dataset", profile.DatasetId), ("$instance", profile.InstanceId)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var tracking = ReadStored<ApTrackingSnapshot>(reader.GetString(1));
                if (tracking.LastValidRunId is not { } validRun) continue;
                var metadata = ReadStored<DeviceMetadata>(reader.GetString(2));
                known.Add(new(reader.GetString(0), metadata.Device.Name, metadata.Device.IpAddress, validRun));
            }
        }
        ScopeComparisonRun? comparison = null;
        long? run = null; CollectionContext? context = null; CommitReceipt? receipt = null;
        using (var command = Command(connection, transaction, """
            SELECT r.run_id,r.context_json,f.receipt_json FROM runs r JOIN run_results f ON f.run_id=r.run_id
            WHERE r.dataset_id=$dataset AND r.scope_version=$scope AND r.kind=0 AND r.lifecycle IN (1,2)
            ORDER BY r.run_id DESC LIMIT 1
            """, ("$dataset", profile.DatasetId), ("$scope", profile.ScopeVersion)))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                run = reader.GetInt64(0); context = ReadStored<CollectionContext>(reader.GetString(1));
                receipt = ReadStored<CommitReceipt>(reader.GetString(2));
            }
        }
        if (run is { } id)
        {
            var rows = ReadSnapshotRows(connection, transaction, id, ct);
            // Unobserved placeholders do not prove a target was in the returned inventory.
            var validIds = rows.Where(row => row.LastValidRunId is not null).Select(row => row.SourceApId).Order(StringComparer.Ordinal).ToImmutableArray();
            comparison = new(id, context!.Profile.ConfigurationVersion, context.Profile.ScopeVersion,
                receipt!.Summary.TargetCount ?? rows.Count, receipt.Outcome, receipt.Summary.BaselineStatus, validIds);
        }
        return new(profile, known.ToImmutable(), comparison);
    }
}
