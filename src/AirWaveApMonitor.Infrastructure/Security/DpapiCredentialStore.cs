using System.Security.Cryptography;
using System.Text;
using AirWaveApMonitor.Core.Contracts;
namespace AirWaveApMonitor.Infrastructure.Security;

public sealed class DpapiCredentialStore(string directory) : ICredentialStore
{
    public async Task SaveAsync(Guid datasetId, Uri origin, AirWaveCredentials credentials, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("실제 자격 증명은 Windows 실행 계정 DPAPI가 필요합니다.");
        Validate(credentials);
        ct.ThrowIfCancellationRequested();
        var entropy = Binding(datasetId, origin);
        byte[] plain;
        using (var payload = new MemoryStream())
        {
            using (var writer = new BinaryWriter(payload, Encoding.UTF8, true))
            {
                writer.Write(1);
                writer.Write(credentials.GetUsername());
                writer.Write(credentials.GetPassword());
            }
            plain = payload.ToArray();
        }
        byte[] encrypted;
        try
        {
            encrypted = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(directory);
        var path = FilePath(entropy);
        var temp = path + "." + Guid.NewGuid() + ".tmp";
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await output.WriteAsync(encrypted, ct);
                await output.FlushAsync(ct);
                output.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); CryptographicOperations.ZeroMemory(encrypted); }
    }
    public async Task<AirWaveCredentials?> ReadAsync(Guid datasetId, Uri origin, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("실제 자격 증명은 Windows 실행 계정 DPAPI가 필요합니다.");
        ct.ThrowIfCancellationRequested();
        var entropy = Binding(datasetId, origin);
        var path = FilePath(entropy);
        if (!File.Exists(path))
            return null;
        byte[]? plain = null;
        try
        {
            if (new FileInfo(path).Length > 128 * 1024)
                throw new CredentialUnavailableException();
            var bytes = await File.ReadAllBytesAsync(path, ct);
            plain = ProtectedData.Unprotect(bytes, entropy, DataProtectionScope.CurrentUser);
            using var input = new MemoryStream(plain);
            using var reader = new BinaryReader(input, Encoding.UTF8);
            if (reader.ReadInt32() != 1)
                throw new CredentialUnavailableException();
            var value = new AirWaveCredentials(reader.ReadString(), reader.ReadString());
            Validate(value);
            if (input.Position != input.Length)
                throw new CredentialUnavailableException();
            return value;
        }
        catch (Exception error) when (error is CryptographicException or IOException or ArgumentException or UnauthorizedAccessException) { throw new CredentialUnavailableException(); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }
    public Task ClearAsync(Guid datasetId, Uri origin, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows DPAPI가 필요합니다.");
        ct.ThrowIfCancellationRequested();
        File.Delete(FilePath(Binding(datasetId, origin)));
        return Task.CompletedTask;
    }
    private string FilePath(byte[] binding) => Path.Combine(directory, Convert.ToHexString(binding) + ".dpapi");
    private static byte[] Binding(Guid datasetId, Uri origin)
    {
        if (datasetId == Guid.Empty || !origin.IsAbsoluteUri || origin.Scheme != "https" || origin.UserInfo.Length > 0 || origin.AbsolutePath != "/" || origin.Query.Length > 0 || origin.Fragment.Length > 0)
            throw new ArgumentException("자격 증명 바인딩이 올바르지 않습니다.");
        return SHA256.HashData(Encoding.UTF8.GetBytes(datasetId.ToString("D") + "|" + origin.GetLeftPart(UriPartial.Authority) + "/"));
    }
    internal static void Validate(AirWaveCredentials value)
    {
        if (string.IsNullOrWhiteSpace(value.GetUsername()) || value.GetUsername().Length > 1024 || string.IsNullOrEmpty(value.GetPassword()) || value.GetPassword().Length > 8192)
            throw new ArgumentException("계정과 비밀번호를 입력하세요. 입력 길이 제한을 확인하세요.");
    }
}
