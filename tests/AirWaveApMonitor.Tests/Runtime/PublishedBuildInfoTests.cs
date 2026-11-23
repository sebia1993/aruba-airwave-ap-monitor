using System.Text.Json;
using AirWaveApMonitor.App.Runtime;
using Xunit;

namespace AirWaveApMonitor.Tests.Runtime;

public sealed class PublishedBuildInfoTests
{
    [Fact]
    public void PackagedMetadataDefinesRunningVersionWithoutSourcePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "airwave-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "app"));
        Directory.CreateDirectory(Path.Combine(root, "metadata"));
        try
        {
            File.WriteAllText(Path.Combine(root, "metadata", "VERSION.json"),
                JsonSerializer.Serialize(new { version="0.1.3-preview.9", source_commit=new string('a',40),
                    working_tree_dirty=false, onsite_verified=false,
                    origin="https://10.0.0.15", username="private", password="private" }));
            var info = PublishedBuildInfo.Read(Path.Combine(root, "app"));
            Assert.True(info.PackageMetadataPresent);
            Assert.Equal("0.1.3-preview.9",info.Version);
            Assert.Equal(new string('a',40),info.SourceCommit);
            Assert.Equal(false, info.OnsiteVerified);
            Assert.DoesNotContain("10.0.0.15",JsonSerializer.Serialize(info));
            Assert.DoesNotContain("private",JsonSerializer.Serialize(info));
        }
        finally { Directory.Delete(root,true); }
    }

    [Fact]
    public void MissingOrInvalidMetadataNeverInventsReleaseVersion()
    {
        var root = Path.Combine(Path.GetTempPath(),"airwave-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "app"));
        try
        {
            Assert.Equal(PublishedBuildInfo.Unknown,PublishedBuildInfo.Read(Path.Combine(root,"app")));
            Directory.CreateDirectory(Path.Combine(root,"metadata"));
            File.WriteAllText(Path.Combine(root,"metadata","VERSION.json"),"{\"version\":\"https://192.0.2.5\"}");
            var info = PublishedBuildInfo.Read(Path.Combine(root,"app"));
            Assert.False(info.PackageMetadataPresent);
            Assert.Equal("development",info.Version);
        }
        finally { Directory.Delete(root,true); }
    }
}
