using System.Text;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Security;
namespace AirWaveApMonitor.WindowsTests;

public sealed class DpapiCredentialStoreTests
{
    [WindowsFact]
    public async Task CurrentProcessAccount_RoundTripsAndBindsToDatasetAndOrigin()
    {
        var path = Path.Combine(Path.GetTempPath(), "AirWave 한글 경로 " + Guid.NewGuid());
        Directory.CreateDirectory(path);
        try
        {
            var id = Guid.NewGuid();
            var origin = new Uri("https://server.example.invalid");
            var store = new DpapiCredentialStore(path);
            await store.SaveAsync(id, origin, new("synthetic-user", "synthetic-password"), default);
            var restored = await new DpapiCredentialStore(path).ReadAsync(id, origin, default);
            Assert.Equal("synthetic-password", restored!.GetPassword());
            Assert.NotEmpty(ExecutionIdentity.Current);
            Assert.Null(await store.ReadAsync(Guid.NewGuid(), origin, default));
            Assert.Null(await store.ReadAsync(id, new Uri("https://other.example.invalid"), default));
            var file = Assert.Single(Directory.GetFiles(path, "*.dpapi"));
            Assert.DoesNotContain("synthetic-password", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
            await store.ClearAsync(id, origin, default);
            Assert.Null(await store.ReadAsync(id, origin, default));
        }
        finally { Directory.Delete(path, true); }
    }
    [WindowsFact]
    public async Task CopiedCiphertext_CannotBeReboundToAnotherDatasetOrOrigin()
    {
        var path = Path.Combine(Path.GetTempPath(), "airwave-binding-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        try
        {
            var store = new DpapiCredentialStore(path);
            var first = Guid.NewGuid();
            var origin = new Uri("https://server.example.invalid");
            await store.SaveAsync(first, origin, new("original-user", "original-secret"), default);
            var original = await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(path, "*.dpapi")));
            foreach (var other in new[] { (Dataset: Guid.NewGuid(), Origin: origin), (Dataset: first, Origin: new Uri("https://other.example.invalid")) })
            {
                var before = Directory.GetFiles(path, "*.dpapi").ToHashSet();
                await store.SaveAsync(other.Dataset, other.Origin, new("target-user", "target-secret"), default);
                var target = Assert.Single(Directory.GetFiles(path, "*.dpapi"), file => !before.Contains(file));
                await File.WriteAllBytesAsync(target, original);
                await Assert.ThrowsAsync<CredentialUnavailableException>(() => store.ReadAsync(other.Dataset, other.Origin, default));
            }
        }
        finally { Directory.Delete(path, true); }
    }
    [WindowsFact]
    public async Task TamperedCiphertext_RequiresReregistrationWithoutPlaintextFallback()
    {
        var path = Path.Combine(Path.GetTempPath(), "airwave-dpapi-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        try
        {
            var id = Guid.NewGuid();
            var origin = new Uri("https://server.example.invalid");
            var store = new DpapiCredentialStore(path);
            await store.SaveAsync(id, origin, new("user", "secret"), default);
            await File.WriteAllBytesAsync(Assert.Single(Directory.GetFiles(path, "*.dpapi")), [1, 2, 3]);
            var error = await Assert.ThrowsAsync<CredentialUnavailableException>(() => store.ReadAsync(id, origin, default));
            Assert.DoesNotContain("secret", error.ToString());
        }
        finally { Directory.Delete(path, true); }
    }
}
