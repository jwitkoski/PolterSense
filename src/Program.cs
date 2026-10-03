using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;
using PolterSense.Models;
using PolterSense.Sensing;
using PolterSense.Services;
using PolterSense.Espectre;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((context, cfg) => cfg.ReadFrom.Configuration(context.Configuration));
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 512 * 1024);
var storage = Environment.GetEnvironmentVariable("POLTERSENSE_CONFIG_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".poltersense");
var demo = Environment.GetEnvironmentVariable("POLTERSENSE_DEMO") == "true";
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.MaxDepth = 32;
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(provider => new ConfigLoader(storage, provider.GetRequiredService<ILogger<ConfigLoader>>()));
builder.Services.AddHostedService(provider => provider.GetRequiredService<ConfigLoader>());
builder.Services.AddSingleton<IMedium, NoOpMedium>();
builder.Services.AddSingleton<IObservationAdapter, NormalizedTestAdapter>();
builder.Services.AddSingleton<ObservationPipeline>();
builder.Services.AddHostedService<PipelineHost>();
builder.Services.AddSingleton(MqttSettings.FromEnvironment(storage));
builder.Services.AddSingleton<MqttCoordinator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<MqttCoordinator>());
builder.Services.AddSingleton<EchoReplay>();
builder.Services.AddSingleton(provider => new EspectreClient(EspectreClient.CreateHttpClient(), provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IEspectreBootstrap, MdnsBootstrap>();
builder.Services.AddSingleton<WispManagement>();
builder.Services.AddHealthChecks();
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.Use(async (context, next) =>
{
    // Local UI writes must be same-origin; application intentionally has no remote auth service yet.
    if ((context.Request.Method != "GET" || context.WebSockets.IsWebSocketRequest) && context.Request.Headers.Origin.Count > 0
        && context.Request.Headers.Origin != $"{context.Request.Scheme}://{context.Request.Host}")
    { context.Response.StatusCode = 403; return; }
    try { await next(context); }
    catch (ConfigConflictException) { await Error(context, 409, "Configuration changed; reload before saving."); }
    catch (ConfigValidationException) { await Error(context, 400, "Invalid configuration. The last valid scene remains active."); }
    catch (ObservationException exception) { await Error(context, 400, exception.Code); }
    catch (EspectreException exception)
    {
        var status = exception.Code switch
        {
            "busy" or "device_busy" or "identity_mismatch" => 409,
            "unconfigured_wisp" or "unsupported_operation" => 404,
            "invalid_edit" => 400,
            _ => 502
        };
        await Error(context, status, exception.Code);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception)
    {
        app.Logger.LogWarning("PolterSense request failed; details withheld to protect configuration data");
        await Error(context, 500, "Request failed. Configuration and credentials are not included in errors.");
    }
});
var staticPath = Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "ui/build"))
    ? Path.Combine(app.Environment.ContentRootPath, "ui/build") : Path.Combine(app.Environment.ContentRootPath, "wwwroot");
if (Directory.Exists(staticPath))
{
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new PhysicalFileProvider(staticPath) });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(staticPath) });
}
app.MapHealthChecks("/healthz");
app.MapGet("/api/config", (ConfigLoader config) => ConfigView(config));
app.MapPut("/api/config", async (ConfigEdit edit, ConfigLoader config, CancellationToken ct) =>
{
    await config.UpdateAsync(edit.Config, edit.Revision, ct);
    return ConfigView(config);
});
app.MapPut("/api/config/yaml", async (YamlEdit edit, ConfigLoader config, CancellationToken ct) =>
{
    await config.UpdateYamlAsync(edit.Yaml, edit.Revision, ct);
    return ConfigView(config);
});
app.MapGet("/api/state", (ConfigLoader config, ObservationPipeline pipeline, MqttCoordinator mqtt) => State(config, pipeline, mqtt, demo));
app.MapGet("/api/adapters", (IObservationAdapter adapter) => new[] { adapter.Capabilities });
app.MapPost("/api/wisps/discover", (DiscoveryRequest request, WispManagement management, CancellationToken ct) => management.DiscoverAsync(request, ct));
app.MapGet("/api/wisps/{id}/espectre", (string id, WispManagement management, CancellationToken ct) => management.ReadAsync(id, ct));
app.MapPatch("/api/wisps/{id}/espectre", (string id, DeviceEdit edit, WispManagement management, CancellationToken ct) => management.ApplyAsync(id, edit, ct));
app.MapPost("/api/demo/replay", async (EchoReplay replay, ObservationPipeline pipeline, TimeProvider time, CancellationToken ct) =>
{
    if (!demo) return Results.NotFound();
    var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic-echo.json");
    return Results.Ok(await replay.RunAsync(path, pipeline, time, ct));
});
// No per-sample broadcasting or unbounded per-browser queue. Slow sockets time out.
app.Map("/ws", async (HttpContext context, ConfigLoader config, ObservationPipeline pipeline, MqttCoordinator mqtt) =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
    var receive = ObserveClose(socket, lifetime);
    try
    {
        while (!lifetime.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var send = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            send.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(State(config, pipeline, mqtt, demo), ContractJson.Options), WebSocketMessageType.Text, true, send.Token);
            await Task.Delay(1000, lifetime.Token);
        }
    }
    catch (Exception) when (socket.State != WebSocketState.Open || lifetime.IsCancellationRequested) { }
    catch (WebSocketException) { }
    catch (OperationCanceledException) { }
    finally { lifetime.Cancel(); await receive; }
});
// Removed APIs remain 404 rather than being routed to the SPA.
app.MapFallback((HttpContext context) =>
{
    if (context.Request.Path.StartsWithSegments("/api") || !File.Exists(Path.Combine(staticPath, "index.html"))) return Results.NotFound();
    return Results.File(Path.Combine(staticPath, "index.html"), "text/html");
});
app.Run();

static object ConfigView(ConfigLoader loader)
{
    return loader.GetView();
}
static object State(ConfigLoader config, ObservationPipeline pipeline, MqttCoordinator mqtt, bool demoEnabled)
{
    var current = config.Current;
    var snapshot = pipeline.Snapshot(current);
    return new { SchemaVersion = 1, ConfigRevision = current.Revision, ConfigurationError = config.Error,
        Mqtt = mqtt.Snapshot(), snapshot.Diagnostics, snapshot.Medium, DemoEnabled = demoEnabled };
}
static async Task Error(HttpContext context, int code, string message)
{
    if (context.Response.HasStarted) return;
    context.Response.StatusCode = code;
    await context.Response.WriteAsJsonAsync(new { Error = message });
}
static async Task ObserveClose(WebSocket socket, CancellationTokenSource lifetime)
{
    try
    {
        var buffer = new byte[128];
        while (!lifetime.IsCancellationRequested)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(), lifetime.Token);
            if (received.MessageType == WebSocketMessageType.Close) break;
            // Server-only feed; unexpected client data is not a command API.
            if (received.Count > 0) break;
        }
    }
    catch (Exception) { }
    finally { lifetime.Cancel(); }
}
public record ConfigEdit(string Revision, Config Config);
public record YamlEdit(string Revision, string Yaml);
public sealed class PipelineHost(ObservationPipeline pipeline) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => pipeline.RunAsync(stoppingToken);
}
