using System.Collections.Immutable;
using System.Xml;
using System.Xml.Linq;
using AirWaveApMonitor.Core.Contracts;
namespace AirWaveApMonitor.Infrastructure.AirWave;

public sealed record DocumentedInterfaceObservation(PortStateObservation State, string? PortIndex,
    string? Name, string? Description, SourceTiming Timing);
public sealed record DocumentedInterfaceResult(ImmutableArray<DocumentedInterfaceObservation> Interfaces,
    ObservationCompleteness Completeness, ImmutableArray<CollectionError> Errors);

/// <summary>Offline parser of the documented AP Detail interface shape. It does not confirm Live capability,
/// interface-to-port mapping, source freshness, wired neighbors, power units, or complete scope coverage.</summary>
public sealed class DocumentedInterfaceParser
{
    public DocumentedInterfaceResult Parse(Stream xml, CollectionContext context, DateTimeOffset readAt, CancellationToken ct = default)
    {
        var rows = ImmutableArray.CreateBuilder<DocumentedInterfaceObservation>();
        var errors = ImmutableArray.CreateBuilder<CollectionError>();
        try
        {
            ct.ThrowIfCancellationRequested();
            using var bounded = new BoundedReadStream(xml, context.Profile.Settings.MaximumDecompressedResponseBytes, ct);
            using var reader = XmlReader.Create(bounded, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = context.Profile.Settings.MaximumDecompressedResponseBytes, CloseInput = false
            });
            var root = XDocument.Load(reader).Root;
            if (root?.Name != XName.Get("amp_ap_detail", "http://www.airwave.com") || root.Elements().Any(e => e.Name != "ap"))
                return Failure(CollectionErrorCategory.InvalidResponse);
            var aps = root.Elements("ap").ToArray();
            var duplicateAps = aps.GroupBy(e => (string?)e.Attribute("id")).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            foreach (var ap in aps)
            {
                ct.ThrowIfCancellationRequested();
                var apId = (string?)ap.Attribute("id");
                if (!ValidId(apId) || duplicateAps.Contains(apId)) { Error(CollectionErrorCategory.DuplicateIdentity); continue; }
                var interfaces = ap.Elements("interface").ToArray();
                var duplicates = interfaces.GroupBy(e => (string?)e.Attribute("id")).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
                foreach (var item in interfaces)
                {
                    ct.ThrowIfCancellationRequested();
                    var id = (string?)item.Attribute("id");
                    if (!ValidId(id) || duplicates.Contains(id)) { Error(CollectionErrorCategory.DuplicateIdentity); continue; }
                    var admin = ReadState<PortAdministrativeState>(item, "admin_status");
                    var oper = ReadState<PortOperationalState>(item, "oper_status");
                    var binding = EnrichmentProfileBinding.From(context.Profile, EvidenceSource.AirWave);
                    var state = new PortStateObservation(Guid.NewGuid(), context.RunId, binding,
                        new(apId!, id, EndpointRole.Other), admin.Value, oper.Value, admin.Evidence, oper.Evidence, admin.Raw, oper.Raw);
                    rows.Add(new(state, Text(item, "port_index"), Text(item, "name"), Text(item, "description"),
                        new(readAt, null, SourceTimeMeaning.Unknown, false)));
                }
            }
            if (rows.Count == 0 && errors.Count == 0) Error(CollectionErrorCategory.MissingRecord);
            return new(rows.ToImmutable(), ObservationCompleteness.Unknown, errors.ToImmutable());
        }
        catch (ResponseLimitException) { return Failure(CollectionErrorCategory.ResponseTooLarge); }
        catch (XmlException) { return Failure(CollectionErrorCategory.InvalidResponse); }

        (T Value, string? Raw, FieldEvidence Evidence) ReadState<T>(XElement item, string field) where T : struct, Enum
        {
            var elements = item.Elements(field).ToArray();
            var raw = elements.Length == 1 ? elements[0].Value.Trim() : null;
            var supplied = !string.IsNullOrEmpty(raw);
            // Numeric enum tokens are not documented state names.
            var valid = elements.Length <= 1 && (!supplied || raw!.Length <= 64 &&
                Enum.GetNames<T>().Contains(raw, StringComparer.OrdinalIgnoreCase));
            var value = supplied && valid ? Enum.Parse<T>(raw!, true) : default;
            if (!valid) Error(CollectionErrorCategory.InvalidField);
            return (value, raw?.Length <= 64 ? raw : null, new(supplied ? FieldAvailability.Provided : FieldAvailability.NotProvided,
                valid ? supplied ? FieldValidity.Valid : FieldValidity.Unknown : FieldValidity.Invalid, EvidenceSource.AirWave, readAt));
        }
        void Error(CollectionErrorCategory category) => errors.Add(new(category, 1, SafeMessages.For(category)));
        DocumentedInterfaceResult Failure(CollectionErrorCategory category) => new([], ObservationCompleteness.Unknown, [new(category, 1, SafeMessages.For(category))]);
    }
    private static bool ValidId(string? id) => id is { Length: > 0 and <= 128 } && id.All(char.IsAsciiDigit);
    private static string? Text(XElement parent, string name)
    {
        var fields = parent.Elements(name).ToArray();
        return fields.Length == 1 && fields[0].Value.Trim() is { Length: > 0 and <= 4096 } value ? value : null;
    }
}
