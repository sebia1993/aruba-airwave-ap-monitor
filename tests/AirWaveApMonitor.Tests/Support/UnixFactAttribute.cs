namespace AirWaveApMonitor.Tests.Support;

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "심볼릭 링크 생성 권한이 필요한 Windows 검사는 별도 환경에서 수행합니다.";
    }
}
