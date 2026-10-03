using System.Net;
using System.Net.Sockets;

namespace PolterSense.Models;

/// <summary>Explicit local-network endpoint; firmware identity remains Wisp.ExternalId.</summary>
public sealed class WispConnection
{
    public string Address { get; set; } = "";
    public int Port { get; set; } = 62587;

    public static bool IsPrivateAddress(string? value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.ToString() != value) return false;
        var bytes = ip.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
    }

    public void Validate()
    {
        if (!IsPrivateAddress(Address) || Port is < 1 or > 65535)
            throw new ConfigValidationException("Use an RFC1918 IPv4 address and port 1-65535 for Direct HTTP.");
    }
}
