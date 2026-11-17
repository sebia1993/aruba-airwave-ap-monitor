using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirWaveApMonitor.Core.Contracts;

public enum AppMode { Demo, Live }

public interface ICredentialStore
{
    Task SaveAsync(Guid datasetId, Uri origin, AirWaveCredentials credentials, CancellationToken ct);
    Task<AirWaveCredentials?> ReadAsync(Guid datasetId, Uri origin, CancellationToken ct);
    Task ClearAsync(Guid datasetId, Uri origin, CancellationToken ct);
}

[JsonConverter(typeof(AirWaveCredentialsJsonConverter))]
public sealed class AirWaveCredentials(string username, string password)
{
    // Methods require deliberate use; serializers and structured log property destructuring
    // cannot enumerate the secrets. Call only inside the credential storage/login boundary.
    public string GetUsername() => username;
    public string GetPassword() => password;
    public override string ToString() => "[AirWave credentials redacted]";
}

public sealed class AirWaveCredentialsJsonConverter : JsonConverter<AirWaveCredentials>
{
    public override AirWaveCredentials Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("자격 증명은 일반 DTO로 역직렬화할 수 없습니다.");
    public override void Write(Utf8JsonWriter writer, AirWaveCredentials value, JsonSerializerOptions options)
        => throw new NotSupportedException("자격 증명은 일반 DTO로 직렬화할 수 없습니다.");
}
