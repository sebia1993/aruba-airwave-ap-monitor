using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Core.Enrichment;

public static class PowerValuePolicy
{
    public static NormalizedPower Normalize(PowerSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.Value is null)
            return new(null, PowerValueReason.Missing);
        if (sample.Value < 0 || !Enum.IsDefined(sample.Metric) || !EnrichmentGuard.ValidProfile(sample.Profile) ||
            !EnrichmentGuard.ValidField(sample.Evidence, sample.Profile))
            return new(null, PowerValueReason.Invalid);
        if (!sample.UnitVerified)
            return new(null, PowerValueReason.UnverifiedUnit);
        return sample.Unit switch
        {
            "W" => new(sample.Value, PowerValueReason.Valid),
            "mW" => new(sample.Value / 1000m, PowerValueReason.Valid),
            "dBm" or "Wh" or "kWh" => new(null, PowerValueReason.DifferentQuantity),
            _ => new(null, PowerValueReason.UnsupportedUnit)
        };
    }
}

public static class PowerSampleIdentityPolicy
{
    public static PowerSampleKey? GetKey(PowerSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return EnrichmentGuard.ValidProfile(sample.Profile) && Enum.IsDefined(sample.Metric) &&
            EnrichmentGuard.ValidEndpoint(sample.Endpoint, sample.Metric != PowerMetric.ApConsumption) &&
            sample.SourceIdentity is { UniquenessVerified: true } identity && !string.IsNullOrWhiteSpace(identity.Id)
            ? new(sample.Profile, sample.Endpoint, sample.Metric, identity.Id) : null;
    }
}
