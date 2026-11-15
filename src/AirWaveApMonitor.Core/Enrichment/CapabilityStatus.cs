using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Core.Enrichment;

public static class CapabilityStatus
{
    public static CapabilityDecision Evaluate(CapabilityEvidence? evidence, CapabilityKey requested, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (!EnrichmentGuard.ValidProfile(requested.Profile) || string.IsNullOrWhiteSpace(requested.DeviceFamily) ||
            string.IsNullOrWhiteSpace(requested.Field) || string.IsNullOrWhiteSpace(requested.ContractRevision))
            return new(CapabilityAvailability.Unverified, false, CapabilityDecisionReason.Unverified);
        if (evidence is null)
            return new(CapabilityAvailability.Unverified, false, CapabilityDecisionReason.Unverified);
        if (evidence.Key != requested)
            return new(CapabilityAvailability.Unverified, false, CapabilityDecisionReason.BindingChanged);
        var availability = CapabilityAvailability.Unverified;
        if (evidence.VerifiedAtUtc is not null && evidence.ContractVerified)
        {
            if (evidence.Availability == CapabilityAvailability.UnsupportedConfirmed &&
                evidence.Verification == CapabilityVerification.ExplicitUnsupported)
                availability = CapabilityAvailability.UnsupportedConfirmed;
            else if (evidence.Availability == CapabilityAvailability.AvailableConfirmed &&
                ((requested.Profile.Source == EvidenceSource.AirWave && evidence.Verification == CapabilityVerification.ProfileResponse) ||
                 (requested.Profile.Source == EvidenceSource.Demo && evidence.Verification == CapabilityVerification.DemoFixture)))
                availability = CapabilityAvailability.AvailableConfirmed;
        }
        if (availability == CapabilityAvailability.UnsupportedConfirmed)
            return new(availability, false, CapabilityDecisionReason.Unsupported);
        if (availability != CapabilityAvailability.AvailableConfirmed)
            return new(availability, false, CapabilityDecisionReason.Unverified);
        if (!evidence.Implemented)
            return new(availability, false, CapabilityDecisionReason.NotImplemented);
        return new(availability, enabled, enabled ? CapabilityDecisionReason.Available : CapabilityDecisionReason.Disabled);
    }
}
