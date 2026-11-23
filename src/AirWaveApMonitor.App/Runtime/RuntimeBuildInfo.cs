using System.Text.Json;
using System.Text.RegularExpressions;

namespace AirWaveApMonitor.App.Runtime;

// Reads only allowlisted build metadata from the running package. Never returns a filesystem path.
public sealed record RuntimeBuildInfo(string Version, string? SourceCommit, bool Packaged)
{
    private static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+(?:-preview\.\d+)?$", RegexOptions.CultureInvariant);
    private static readonly Regex CommitPattern = new("^[a-f0-9]{40}$", RegexOptions.CultureInvariant);

    public static RuntimeBuildInfo Read(string appDirectory)
    {
        try
        {
            var parent = Directory.GetParent(Path.GetFullPath(appDirectory));
            if (parent is null) return new("unknown", null, false);
            var file = Path.Combine(parent.FullName,"metadata","VERSION.json");
            var info = new FileInfo(file);
            if (!info.Exists || info.Length is < 2 or > 16384) return new("unknown",null,false);
            using var doc=JsonDocument.Parse(File.ReadAllText(file));
            var root=doc.RootElement;
            if (!root.TryGetProperty("stage",out var stage) || stage.GetString()!="core-monitoring" ||
                !root.TryGetProperty("rid",out var rid) || rid.GetString()!="win-x64" ||
                !root.TryGetProperty("working_tree_dirty",out var dirty) || dirty.ValueKind!=JsonValueKind.False)
                return new("unknown",null,false);
            var version=root.TryGetProperty("version",out var v)?v.GetString():null;
            var commit=root.TryGetProperty("source_commit",out var c)?c.GetString():null;
            if (version is null || !VersionPattern.IsMatch(version) || commit is null || !CommitPattern.IsMatch(commit))
                return new("unknown",null,false);
            return new(version,commit,true);
        }
        catch (Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            return new("unknown",null,false);
        }
    }
}
