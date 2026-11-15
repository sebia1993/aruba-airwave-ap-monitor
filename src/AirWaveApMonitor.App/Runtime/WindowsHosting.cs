using System.Text.RegularExpressions;

namespace AirWaveApMonitor.App.Runtime;

/// <summary>SCM lifetime is activated only when Windows starts this executable as a service.</summary>
public static partial class WindowsHosting
{
    public const string DefaultServiceName = "AirWaveAPMonitor";

    public static string NormalizeServiceName(string? serviceName)
    {
        var name = serviceName ?? DefaultServiceName;
        if (!ServiceNamePattern().IsMatch(name))
            throw new ArgumentException("서비스 이름은 영문·숫자·밑줄·하이픈 1~80자로 지정하세요.", nameof(serviceName));
        return name;
    }

    public static IServiceCollection AddAirWaveWindowsService(this IServiceCollection services, string? serviceName = null)
    {
        var name = NormalizeServiceName(serviceName);
        return services.AddWindowsService(options => options.ServiceName = name);
    }

    [GeneratedRegex("\\A[A-Za-z0-9_-]{1,80}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceNamePattern();
}
