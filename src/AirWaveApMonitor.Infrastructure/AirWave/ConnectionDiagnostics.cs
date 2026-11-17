using System.Diagnostics;
using System.Text;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Demo;
using AirWaveApMonitor.Infrastructure.Security;
namespace AirWaveApMonitor.Infrastructure.AirWave;

public sealed class ConnectionDiagnostics(AppMode mode, AirWaveClient live, TimeProvider clock)
{
    public async Task<DiagnosticReport> RunAsync(ProfileSnapshot profile, CancellationToken ct)
    {
        if (mode == AppMode.Live)
        {
            var report = (await live.ReadAsync(profile, ct)).Report;
            // Resolver hint is read only for an explicit diagnostic, never every
            // scheduled AP polling pass. Do not mistake it for an observed route.
            return report with { ProxyRouteHint = AirWaveSession.ProxyRouteHint(profile.Proxy, profile.Origin) };
        }
        if (profile.Origin != ScenarioApSource.DemoOrigin)
            throw new InvalidOperationException("Demo 출처가 올바르지 않습니다.");
        ct.ThrowIfCancellationRequested();
        var started = clock.GetUtcNow();
        var timer = Stopwatch.StartNew();
        var xml = ScenarioApSource.CreateXml(ScenarioApSource.Frames.Single(f => f.Name == "정상"));
        var batch = new ApListParser(EvidenceSource.Demo).Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), clock.GetUtcNow(), ct);
        ct.ThrowIfCancellationRequested();
        return new(Guid.NewGuid(), started, ExecutionIdentity.Current, "synthetic-memory", "not-applicable-demo", null, 0, 0, batch.Observations.Length, batch.ExcludedRecords.Length, timer.ElapsedMilliseconds, batch.Outcome, false,
            [new("demo-fixture", "succeeded", null, timer.ElapsedMilliseconds, "합성 XML을 실제 AP 파서로 읽음"), new("parse", batch.Outcome.ToString().ToLowerInvariant(), null, timer.ElapsedMilliseconds, $"유효 AP {batch.Observations.Length}")], batch.Errors,
            ["가상 자료 검사입니다. 네트워크·TLS·로그인·실제 장비 검증이 아닙니다.", "이력 DB·운영 RunId·장애/복구 기록은 아직 제공하지 않음."]);
    }
}
