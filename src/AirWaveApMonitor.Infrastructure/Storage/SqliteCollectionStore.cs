using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Core.Monitoring;
using AirWaveApMonitor.Infrastructure.Maintenance;
using Microsoft.Data.Sqlite;
using static AirWaveApMonitor.Infrastructure.Storage.StorageDatabase;

namespace AirWaveApMonitor.Infrastructure.Storage;

public enum StorageTransactionBoundary { BeforeFinalCommit, AfterFinalCommit, BeforeBeginCommit, AfterBeginCommit }

/// <summary>
/// File-backed SQLite. The shared gate serializes short writes with authentication storage.
/// Network collection occurs between BeginRun and Apply; neither retains a connection or transaction.
/// SQLite I/O is synchronous; cancellation is checked at operation/AP/commit boundaries and busy waits are bounded.
/// </summary>
public sealed partial class SqliteCollectionStore : ICollectionStore, ISourceCheckpointStore,
    IEnrichmentTargetStore, IEnrichmentHealthStore, IEnrichmentInterruptionStore, IEnrichmentReadStore
{
    private readonly StorageLocation location;
    private readonly SemaphoreSlim writeGate;
    private readonly TimeProvider timeProvider;
    private readonly Action<StorageTransactionBoundary>? transactionObserver;
    private readonly StateTransitionEngine transitions = new();
    private readonly RunClassifier classifier = new();
    private bool initialized;

    public SqliteCollectionStore(StorageLocation location, SemaphoreSlim writeGate,
        TimeProvider? timeProvider = null, Action<StorageTransactionBoundary>? transactionObserver = null)
    {
        this.location = location;
        this.writeGate = writeGate;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.transactionObserver = transactionObserver;
    }

    public async Task<CollectionContext> BeginRunAsync(ProfileSnapshot profile, CollectionKind kind, RunTrigger trigger, CancellationToken ct)
    {
        ValidateDataset(profile.DatasetId);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        await writeGate.WaitAsync(ct);
        try
        {
            using var connection = Open();
            Initialize(connection, ct);
            using var transaction = connection.BeginTransaction();
            if (Convert.ToInt64(Scalar(connection, transaction,
                "SELECT COUNT(*) FROM runs WHERE dataset_id=$dataset AND kind=$kind AND lifecycle=0", ("$dataset", location.DatasetId), ("$kind", kind))) != 0)
            {
                throw new InvalidOperationException("같은 종류의 수집 회차가 이미 진행 중입니다.");
            }
            var rawProfile = Encode(profile);
            var existingProfile = Scalar(connection, transaction,
                "SELECT profile_json FROM profiles WHERE dataset_id=$dataset AND configuration_version=$version",
                ("$dataset", location.DatasetId), ("$version", profile.ConfigurationVersion)) as string;
            if (existingProfile is not null && !JsonNode.DeepEquals(JsonNode.Parse(existingProfile), JsonNode.Parse(rawProfile)))
            {
                throw new ArgumentException("같은 설정 버전의 내용을 변경할 수 없습니다.", nameof(profile));
            }
            Execute(connection, transaction,
                "INSERT OR IGNORE INTO profiles VALUES($dataset,$version,$json)",
                ("$dataset", location.DatasetId), ("$version", profile.ConfigurationVersion), ("$json", rawProfile));
            var scopeExists = Scalar(connection, transaction,
                "SELECT instance_id || '|' || origin FROM scopes WHERE dataset_id=$dataset AND scope_version=$scope",
                ("$dataset", location.DatasetId), ("$scope", profile.ScopeVersion)) as string;
            if (scopeExists is not null && scopeExists != profile.InstanceId.ToString("D") + "|" + profile.Origin.AbsoluteUri)
            {
                throw new ArgumentException("같은 범위 버전의 서버 식별을 변경할 수 없습니다.", nameof(profile));
            }
            Execute(connection, transaction,
                "INSERT OR IGNORE INTO scopes VALUES($dataset,$scope,$instance,$origin,$baseline,$known)",
                ("$dataset", location.DatasetId), ("$scope", profile.ScopeVersion), ("$instance", profile.InstanceId),
                ("$origin", profile.Origin.AbsoluteUri), ("$baseline", BaselineStatus.Unverified),
                ("$known", profile.ExpectedTargets.Length > 0 ? 1 : 0));
            if (scopeExists is not null && profile.BaselineStatus == BaselineStatus.Confirmed)
            {
                Execute(connection, transaction, "UPDATE scopes SET baseline_status=$baseline WHERE dataset_id=$dataset AND scope_version=$scope",
                    ("$baseline", BaselineStatus.Confirmed), ("$dataset", location.DatasetId), ("$scope", profile.ScopeVersion));
            }
            foreach (var target in profile.ExpectedTargets)
            {
                Execute(connection, transaction, "INSERT OR IGNORE INTO expected_targets VALUES($dataset,$scope,$source)",
                    ("$dataset", location.DatasetId), ("$scope", profile.ScopeVersion), ("$source", target.SourceApId));
            }
            var targets = ImmutableArray.CreateBuilder<ExpectedTarget>();
            using (var command = Command(connection, transaction, """
                SELECT t.source_ap_id,c.generation_id,c.interval_id FROM expected_targets t
                LEFT JOIN aps a ON a.dataset_id=t.dataset_id AND a.instance_id=$instance AND a.source_ap_id=t.source_ap_id
                LEFT JOIN ap_current c ON c.ap_id=a.ap_id
                WHERE t.dataset_id=$dataset AND t.scope_version=$scope ORDER BY t.source_ap_id
                """, ("$instance", profile.InstanceId), ("$dataset", location.DatasetId), ("$scope", profile.ScopeVersion)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var seed = profile.ExpectedTargets.FirstOrDefault(target => target.SourceApId == reader.GetString(0));
                    targets.Add(new(reader.GetString(0), reader.IsDBNull(1) ? seed?.DeviceGenerationId : Guid.Parse(reader.GetString(1)),
                        reader.IsDBNull(2) ? seed?.MonitoringIntervalId : Guid.Parse(reader.GetString(2))));
                }
            }
            if (targets.Count > 0)
            {
                Execute(connection, transaction, "UPDATE scopes SET target_universe_known=1 WHERE dataset_id=$dataset AND scope_version=$scope",
                    ("$dataset", location.DatasetId), ("$scope", profile.ScopeVersion));
            }
            var baseline = (BaselineStatus)Convert.ToInt32(Scalar(connection, transaction,
                "SELECT baseline_status FROM scopes WHERE dataset_id=$dataset AND scope_version=$scope",
                ("$dataset", location.DatasetId), ("$scope", profile.ScopeVersion)));
            var frozenProfile = new ProfileSnapshot(profile.InstanceId, profile.DatasetId, profile.Origin,
                profile.ConfigurationVersion, profile.ScopeVersion, targets.ToImmutable(), baseline, profile.Settings)
            {
                Proxy = profile.Proxy,
                CredentialsVersion = profile.CredentialsVersion,
                MonitoringScope = profile.MonitoringScope, ScopeRevisions = profile.ScopeRevisions,
                BaselineConfirmedSourceApIds = profile.BaselineConfirmedSourceApIds
            };
            var now = timeProvider.GetUtcNow().ToUniversalTime();
            Execute(connection, transaction,
                "INSERT INTO runs(dataset_id,scope_version,kind,lifecycle,context_json,target_universe_known) VALUES($dataset,$scope,$kind,0,'',$known)",
                ("$dataset", location.DatasetId), ("$scope", profile.ScopeVersion), ("$kind", kind), ("$known", targets.Count > 0 ? 1 : 0));
            var runId = Convert.ToInt64(Scalar(connection, transaction, "SELECT last_insert_rowid()"));
            var context = new CollectionContext(runId, frozenProfile, kind, trigger, now, now + profile.Settings.RunTimeout);
            Execute(connection, transaction, "UPDATE runs SET context_json=$json WHERE run_id=$run", ("$json", Encode(context)), ("$run", runId));
            FreezeOperationBindings(connection, transaction, context);
            if (kind != CollectionKind.ApStatus) FreezeEnrichmentBindings(connection, transaction, context);
            ct.ThrowIfCancellationRequested();
            transactionObserver?.Invoke(StorageTransactionBoundary.BeforeBeginCommit);
            transaction.Commit();
            transactionObserver?.Invoke(StorageTransactionBoundary.AfterBeginCommit);
            return context;
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task<CommitReceipt> ApplyApBatchAsync(CollectionContext context, ApCollectionBatch batch, CancellationToken ct)
    {
        ValidateDataset(context.Profile.DatasetId);
        if (context.Kind != CollectionKind.ApStatus)
        {
            throw new ArgumentException("AP 상태 회차가 필요합니다.", nameof(context));
        }
        await writeGate.WaitAsync(ct);
        try
        {
            using var connection = Open();
            Initialize(connection, ct);
            using var transaction = connection.BeginTransaction();
            var stored = ReadContext(connection, transaction, context.RunId);
            if (!JsonNode.DeepEquals(JsonNode.Parse(Encode(stored)), JsonNode.Parse(Encode(context))))
            {
                throw new ArgumentException("실행 시작 당시 고정한 컨텍스트와 일치하지 않습니다.", nameof(context));
            }
            var existing = Scalar(connection, transaction, "SELECT receipt_json FROM run_results WHERE run_id=$run", ("$run", context.RunId)) as string;
            if (existing is not null)
            {
                return Decode<CommitReceipt>(existing) with { AlreadyApplied = true };
            }
            var receipt = FinalizeAp(connection, transaction, stored, batch, RunLifecycle.Finalized, ct);
            ct.ThrowIfCancellationRequested();
            transactionObserver?.Invoke(StorageTransactionBoundary.BeforeFinalCommit);
            transaction.Commit();
            transactionObserver?.Invoke(StorageTransactionBoundary.AfterFinalCommit);
            return receipt;
        }
        finally
        {
            writeGate.Release();
        }
    }


    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(location.DataDirectory, "monitor.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5,
            ForeignKeys = true
        }.ToString());
        try
        {
            connection.Open();
            Execute(connection, null, "PRAGMA synchronous=FULL;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void Initialize(SqliteConnection connection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (initialized)
        {
            return;
        }
        new SchemaMigrator().Migrate(connection, location, timeProvider.GetUtcNow());
        using var transaction = connection.BeginTransaction();
        InitializeOperations(connection, transaction, ct);
        var interrupted = new List<long>();
        using (var command = Command(connection, transaction, "SELECT run_id FROM runs WHERE lifecycle=0 ORDER BY run_id"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                interrupted.Add(reader.GetInt64(0));
            }
        }
        // A backup can contain a Running run. Its recovery must not open a gap at
        // restoration time and thereby hide the earlier loss since the backup.
        var recoveryObservedAt = PendingRestoreBoundary(connection, transaction) ?? timeProvider.GetUtcNow();
        foreach (var runId in interrupted)
        {
            var context = ReadContext(connection, transaction, runId);
            ValidateDataset(context.Profile.DatasetId);
            if (IsEnrichmentKind(context.Kind))
            {
                FinalizeEnrichment(connection, transaction, context, InterruptedEnrichment(context, recoveryObservedAt), RunLifecycle.Interrupted, ct);
                continue;
            }
            FinalizeAp(connection, transaction, context, new(RunOutcome.Failed, [], [], recoveryObservedAt,
                ObservationCompleteness.Unknown, [new(CollectionErrorCategory.Interrupted, 1, "이전 실행이 최종 저장 전에 중단되었습니다.")]),
                RunLifecycle.Interrupted, ct);
        }
        ConsumeRestoreEvents(connection, transaction, ct);
        ct.ThrowIfCancellationRequested();
        transaction.Commit();
        initialized = true;
    }

    private static CollectionContext ReadContext(SqliteConnection connection, SqliteTransaction transaction, long runId)
    {
        using var command = Command(connection, transaction,
            "SELECT context_json,dataset_id,scope_version,kind FROM runs WHERE run_id=$run", ("$run", runId));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new KeyNotFoundException("저장된 실행이 없습니다.");
        }
        var context = Decode<CollectionContext>(reader.GetString(0));
        if (context.RunId != runId || !Enum.IsDefined(context.Kind) ||
            context.Profile.DatasetId.ToString("D") != reader.GetString(1) ||
            context.Profile.ScopeVersion != reader.GetInt64(2) || (int)context.Kind != reader.GetInt32(3))
        {
            throw new InvalidDataException("저장된 실행 ID·데이터셋·범위 또는 종류가 일치하지 않습니다.");
        }
        return context;
    }

    private void ValidateDataset(Guid datasetId)
    {
        if (datasetId != location.DatasetId)
        {
            throw new ArgumentException("다른 데이터셋의 자료에 접근할 수 없습니다.");
        }
    }
}
