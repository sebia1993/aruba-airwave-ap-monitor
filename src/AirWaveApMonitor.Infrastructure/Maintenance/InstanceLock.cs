namespace AirWaveApMonitor.Infrastructure.Maintenance;

/// <summary>Lifetime ownership of one physical data directory; never delete the lock file.</summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream handle;
    public InstanceLock(StorageLocation location)
    {
        try
        {
            handle = new FileStream(Path.Combine(location.DataDirectory, "collector.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new IOException("이 데이터 폴더의 수집기가 이미 실행 중이거나 잠금 파일을 열 수 없습니다.", error);
        }
    }
    public void Dispose() => handle.Dispose();
}
