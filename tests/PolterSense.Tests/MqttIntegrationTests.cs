using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Formatter;
using NUnit.Framework;
using PolterSense.Models;
using PolterSense.Sensing;
using PolterSense.Services;

namespace PolterSense.Tests;

[TestFixture, Category("MqttIntegration")]
public class MqttIntegrationTests
{
    [Test]
    public async Task DisposableBrokerExercisesRetainedIsolationLastWillAndReconnect()
    {
        var portText = Environment.GetEnvironmentVariable("POLTERSENSE_TEST_BROKER_PORT");
        if (portText is null) { Assert.Ignore("Run scripts/test-mqtt.sh for an isolated disposable broker."); return; }
        int port = int.Parse(portText);
        var dir = Path.Combine(Path.GetTempPath(), "poltersense-mqtt-" + Guid.NewGuid());
        using var config = new ConfigLoader(dir, NullLogger<ConfigLoader>.Instance);
        var scene = new Config { Wisps = [new Wisp { Id = "w1", Name = "Test Wisp", Point = [0, 0, 1] }] };
        await config.UpdateAsync(scene, config.Current.Revision, CancellationToken.None);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var pipeline = new ObservationPipeline(TimeProvider.System, new NoOpMedium());
        var processing = pipeline.RunAsync(lifetime.Token);
        await using var proxy = new CuttingProxy(port);
        var settings = new MqttSettings("127.0.0.1", proxy.Port, false, null, null, true, "integration-test");
        using var gateway = new MqttCoordinator(settings, config, pipeline, new NormalizedTestAdapter(), TimeProvider.System, NullLogger<MqttCoordinator>.Instance);
        using var monitor = new MqttClientFactory().CreateMqttClient();
        var messages = new ConcurrentQueue<(string Topic, string Payload)>();
        monitor.ApplicationMessageReceivedAsync += args =>
        {
            messages.Enqueue((args.ApplicationMessage.Topic, args.ApplicationMessage.ConvertPayloadToString()));
            return Task.CompletedTask;
        };
        await Connect(monitor, port, "poltersense-test-monitor", lifetime.Token);
        await monitor.SubscribeAsync("#", cancellationToken: lifetime.Token);
        TestObservation Input(long sequence) => new(1, "motion", 1, "w1", null, null, config.Current.Revision,
            "broker-test", sequence, new(null, null, ClockBasis.Unknown), null, null, new(true, 0.8), new(true));
        async Task Publish(string topic, string payload, bool retained = false) =>
            await monitor.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithRetainFlag(retained).Build(), lifetime.Token);
        try
        {
            // Sentinel represents pre-existing foreign state; gateway must never subscribe, publish or clear it.
            await Publish("espresense/companion/sentinel", "untouched", true);
            await Publish(gateway.TestTopic, JsonSerializer.Serialize(Input(0), ContractJson.Options), true);
            await Until(() => messages.Count >= 2);
            messages.Clear();
            await gateway.StartAsync(lifetime.Token);
            await Until(() => gateway.Snapshot().Connections >= 1);
            await Until(() => pipeline.Snapshot(config.Current).Diagnostics.Retained == 1);
            Assert.That(pipeline.Snapshot(config.Current).Diagnostics.Accepted, Is.Zero);
            Assert.That(gateway.ClientId, Does.StartWith("poltersense-"));
            await Publish(gateway.TestTopic, JsonSerializer.Serialize(Input(1), ContractJson.Options));
            await Until(() => pipeline.Snapshot(config.Current).Diagnostics.Accepted == 1);
            await Publish(gateway.TestTopic, JsonSerializer.Serialize(Input(1), ContractJson.Options));
            await Publish(gateway.TestTopic, "{malformed");
            await Until(() => pipeline.Snapshot(config.Current).Diagnostics.Duplicate == 1);
            await Until(() => pipeline.Snapshot(config.Current).Diagnostics.Rejected >= 3);
            // MQTT5 RetainAsPublished also rejects retained input while already subscribed.
            await Publish(gateway.TestTopic, JsonSerializer.Serialize(Input(2), ContractJson.Options), true);
            await Until(() => pipeline.Snapshot(config.Current).Diagnostics.Retained == 2);
            proxy.Cut();
            await Until(() => messages.Any(m => m.Topic == gateway.StatusTopic && m.Payload == "offline"));
            await Until(() => gateway.Snapshot().Connections >= 2);
            await Until(() => pipeline.Snapshot(config.Current).Diagnostics.Retained >= 3);
            await Publish(gateway.TestTopic, JsonSerializer.Serialize(Input(3), ContractJson.Options));
            await Until(() => pipeline.Snapshot(config.Current).Diagnostics.Accepted == 2);
            var state = pipeline.Snapshot(config.Current);
            Assert.That(state.Medium.Availability, Is.EqualTo("notImplemented"));
            Assert.That(state.Medium.Tracks, Is.Null);
            Assert.That(state.Diagnostics.Latest.Single().Freshness, Is.EqualTo("arrivalOnly"));
            Assert.That(messages.All(m => m.Topic == gateway.StatusTopic || m.Topic == gateway.TestTopic), Is.True,
                "Gateway must not produce legacy/HA/command topics");
            using var sentinel = new MqttClientFactory().CreateMqttClient();
            var readback = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            sentinel.ApplicationMessageReceivedAsync += args => { readback.TrySetResult(args.ApplicationMessage.ConvertPayloadToString()); return Task.CompletedTask; };
            await Connect(sentinel, port, "poltersense-test-sentinel", lifetime.Token);
            await sentinel.SubscribeAsync("espresense/companion/sentinel", cancellationToken: lifetime.Token);
            Assert.That(await readback.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo("untouched"));
        }
        finally
        {
            await gateway.StopAsync(CancellationToken.None);
            lifetime.Cancel();
            await processing;
            Directory.Delete(dir, true);
        }
    }
    private static async Task Connect(IMqttClient client, int port, string id, CancellationToken ct)
    {
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).WithClientId(id)
            .WithProtocolVersion(MqttProtocolVersion.V500).Build(), ct);
    }
    private static async Task Until(Func<bool> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(12);
        while (!predicate() && DateTime.UtcNow < until) await Task.Delay(50);
        Assert.That(predicate(), Is.True, "Expected broker/pipeline state before timeout");
    }
    // A single-client TCP relay permits a real network outage without commanding the broker.
    private sealed class CuttingProxy : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        private TcpClient? downstream;
        private TcpClient? upstream;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public CuttingProxy(int brokerPort)
        {
            listener.Start(1);
            loop = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        using var down = await listener.AcceptTcpClientAsync(stop.Token);
                        using var up = new TcpClient();
                        downstream = down; upstream = up;
                        await up.ConnectAsync(IPAddress.Loopback, brokerPort, stop.Token);
                        using var relay = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        var forward = down.GetStream().CopyToAsync(up.GetStream(), relay.Token);
                        var reverse = up.GetStream().CopyToAsync(down.GetStream(), relay.Token);
                        await Task.WhenAny(forward, reverse);
                        relay.Cancel();
                        try { await Task.WhenAll(forward, reverse); } catch (Exception) { }
                    }
                    catch (Exception) { if (stop.IsCancellationRequested) break; }
                }
            });
        }
        public void Cut() { downstream?.Dispose(); upstream?.Dispose(); }
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); Cut(); await loop; stop.Dispose(); }
    }
}
