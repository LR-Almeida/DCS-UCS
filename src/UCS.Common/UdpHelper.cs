using System.Net;
using System.Net.Sockets;

namespace UCS.Common;

public static class UdpHelper
{
    private const int SioUdpConnReset = -1744830452;

    /// <summary>
    /// Creates a UdpClient bound to the given endpoint. On Windows, disables the
    /// "ICMP port unreachable" reset that otherwise breaks the receive loop when a
    /// peer goes away.
    /// </summary>
    public static UdpClient Create(IPEndPoint bind)
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        if (OperatingSystem.IsWindows())
        {
            try { udp.Client.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null); } catch { }
        }
        udp.Client.ReceiveBufferSize = 1 << 20;
        udp.Client.SendBufferSize = 1 << 20;
        udp.Client.Bind(bind);
        return udp;
    }

    /// <summary>Parses "host" or "host:port" (DNS names allowed).</summary>
    public static bool TryParseAddress(string? text, int defaultPort, out string host, out int port)
    {
        host = "";
        port = defaultPort;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.StartsWith("udp://", StringComparison.OrdinalIgnoreCase)) text = text[6..];
        var idx = text.LastIndexOf(':');
        if (idx > 0 && text.IndexOf(':') == idx)
        {
            if (!int.TryParse(text[(idx + 1)..], out port) || port is < 1 or > 65535) return false;
            host = text[..idx];
        }
        else
        {
            host = text;
        }
        return host.Length > 0;
    }

    public static async Task<IPEndPoint?> ResolveAsync(string host, int port, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var ip)) return new IPEndPoint(ip, port);
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            var v4 = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return v4 is null ? null : new IPEndPoint(v4, port);
        }
        catch
        {
            return null;
        }
    }
}
