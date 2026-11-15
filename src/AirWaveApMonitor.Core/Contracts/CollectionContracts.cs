using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AirWaveApMonitor.Core.Contracts;

public enum CollectionKind
{
    ApStatus, ConnectionNeighbor, Power
}
public enum RunTrigger
{
    Scheduled, Manual, Startup, Resume, DatabaseRestore
}
public enum RunOutcome
{
    Succeeded, Partial, Failed
}
public enum RunLifecycle
{
    Running, Finalized, Interrupted
}
public enum BaselineStatus
{
    Unverified, Confirmed
}
public enum RunRequestStatus
{
    Accepted, Queued, AlreadyRunning, ScheduleSkipped, AuthenticationBlocked, Paused, Unconfigured, Unsupported, StorageBlocked, Stopping
}
public enum CollectionErrorCategory
{
    Unverified, Permission, AuthenticationBlocked, CredentialUnavailable, Configuration, AuthenticationRejected, SessionExpired, Timeout, Transport, Tls, InvalidResponse, ResponseTooLarge, MissingRecord, DuplicateIdentity, InvalidField, Interrupted, Storage, Unsupported
}

public interface IApSource
{
    Task<ApCollectionBatch> CollectAsync(CollectionContext context, CancellationToken ct);
}

public interface ICollectorControl
{
    Task<RunRequestReceipt> RequestAsync(CollectionKind kind, RunTrigger trigger, CancellationToken ct);
    Task SetPausedAsync(bool paused, CancellationToken ct);
}

public sealed record ExpectedTarget(string SourceApId, Guid? DeviceGenerationId = null, Guid? MonitoringIntervalId = null);

public sealed record CollectionSettings
{
    public int ApIntervalMinutes
    {
        get;
    }
    public int EnrichmentIntervalMinutes
    {
        get;
    }
    public bool ConnectionCollectionEnabled
    {
        get;
    }
    public bool PowerCollectionEnabled
    {
        get;
    }
    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(45);
    public TimeSpan RunTimeout => TimeSpan.FromSeconds(120);
    public long MaximumDecompressedResponseBytes => 64L * 1024 * 1024;
    public CollectionSettings(int apIntervalMinutes = 5, int enrichmentIntervalMinutes = 15,
        bool connectionCollectionEnabled = false, bool powerCollectionEnabled = false)
    {
        if (apIntervalMinutes is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(apIntervalMinutes));
        if (enrichmentIntervalMinutes is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(enrichmentIntervalMinutes));
        ApIntervalMinutes = apIntervalMinutes;
        EnrichmentIntervalMinutes = enrichmentIntervalMinutes;
        ConnectionCollectionEnabled = connectionCollectionEnabled;
        PowerCollectionEnabled = powerCollectionEnabled;
    }
}

public sealed record ProfileSnapshot
{
    public int SchemaVersion => 1;
    public Guid InstanceId
    {
        get;
    }
    public Guid DatasetId
    {
        get;
    }
    public Uri Origin
    {
        get;
    }
    public long ConfigurationVersion
    {
        get;
    }
    public long ScopeVersion
    {
        get;
    }
    public ImmutableArray<ExpectedTarget> ExpectedTargets
    {
        get;
    }
    public BaselineStatus BaselineStatus
    {
        get;
    }
    public CollectionSettings Settings
    {
        get;
    }
    public ProxySettings Proxy { get; init; } = new();
    public long CredentialsVersion { get; init; } = 1;

    [JsonConstructor]
    public ProfileSnapshot(Guid instanceId, Guid datasetId, Uri origin, long configurationVersion, long scopeVersion,
        ImmutableArray<ExpectedTarget> expectedTargets, BaselineStatus baselineStatus, CollectionSettings settings)
        : this(instanceId, datasetId, origin, configurationVersion, scopeVersion, (IEnumerable<ExpectedTarget>)expectedTargets, baselineStatus, settings) { }

    public ProfileSnapshot(Guid instanceId, Guid datasetId, Uri origin, long configurationVersion, long scopeVersion,
        IEnumerable<ExpectedTarget> expectedTargets, BaselineStatus baselineStatus, CollectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps || origin.UserInfo.Length != 0 ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ArgumentException("서버 주소는 사용자 정보·경로·쿼리가 없는 HTTPS 출처여야 합니다.", nameof(origin));
        if (instanceId == Guid.Empty)
            throw new ArgumentException("인스턴스 ID가 필요합니다.", nameof(instanceId));
        if (datasetId == Guid.Empty)
            throw new ArgumentException("데이터셋 ID가 필요합니다.", nameof(datasetId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configurationVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scopeVersion);
        ArgumentNullException.ThrowIfNull(settings);
        var frozenTargets = expectedTargets.ToImmutableArray();
        if (frozenTargets.Any(target => string.IsNullOrWhiteSpace(target.SourceApId)) ||
            frozenTargets.Select(target => target.SourceApId).Distinct(StringComparer.Ordinal).Count() != frozenTargets.Length)
            throw new ArgumentException("기대 대상의 AP ID는 비어 있지 않고 중복되지 않아야 합니다.", nameof(expectedTargets));
        InstanceId = instanceId;
        DatasetId = datasetId;
        Origin = new Uri(origin.GetLeftPart(UriPartial.Authority) + "/");
        ConfigurationVersion = configurationVersion;
        ScopeVersion = scopeVersion;
        ExpectedTargets = frozenTargets;
        BaselineStatus = baselineStatus;
        Settings = settings;
    }
}

public sealed record CollectionContext(long RunId, ProfileSnapshot Profile, CollectionKind Kind,
    RunTrigger Trigger, DateTimeOffset StartedAtUtc, DateTimeOffset DeadlineUtc)
{
    public int SchemaVersion => 1;
}

public sealed record CollectionError(CollectionErrorCategory Category, int Count, string SafeMessage);
public sealed record ExcludedRecord(int RecordIndex, string? SourceApId, CollectionErrorCategory Reason, string SafeDetail);

/// <summary>Failed with no observations means no valid result, never a confirmed empty AP inventory.</summary>
public sealed record ApCollectionBatch(RunOutcome Outcome, ImmutableArray<ApObservation> Observations,
    ImmutableArray<ExcludedRecord> ExcludedRecords, DateTimeOffset CollectedAtUtc,
    ObservationCompleteness Completeness, ImmutableArray<CollectionError> Errors)
{
    public int SchemaVersion => 1;
    public SourceCheckpoint? Checkpoint { get; init; }
}

public sealed record RunRequestReceipt(RunRequestStatus Status, long? RunId, Guid? QueueId, string Message)
{
    public int SchemaVersion => 1;
}

public sealed record CollectionRequest(CollectionKind Kind, RunTrigger Trigger);
public sealed record PauseRequest([property: JsonRequired] bool Paused);
public sealed record ProfileSettingsRequest(Uri Origin, int ApIntervalMinutes, ImmutableArray<string> SourceApIds,
    long ExpectedConfigurationVersion);
