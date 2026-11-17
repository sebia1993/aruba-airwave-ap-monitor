using System.Collections.Immutable;
using AirWaveApMonitor.Core.Contracts;

namespace AirWaveApMonitor.Infrastructure.Storage;

internal static class DeviceMetadataMerger
{
    public static readonly DeviceMetadata Empty = new(new(null, null), new(null, null, null, null), new(), new());

    public static CommittedDeviceView Merge(CommittedDeviceView? previous, ApTransitionDecision decision, Guid apId)
    {
        var metadata = decision.InvalidatesEnrichment ? Empty : previous?.Metadata ?? Empty;
        var fields = (decision.InvalidatesEnrichment ? ImmutableDictionary<string, FieldEvidence>.Empty : previous?.Fields ?? []).ToBuilder();
        var sourceAt = decision.InvalidatesEnrichment ? null : previous?.SourceAtUtc;
        var lastContacted = decision.InvalidatesEnrichment ? null : previous?.LastContactedAtUtc;
        var enrichment = decision.InvalidatesEnrichment ? ImmutableArray<EnrichmentObservation>.Empty : previous?.Enrichment ?? [];
        if (decision.AcceptedObservation is { } observation)
        {
            bool Accept(string name)
            {
                if (!observation.Fields.TryGetValue(name, out var evidence) ||
                    evidence.Availability != FieldAvailability.Provided || evidence.Validity != FieldValidity.Valid)
                {
                    if (evidence is not null && !fields.ContainsKey(name))
                    {
                        fields[name] = evidence;
                    }
                    return false;
                }
                if (fields.TryGetValue(name, out var older) && evidence.SourceAtUtc is { } source &&
                    older.SourceAtUtc is { } oldSource && source < oldSource)
                {
                    return false;
                }
                fields[name] = evidence;
                return true;
            }
            string? Text(string name, string? value, string? old)
                => !string.IsNullOrWhiteSpace(value) && Accept(name) ? value : old;
            var device = metadata.Device;
            device = device with
            {
                Name = Text("name", observation.Device.Name, device.Name),
                IpAddress = Text("lan_ip", observation.Device.IpAddress, device.IpAddress),
                Model = Text("model", observation.Device.Model, device.Model),
                Firmware = Text("firmware", observation.Device.Firmware, device.Firmware),
                Uptime = observation.Device.Uptime is not null && Accept("snmp_uptime") ? observation.Device.Uptime : device.Uptime,
                RebootedAtUtc = observation.Device.RebootedAtUtc is not null && Accept("last_reboot") ? observation.Device.RebootedAtUtc : device.RebootedAtUtc
            };
            var location = metadata.Location;
            location = location with
            {
                Site = Text("site", observation.Location.Site, location.Site),
                Building = Text("building", observation.Location.Building, location.Building),
                Floor = Text("floor", observation.Location.Floor, location.Floor),
                Zone = Text("zone", observation.Location.Zone, location.Zone),
                Detail = Text("detail", observation.Location.Detail, location.Detail),
                InstallationPointId = observation.Location.InstallationPointId is not null && Accept("installation_point_id")
                    ? observation.Location.InstallationPointId : location.InstallationPointId,
                AirWaveRawLocation = Text("syslocation", observation.Location.AirWaveRawLocation, location.AirWaveRawLocation),
                Folder = Text("folder", observation.Location.Folder, location.Folder),
                Group = Text("group", observation.Location.Group, location.Group)
            };
            var controller = metadata.Controller;
            controller = controller with
            {
                SourceId = Text("controller_id", observation.Controller.SourceId, controller.SourceId),
                Name = Text("controller_name", observation.Controller.Name, controller.Name),
                IpAddress = Text("controller_ip", observation.Controller.IpAddress, controller.IpAddress)
            };
            var management = metadata.SourceManagement ?? new();
            management = management with
            {
                MaintenanceMode = observation.SourceManagement?.MaintenanceMode is not null && Accept("planned_maintenance_mode")
                    ? observation.SourceManagement.MaintenanceMode : management.MaintenanceMode,
                ManagementStatus = Text("operating_mode", observation.SourceManagement?.ManagementStatus, management.ManagementStatus)
            };
            var upstream = metadata.Upstream ?? new(null, null, null, null);
            upstream = upstream with
            {
                SourceSwitchId = Text("upstream_device_id", observation.Upstream?.SourceSwitchId, upstream.SourceSwitchId),
                PortIndex = Text("upstream_port_index", observation.Upstream?.PortIndex, upstream.PortIndex),
                PortName = Text("upstream_port_name", observation.Upstream?.PortName, upstream.PortName),
                ApInterface = Text("ap_interface", observation.Upstream?.ApInterface, upstream.ApInterface)
            };
            foreach (var (name, evidence) in observation.Fields)
            {
                if (!fields.ContainsKey(name))
                {
                    fields[name] = evidence;
                }
            }
            if (observation.Fields.TryGetValue(MonitoringFieldNames.State, out var stateEvidence))
            {
                fields[MonitoringFieldNames.State] = stateEvidence;
            }
            if (observation.SourceAtUtc is not null)
            {
                sourceAt = observation.SourceAtUtc;
            }
            if (observation.LastContactedAtUtc is not null && Accept("last_contacted"))
            {
                lastContacted = observation.LastContactedAtUtc;
            }
            var raw = (metadata.RawSourceValues ?? ImmutableDictionary<string, string>.Empty).ToBuilder();
            if (observation.RawSourceValues is not null)
            {
                foreach (var (name, value) in observation.RawSourceValues)
                {
                    if (observation.Fields.TryGetValue(name, out var evidence) &&
                        evidence.Availability != FieldAvailability.NotProvided && evidence.Validity != FieldValidity.Invalid)
                    {
                        raw[name] = value;
                        fields[name] = evidence;
                    }
                }
            }
            metadata = new(decision.Next.Identity.Identity, device, location, controller, management,
                metadata.ManualManagement, upstream, raw.Count == 0 ? null : raw.ToImmutable());
        }
        metadata = metadata with { Identity = decision.Next.Identity.Identity };
        foreach (var (name, evidence) in decision.Next.Identity.Fields)
        {
            fields[name] = evidence;
        }
        return new(apId, decision.SourceApId, decision.Next.Identity.DeviceGenerationId!.Value,
            decision.Next.MonitoringIntervalId!.Value, metadata, fields.ToImmutable(), sourceAt, lastContacted,
            enrichment.IsDefault ? [] : enrichment);
    }
}
