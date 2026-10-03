using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PolterSense.Models;

namespace PolterSense.Espectre;

/// <summary>Bounded Direct HTTP adapter. Fixed paths, no redirects/proxies, no credentials or raw response logging.</summary>
public sealed class EspectreClient(HttpClient http, TimeProvider time)
{
    private const string BasePath = "/espectre/v1";
    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(3), MaxConnectionsPerServer = 2
    }) { Timeout = TimeSpan.FromSeconds(7) };

    public async Task<JsonElement> ReadResourceAsync(WispConnection endpoint, string resource, CancellationToken ct)
    {
        if (resource is not ("capabilities" or "device" or "sensing" or "devices")) throw new EspectreException("unsupported");
        return await SendAsync(endpoint, HttpMethod.Get, resource, null, ct);
    }

    private async Task<JsonElement> SendAsync(WispConnection endpoint, HttpMethod method, string resource, object? body, CancellationToken ct)
    {
        endpoint.Validate();
        using var request = new HttpRequestMessage(method, new UriBuilder("http", endpoint.Address, endpoint.Port, $"{BasePath}/{resource}").Uri);
        // Required by published firmware for all clients, including non-browser SDK clients.
        request.Headers.Add("Origin", "https://espectre.dev");
        // Firmware request framing uses a bounded Content-Length, not chunked transfer.
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(7));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) throw new EspectreException(response.StatusCode == System.Net.HttpStatusCode.Conflict ? "device_busy" : "device_rejected");
            if (response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentLength > 16384)
                throw new EspectreException("invalid_device_response");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var memory = new MemoryStream();
            var buffer = new byte[2048];
            int count;
            while ((count = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (memory.Length + count > 16384) throw new EspectreException("invalid_device_response");
                memory.Write(buffer, 0, count);
            }
            using var document = JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new EspectreException("invalid_device_response");
            RejectDuplicateKeys(document.RootElement);
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new EspectreException("device_unreachable"); }
        catch (HttpRequestException) { throw new EspectreException("device_unreachable"); }
        catch (JsonException) { throw new EspectreException("invalid_device_response"); }
        catch (IOException) { throw new EspectreException("device_unreachable"); }
    }

    public async Task<EspectreSnapshot> ReadAsync(WispConnection endpoint, string? expectedId, CancellationToken ct)
    {
        var capabilities = await ReadResourceAsync(endpoint, "capabilities", ct);
        if (String(capabilities, "protocol_version", 16) != "1.0") throw new EspectreException("unsupported_protocol");
        var resourceValues = Array(capabilities, "resources", 32);
        if (resourceValues.Any(x => x.ValueKind != JsonValueKind.String)) throw new EspectreException("invalid_device_response");
        var resources = resourceValues.Select(x => x.GetString()).ToArray();
        if (!resources.Contains("device")) throw new EspectreException("unsupported_device");
        var raw = await ReadResourceAsync(endpoint, "device", ct);
        var id = String(raw, "device_id", 16);
        if (!Regex.IsMatch(id, "^[a-f0-9]{16}$")) throw new EspectreException("invalid_device_response");
        if (expectedId != null && expectedId != id) throw new EspectreException("identity_mismatch");
        var device = new EspectreDevice(id, String(raw, "name", 128), String(raw, "label", 128),
            String(raw, "frontend", 16), String(raw, "firmware", 128), String(raw, "chip", 32));
        if (device.Frontend is not ("native" or "esphome" or "matter" or "micro")) throw new EspectreException("unsupported_device");
        var operations = Array(capabilities, "operations", 32);
        bool Supports(string name, string method, string path) => operations.Any(op =>
            String(op, "name", 64) == name && String(op, "method", 8) == method && String(op, "path", 128) == BasePath + path);
        var supported = new EspectreCapabilities("1.0", Supports("update_device", "PATCH", "/device"),
            resources.Contains("sensing") && Supports("update_sensing", "PATCH", "/sensing"));
        EspectreSensing? sensing = null;
        if (resources.Contains("sensing"))
        {
            var s = await ReadResourceAsync(endpoint, "sensing", ct);
            sensing = new EspectreSensing(Bool(s, "enabled"), Bool(s, "ready"), Bool(s, "calibrating"), String(s, "mode", 32),
                Bool(s, "derived_events_paused"), String(s, "detector", 32), Number(s, "threshold"),
                Integer(s, "motion_on_hits"), Integer(s, "motion_off_hits"), String(s, "traffic_generator_mode", 32));
            if (sensing.Threshold is < 0 or > 1 || sensing.MotionOnHits is < 1 or > 20 || sensing.MotionOffHits is < 1 or > 20)
                throw new EspectreException("invalid_device_response");
        }
        return new EspectreSnapshot(endpoint.Address, endpoint.Port, device, supported, sensing, time.GetUtcNow());
    }

    public static void ValidateEdit(DeviceEdit edit)
    {
        if ((edit.Label == null) == (edit.Sensing == null)) throw new EspectreException("invalid_edit");
        if (edit.Label != null && (Encoding.UTF8.GetByteCount(edit.Label) > 32 || edit.Label.Any(char.IsControl))) throw new EspectreException("invalid_edit");
        if (edit.Sensing is not { } s) return;
        if (s.Enabled == null && s.Detector == null && s.Threshold == null && s.MotionOnHits == null && s.MotionOffHits == null && s.TrafficGeneratorMode == null)
            throw new EspectreException("invalid_edit");
        if ((s.Detector != null && s.Detector is not ("lightweight" or "high_accuracy"))
            || (s.Threshold != null && (!double.IsFinite(s.Threshold.Value) || s.Threshold is < 0 or > 1))
            || (s.MotionOnHits == null) != (s.MotionOffHits == null)
            || s.MotionOnHits is < 1 or > 20 || s.MotionOffHits is < 1 or > 20
            || (s.TrafficGeneratorMode != null && s.TrafficGeneratorMode is not ("ping" or "dns" or "dns_tcp" or "wifi_raw" or "external")))
            throw new EspectreException("invalid_edit");
    }

    public async Task<DeviceEditResult> ApplyAsync(WispConnection endpoint, EspectreSnapshot before, DeviceEdit edit, CancellationToken ct)
    {
        ValidateEdit(edit);
        if (before.Device.DeviceId != edit.ExpectedDeviceId) throw new EspectreException("identity_mismatch");
        if (edit.Label != null ? !before.Capabilities.CanUpdateDevice : !before.Capabilities.CanUpdateSensing)
            throw new EspectreException("unsupported_operation");
        var payload = new Dictionary<string, object>();
        if (edit.Label != null) payload["label"] = edit.Label;
        else
        {
            var s = edit.Sensing!;
            if (s.Enabled != null) payload["enabled"] = s.Enabled.Value;
            if (s.Detector != null) payload["detector"] = s.Detector;
            if (s.Threshold != null) payload["threshold"] = s.Threshold.Value;
            if (s.MotionOnHits != null) { payload["motion_on_hits"] = s.MotionOnHits.Value; payload["motion_off_hits"] = s.MotionOffHits!.Value; }
            if (s.TrafficGeneratorMode != null) payload["traffic_generator_mode"] = s.TrafficGeneratorMode;
        }
        try
        {
            var result = await SendAsync(endpoint, HttpMethod.Patch, edit.Label != null ? "device" : "sensing", payload, ct);
            if (!Bool(result, "accepted") || String(result, "code", 64) != "ok")
                return new(false, "device_rejected", "Device did not confirm the change. Refresh its settings before retrying.", null);
            var after = await ReadAsync(endpoint, edit.ExpectedDeviceId, ct);
            var confirmed = edit.Label != null ? after.Device.Label == edit.Label : Matches(edit.Sensing!, after.Sensing);
            return new(confirmed, confirmed ? "ok" : "readback_mismatch",
                confirmed ? "Device settings confirmed by readback." : "Readback differs from the requested settings. Review actual device state.", after);
        }
        catch (EspectreException)
        {
            // A timeout/error after dispatch cannot prove the firmware did not apply the operation.
            return new(false, "unconfirmed", "Change could not be confirmed. It may have applied; refresh the device before retrying.", null);
        }
    }

    private static bool Matches(SensingEdit edit, EspectreSensing? s) => s != null
        && (edit.Enabled == null || edit.Enabled == s.Enabled) && (edit.Detector == null || edit.Detector == s.Detector)
        && (edit.Threshold == null || Math.Abs(edit.Threshold.Value - s.Threshold) <= 0.000001)
        && (edit.MotionOnHits == null || edit.MotionOnHits == s.MotionOnHits) && (edit.MotionOffHits == null || edit.MotionOffHits == s.MotionOffHits)
        && (edit.TrafficGeneratorMode == null || edit.TrafficGeneratorMode == s.TrafficGeneratorMode);

    public static string String(JsonElement obj, string key, int limit)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) throw new EspectreException("invalid_device_response");
        var text = value.GetString()!;
        if (text.Length > limit || text.Any(char.IsControl)) throw new EspectreException("invalid_device_response");
        return text;
    }
    public static JsonElement[] Array(JsonElement obj, string key, int limit)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > limit) throw new EspectreException("invalid_device_response");
        return value.EnumerateArray().ToArray();
    }
    public static int Integer(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw new EspectreException("invalid_device_response");
        return number;
    }
    public static bool Bool(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new EspectreException("invalid_device_response");
        return value.GetBoolean();
    }
    private static double Number(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)) throw new EspectreException("invalid_device_response");
        return number;
    }
    private static void RejectDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>();
            foreach (var property in value.EnumerateObject())
            { if (!keys.Add(property.Name)) throw new EspectreException("invalid_device_response"); RejectDuplicateKeys(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateKeys(item);
    }
}
