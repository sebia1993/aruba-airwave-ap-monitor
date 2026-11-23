using System.Text.Json;
using System.Text.RegularExpressions;

namespace AirWaveApMonitor.App.Runtime;

/// <summary>Reads published build metadata, never the Live profile or user data folder.</summary>
public sealed record PublishedBuildInfo(string Version, string? SourceCommit, bool PackageMetadataPresent,
    bool? WorkingTreeDirty, bool? OnsiteVerified)
{
    private static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private static readonly Regex CommitPattern = new(@"^[a-f0-9]{40}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

    public static PublishedBuildInfo Read(string applicationDirectory)
    {
        try
        {
            // Windows ZIP layout: app/ and metadata/ are siblings. Runtime passes AppContext.BaseDirectory only.
            var path = Path.GetFullPath(Path.Combine(applicationDirectory, "..", "metadata", "VERSION.json"));
            using var file = File.OpenRead(path);
            using var data = JsonDocument.Parse(file);
            var root = data.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Unknown;
            if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.String) return Unknown;
            var version = v.GetString();
            if (version is null || version.Length > 48 || !VersionPattern.IsMatch(version)) return Unknown;
            string? commit = null;
            if (root.TryGetProperty("source_commit", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var raw = c.GetString();
                if (raw is not null && CommitPattern.IsMatch(raw)) commit = raw;
            }
            bool? Flag(string key) => root.TryGetProperty(key,out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean() : null;
            return new(version,commit,true,Flag("working_tree_dirty"),Flag("onsite_verified"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return Unknown;
        }
    }

    public static PublishedBuildInfo Unknown { get; } = new("development", null, false, null, null);
}
