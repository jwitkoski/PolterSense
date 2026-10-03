using PolterSense.Models;
using PolterSense.Services;
using static PolterSense.Espectre.EspectreClient;

namespace PolterSense.Espectre;

/// <summary>On-demand, bounded discovery and explicit capability/identity checked settings changes.</summary>
public sealed class WispManagement(EspectreClient client, IEspectreBootstrap bootstrap, ConfigLoader config)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<DiscoveryResult> DiscoverAsync(DiscoveryRequest request, CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) throw new EspectreException("busy");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            WispConnection[] seeds;
            if (!string.IsNullOrWhiteSpace(request.Address))
            {
                var seed = new WispConnection { Address = request.Address, Port = request.Port };
                seed.Validate(); seeds = [seed];
            }
            else
            {
                var saved = config.Current.Config.Wisps.Where(w => w.DirectHttp != null).Select(w => w.DirectHttp!).Take(4);
                var discovered = await bootstrap.FindAsync(timeout.Token);
                seeds = discovered.Concat(saved).DistinctBy(e => (e.Address, e.Port)).Take(8).ToArray();
            }
            var candidates = new Dictionary<string, EspectreSnapshot>();
            var conflicts = new HashSet<string>();
            var partial = false;
            foreach (var seed in seeds)
            {
                try
                {
                    var first = await client.ReadAsync(seed, null, timeout.Token);
                    Add(first);
                    var caps = await client.ReadResourceAsync(seed, "capabilities", timeout.Token);
                    if (!Array(caps, "resources", 32).Any(v => v.ValueKind == System.Text.Json.JsonValueKind.String && v.GetString() == "devices")) continue;
                    var found = await client.ReadResourceAsync(seed, "devices", timeout.Token);
                    if (Integer(found, "schema_version") != 2 || String(found, "status", 16) is not ("complete" or "timeout")) throw new EspectreException("unsupported_discovery");
                    if (Integer(found, "elapsed_ms") is < 0 or > 10000 || Integer(found, "rejected_results") < 0) throw new EspectreException("invalid_device_response");
                    partial |= Bool(found, "truncated") || String(found, "status", 16) == "timeout" || Integer(found, "rejected_results") > 0;
                    foreach (var item in Array(found, "devices", 8))
                    {
                        if (Integer(item, "dns_sd_schema_version") != 1 || String(item, "protocol_version", 16) != "1.0"
                            || String(item, "transport", 8) != "http" || String(item, "path", 64) != "/espectre/v1") { partial = true; continue; }
                        var expectedId = String(item, "device_id", 16);
                        if (!System.Text.RegularExpressions.Regex.IsMatch(expectedId, "^[a-f0-9]{16}$")) { partial = true; continue; }
                        var address = Array(item, "addresses", 2).FirstOrDefault();
                        if (address.ValueKind != System.Text.Json.JsonValueKind.String || !WispConnection.IsPrivateAddress(address.GetString())) { partial = true; continue; }
                        var endpoint = new WispConnection { Address = address.GetString()!, Port = Integer(item, "port") };
                        endpoint.Validate();
                        if (endpoint.Address == seed.Address && endpoint.Port == seed.Port && expectedId == first.Device.DeviceId) continue;
                        if (candidates.Count >= 8) { partial = true; break; }
                        // DNS advertisements are hints: verify identity/frontend against the actual endpoint.
                        var verified = await client.ReadAsync(endpoint, expectedId, timeout.Token);
                        if (verified.Device.Frontend != String(item, "frontend", 16)) { partial = true; continue; }
                        Add(verified);
                    }
                }
                catch (Exception ex) when (ex is EspectreException or ConfigValidationException) { partial = true; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { partial = true; break; }
            }
            return new(candidates.Values.OrderBy(v => v.Device.DeviceId).ToArray(), partial ? "partial" : candidates.Count > 0 ? "complete" : "empty",
                candidates.Count == 0 ? "No verified Wisps found. Enter a known receiver IP; multicast may not cross Docker or VLAN boundaries."
                : partial ? "Some results could not be verified. Listed Wisps were checked directly." : "Discovery complete. Assign a receiver to a Wisp to manage it.");

            void Add(EspectreSnapshot candidate)
            {
                if (conflicts.Contains(candidate.Device.DeviceId)) return;
                if (candidates.TryGetValue(candidate.Device.DeviceId, out var old) &&
                    (old.Device.Frontend != candidate.Device.Frontend || old.Port != candidate.Port))
                { candidates.Remove(candidate.Device.DeviceId); conflicts.Add(candidate.Device.DeviceId); partial = true; return; }
                if (candidates.Count < 8) candidates[candidate.Device.DeviceId] = candidate;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<EspectreSnapshot> ReadAsync(string id, CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) throw new EspectreException("busy");
        try
        {
            var wisp = Find(config.Current, id);
            return await client.ReadAsync(wisp.DirectHttp!, wisp.ExternalId, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<DeviceEditResult> ApplyAsync(string id, DeviceEdit edit, CancellationToken ct)
    {
        ValidateEdit(edit);
        if (!await gate.WaitAsync(0, ct)) throw new EspectreException("busy");
        try
        {
            var scene = config.Current;
            if (scene.Revision != edit.Revision) throw new ConfigConflictException();
            var wisp = Find(scene, id);
            if (wisp.ExternalId != edit.ExpectedDeviceId) throw new EspectreException("identity_mismatch");
            var before = await client.ReadAsync(wisp.DirectHttp!, wisp.ExternalId, ct);
            if (config.Current.Revision != edit.Revision) throw new ConfigConflictException();
            return await client.ApplyAsync(wisp.DirectHttp!, before, edit, ct);
        }
        finally { gate.Release(); }
    }
    private static Wisp Find(ConfigSnapshot scene, string id) => scene.Config.Wisps.FirstOrDefault(w => w.Id == id && w.DirectHttp != null)
        ?? throw new EspectreException("unconfigured_wisp");
}
