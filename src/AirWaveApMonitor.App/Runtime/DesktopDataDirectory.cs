namespace AirWaveApMonitor.App.Runtime;

/// <summary>Desktop updates replace the package, so explicit persistent data must live outside it.</summary>
public static class DesktopDataDirectory
{
    public static void Validate(RuntimeOptions options, string applicationDirectory)
    {
        // Preserve unattended CLI contracts and the existing platform-specific default location.
        if (!options.Desktop || options.DataDirectory is null) return;
        var requested = options.DataDirectory;
        if (requested.Any(character => char.IsControl(character) || character == '"') || !Path.IsPathFullyQualified(requested))
            throw new ArgumentException("데이터 폴더는 제어 문자 없는 절대 경로로 지정하세요. 프로그램 폴더 밖의 기존 데이터 경로를 사용하세요.");
        var data = Normalize(requested);
        RequireLocalDedicatedDirectory(data);
        var application = Normalize(applicationDirectory);
        var package = Path.GetFileName(application).Equals("app", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(application)! : application;
        if (Inside(data, package)) throw PackageDirectory();

        // Existing directory links/junctions must not disguise a path back into the package.
        // Missing descendants are only appended; validation never creates a directory or marker.
        var physicalData = ResolveExistingLinks(data);
        RequireLocalDedicatedDirectory(physicalData);
        if (Inside(physicalData, ResolveExistingLinks(package))) throw PackageDirectory();
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool Inside(string data, string package)
        => data.Equals(package, StringComparison.OrdinalIgnoreCase) ||
            data.StartsWith(Path.EndsInDirectorySeparator(package) ? package : package + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RequireLocalDedicatedDirectory(string data)
    {
        var root = Path.GetPathRoot(data)!;
        if (data.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("드라이브 루트 대신 전용 데이터 폴더를 지정하세요.");
        if (!OperatingSystem.IsWindows()) return;
        if (data.Length < 3 || !char.IsAsciiLetter(data[0]) || data[1] != ':' || data[2] is not ('\\' or '/'))
            throw new ArgumentException("Windows 로컬 드라이브의 완전한 데이터 경로를 지정하세요. UNC·드라이브 상대 경로는 사용할 수 없습니다.");
        if (new DriveInfo(root).DriveType == DriveType.Network)
            throw new ArgumentException("SQLite 데이터 폴더는 네트워크 드라이브에 둘 수 없습니다.");
    }

    private static string ResolveExistingLinks(string path, int depth = 0)
    {
        if (depth > 32) throw new ArgumentException("데이터 경로의 디렉터리 연결이 너무 깊어 확인할 수 없습니다.");
        var root = Path.GetPathRoot(path)!;
        var resolved = root;
        foreach (var segment in path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, segment);
            var directory = new DirectoryInfo(resolved);
            if (directory.LinkTarget is not null)
            {
                var target = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new ArgumentException("데이터 경로의 디렉터리 연결을 확인할 수 없습니다.");
                // A final link target can itself contain linked ancestors (for example /var
                // on macOS). Normalize those too before comparing physical directories.
                resolved = ResolveExistingLinks(Normalize(target), depth + 1);
            }
        }
        return Normalize(resolved);
    }

    private static ArgumentException PackageDirectory()
        => new("데이터 폴더는 프로그램 버전 폴더 밖에 지정하세요. 업데이트할 때 기존 데이터가 유지되는 절대 경로를 사용하세요.");
}
