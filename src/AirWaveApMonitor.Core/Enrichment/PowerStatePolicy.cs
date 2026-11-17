using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Core.Enrichment;

public static class PowerStatePolicy
{
    public static PowerStateAssessment Evaluate(PowerStateObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        bool Valid(FieldEvidence evidence) => EnrichmentGuard.ValidProfile(observation.Profile) && EnrichmentGuard.ValidField(evidence, observation.Profile);
        return new(
            Valid(observation.SupplyEvidence) && Enum.IsDefined(observation.SupplyState) ? observation.SupplyState : PowerSupplyState.Unknown,
            Valid(observation.MethodEvidence) && Enum.IsDefined(observation.SupplyMethod) ? observation.SupplyMethod : PowerSupplyMethod.Unknown,
            Valid(observation.ErrorEvidence) && observation.ErrorCount >= 0 ? observation.ErrorCount : null,
            Valid(observation.NegotiationEvidence) ? observation.LldpNegotiationSupported : null);
    }
}
