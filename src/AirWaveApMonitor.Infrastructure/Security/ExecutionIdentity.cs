using System.Security.Principal;
namespace AirWaveApMonitor.Infrastructure.Security;

public static class ExecutionIdentity
{
    public static string Current
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return Environment.UserName;
            using var identity = WindowsIdentity.GetCurrent();
            return identity.Name;
        }
    }
}
public sealed class CredentialUnavailableException : Exception
{
    public CredentialUnavailableException() : base("실행 계정에서 자격 증명을 복호화하지 못했습니다. 재등록이 필요합니다.") { }
}
