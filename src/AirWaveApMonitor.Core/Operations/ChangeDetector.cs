using System.Collections.Immutable;
using System.Text.RegularExpressions;
using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Core.Operations;

public sealed class ChangeDetector
{
    public ImmutableArray<ChangeEvent> Compare(DeviceMetadata? previous, DeviceMetadata current, ObservationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.DatasetId == Guid.Empty || evidence.InstanceId == Guid.Empty || evidence.ApId == Guid.Empty ||
            evidence.DeviceGenerationId == Guid.Empty || evidence.RunId <= 0 || evidence.ScopeVersion <= 0)
            throw new ArgumentException("Automatic history requires a saved AP run and exact device identity.", nameof(evidence));
        if (!Enum.IsDefined(evidence.IdentityChange)) throw new ArgumentException("Unknown identity decision.", nameof(evidence));
        var replaced = evidence.IdentityChange == IdentityChange.Replaced;
        string? OldIdentity(string field) => previous is null ? null : Valid(field == "serial_number" ? previous.Identity.SerialNumber : previous.Identity.WiredMac, evidence.BeforeFields, field);
        string? NewIdentity(string field) => Valid(field == "serial_number" ? current.Identity.SerialNumber : current.Identity.WiredMac, evidence.AfterFields, field);
        var oldSerial = OldIdentity("serial_number");
        var newSerial = NewIdentity("serial_number");
        var oldMac = OldIdentity("lan_mac");
        var newMac = NewIdentity("lan_mac");
        var identityChanged = oldSerial is not null && newSerial is not null ? oldSerial != newSerial :
            oldMac is not null && newMac is not null && oldMac != newMac;
        if (identityChanged != replaced)
            throw new ArgumentException("Hardware identity change and generation decision must agree.", nameof(evidence));
        if (replaced && (previous is null || evidence.PreviousDeviceGenerationId is null ||
            evidence.PreviousDeviceGenerationId == Guid.Empty || evidence.PreviousDeviceGenerationId == evidence.DeviceGenerationId))
            throw new ArgumentException("Replacement requires distinct previous and current generations.", nameof(evidence));
        if (!replaced && previous is not null && evidence.PreviousDeviceGenerationId is { } previousGeneration &&
            previousGeneration != evidence.DeviceGenerationId)
            throw new ArgumentException("Different generations require an explicit replacement decision.", nameof(evidence));
        var result = ImmutableArray.CreateBuilder<ChangeEvent>();
        ChangeEvent Event(OperationHistoryKind kind, ChangeSemantics semantics, string field, string? before,
            string? after, FieldEvidence? beforeEvidence, FieldEvidence? afterEvidence, bool needsConfirmation = false)
            => new(kind, semantics, field, before, after, evidence.DatasetId, evidence.InstanceId, evidence.ApId,
                evidence.DeviceGenerationId, evidence.PreviousDeviceGenerationId, evidence.RunId, evidence.ScopeVersion,
                evidence.ObservedAtUtc, beforeEvidence, afterEvidence, evidence.HasObservationGap,
                evidence.TimeNeedsReview || (beforeEvidence is not null && afterEvidence is not null &&
                    afterEvidence.CollectedAtUtc < beforeEvidence.CollectedAtUtc), needsConfirmation);
        if (replaced)
        {
            var identityField = Valid(current.Identity.SerialNumber, evidence.AfterFields, "serial_number") is not null &&
                Valid(previous!.Identity.SerialNumber, evidence.BeforeFields, "serial_number") is not null ? "serial_number" : "lan_mac";
            evidence.BeforeFields.TryGetValue(identityField, out var before);
            evidence.AfterFields.TryGetValue(identityField, out var after);
            // Identity must be filtered before generation resolution by the storage integration.
            // Silently omitting this event cannot undo an already selected, invalid generation.
            if (before?.SourceAtUtc is { } oldSource && after?.SourceAtUtc is { } newSource && newSource < oldSource)
                throw new ArgumentException("Stale identity evidence cannot justify a replacement.", nameof(evidence));
            result.Add(Event(OperationHistoryKind.HardwareReplacement, ChangeSemantics.ReplacementDetected,
                identityField, OldIdentity(identityField), NewIdentity(identityField),
                before, after, true));
        }
        void CompareField(string name, string? oldValue, string? newValue, OperationHistoryKind kind = OperationHistoryKind.BasicInformation)
        {
            var after = Valid(newValue, evidence.AfterFields, name);
            if (after is null) return;
            var before = previous is null || replaced ? null : Valid(oldValue, evidence.BeforeFields, name);
            var afterEvidence = evidence.AfterFields[name];
            FieldEvidence? beforeEvidence = null;
            if (before is not null) beforeEvidence = evidence.BeforeFields[name];
            if (beforeEvidence?.SourceAtUtc is { } previousSource && afterEvidence.SourceAtUtc is { } source && source < previousSource) return;
            if (StringComparer.Ordinal.Equals(before, after)) return;
            result.Add(Event(kind, before is null ? ChangeSemantics.BaselineRegistered : ChangeSemantics.ValueChanged,
                name, before, after, beforeEvidence, afterEvidence));
        }
        CompareField("serial_number", previous?.Identity.SerialNumber, current.Identity.SerialNumber);
        CompareField("lan_mac", previous?.Identity.WiredMac, current.Identity.WiredMac);
        CompareField("name", previous?.Device.Name, current.Device.Name);
        CompareField("lan_ip", previous?.Device.IpAddress, current.Device.IpAddress);
        CompareField("model", previous?.Device.Model, current.Device.Model);
        CompareField("firmware", previous?.Device.Firmware, current.Device.Firmware, OperationHistoryKind.FirmwareController);
        CompareField("site", previous?.Location.Site, current.Location.Site);
        CompareField("building", previous?.Location.Building, current.Location.Building);
        CompareField("floor", previous?.Location.Floor, current.Location.Floor);
        CompareField("zone", previous?.Location.Zone, current.Location.Zone);
        CompareField("detail", previous?.Location.Detail, current.Location.Detail);
        CompareField("syslocation", previous?.Location.AirWaveRawLocation, current.Location.AirWaveRawLocation);
        CompareField("folder", previous?.Location.Folder, current.Location.Folder);
        CompareField("group", previous?.Location.Group, current.Location.Group);
        CompareField("controller_id", previous?.Controller.SourceId, current.Controller.SourceId, OperationHistoryKind.FirmwareController);
        CompareField("controller_name", previous?.Controller.Name, current.Controller.Name, OperationHistoryKind.FirmwareController);
        CompareField("controller_ip", previous?.Controller.IpAddress, current.Controller.IpAddress, OperationHistoryKind.FirmwareController);
        CompareField("planned_maintenance_mode", previous?.SourceManagement?.MaintenanceMode?.ToString(), current.SourceManagement?.MaintenanceMode?.ToString());
        CompareField("operating_mode", previous?.SourceManagement?.ManagementStatus, current.SourceManagement?.ManagementStatus);
        CompareField("upstream_device_id", previous?.Upstream?.SourceSwitchId, current.Upstream?.SourceSwitchId, OperationHistoryKind.Connection);
        CompareField("upstream_port_index", previous?.Upstream?.PortIndex, current.Upstream?.PortIndex, OperationHistoryKind.Connection);
        CompareField("upstream_port_name", previous?.Upstream?.PortName, current.Upstream?.PortName, OperationHistoryKind.Connection);
        CompareField("ap_interface", previous?.Upstream?.ApInterface, current.Upstream?.ApInterface, OperationHistoryKind.Connection);
        return result.ToImmutable();
    }

    private static string? Valid(string? value, ImmutableDictionary<string, FieldEvidence> fields, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || !fields.TryGetValue(field, out var evidence) ||
            evidence.Availability != FieldAvailability.Provided || evidence.Validity != FieldValidity.Valid ||
            evidence.Source is not (EvidenceSource.AirWave or EvidenceSource.Demo) || !Enum.IsDefined(evidence.Freshness)) return null;
        var normalized = value.Trim();
        if (field != "lan_mac") return normalized;
        return Regex.IsMatch(normalized, "^(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$", RegexOptions.CultureInvariant)
            ? normalized.ToUpperInvariant() : null;
    }
}
