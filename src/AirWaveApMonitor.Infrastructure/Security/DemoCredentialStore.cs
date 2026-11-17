using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Demo;
namespace AirWaveApMonitor.Infrastructure.Security;
/// <summary>Only built-in synthetic credentials live in memory. Operator secrets are never accepted in Demo.</summary>
public sealed class DemoCredentialStore : ICredentialStore
{
    private readonly AirWaveCredentials synthetic = new("demo-reader", "demo-only");
    public Task SaveAsync(Guid datasetId, Uri origin, AirWaveCredentials credentials, CancellationToken ct) => throw new NotSupportedException("Demo 모드에는 실제 자격 증명을 등록할 수 없습니다.");
    public Task<AirWaveCredentials?> ReadAsync(Guid datasetId, Uri origin, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(origin == ScenarioApSource.DemoOrigin ? (AirWaveCredentials?)synthetic : null);
    }
    public Task ClearAsync(Guid datasetId, Uri origin, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
