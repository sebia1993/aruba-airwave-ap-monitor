using System.Text.Json;
using AirWaveApMonitor.App.Runtime;

namespace AirWaveApMonitor.Tests.Runtime;

public sealed class RuntimeBuildInfoTests
{
    [Fact]
    public void ReadsOnlyAllowlistedValuesFromMatchingPackage()
    {
        var folder=Path.Combine(Path.GetTempPath(),"airwave-version-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(folder,"app"));
            Directory.CreateDirectory(Path.Combine(folder,"metadata"));
            File.WriteAllText(Path.Combine(folder,"metadata","VERSION.json"),JsonSerializer.Serialize(new {
                version="0.1.3-preview.9",source_commit=new string('a',40),stage="core-monitoring",rid="win-x64",
                working_tree_dirty=false,origin="https://10.20.30.40",password="DO_NOT_RETURN"
            }));
            var info=RuntimeBuildInfo.Read(Path.Combine(folder,"app"));
            Assert.Equal("0.1.3-preview.9",info.Version);
            Assert.Equal(new string('a',40),info.SourceCommit);
            Assert.True(info.Packaged);
            var json=JsonSerializer.Serialize(info);
            Assert.DoesNotContain("10.20.30.40",json);
            Assert.DoesNotContain("DO_NOT_RETURN",json);
        }
        finally { Directory.Delete(folder,true); }
    }
    [Fact]
    public void MissingOrCorruptMetadataNeverClaimsAPackageVersion()
    {
        var folder=Path.Combine(Path.GetTempPath(),"airwave-version-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(folder,"app"));
            var directory=Path.Combine(folder,"app");
            Assert.Equal("unknown",RuntimeBuildInfo.Read(directory).Version);
            Directory.CreateDirectory(Path.Combine(folder,"metadata"));
            File.WriteAllText(Path.Combine(folder,"metadata","VERSION.json"),"{not JSON");
            Assert.False(RuntimeBuildInfo.Read(directory).Packaged);
        }
        finally { Directory.Delete(folder,true); }
    }
}
