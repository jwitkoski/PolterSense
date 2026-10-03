using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using PolterSense.Services;
using PolterSense.Sensing;

namespace PolterSense.Tests;

public class MqttFailureTests
{
    [Test]
    public async Task RejectedSubscriptionDoesNotReportConnectedAndRetriesSetup()
    {
        // Minimal MQTT5 protocol peer: accept CONNECT, explicitly reject SUBSCRIBE via SUBACK.
        // Real successful broker transport is separately covered by scripts/test-mqtt.sh.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var retries = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int connections = 0;
        var peer = Task.Run(async () =>
        {
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                    var stream = client.GetStream();
                    var connect = await ReadPacket(stream, timeout.Token);
                    Assert.That(connect.Type, Is.EqualTo(0x10));
                    await stream.WriteAsync(new byte[] { 0x20, 0x03, 0x00, 0x00, 0x00 }, timeout.Token);
                    var subscribe = await ReadPacket(stream, timeout.Token);
                    Assert.That(subscribe.Type, Is.EqualTo(0x82));
                    await stream.WriteAsync(new byte[] { 0x90, 0x04, subscribe.Body[0], subscribe.Body[1], 0x00, 0x87 }, timeout.Token);
                    if (++connections >= 2) retries.TrySetResult();
                    var disconnect = await ReadPacket(stream, timeout.Token);
                    Assert.That(disconnect.Type, Is.EqualTo(0xe0));
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            catch (Exception ex) { retries.TrySetException(ex); }
        });
        var dir = Path.Combine(Path.GetTempPath(), "poltersense-suback-" + Guid.NewGuid());
        using var config = new ConfigLoader(dir, NullLogger<ConfigLoader>.Instance);
        var pipeline = new ObservationPipeline(TimeProvider.System, new NoOpMedium());
        using var gateway = new MqttCoordinator(new("127.0.0.1", port, false, null, null, true, "rejection-test"),
            config, pipeline, new NormalizedTestAdapter(), TimeProvider.System, NullLogger<MqttCoordinator>.Instance);
        try
        {
            await gateway.StartAsync(timeout.Token);
            await retries.Task.WaitAsync(timeout.Token);
            Assert.That(gateway.Snapshot().Connections, Is.Zero);
            Assert.That(gateway.Snapshot().Status, Is.Not.EqualTo("connected"));
        }
        finally
        {
            await gateway.StopAsync(CancellationToken.None);
            timeout.Cancel(); listener.Stop(); await peer;
            Directory.Delete(dir, true);
        }
    }

    private static async Task<(byte Type, byte[] Body)> ReadPacket(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[1];
        await stream.ReadExactlyAsync(header, ct);
        int size = 0, multiplier = 1;
        var part = new byte[1];
        do
        {
            await stream.ReadExactlyAsync(part, ct);
            size += (part[0] & 127) * multiplier;
            multiplier *= 128;
            if (size > 2048 || multiplier > 2097152) throw new InvalidOperationException("Oversized test packet");
        } while ((part[0] & 128) != 0);
        var body = new byte[size];
        await stream.ReadExactlyAsync(body, ct);
        return (header[0], body);
    }
}
