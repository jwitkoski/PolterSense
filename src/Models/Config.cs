namespace PolterSense.Models;

/// <summary>Scene-only configuration. Secrets belong in transport environment settings.</summary>
public sealed class Config
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "PolterSense";
    public ConfigMap Map { get; set; } = new();
    public ConfigFloor[] Floors { get; set; } = [];
    public Wisp[] Wisps { get; set; } = [];
    public Source[] Sources { get; set; } = [];
    public SensingLink[] Links { get; set; } = [];
}

public sealed class ConfigMap
{
    public bool FlipX { get; set; }
    public bool FlipY { get; set; } = true;
    public double WallThickness { get; set; } = 0.1;
    public string? WallColor { get; set; }
    public double? WallOpacity { get; set; }
}

public sealed class ConfigFloor
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double Elevation { get; set; }
    public double[][] Bounds { get; set; } = [];
    public ConfigRoom[] Rooms { get; set; } = [];
}

public sealed class ConfigRoom
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double[][] Points { get; set; } = [];
    public string? Color { get; set; }
}

public sealed class Wisp
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ExternalId { get; set; }
    // Omit absent connections to preserve recorded scene hashes from before live adapters.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public WispConnection? DirectHttp { get; set; }
    public double[] Point { get; set; } = [];
    public string[] Floors { get; set; } = [];
    public double[]? Orientation { get; set; }
}

public sealed class Source
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ApLabel { get; set; }
    public string? RadioId { get; set; }
    public string? Bssid { get; set; }
    public int? Channel { get; set; }
    public double[] Point { get; set; } = [];
    public string[] Floors { get; set; } = [];
    public double[]? Orientation { get; set; }
}

public sealed class SensingLink
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string WispId { get; set; } = "";
    public string SourceId { get; set; } = "";
}

public sealed record ConfigSnapshot(string Revision, Config Config);
public sealed record ConfigView(string Revision, Config Config, string Yaml, string? Error);

public sealed class ConfigValidationException(string message) : Exception(message);
public sealed class ConfigConflictException() : Exception("Configuration changed on disk or the supplied revision is no longer current. Reload before editing.");
