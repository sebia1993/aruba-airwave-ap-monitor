using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.Demo;

namespace AirWaveApMonitor.App.Collection;

/// <summary>Demo-only decision; the coordinator owns every run, token, gate and finalization.</summary>
public sealed class DemoPrehistoryInitializer(ISourceCheckpointStore checkpoints)
{
    public async Task<int?> GetNextPositionAsync(ProfileSnapshot profile, CancellationToken ct)
    {
        if (profile.Origin != ScenarioApSource.DemoOrigin) return null;
        var checkpoint = await checkpoints.GetSourceCheckpointAsync(profile.DatasetId, ScenarioApSource.CheckpointSource, ct);
        var position = checkpoint?.Position ?? -1;
        if (position < -1 || position >= ScenarioApSource.Frames.Length)
            throw new InvalidOperationException("저장된 Demo 진행 위치가 올바르지 않습니다.");
        var next = position + 1;
        return next < ScenarioApSource.Frames.Length && ScenarioApSource.Frames[next].IsPrehistory ? next : null;
    }
}
