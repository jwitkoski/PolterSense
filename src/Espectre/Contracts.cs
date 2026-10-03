using System.Text.Json.Serialization;

namespace PolterSense.Espectre;

public sealed record EspectreDevice(string DeviceId, string Name, string Label, string Frontend, string Firmware, string Chip);
public sealed record EspectreCapabilities(string ProtocolVersion, bool CanUpdateDevice, bool CanUpdateSensing);
public sealed record EspectreSensing(bool Enabled, bool Ready, bool Calibrating, string Mode, bool DerivedEventsPaused,
    string Detector, double Threshold, int MotionOnHits, int MotionOffHits, string TrafficGeneratorMode);
public sealed record EspectreSnapshot(string Address, int Port, EspectreDevice Device, EspectreCapabilities Capabilities,
    EspectreSensing? Sensing, DateTimeOffset ReadAt);
public sealed record DiscoveryRequest(string? Address = null, int Port = 62587);
public sealed record DiscoveryResult(EspectreSnapshot[] Candidates, string Status, string Message);
public sealed record DeviceEdit(string Revision, string ExpectedDeviceId, string? Label = null, SensingEdit? Sensing = null);
public sealed record DeviceEditResult(bool Confirmed, string Code, string Message, EspectreSnapshot? Snapshot);
public sealed class SensingEdit
{
    public bool? Enabled { get; set; }
    public string? Detector { get; set; }
    public double? Threshold { get; set; }
    public int? MotionOnHits { get; set; }
    public int? MotionOffHits { get; set; }
    public string? TrafficGeneratorMode { get; set; }
}
public sealed class EspectreException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
