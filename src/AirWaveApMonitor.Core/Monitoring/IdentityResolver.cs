using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Core.Monitoring;

public sealed class IdentityResolver
{
    /// <summary>The caller resolves the instance/dataset key; this overload additionally checks AP ID.</summary>
    public IdentityDecision Resolve(ApIdentitySnapshot? previous, ApObservation current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (string.IsNullOrWhiteSpace(current.SourceApId))
        {
            return new(IdentityChange.Unattributable, new(null, null), [], false, false);
        }
        if (previous is not null && previous.SourceApId != current.SourceApId)
        {
            throw new ArgumentException("Previous identity belongs to a different AP.", nameof(previous));
        }

        var serial = ValidValue(current.Identity.SerialNumber, current.Fields, MonitoringFieldNames.SerialNumber);
        var mac = ValidValue(current.Identity.WiredMac, current.Fields, MonitoringFieldNames.WiredMac);
        var oldSerial = previous is null ? null : ValidValue(previous.Identity.SerialNumber,
            previous.Fields, MonitoringFieldNames.SerialNumber);
        var oldMac = previous is null ? null : ValidValue(previous.Identity.WiredMac,
            previous.Fields, MonitoringFieldNames.WiredMac);
        var macChanged = oldMac is not null && mac is not null &&
            !StringComparer.OrdinalIgnoreCase.Equals(oldMac, mac);
        var replaced = oldSerial is not null && serial is not null
            ? !StringComparer.Ordinal.Equals(oldSerial, serial)
            : macChanged;
        var enriched = (oldSerial is null && serial is not null) || (oldMac is null && mac is not null);
        var change = previous is null ? IdentityChange.FirstGeneration
            : replaced ? IdentityChange.Replaced
            : enriched ? IdentityChange.Enriched
            : IdentityChange.Unchanged;

        var fields = ImmutableDictionary.CreateBuilder<string, FieldEvidence>();
        void PreserveEvidence(string field, string? value, string? oldValue)
        {
            if (value is not null)
            {
                fields[field] = current.Fields[field];
            }
            else if (!replaced && oldValue is not null)
            {
                fields[field] = previous!.Fields[field];
            }
        }
        PreserveEvidence(MonitoringFieldNames.SerialNumber, serial, oldSerial);
        PreserveEvidence(MonitoringFieldNames.WiredMac, mac, oldMac);
        return new(change, new(serial ?? (replaced ? null : oldSerial), mac ?? (replaced ? null : oldMac)),
            fields.ToImmutable(), replaced, macChanged);
    }

    public IdentityBatchConflicts FindBatchConflicts(IEnumerable<ApObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var rows = observations.ToArray();
        var duplicates = rows.GroupBy(row => row.SourceApId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToImmutableHashSet(StringComparer.Ordinal);
        var collisions = rows.Where(row => !string.IsNullOrWhiteSpace(row.SourceApId) &&
                !duplicates.Contains(row.SourceApId))
            .Select(row => (row.SourceApId, Serial: ValidValue(row.Identity.SerialNumber,
                row.Fields, MonitoringFieldNames.SerialNumber)))
            .Where(row => row.Serial is not null)
            .GroupBy(row => row.Serial!, StringComparer.Ordinal)
            .Where(group => group.Select(row => row.SourceApId).Distinct(StringComparer.Ordinal).Count() > 1)
            .ToImmutableDictionary(group => group.Key,
                group => group.Select(row => row.SourceApId).Order(StringComparer.Ordinal).ToImmutableArray(),
                StringComparer.Ordinal);
        return new(duplicates, collisions);
    }

    internal static bool HasValidEvidence(ImmutableDictionary<string, FieldEvidence> fields, string field)
        => fields.TryGetValue(field, out var evidence) && evidence.Availability == FieldAvailability.Provided &&
            evidence.Validity == FieldValidity.Valid;

    private static string? ValidValue(string? value, ImmutableDictionary<string, FieldEvidence> fields, string field)
        => !string.IsNullOrWhiteSpace(value) && HasValidEvidence(fields, field) ? value.Trim() : null;
}
