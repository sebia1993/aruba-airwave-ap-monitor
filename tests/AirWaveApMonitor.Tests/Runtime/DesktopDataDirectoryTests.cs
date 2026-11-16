using AirWaveApMonitor.App.Runtime;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Tests.Support;
using Microsoft.AspNetCore.Builder;

namespace AirWaveApMonitor.Tests.Runtime;

[Collection("Host environment")]
public sealed class DesktopDataDirectoryTests
{
    [Fact]
    public async Task RelativeDesktopDataDirectory_IsRejectedBeforeCreatingAnyFiles()
    {
        using var directory = new TemporaryDirectory();
        var previous = Environment.CurrentDirectory;
        WebApplication? unexpected = null;
        Environment.CurrentDirectory = directory.Path;
        try
        {
            Assert.Throws<ArgumentException>(() => unexpected = AppBootstrap.Build(
                ["--desktop", "true", "--no-browser", "true", "--data-dir", "한글 데이터"]));
            Assert.False(Directory.Exists(Path.Combine(directory.Path, "한글 데이터")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
        }
        finally
        {
            if (unexpected is not null) await unexpected.DisposeAsync();
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public async Task DataInsideApplicationDirectory_IsRejectedBeforeWritingModeMarker()
    {
        var data = Path.Combine(AppContext.BaseDirectory, "desktop-rejected-" + Guid.NewGuid().ToString("N"), "데이터");
        WebApplication? unexpected = null;
        try
        {
            Assert.Throws<ArgumentException>(() => unexpected = AppBootstrap.Build(
                ["--desktop", "true", "--no-browser", "true", "--data-dir", data]));
            Assert.False(Directory.Exists(data));
            Assert.False(File.Exists(Path.Combine(data, "runtime-mode.json")));
        }
        finally
        {
            if (unexpected is not null) await unexpected.DisposeAsync();
            var ownedParent = Path.GetDirectoryName(data)!;
            if (Directory.Exists(ownedParent)) Directory.Delete(ownedParent, recursive: true);
        }
    }

    [Theory]
    [InlineData(".")]
    [InlineData("app")]
    [InlineData("app/data")]
    [InlineData("tools/data")]
    [InlineData("../release/app/../data")]
    public void PackagedApplication_RejectsPackageRootAndEveryInternalDataLocation(string relative)
    {
        using var directory = new TemporaryDirectory();
        var package = Path.Combine(directory.Path, "release");
        var data = Path.GetFullPath(Path.Combine(package, relative.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Throws<ArgumentException>(() => DesktopDataDirectory.Validate(Desktop(data), Path.Combine(package, "app")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public void ExternalUnicodeDirectoryAndSimilarPrefix_RemainUntouchedAndAllowed()
    {
        using var directory = new TemporaryDirectory();
        var package = Path.Combine(directory.Path, "한글 프로그램");
        var data = Path.Combine(directory.Path, "한글 프로그램-데이터", "기존 데이터");
        DesktopDataDirectory.Validate(Desktop(data), Path.Combine(package, "app"));
        Assert.False(Directory.Exists(data));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public void RootDirectory_IsRejectedBeforeAnyStorageInitialization()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.GetPathRoot(directory.Path)!;
        Assert.Throws<ArgumentException>(() => DesktopDataDirectory.Validate(Desktop(root), Path.Combine(directory.Path, "release", "app")));
    }

    [Fact]
    public void PackageAtDriveRoot_StillRejectsItsChildDataDirectory()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.GetPathRoot(directory.Path)!;
        Assert.Throws<ArgumentException>(() => DesktopDataDirectory.Validate(Desktop(Path.Combine(root, "synthetic-desktop-data")), Path.Combine(root, "app")));
    }

    [Theory]
    [InlineData("folder\nname")]
    [InlineData("folder\"name")]
    public void InvalidPathCharacters_AreRejectedWithoutCreatingDirectories(string segment)
    {
        using var directory = new TemporaryDirectory();
        Assert.Throws<ArgumentException>(() => DesktopDataDirectory.Validate(Desktop(Path.Combine(directory.Path, segment)), Path.Combine(directory.Path, "release", "app")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public void DefaultLocationAndUnattendedBackendKeepTheirExistingContracts()
    {
        DesktopDataDirectory.Validate(new(AppMode.Live, null, 8765, Desktop: true), AppContext.BaseDirectory);
        DesktopDataDirectory.Validate(new(AppMode.Live, "relative-backend-data", 8765), AppContext.BaseDirectory);
    }

    [UnixFact]
    public void ExistingSymlinkCannotDisguiseDataInsideThePackage()
    {
        using var directory = new TemporaryDirectory();
        var package = Path.Combine(directory.Path, "release");
        Directory.CreateDirectory(Path.Combine(package, "app"));
        var alias = Path.Combine(directory.Path, "external-looking-alias");
        Directory.CreateSymbolicLink(alias, package);
        var data = Path.Combine(alias, "new-data");
        Assert.Throws<ArgumentException>(() => DesktopDataDirectory.Validate(Desktop(data), Path.Combine(package, "app")));
        Assert.False(Directory.Exists(Path.Combine(package, "new-data")));
    }

    private static RuntimeOptions Desktop(string path) => new(AppMode.Live, path, 8765, Desktop: true);
}
