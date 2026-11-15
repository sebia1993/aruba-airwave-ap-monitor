using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Security;
using AirWaveApMonitor.Tests.Support;

namespace AirWaveApMonitor.Tests.Runtime;

public sealed class ModeIsolationTests
{
    // Catches switching modes after a process restart against the same physical directory.
    [Fact]
    public void SameDataDirectoryForDifferentModes_IsRejected()
    {
        using var temp = new TemporaryDirectory();
        var first = StoragePaths.Open(AppMode.Demo, temp.Path);
        Assert.Equal(AppMode.Demo, first.Mode);
        Assert.Throws<InvalidOperationException>(() => StoragePaths.Open(AppMode.Live, temp.Path));
    }

    [Fact]
    public void ExistingMarker_ReopensSameDataset()
    {
        using var temp = new TemporaryDirectory();
        var first = StoragePaths.Open(AppMode.Demo, temp.Path);
        var reopened = StoragePaths.Open(AppMode.Demo, Path.Combine(temp.Path, "."));
        Assert.NotEqual(Guid.Empty, first.DatasetId);
        Assert.Equal(first.DatasetId, reopened.DatasetId);
    }

    [Fact]
    public void MarkerlessExistingData_IsNotAdopted()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "monitor.db"), "preexisting data");
        Assert.Throws<InvalidOperationException>(() => StoragePaths.Open(AppMode.Demo, temp.Path));
        Assert.Equal("preexisting data", File.ReadAllText(Path.Combine(temp.Path, "monitor.db")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "runtime-mode.json")));
    }

    [Fact]
    public void CorruptMarker_IsRejectedWithoutReplacingIt()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "runtime-mode.json"), "broken");
        Assert.Throws<InvalidOperationException>(() => StoragePaths.Open(AppMode.Demo, temp.Path));
        Assert.Equal("broken", File.ReadAllText(Path.Combine(temp.Path, "runtime-mode.json")));
    }

    [Fact]
    public void MarkerWithoutMode_IsRejectedWithoutAssumingDemo()
    {
        using var temp = new TemporaryDirectory();
        var original = "{\"SchemaVersion\":1,\"DatasetId\":\"f1b98ed4-3229-48af-9b85-d2e5df5cef17\"}";
        File.WriteAllText(Path.Combine(temp.Path, "runtime-mode.json"), original);
        Assert.Throws<InvalidOperationException>(() => StoragePaths.Open(AppMode.Demo, temp.Path));
        Assert.Equal(original, File.ReadAllText(Path.Combine(temp.Path, "runtime-mode.json")));
    }

    [UnixFact]
    public void PhysicalAlias_CannotBypassModeMarker()
    {
        using var temp = new TemporaryDirectory();
        var real = Path.Combine(temp.Path, "real");
        var alias = Path.Combine(temp.Path, "alias");
        StoragePaths.Open(AppMode.Live, real);
        Directory.CreateSymbolicLink(alias, real);
        Assert.Throws<InvalidOperationException>(() => StoragePaths.Open(AppMode.Demo, alias));
    }

    [Fact]
    public void DefaultPaths_AreSeparatedByMode()
    {
        Assert.NotEqual(StoragePaths.Resolve(AppMode.Demo, null), StoragePaths.Resolve(AppMode.Live, null));
        Assert.True(Path.IsPathFullyQualified(StoragePaths.Resolve(AppMode.Demo, null)));
    }

    // If Demo resolves the Live provider (even without reading a secret), this fails.
    [Fact]
    public async Task Demo_DoesNotResolveLiveCredentials()
    {
        var store = CredentialStoreSelection.Create(AppMode.Demo,
            () => throw new InvalidOperationException("Live credentials must not be resolved"));
        var result = await store.ReadAsync(Guid.NewGuid(), new Uri("https://live.example.invalid"), default);
        Assert.Null(result);
    }

    [Fact]
    public async Task Demo_RejectsCredentialRegistration()
    {
        var store = CredentialStoreSelection.Create(AppMode.Demo,
            () => throw new InvalidOperationException("Live provider"));
        await Assert.ThrowsAsync<NotSupportedException>(() => store.SaveAsync(Guid.NewGuid(),
            new Uri("https://demo.example.invalid"), new AirWaveCredentials("operator", "real-password"), default));
    }
}
