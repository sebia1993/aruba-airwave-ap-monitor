using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Storage;
using AirWaveApMonitor.Tests.Support;
using Microsoft.Data.Sqlite;

namespace AirWaveApMonitor.Tests.Web;

internal sealed class ReadFixture : IDisposable
{
    private readonly TemporaryDirectory directory = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    public StorageLocation Location { get; }
    public SqliteCollectionStore Store { get; }
    public Guid InstanceId { get; } = Guid.NewGuid();
    public DateTimeOffset Now { get; private set; } = DateTimeOffset.Parse("2026-11-16T01:00:00Z");
    public ReadFixture()
    {
        Location = StoragePaths.Open(AppMode.Demo, directory.Path);
        Store = new(Location, gate, new Clock(this));
    }
    public ProfileSnapshot Profile(long scope = 1, params string[] ids) => new(InstanceId, Location.DatasetId,
        new("https://demo.example.invalid/"), scope, scope,
        (ids.Length == 0 ? new[] { "100", "200" } : ids).Select(id => new ExpectedTarget(id)), BaselineStatus.Unverified, new());
    public ApObservation Ap(string id, ApState? state, string? name = null, string? serial = null)
    {
        var evidence = new FieldEvidence(FieldAvailability.Provided, FieldValidity.Valid, EvidenceSource.Demo, Now);
        var names = new[] { "name", "lan_ip", "serial_number", "lan_mac", "is_up", "site", "building", "floor", "zone", "detail",
            "syslocation", "folder", "group", "controller_id", "controller_name", "controller_ip", "upstream_device_id", "upstream_port_index",
            "upstream_port_name", "ap_interface", "planned_maintenance_mode" };
        return new(id, state, new(serial ?? "SERIAL-" + id, "02:00:00:00:00:" + (id == "100" ? "01" : "02")),
            new(name ?? "AP-" + id, "192.0.2." + (id == "100" ? "1" : "2"), "Synthetic", "test-1"),
            new(Site: "site-" + id, Building: "building-" + id, Floor: "floor-" + id, Zone: "zone-" + id, Detail: "detail-" + id),
            new("controller-" + id, "controller-name-" + id, "198.51.100." + (id == "100" ? "1" : "2")),
            Now, null, null, names.ToImmutableDictionary(n => n, _ => evidence).SetItem("is_up", state is null ? evidence with { Validity = FieldValidity.Unknown } : evidence),
            new(MaintenanceMode: id == "200"), new("switch-" + id, "port-index-" + id, "port-name-" + id, "ap-interface-" + id, evidence));
    }
    public async Task<CollectionContext> Apply(ApObservation[] observations, long scope = 1, RunOutcome outcome = RunOutcome.Succeeded)
    {
        var context = await Store.BeginRunAsync(Profile(scope), CollectionKind.ApStatus, RunTrigger.Manual, default);
        await Store.ApplyApBatchAsync(context, new(outcome, observations.ToImmutableArray(), [], Now,
            outcome == RunOutcome.Failed ? ObservationCompleteness.Unknown : ObservationCompleteness.Complete, []), default);
        Now += TimeSpan.FromMinutes(5);
        return context;
    }
    public Task<DashboardSnapshot> Dashboard(long? run = null, long scope = 1, DashboardFilter filter = DashboardFilter.All)
        => Store.GetDashboardAsync(new(Location.DatasetId, scope, run, Filter: filter), default);
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Location.DataDirectory, "monitor.db"), Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
    public void SetManualStatus(string sourceId, OperationalStatus status)
    {
        // Seed the current manual layer for a subsequent real AP commit; prior immutable rows are untouched.
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var select = connection.CreateCommand(); select.Transaction = transaction;
        select.CommandText = "SELECT c.ap_id,c.generation_id,m.metadata_json FROM ap_current c JOIN aps a ON a.ap_id=c.ap_id JOIN metadata_versions m ON m.metadata_version_id=c.metadata_version_id WHERE a.source_ap_id=$source";
        select.Parameters.AddWithValue("$source", sourceId);
        string ap; string generation; JsonObject metadata;
        using (var reader = select.ExecuteReader()) { Assert.True(reader.Read()); ap = reader.GetString(0); generation = reader.GetString(1); metadata = JsonNode.Parse(reader.GetString(2))!.AsObject(); }
        metadata["ManualManagement"] = JsonSerializer.SerializeToNode(new ManualManagementDetails(status, "synthetic planned work", Now));
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO metadata_versions(ap_id,generation_id,metadata_json) VALUES($ap,$generation,$json); UPDATE ap_current SET metadata_version_id=last_insert_rowid() WHERE ap_id=$ap;";
        insert.Parameters.AddWithValue("$ap", ap); insert.Parameters.AddWithValue("$generation", generation); insert.Parameters.AddWithValue("$json", metadata.ToJsonString());
        insert.ExecuteNonQuery(); transaction.Commit();
    }
    public void Dispose() { gate.Dispose(); directory.Dispose(); }
    private sealed class Clock(ReadFixture fixture) : TimeProvider { public override DateTimeOffset GetUtcNow() => fixture.Now; }
}
