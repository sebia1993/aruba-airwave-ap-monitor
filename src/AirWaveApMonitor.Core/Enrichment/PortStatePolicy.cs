using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Core.Enrichment;

public static class PortStatePolicy
{
    public static PortStateAssessment Evaluate(PortStateObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        bool Valid(FieldEvidence evidence) => EnrichmentGuard.ValidProfile(observation.Profile) &&
            EnrichmentGuard.ValidField(evidence, observation.Profile);
        return new(
            Valid(observation.AdministrativeEvidence) && Enum.IsDefined(observation.AdministrativeState)
                ? observation.AdministrativeState : PortAdministrativeState.Unknown,
            Valid(observation.OperationalEvidence) && Enum.IsDefined(observation.OperationalState)
                ? observation.OperationalState : PortOperationalState.Unknown);
    }
}
