using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using PolterSense.Models;

namespace PolterSense.Espectre;

public interface IEspectreBootstrap
{
    Task<WispConnection[]> FindAsync(CancellationToken ct);
}

/// <summary>ESPectre's documented random-name bootstrap. No subnet scan or background device commands.</summary>
public sealed class MdnsBootstrap : IEspectreBootstrap
{
    public async Task<WispConnection[]> FindAsync(CancellationToken ct)
    {
        var name = $"espectre-devices-{Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant()}.local";
        var id = (ushort)RandomNumberGenerator.GetInt32(1, 65536);
        using var packet = new MemoryStream();
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id); header[5] = 1;
        packet.Write(header);
        foreach (var label in name.Split('.')) { packet.WriteByte((byte)label.Length); packet.Write(Encoding.ASCII.GetBytes(label)); }
        packet.Write(new byte[] { 0, 0, 1, 0, 1 }); // A / IN; ephemeral port requests legacy unicast reply.
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var addresses = new HashSet<string>();
        try
        {
            await udp.SendAsync(packet.ToArray(), new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353), timeout.Token);
            for (var received = 0; received < 32 && addresses.Count < 4; received++)
            {
                var response = await udp.ReceiveAsync(timeout.Token);
                if (response.RemoteEndPoint.Port != 5353 || !WispConnection.IsPrivateAddress(response.RemoteEndPoint.Address.ToString())) continue;
                foreach (var address in ParseAddresses(response.Buffer, name, id)) addresses.Add(address);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (SocketException) { }
        return addresses.Take(4).Select(a => new WispConnection { Address = a }).ToArray();
    }

    public static string[] ParseAddresses(byte[] data, string question, ushort id)
    {
        if (data.Length < 12 || data.Length > 4096 || Read16(data, 0) != id || (data[2] & 0x80) == 0 || (data[3] & 15) != 0) return [];
        try
        {
            var offset = 12;
            var questions = Read16(data, 4);
            var records = Read16(data, 6) + Read16(data, 8) + Read16(data, 10);
            if (questions > 4 || records > 64) return [];
            for (var i = 0; i < questions; i++) { ReadName(data, ref offset); offset += 4; }
            var result = new HashSet<string>();
            for (var i = 0; i < records; i++)
            {
                var name = ReadName(data, ref offset);
                if (offset + 10 > data.Length) return [];
                var type = Read16(data, offset); var cls = Read16(data, offset + 2) & 0x7fff;
                var ttl = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4, 4));
                var length = Read16(data, offset + 8); offset += 10;
                if (offset + length > data.Length) return [];
                if (type == 1 && cls == 1 && ttl > 0 && length == 4 && name.Equals(question, StringComparison.OrdinalIgnoreCase))
                {
                    var address = new IPAddress(data.AsSpan(offset, 4)).ToString();
                    if (WispConnection.IsPrivateAddress(address)) result.Add(address);
                }
                offset += length;
            }
            return result.Take(4).ToArray();
        }
        catch (ArgumentException) { return []; }
    }
    private static ushort Read16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    private static string ReadName(byte[] data, ref int offset)
    {
        var labels = new List<string>();
        var cursor = offset; var jumped = false;
        for (var hops = 0; hops < 64; hops++)
        {
            if (cursor >= data.Length) throw new ArgumentException();
            var length = data[cursor++];
            if ((length & 0xc0) == 0xc0)
            {
                if (cursor >= data.Length) throw new ArgumentException();
                if (!jumped) offset = cursor + 1;
                cursor = ((length & 63) << 8) | data[cursor]; jumped = true; continue;
            }
            if (length > 63 || cursor + length > data.Length) throw new ArgumentException();
            if (length == 0)
            {
                if (!jumped) offset = cursor;
                var name = string.Join('.', labels);
                if (name.Length > 253) throw new ArgumentException();
                return name;
            }
            labels.Add(Encoding.ASCII.GetString(data, cursor, length)); cursor += length;
        }
        throw new ArgumentException();
    }
}
