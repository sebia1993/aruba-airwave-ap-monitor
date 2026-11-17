using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Core.Enrichment;

internal static class EnrichmentGuard
{
    internal static bool ValidProfile(EnrichmentProfileBinding? profile) => profile is not null &&
        profile.DatasetId != Guid.Empty && profile.InstanceId != Guid.Empty && profile.ConfigurationVersion > 0 &&
        profile.ScopeVersion > 0 && profile.CredentialsVersion > 0 && profile.Source is EvidenceSource.AirWave or EvidenceSource.Demo &&
        profile.Origin is { IsAbsoluteUri: true } origin && origin.Scheme == Uri.UriSchemeHttps &&
        origin.UserInfo.Length == 0 && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0;

    internal static bool ValidEndpoint(ConnectionEndpoint? endpoint, bool requireInterface = false)
        => endpoint is not null && !string.IsNullOrWhiteSpace(endpoint.SourceDeviceId) && Enum.IsDefined(endpoint.Role) &&
            (!requireInterface || !string.IsNullOrWhiteSpace(endpoint.SourceInterfaceId));

    internal static bool ValidField(FieldEvidence evidence, EnrichmentProfileBinding profile)
        => evidence.Availability == FieldAvailability.Provided && evidence.Validity == FieldValidity.Valid &&
            evidence.Source == profile.Source;
}
