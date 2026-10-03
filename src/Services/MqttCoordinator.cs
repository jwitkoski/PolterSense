using System.Buffers;
using MQTTnet;
using MQTTnet.Formatter;
using PolterSense.Sensing;

namespace PolterSense.Services;

// Operational credentials deliberately do not belong to public scene/config snapshots.
public sealed record MqttSettings(string? Host, int Port, bool Tls, string? Username, string? Password, bool TestInputEnabled, string InstanceId)
{
    public static MqttSettings FromEnvironment(string storageDirectory)
    {
        Directory.CreateDirectory(storageDirectory);
        var identityPath = Path.Combine(storageDirectory, "instance-id");
        if (!File.Exists(identityPath))
        {
            try
            {
                using var identity = new FileStream(identityPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                identity.Write(System.Text.Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N")));
                identity.Flush(true);
            }
            catch (IOException) when (File.Exists(identityPath)) { }
        }
        var instance = File.ReadAllText(identityPath).Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(instance, "^[a-z0-9-]{1,48}$"))
            throw new InvalidOperationException("Invalid PolterSense instance identity file");
        var port = Environment.GetEnvironmentVariable("POLTERSENSE_MQTT_PORT");
        return new(Environment.GetEnvironmentVariable("POLTERSENSE_MQTT_HOST"),
            int.TryParse(port, out var parsed) && parsed is > 0 and <= 65535 ? parsed : 1883,
            Environment.GetEnvironmentVariable("POLTERSENSE_MQTT_TLS") == "true",
            Environment.GetEnvironmentVariable("POLTERSENSE_MQTT_USERNAME"),
            Environment.GetEnvironmentVariable("POLTERSENSE_MQTT_PASSWORD"),
            Environment.GetEnvironmentVariable("POLTERSENSE_TEST_INPUT") == "true", instance);
    }
}
public record MqttStatus(string Status, bool TestInputEnabled, string ClientId, string StatusTopic, string TestTopic, long Connections);
public sealed class MqttCoordinator(MqttSettings settings, ConfigLoader config, ObservationPipeline pipeline,
    IObservationAdapter adapter, TimeProvider time, ILogger<MqttCoordinator> logger) : BackgroundService
{
    public string ClientId => $"poltersense-{settings.InstanceId}";
    public string StatusTopic => $"poltersense/{settings.InstanceId}/status";
    public string TestTopic => $"poltersense/{settings.InstanceId}/test/observations";
    private string status = "unconfigured";
    private long connections;
    public MqttStatus Snapshot() => new(Volatile.Read(ref status), settings.TestInputEnabled, ClientId, StatusTopic, TestTopic, Interlocked.Read(ref connections));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Host)) return;
        // Topic and client identity never accept a caller-supplied prefix or legacy identity.
        if (!System.Text.RegularExpressions.Regex.IsMatch(settings.InstanceId, "^[a-z0-9-]{1,48}$"))
            throw new InvalidOperationException("Invalid PolterSense instance identifier");
        using var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += async args =>
        {
            if (!settings.TestInputEnabled || args.ApplicationMessage.Topic != TestTopic) return;
            try
            {
                if (args.ApplicationMessage.Payload.Length > NormalizedTestAdapter.MaxPayloadBytes)
                { pipeline.Reject("oversized"); return; }
                var observation = adapter.Decode(args.ApplicationMessage.Payload.ToArray(),
                    new(time.GetUtcNow(), args.ApplicationMessage.Retain));
                await pipeline.SubmitAsync(observation, config.Current, stoppingToken);
            }
            catch (ObservationException ex) { pipeline.Reject(ex.Code); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception) { pipeline.Reject("malformed"); }
        };
        var options = new MqttClientOptionsBuilder().WithClientId(ClientId)
            .WithTcpServer(settings.Host, settings.Port).WithProtocolVersion(MqttProtocolVersion.V500)
            .WithCleanSession().WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .WithTimeout(TimeSpan.FromSeconds(5))
            .WithWillTopic(StatusTopic).WithWillPayload("offline").WithWillRetain();
        if (settings.Username != null) options.WithCredentials(settings.Username, settings.Password);
        if (settings.Tls) options.WithTlsOptions(tls => tls.UseTls());
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!client.IsConnected)
                {
                    Volatile.Write(ref status, "connecting");
                    try
                    {
                        await client.ConnectAsync(options.Build(), stoppingToken);
                        if (settings.TestInputEnabled)
                        {
                            // Retain-as-published lets admission reject *all* retained test messages,
                            // including publications delivered to an already connected subscriber.
                            var subscription = await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                                .WithTopicFilter(filter => filter.WithTopic(TestTopic).WithRetainAsPublished(true))
                                .Build(), stoppingToken);
                            if (subscription.Items.Count != 1 || subscription.Items.Any(item => (int)item.ResultCode >= 0x80))
                                throw new InvalidOperationException("PolterSense test subscription rejected");
                        }
                        await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(StatusTopic)
                            .WithPayload("online").WithRetainFlag().Build(), stoppingToken);
                        Interlocked.Increment(ref connections);
                        Volatile.Write(ref status, "connected");
                        logger.LogInformation("PolterSense MQTT connected");
                    }
                    catch (Exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        Volatile.Write(ref status, "disconnected");
                        // A successful CONNECT does not imply successful subscription/status setup.
                        // Tear down partial setup so the next iteration actually retries it.
                        if (client.IsConnected)
                        {
                            using var disconnect = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            try { await client.DisconnectAsync(new MqttClientDisconnectOptions(), disconnect.Token); }
                            catch (Exception) { }
                        }
                        // Do not log exception text: libraries may embed hosts, credentials or payloads.
                        logger.LogWarning("PolterSense MQTT unavailable; retrying");
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            Volatile.Write(ref status, "disconnected");
            if (client.IsConnected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(StatusTopic)
                        .WithPayload("offline").WithRetainFlag().Build(), timeout.Token);
                    await client.DisconnectAsync(new MqttClientDisconnectOptions(), timeout.Token);
                }
                catch (Exception) { /* Last Will remains the fallback; no retained deletion. */ }
            }
        }
    }
}
