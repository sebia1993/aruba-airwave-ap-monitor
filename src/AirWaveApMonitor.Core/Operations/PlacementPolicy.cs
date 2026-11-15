using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Core.Operations;

public sealed class PlacementPolicy
{
    /// <summary>Returns immutable writes to commit together. It cannot update observations or incident snapshots.</summary>
    public PlacementDecision Move(PlacementPeriod? current, PlacementRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DatasetId == Guid.Empty || request.ApId == Guid.Empty || request.DeviceGenerationId == Guid.Empty ||
            request.Source is not (EvidenceSource.Manual or EvidenceSource.Import) || string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("Placement needs explicit identity, manual/import source and a reason.", nameof(request));
        if (request.InstallationPointId is { } point && (point == Guid.Empty || request.PointVersion is null or <= 0 || request.NewPlacementId == Guid.Empty))
            throw new ArgumentException("A registered point version and new placement ID are required.", nameof(request));
        if (request.InstallationPointId is null && request.PointVersion is not null)
            throw new ArgumentException("An unassigned placement cannot have a point version.", nameof(request));
        if (current is not null && (current.DatasetId != request.DatasetId || current.ApId != request.ApId ||
            current.DeviceGenerationId != request.DeviceGenerationId || current.RecordedEndAtUtc is not null))
            throw new ArgumentException("The previous placement must be active and belong to this exact generation.", nameof(current));
        if (current?.InstallationPointId == request.InstallationPointId) return new(null, current, false);
        if (current is not null && current.PlacementId == request.NewPlacementId && request.InstallationPointId is not null)
            throw new ArgumentException("Moving requires a new placement ID.", nameof(request));
        var timeNeedsReview = (current?.TimeNeedsReview ?? false) ||
            (current is not null && request.RecordedAtUtc < current.RecordedStartAtUtc) ||
            (current?.ActualStartAtUtc is { } actualStart && request.ActualWorkAtUtc is { } actualEnd && actualEnd < actualStart);
        var closed = current is null ? null : current with
        {
            RecordedEndAtUtc = request.RecordedAtUtc,
            ActualEndAtUtc = request.ActualWorkAtUtc,
            EndReason = request.Reason.Trim(),
            TimeNeedsReview = timeNeedsReview
        };
        var opened = request.InstallationPointId is { } nextPoint ? new PlacementPeriod(request.NewPlacementId,
            request.DatasetId, request.ApId, request.DeviceGenerationId, nextPoint, request.PointVersion!.Value,
            request.RecordedAtUtc, request.ActualWorkAtUtc, request.Source, request.WorkId, TimeNeedsReview: timeNeedsReview) : null;
        return new(closed, opened, true);
    }
}
