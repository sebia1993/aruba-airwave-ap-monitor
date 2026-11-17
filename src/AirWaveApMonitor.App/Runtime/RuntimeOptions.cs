using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.App.Runtime;

public sealed record RuntimeOptions(AppMode Mode, string? DataDirectory, int Port, string? ServiceName = null,
    bool Desktop = false, bool NoBrowser = false)
{
    public static RuntimeOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var name = args[index];
            if (name is not ("--mode" or "--data-dir" or "--port" or "--service-name" or "--desktop" or "--no-browser") || index + 1 >= args.Length ||
                string.IsNullOrWhiteSpace(args[index + 1]) || !values.TryAdd(name, args[index + 1]))
                throw new ArgumentException("옵션: --mode demo|live [--data-dir 경로] [--port 1~65535] [--service-name 이름] [--desktop true|false] [--no-browser true|false]. 중복·미지원 옵션은 허용하지 않습니다.");
        }
        bool Flag(string name) => !values.TryGetValue(name, out var text) ? false : text switch
        { "true" => true, "false" => false, _ => throw new ArgumentException(name + " 값은 true 또는 false여야 합니다.") };
        var desktop = Flag("--desktop"); var noBrowser = Flag("--no-browser");
        if (desktop && values.ContainsKey("--service-name") || noBrowser && !desktop)
            throw new ArgumentException("desktop은 서비스 옵션과 함께 사용할 수 없으며 no-browser는 desktop 실행에만 사용합니다.");
        var modeText = values.GetValueOrDefault("--mode", desktop ? "live" : "");
        if (modeText is not ("demo" or "live"))
            throw new ArgumentException("--mode demo 또는 --mode live를 명시해야 합니다.");
        var port = 8765;
        if (values.TryGetValue("--port", out var portText) && (!int.TryParse(portText, out port) || port is < 1 or > 65535))
            throw new ArgumentException("포트는 1~65535 범위의 정수여야 합니다.");
        return new RuntimeOptions(modeText == "demo" ? AppMode.Demo : AppMode.Live, values.GetValueOrDefault("--data-dir"), port,
            values.TryGetValue("--service-name", out var serviceName) ? WindowsHosting.NormalizeServiceName(serviceName) : null, desktop, noBrowser);
    }
}
