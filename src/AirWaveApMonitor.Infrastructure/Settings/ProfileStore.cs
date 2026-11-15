using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Demo;
using AirWaveApMonitor.Infrastructure.Maintenance;
namespace AirWaveApMonitor.Infrastructure.Settings;

public sealed class ProfileStore
{
    private readonly StorageLocation location;
    public ProfileSnapshot? Current
    {
        get; private set;
    }
    // Shared by settings, credential registration, explicit retry, and diagnostic reads.
    public SemaphoreSlim OperationGate { get; } = new(1, 1);
    public ProfileStore(StorageLocation location)
    {
        this.location = location;
        var path = Path.Combine(location.DataDirectory, "profile.json");
        if (File.Exists(path))
        {
            try
            {
                Current = JsonSerializer.Deserialize<ProfileSnapshot>(File.ReadAllText(path));
                if (Current is null || Current.DatasetId != location.DatasetId || Current.CredentialsVersion < 1 || Current.Proxy is null || (location.Mode == AppMode.Demo && Current.Origin != ScenarioApSource.DemoOrigin))
                    throw new InvalidDataException();
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException or NotSupportedException) { throw new InvalidOperationException("설정 파일이 올바르지 않습니다. 자동으로 대체하지 않습니다."); }
        }
        else if (location.Mode == AppMode.Demo)
            Current = new(Guid.NewGuid(), location.DatasetId, ScenarioApSource.DemoOrigin, 1, 1, [], BaselineStatus.Unverified, new());
    }
    public async Task UpdateAsync(Uri origin, int interval, ProxySettings proxy, long expectedVersion, CancellationToken ct)
    {
        if (expectedVersion != (Current?.ConfigurationVersion ?? 0))
            throw new ProfileConflictException("설정이 변경되었습니다. 현재 설정을 다시 읽으세요.");
        if (location.Mode == AppMode.Demo && origin != ScenarioApSource.DemoOrigin)
            throw new ArgumentException("Demo 모드는 가상 출처만 사용합니다.");
        if (Current is not null && origin != Current.Origin)
            throw new ProfileConflictException("서버 출처 변경은 새 데이터셋이 필요합니다. 새 전용 데이터 폴더로 시작하세요.");
        var next = new ProfileSnapshot(Current?.InstanceId ?? Guid.NewGuid(), location.DatasetId, origin, (Current?.ConfigurationVersion ?? 0) + 1, Current?.ScopeVersion ?? 1, Current?.ExpectedTargets ?? [], Current?.BaselineStatus ?? BaselineStatus.Unverified, new(interval)) { Proxy = proxy, CredentialsVersion = Current?.CredentialsVersion ?? 1 };
        await SaveAsync(next, ct);
    }
    public Task CredentialsChangedAsync(CancellationToken ct)
    {
        var current = Current ?? throw new ArgumentException("서버 설정을 먼저 저장하세요.");
        return SaveAsync(new(current.InstanceId, current.DatasetId, current.Origin, current.ConfigurationVersion + 1, current.ScopeVersion + 1, current.ExpectedTargets, BaselineStatus.Unverified, current.Settings)
        {
            Proxy = current.Proxy,
            CredentialsVersion = current.CredentialsVersion + 1
        }, ct);
    }
    private async Task SaveAsync(ProfileSnapshot next, CancellationToken ct)
    {
        var path = Path.Combine(location.DataDirectory, "profile.json");
        var temporary = path + "." + Guid.NewGuid() + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, next, cancellationToken: ct);
                await file.FlushAsync(ct);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
            Current = next;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
public sealed class ProfileConflictException(string message) : Exception(message);
