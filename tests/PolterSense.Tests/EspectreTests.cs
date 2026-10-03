using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using PolterSense.Espectre;
using PolterSense.Models;
using PolterSense.Sensing;
using PolterSense.Services;

namespace PolterSense.Tests;

[TestFixture]
public sealed class EspectreTests
{
    private const string DeviceId = "0123456789abcdef";
    private const string OtherId = "fedcba9876543210";
    private static readonly WispConnection Endpoint = new() { Address = "10.0.0.20" };
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T00:00:00Z");
    private string directory = null!;
    [SetUp] public void SetUp() => directory = Path.Combine(Path.GetTempPath(), "poltersense-espectre-tests-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void TearDown() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private ConfigLoader Loader() => new(directory, NullLogger<ConfigLoader>.Instance);
    private static Config Scene() => new() { Wisps = [new() { Id = "test-wisp", Name = "Separate display name", ExternalId = DeviceId, DirectHttp = Endpoint, Point = [0, 0, 1] }] };
    private static string Capabilities(string path = "/espectre/v1/device", string method = "PATCH", string name = "update_device", bool discovery = false) => JsonSerializer.Serialize(new
    {
        protocol_version = "1.0", resources = discovery ? new[] { "device", "sensing", "devices" } : new[] { "device", "sensing" },
        operations = new[] { new { name, method, path }, new { name = "update_sensing", method = "PATCH", path = "/espectre/v1/sensing" } }
    });
    private static string Device(string id = DeviceId, string label = "Board") => JsonSerializer.Serialize(new { device_id = id, name = "espectre-test", label, frontend = "native", firmware = "test-firmware", chip = "ESP32" });
    private const string Sensing = """{"enabled":true,"ready":false,"calibrating":false,"mode":"normal","derived_events_paused":false,"detector":"lightweight","threshold":0.5,"motion_on_hits":2,"motion_off_hits":3,"traffic_generator_mode":"external"}""";
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Normal(HttpRequestMessage request) => Json(request.RequestUri!.AbsolutePath switch
    {
        "/espectre/v1/capabilities" => Capabilities(), "/espectre/v1/device" => Device(), "/espectre/v1/sensing" => Sensing,
        _ => throw new AssertionException("Unexpected resource requested")
    });
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public List<(string Method, string Path, string? Body)> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = respond ?? Normal;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (request.Method == HttpMethod.Patch)
            {
                Assert.That(request.Headers.TransferEncodingChunked, Is.Not.True, "Firmware requires a bounded Content-Length body");
                Assert.That(request.Content!.Headers.ContentLength, Is.GreaterThan(0));
            }
            Assert.That(request.RequestUri.Scheme, Is.EqualTo("http"));
            Assert.That(request.Headers.GetValues("Origin").Single(), Is.EqualTo("https://espectre.dev"));
            return Respond(request);
        }
    }
    private sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class FakeBootstrap(params WispConnection[] seeds) : IEspectreBootstrap
    {
        public int Calls { get; private set; }
        public Task<WispConnection[]> FindAsync(CancellationToken ct) { Calls++; ct.ThrowIfCancellationRequested(); return Task.FromResult(seeds); }
    }
    private static EspectreClient Client(FakeHandler handler) => new(new HttpClient(handler), new FixedTime());
    private static void Error(string code, AsyncTestDelegate action) => Assert.That(Assert.ThrowsAsync<EspectreException>(action)!.Code, Is.EqualTo(code));

    [Test] public async Task ReadPreservesFirmwareIdentityCapabilitiesReadinessAndInjectedTime()
    {
        using var handler = new FakeHandler();
        var result = await Client(handler).ReadAsync(Endpoint, DeviceId, default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Device.DeviceId, Is.EqualTo(DeviceId));
            Assert.That(result.Device.Name, Is.EqualTo("espectre-test"));
            Assert.That(result.Device.Label, Is.EqualTo("Board"));
            Assert.That(result.ReadAt, Is.EqualTo(Now));
            Assert.That(result.Sensing!.Ready, Is.False);
            Assert.That(result.Capabilities.CanUpdateDevice, Is.True);
            Assert.That(result.Capabilities.CanUpdateSensing, Is.True);
            Assert.That(handler.Requests.Select(r => r.Method), Is.All.EqualTo("GET"));
        });
    }

    [TestCase("{")]
    [TestCase("[]")]
    [TestCase("{\"protocol_version\":\"1.0\",\"protocol_version\":\"2.0\"}")]
    [TestCase("{\"protocol_version\":1}")]
    [TestCase("{\"protocol_version\":\"1.0\",\"resources\":[1],\"operations\":[]}")]
    public void MalformedResponseIsRejectedWithoutEchoingRawContent(string response)
    {
        using var handler = new FakeHandler(_ => Json(response));
        Error("invalid_device_response", async () => await Client(handler).ReadAsync(Endpoint, null, default));
    }

    [Test] public void OversizedOrNonJsonResponseIsRejected()
    {
        using var handler = new FakeHandler(_ => Json("{\"secret\":\"" + new string('a', 17000) + "\"}"));
        Error("invalid_device_response", async () => await Client(handler).ReadAsync(Endpoint, null, default));
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("sentinel-secret") };
        Error("invalid_device_response", async () => await Client(handler).ReadAsync(Endpoint, null, default));
    }

    [TestCase("\"ready\":false", "\"ready\":\"false\"")]
    [TestCase("\"threshold\":0.5", "\"threshold\":1.01")]
    [TestCase("\"motion_on_hits\":2", "\"motion_on_hits\":0")]
    [TestCase("\"motion_off_hits\":3", "\"motion_off_hits\":21")]
    public void MalformedOrOutOfBoundsSensingIsRejected(string original, string replacement)
    {
        using var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/sensing") ? Json(Sensing.Replace(original, replacement)) : Normal(r));
        Error("invalid_device_response", async () => await Client(handler).ReadAsync(Endpoint, null, default));
    }

    [Test] public void MalformedOperationObjectIsRejected()
    {
        using var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/capabilities")
            ? Json("""{"protocol_version":"1.0","resources":["device"],"operations":[3]}""") : Normal(r));
        Error("invalid_device_response", async () => await Client(handler).ReadAsync(Endpoint, null, default));
    }

    [Test] public void UnknownProtocolAndInvalidReportedIdentityAreRejected()
    {
        using var handler = new FakeHandler(_ => Json(Capabilities().Replace("1.0", "2.0")));
        Error("unsupported_protocol", async () => await Client(handler).ReadAsync(Endpoint, null, default));
        handler.Respond = r => r.RequestUri!.AbsolutePath.EndsWith("/device") ? Json(Device("Not-a-wire-ID")) : Normal(r);
        Error("invalid_device_response", async () => await Client(handler).ReadAsync(Endpoint, null, default));
    }

    [TestCase("/wrong/device", "PATCH", "update_device")]
    [TestCase("/espectre/v1/device", "POST", "update_device")]
    [TestCase("/espectre/v1/device", "PATCH", "update_something")]
    public async Task UnsupportedOperationRequiresExactCapabilityAndNeverSendsPatch(string path, string method, string name)
    {
        using var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/capabilities") ? Json(Capabilities(path, method, name)) : Normal(r));
        var client = Client(handler);
        var before = await client.ReadAsync(Endpoint, DeviceId, default);
        Error("unsupported_operation", async () => await client.ApplyAsync(Endpoint, before, new("revision", DeviceId, "New label"), default));
        Assert.That(handler.Requests.Any(r => r.Method == "PATCH"), Is.False);
    }

    [TestCase(false, "ok")]
    [TestCase(true, "busy")]
    public async Task HttpSuccessWithoutFirmwareAcceptanceIsNotSuccess(bool accepted, string code)
    {
        using var handler = new FakeHandler(); var client = Client(handler);
        var before = await client.ReadAsync(Endpoint, DeviceId, default);
        handler.Respond = r => r.Method == HttpMethod.Patch ? Json(JsonSerializer.Serialize(new { accepted, code })) : Normal(r);
        var result = await client.ApplyAsync(Endpoint, before, new("revision", DeviceId, "New label"), default);
        Assert.That(result.Confirmed, Is.False);
        Assert.That(result.Code, Is.EqualTo("device_rejected"));
        Assert.That(result.Snapshot, Is.Null);
    }

    [Test] public async Task LabelWriteRequiresActualReadbackAndUsesOnlyExternalLabelField()
    {
        using var handler = new FakeHandler(); var client = Client(handler);
        var before = await client.ReadAsync(Endpoint, DeviceId, default);
        handler.Respond = r => r.Method == HttpMethod.Patch ? Json("{\"accepted\":true,\"code\":\"ok\"}") : r.RequestUri!.AbsolutePath.EndsWith("/device") ? Json(Device(label: "New label")) : Normal(r);
        var result = await client.ApplyAsync(Endpoint, before, new("revision", DeviceId, "New label"), default);
        Assert.That(result.Confirmed, Is.True);
        Assert.That(result.Snapshot!.Device.Label, Is.EqualTo("New label"));
        Assert.That(handler.Requests.Single(r => r.Method == "PATCH"), Is.EqualTo(("PATCH", "/espectre/v1/device", "{\"label\":\"New label\"}")));
    }

    [TestCase("mismatch", "readback_mismatch")]
    [TestCase("timeout", "unconfirmed")]
    [TestCase("identity", "unconfirmed")]
    [TestCase("malformed", "unconfirmed")]
    public async Task FailedOrPartialReadbackNeverReportsSuccess(string failure, string expectedCode)
    {
        using var handler = new FakeHandler(); var client = Client(handler);
        var before = await client.ReadAsync(Endpoint, DeviceId, default);
        handler.Respond = r => r.Method == HttpMethod.Patch ? Json("{\"accepted\":true,\"code\":\"ok\"}") : failure switch
        {
            "timeout" => throw new HttpRequestException("sentinel-secret"),
            "malformed" => Json("{invalid"),
            "identity" when r.RequestUri!.AbsolutePath.EndsWith("/device") => Json(Device(OtherId)),
            _ => Normal(r)
        };
        var result = await client.ApplyAsync(Endpoint, before, new("revision", DeviceId, "New label"), default);
        Assert.That(result.Confirmed, Is.False);
        Assert.That(result.Code, Is.EqualTo(expectedCode));
        Assert.That(result.Message, Does.Not.Contain("sentinel-secret"));
    }

    private static IEnumerable<SensingEdit> InvalidSensing()
    {
        yield return new(); yield return new() { Threshold = -0.1 }; yield return new() { Threshold = 1.1 };
        yield return new() { Threshold = double.NaN }; yield return new() { Threshold = double.PositiveInfinity };
        yield return new() { Detector = "unknown" }; yield return new() { TrafficGeneratorMode = "unknown" };
        yield return new() { MotionOnHits = 1 }; yield return new() { MotionOffHits = 1 };
        yield return new() { MotionOnHits = 0, MotionOffHits = 1 }; yield return new() { MotionOnHits = 1, MotionOffHits = 21 };
    }
    [TestCaseSource(nameof(InvalidSensing))] public void InvalidSensingEditFailsBeforeDispatch(SensingEdit edit)
    {
        var ex = Assert.Throws<EspectreException>(() => EspectreClient.ValidateEdit(new("revision", DeviceId, Sensing: edit)));
        Assert.That(ex!.Code, Is.EqualTo("invalid_edit"));
    }
    [Test] public void LabelLimitsCountUtf8BytesAndDisallowControlCharacters()
    {
        Assert.DoesNotThrow(() => EspectreClient.ValidateEdit(new("revision", DeviceId, new string('é', 16))));
        Assert.Throws<EspectreException>(() => EspectreClient.ValidateEdit(new("revision", DeviceId, new string('é', 17))));
        Assert.Throws<EspectreException>(() => EspectreClient.ValidateEdit(new("revision", DeviceId, "line\nbreak")));
        Assert.Throws<EspectreException>(() => EspectreClient.ValidateEdit(new("revision", DeviceId)));
        Assert.Throws<EspectreException>(() => EspectreClient.ValidateEdit(new("revision", DeviceId, "Label", new() { Enabled = false })));
    }

    [Test] public async Task ManagementRejectsStaleRevisionAndChangedIdentityBeforeAnyPatch()
    {
        using var loader = Loader(); await loader.UpdateAsync(Scene(), loader.Current.Revision);
        using var handler = new FakeHandler(); var manager = new WispManagement(Client(handler), new FakeBootstrap(), loader);
        Assert.ThrowsAsync<ConfigConflictException>(async () => await manager.ApplyAsync("test-wisp", new("old-revision", DeviceId, "New label"), default));
        Assert.That(handler.Requests, Is.Empty);
        Error("identity_mismatch", async () => await manager.ApplyAsync("test-wisp", new(loader.Current.Revision, OtherId, "New label"), default));
        Assert.That(handler.Requests, Is.Empty);
        handler.Respond = r => r.RequestUri!.AbsolutePath.EndsWith("/device") ? Json(Device(OtherId)) : Normal(r);
        Error("identity_mismatch", async () => await manager.ApplyAsync("test-wisp", new(loader.Current.Revision, DeviceId, "New label"), default));
        Assert.That(handler.Requests.Any(r => r.Method == "PATCH"), Is.False);
        Assert.That(loader.Current.Config.Wisps[0].Name, Is.EqualTo("Separate display name"));
    }

    [Test] public async Task SceneChangedDuringPreflightPreventsPatch()
    {
        using var loader = Loader(); await loader.UpdateAsync(Scene(), loader.Current.Revision);
        var initial = loader.Current.Revision;
        using var handler = new FakeHandler(r =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith("/device"))
            {
                var changed = Scene(); changed.Name = "Changed during network request";
                loader.UpdateAsync(changed, initial).GetAwaiter().GetResult();
            }
            return Normal(r);
        });
        var manager = new WispManagement(Client(handler), new FakeBootstrap(), loader);
        Assert.ThrowsAsync<ConfigConflictException>(async () => await manager.ApplyAsync("test-wisp", new(initial, DeviceId, "New label"), default));
        Assert.That(handler.Requests.Any(r => r.Method == "PATCH"), Is.False);
    }

    [Test] public async Task SensingWriteSendsOnlyRequestedFieldsAndConfirmsAllByReadback()
    {
        using var handler = new FakeHandler(); var client = Client(handler);
        var before = await client.ReadAsync(Endpoint, DeviceId, default);
        handler.Respond = r => r.Method == HttpMethod.Patch ? Json("""{"accepted":true,"code":"ok"}""")
            : r.RequestUri!.AbsolutePath.EndsWith("/sensing") ? Json(Sensing.Replace("\"threshold\":0.5", "\"threshold\":0.75").Replace("\"motion_on_hits\":2", "\"motion_on_hits\":4")) : Normal(r);
        var result = await client.ApplyAsync(Endpoint, before, new("revision", DeviceId, Sensing: new() { Threshold = .75, MotionOnHits = 4, MotionOffHits = 3 }), default);
        Assert.That(result.Confirmed, Is.True);
        var patch = handler.Requests.Single(r => r.Method == "PATCH");
        Assert.That(patch.Path, Is.EqualTo("/espectre/v1/sensing"));
        using var payload = JsonDocument.Parse(patch.Body!);
        Assert.That(payload.RootElement.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "threshold", "motion_on_hits", "motion_off_hits" }));
        Assert.That(payload.RootElement.GetProperty("threshold").GetDouble(), Is.EqualTo(.75));
        Assert.That(payload.RootElement.GetProperty("motion_on_hits").GetInt32(), Is.EqualTo(4));
    }

    [Test] public async Task UnconfiguredDiscoveryUsesBootstrapAndDoesNotChangeSceneOrSendCommands()
    {
        using var loader = Loader(); var revision = loader.Current.Revision;
        using var handler = new FakeHandler(); var bootstrap = new FakeBootstrap(Endpoint);
        var result = await new WispManagement(Client(handler), bootstrap, loader).DiscoverAsync(new(), default);
        Assert.That(result.Status, Is.EqualTo("complete")); Assert.That(result.Candidates.Single().Device.DeviceId, Is.EqualTo(DeviceId));
        Assert.That(bootstrap.Calls, Is.EqualTo(1)); Assert.That(loader.Current.Revision, Is.EqualTo(revision));
        Assert.That(handler.Requests.Select(r => r.Method), Is.All.EqualTo("GET"));
    }

    [Test] public async Task AdvertisedPublicAddressIsNeverContactedAndResultIsPartial()
    {
        using var loader = Loader();
        using var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath switch
        {
            "/espectre/v1/capabilities" => Json(Capabilities(discovery: true)),
            "/espectre/v1/devices" => Json("""{"schema_version":2,"status":"complete","truncated":false,"rejected_results":0,"elapsed_ms":10,"devices":[{"dns_sd_schema_version":1,"protocol_version":"1.0","transport":"http","path":"/espectre/v1","device_id":"fedcba9876543210","addresses":["8.8.8.8"],"port":62587,"frontend":"native"}]}"""),
            _ => Normal(r)
        });
        var result = await new WispManagement(Client(handler), new FakeBootstrap(), loader).DiscoverAsync(new(Endpoint.Address), default);
        Assert.That(result.Status, Is.EqualTo("partial")); Assert.That(result.Candidates, Has.Length.EqualTo(1));
        Assert.That(handler.Requests.Count(r => r.Path.EndsWith("/device")), Is.EqualTo(1));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ConflictingDiscoveryIdentityIsRemovedInsteadOfSelectingArbitraryReceiver(bool differentPort)
    {
        using var loader = Loader();
        var second = new WispConnection { Address = "10.0.0.21", Port = differentPort ? 62588 : 62587 };
        using var handler = new FakeHandler(r => !differentPort && r.RequestUri!.Host == second.Address && r.RequestUri.AbsolutePath.EndsWith("/device")
            ? Json(Device().Replace("native", "esphome")) : Normal(r));
        var result = await new WispManagement(Client(handler), new FakeBootstrap(Endpoint, second), loader).DiscoverAsync(new(), default);
        Assert.That(result.Status, Is.EqualTo("partial"));
        Assert.That(result.Candidates, Is.Empty);
    }

    [Test] public async Task SameReceiverWithMultipleAddressesIsOneDiscoveryCandidate()
    {
        using var loader = Loader(); using var handler = new FakeHandler();
        var second = new WispConnection { Address = "10.0.0.21" };
        var result = await new WispManagement(Client(handler), new FakeBootstrap(Endpoint, second), loader).DiscoverAsync(new(), default);
        Assert.That(result.Status, Is.EqualTo("complete"));
        Assert.That(result.Candidates, Has.Length.EqualTo(1));
        Assert.That(result.Candidates[0].Device.DeviceId, Is.EqualTo(DeviceId));
    }

    [TestCase("127.0.0.1")]
    [TestCase("169.254.169.254")]
    [TestCase("8.8.8.8")]
    [TestCase("172.32.0.1")]
    [TestCase("10.1")]
    [TestCase("::ffff:10.0.0.1")]
    [TestCase("receiver.local")]
    public void EndpointRejectsNonCanonicalOrNonPrivateAddresses(string address) => Assert.Throws<ConfigValidationException>(() => new WispConnection { Address = address }.Validate());

    [Test] public void DuplicateManagedIdentityOrEndpointIsRejected()
    {
        var scene = Scene();
        scene.Wisps = [scene.Wisps[0], new() { Id = "second", Name = "Second", ExternalId = DeviceId, DirectHttp = new() { Address = "10.0.0.21" }, Point = [1, 1, 1] }];
        Assert.Throws<ConfigValidationException>(() => ConfigLoader.Snapshot(scene));
        scene.Wisps[1].ExternalId = OtherId; scene.Wisps[1].DirectHttp = Endpoint;
        Assert.Throws<ConfigValidationException>(() => ConfigLoader.Snapshot(scene));
        scene.Wisps[1].DirectHttp = new() { Address = "10.0.0.21" };
        Assert.DoesNotThrow(() => ConfigLoader.Snapshot(scene));
        scene.Wisps[1].ExternalId = "UpperCaseOrLegacy";
        Assert.Throws<ConfigValidationException>(() => ConfigLoader.Snapshot(scene));
    }

    [Test] public void NewOptionalConnectionDoesNotChangeRecordedFixtureRevision()
    {
        var fixture = JsonSerializer.Deserialize<EchoSession>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/synthetic-echo.json")), ContractJson.Options)!;
        Assert.That(ConfigLoader.Snapshot(fixture.Scene).Revision, Is.EqualTo(fixture.ConfigRevision));
    }

    [Test] public void MdnsParserAcceptsMatchingCompressedAnswerAndRejectsLoopsTruncationAndPublicAddress()
    {
        const string question = "espectre-devices-test.local"; const ushort id = 42;
        using var stream = new MemoryStream(); var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id); header[2] = 0x80; header[5] = 1; header[7] = 1; stream.Write(header);
        foreach (var label in question.Split('.')) { stream.WriteByte((byte)label.Length); stream.Write(Encoding.ASCII.GetBytes(label)); }
        stream.Write(new byte[] { 0, 0, 1, 0, 1 });
        var answerOffset = (int)stream.Length;
        stream.Write(new byte[] { 0xc0, 12, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 10, 0, 0, 20 });
        var packet = stream.ToArray();
        Assert.That(MdnsBootstrap.ParseAddresses(packet, question, id), Is.EqualTo(new[] { "10.0.0.20" }));
        Assert.That(MdnsBootstrap.ParseAddresses(packet, question, 43), Is.Empty);
        Assert.That(MdnsBootstrap.ParseAddresses(packet, "unrelated.local", id), Is.Empty);
        for (var length = 0; length < packet.Length; length++) Assert.That(MdnsBootstrap.ParseAddresses(packet[..length], question, id), Is.Empty, $"truncation {length}");
        packet[answerOffset + 1] = (byte)answerOffset;
        Assert.That(MdnsBootstrap.ParseAddresses(packet, question, id), Is.Empty, "self-referencing compression pointer");
        packet[answerOffset + 1] = 12; packet[^4] = 8;
        Assert.That(MdnsBootstrap.ParseAddresses(packet, question, id), Is.Empty, "nonprivate result");
    }
}
