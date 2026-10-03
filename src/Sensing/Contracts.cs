using System.Text.Json;
using System.Text.Json.Serialization;
using PolterSense.Models;

namespace PolterSense.Sensing;

// These contracts deliberately have no MQTT, HTTP or UI dependencies.
public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
}

public enum ClockBasis { Unknown, DeviceMonotonic, ReportedUtc }
public enum ObservationOrigin { Synthetic, Replay, Live }
// DeviceTimeMs is the device-reported event clock; it is not an acquisition timestamp.
public record ObservationTiming(DateTimeOffset? AcquiredAt, long? DeviceTimeMs, ClockBasis ClockBasis);
public record ReportedRadio(string? RadioId = null, string? Bssid = null, int? Channel = null);
public record FirmwareMetadata(string? Version = null, string? FeatureVersion = null, string? Detector = null);
public record MotionFeatures(bool? Motion, double? Score);
public record ObservationQuality(bool? Ready, double? Confidence = null, int? DroppedSamples = null);
public record ObservationProvenance(ObservationOrigin Origin, string Adapter, string ReferenceOrigin = "unknown", string RadioOrigin = "unknown");
public record NormalizedObservation(
    int SchemaVersion, string Kind, int KindVersion, string WispId,
    string? SourceId, string? LinkId, string ConfigRevision,
    string? SessionId, long? Sequence, ObservationTiming Timing,
    DateTimeOffset ReceivedAt, FirmwareMetadata? Firmware, ReportedRadio? ObservedRadio,
    MotionFeatures Motion, ObservationQuality Quality, ObservationProvenance Provenance);

// OUR opt-in test protocol, not ESPectre's MQTT wire format. No arbitrary extension bag.
public record TestObservation(
    int SchemaVersion, string Kind, int KindVersion, string WispId,
    string? SourceId, string? LinkId, string ConfigRevision,
    string? SessionId, long? Sequence, ObservationTiming Timing,
    FirmwareMetadata? Firmware, ReportedRadio? ObservedRadio,
    MotionFeatures Motion, ObservationQuality Quality);
public record AdapterCapabilities(string Id, bool Motion, bool RawCsi, bool FirmwareControl);
public record TransportContext(DateTimeOffset ReceivedAt, bool Retained, bool Replay = false);
public interface IObservationAdapter
{
    AdapterCapabilities Capabilities { get; }
    NormalizedObservation Decode(ReadOnlyMemory<byte> payload, TransportContext context);
}
public sealed class ObservationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
public sealed class NormalizedTestAdapter : IObservationAdapter
{
    public const int MaxPayloadBytes = 16 * 1024;
    public AdapterCapabilities Capabilities { get; } = new("poltersense-normalized-test-v1", true, false, false);
    public NormalizedObservation Decode(ReadOnlyMemory<byte> payload, TransportContext context)
    {
        if (context.Retained) throw new ObservationException("retained");
        if (payload.Length > MaxPayloadBytes) throw new ObservationException("oversized");
        TestObservation input;
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            CheckDuplicateProperties(document.RootElement);
            input = JsonSerializer.Deserialize<TestObservation>(payload.Span, ContractJson.Options)
                ?? throw new ObservationException("malformed");
        }
        catch (JsonException) { throw new ObservationException("malformed"); }
        if (input.SchemaVersion != 1 || input.KindVersion != 1 || input.Kind != "motion")
            throw new ObservationException("unsupported");
        if (input.Timing is null || input.Motion is null || input.Quality is null)
            throw new ObservationException("malformed");
        return new(1, "motion", 1, input.WispId, input.SourceId, input.LinkId, input.ConfigRevision,
            input.SessionId, input.Sequence, input.Timing, context.ReceivedAt,
            input.Firmware, input.ObservedRadio, input.Motion, input.Quality,
            new(context.Replay ? ObservationOrigin.Replay : ObservationOrigin.Synthetic, Capabilities.Id,
                input.SourceId is not null || input.LinkId is not null ? "configured" : "unknown",
                input.ObservedRadio is { } radio && (radio.RadioId is not null || radio.Bssid is not null || radio.Channel is not null) ? "observed" : "unknown"));
    }
    private static void CheckDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ObservationException("malformed");
                CheckDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicateProperties(item);
    }
}

public record RoomEstimate(string RoomId, string State, DateTimeOffset? EstimatedAt = null, double? Confidence = null);
public record EntityTrack(string TrackId, string EntityClass, DateTimeOffset EstimatedAt, double[]? Position, double? UncertaintyMeters);
public record MediumResult(int SchemaVersion, string Availability, string InputHealth, string ConfigRevision,
    RoomEstimate[] Rooms, EntityTrack[]? Tracks);
public interface IMedium
{
    ValueTask<MediumResult> EvaluateAsync(NormalizedObservation? observation, ConfigSnapshot scene, DateTimeOffset now, CancellationToken cancellationToken);
}
public sealed class NoOpMedium : IMedium
{
    public ValueTask<MediumResult> EvaluateAsync(NormalizedObservation? observation, ConfigSnapshot scene,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Unknown(scene, observation is null ? "noInput" : observation.Quality.Ready == true ? "receiving" : "notReady"));
    }
    public static MediumResult Unknown(ConfigSnapshot scene, string health) => new(1, "notImplemented", health, scene.Revision,
        scene.Config.Floors.SelectMany(f => f.Rooms).Select(r => new RoomEstimate(r.Id, "unknown")).ToArray(), null);
}
