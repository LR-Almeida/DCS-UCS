using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace UCS.Server;

internal sealed record LocalAddress(IPAddress Ip, bool HasGateway, string? VpnName)
{
    /// <summary>Address of the adapter that reaches the router (the port-forward target).</summary>
    public bool IsRouterLan => HasGateway && VpnName == null;

    public string Describe() =>
        VpnName != null ? $"{VpnName} - players in the same {VpnName} network"
        : HasGateway ? "local network / router"
        : "local adapter without gateway";
}

/// <summary>
/// Finds the addresses players should use to reach this server: the public IP (asked to an
/// external "what is my IP" service, like games do) and the local addresses of this machine.
/// </summary>
internal static class NetworkInfo
{
    private static readonly string[] PublicIpServices =
    {
        "https://api.ipify.org",
        "https://checkip.amazonaws.com",
        "https://icanhazip.com",
    };

    public static async Task<IPAddress?> DetectPublicIpAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DCS-UCS-Server");
        foreach (var url in PublicIpServices)
        {
            try
            {
                var text = (await http.GetStringAsync(url, ct).ConfigureAwait(false)).Trim();
                if (IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                    return ip;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch
            {
                // try the next service
            }
        }
        return null;
    }

    /// <summary>IPv4 addresses of active adapters; the router LAN first, then VPNs/others.</summary>
    public static List<LocalAddress> LocalAddresses()
    {
        var result = new List<LocalAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = nic.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    var ip = ua.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                    if (ip.ToString().StartsWith("169.254.", StringComparison.Ordinal)) continue; // APIPA
                    if (result.Any(r => r.Ip.Equals(ip))) continue;
                    result.Add(new LocalAddress(ip, hasGateway, VpnName(ip, nic)));
                }
            }
        }
        catch
        {
            // ignore
        }
        return result.OrderByDescending(r => r.IsRouterLan).ThenByDescending(r => r.HasGateway).ToList();
    }

    /// <summary>Recognizes common virtual LAN tools by address range or adapter name.</summary>
    private static string? VpnName(IPAddress ip, NetworkInterface nic)
    {
        var b = ip.GetAddressBytes();
        var desc = (nic.Name + " " + nic.Description).ToLowerInvariant();
        if (b[0] == 26 || desc.Contains("radmin")) return "Radmin VPN";
        if (b[0] == 25 || desc.Contains("hamachi")) return "Hamachi";
        if (desc.Contains("tailscale") || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)) return "Tailscale";
        if (desc.Contains("zerotier")) return "ZeroTier";
        if (desc.Contains("wireguard") || desc.Contains("openvpn") || desc.Contains("tap-windows") || desc.Contains("vpn")) return "VPN";
        if (desc.Contains("hyper-v") || desc.Contains("vethernet") || desc.Contains("virtualbox") || desc.Contains("vmware") || desc.Contains("wsl"))
            return "virtual machine";
        return null;
    }
}
