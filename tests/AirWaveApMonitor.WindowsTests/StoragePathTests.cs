using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;

namespace AirWaveApMonitor.WindowsTests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows 러너에서만 실행합니다. macOS 검사는 Windows 검증이 아닙니다.";
    }
}

public sealed class StoragePathTests
{
    [WindowsFact]
    public void LiveData_UsesMachineDataDirectory()
        => Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AirWaveAPMonitor", "live"), StoragePaths.Resolve(AppMode.Live, null));

    [WindowsFact]
    public void DemoData_UsesUserDirectorySeparateFromLiveData()
        => Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AirWaveAPMonitor", "demo"), StoragePaths.Resolve(AppMode.Demo, null));
}
