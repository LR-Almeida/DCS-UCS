using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using UCS.Common;

namespace UCS.Server;

internal sealed class ConnectedClient
{
    public required string Id { get; init; }
    public string Name { get; set; } = "Pilot";
    public string Version { get; set; } = "";
    public required IPEndPoint EndPoint { get; set; }
    public string SessionKey { get; set; } = "";
    public string Aircraft { get; set; } = "";
    public string ModVersion { get; set; } = "";
    public string ProfileHash { get; set; } = "";
    public int ArgsFormat { get; set; } = 1;
    public bool Standby { get; set; } = true;
    public long LastSeen { get; set; }
    public long JoinedAt { get; init; }
    public long RateWindowStart { get; set; }
    public int RateCount { get; set; }
    public long FlightWindowStart { get; set; }
    public int FlightCount { get; set; }

    /// <summary>A client relays only when paired to a session, flying a profiled aircraft and not in standby.</summary>
    public bool Eligible => !Standby && SessionKey.Length > 0 && Aircraft.Length > 0;

    public bool SameCockpit(ConnectedClient other) =>
        string.Equals(SessionKey, other.SessionKey, StringComparison.Ordinal) &&
        string.Equals(Aircraft, other.Aircraft, StringComparison.OrdinalIgnoreCase);

    /// <summary>Same mod version and same profile mapping: only then commands are relayed.</summary>
    public bool Compatible(ConnectedClient other) =>
        string.Equals(ModVersion, other.ModVersion, StringComparison.Ordinal) &&
        string.Equals(ProfileHash, other.ProfileHash, StringComparison.Ordinal) &&
        ArgsFormat == other.ArgsFormat;

    /// <summary>Crew members that can exchange cockpit commands.</summary>
    public bool CanSyncWith(ConnectedClient other) => other != this && other.Eligible && SameCockpit(other) && Compatible(other);

    public PeerInfo ToPeerInfo() => new() { Name = Name, ModVersion = ModVersion, ProfileHash = ProfileHash, UcsVersion = Version, ArgsFormat = ArgsFormat };
}

/// <summary>
/// Asynchronous UDP relay. Groups clients by SessionKey + aircraft and forwards cockpit
/// argument changes only inside the same group.
/// </summary>
internal sealed class SyncServer
{
    private readonly ServerConfig _cfg;
    private readonly Dictionary<string, ConnectedClient> _clients = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private UdpClient? _udp;
    private long _packetsIn, _packetsOut;

    public SyncServer(ServerConfig cfg) => _cfg = cfg;

    private long NowMs => _clock.ElapsedMilliseconds;

    private static void Log(string level, string msg) =>
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} [{level}] {msg}");

    public async Task RunAsync(CancellationToken ct)
    {
        var bindIp = IPAddress.TryParse(_cfg.BindAddress, out var ip) ? ip : IPAddress.Any;
        _udp = UdpHelper.Create(new IPEndPoint(bindIp, _cfg.Port));
        Log("INFO", $"UCS-Server {Protocol.Version} listening on udp://{bindIp}:{_cfg.Port} " +
                    $"(session match: {_cfg.SessionMatch}, timeout: {_cfg.TimeoutSeconds}s)");
        _ = AnnounceAddressesAsync(ct);
        _ = UpdateNoticeLoopAsync(ct);

        var maintenance = MaintenanceLoopAsync(ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException ex)
                {
                    if (_cfg.Verbose) Log("DEBUG", $"socket: {ex.SocketErrorCode}");
                    continue;
                }

                Interlocked.Increment(ref _packetsIn);
                if (result.Buffer.Length == 0 || result.Buffer.Length > Protocol.MaxDatagram) continue;
                var msg = Protocol.Deserialize(result.Buffer);
                if (msg == null || string.IsNullOrEmpty(msg.ClientId) || msg.ClientId.Length > 64) continue;

                try
                {
                    Handle(msg, result.RemoteEndPoint);
                }
                catch (Exception ex)
                {
                    Log("ERROR", $"handling {msg.Type} from {result.RemoteEndPoint}: {ex.Message}");
                }
            }
        }
        finally
        {
            _udp.Dispose();
            try { await maintenance.ConfigureAwait(false); } catch (OperationCanceledException) { }
            Log("INFO", "Server stopped.");
        }
    }

    /// <summary>Prints the addresses to give to players (runs in background, never delays startup).</summary>
    private async Task AnnounceAddressesAsync(CancellationToken ct)
    {
        try
        {
            var port = _cfg.Port;
            var local = NetworkInfo.LocalAddresses();
            foreach (var a in local.Where(a => a.VpnName != "virtual machine"))
                Log("INFO", $"Local address:  {a.Ip}:{port}   ({a.Describe()})");
            var routerTarget = local.FirstOrDefault(a => a.IsRouterLan)?.Ip;

            if (!_cfg.DetectPublicIp)
            {
                Log("INFO", "Public IP detection disabled (DetectPublicIp = false).");
                return;
            }
            var pub = await NetworkInfo.DetectPublicIpAsync(ct).ConfigureAwait(false);
            if (pub == null)
            {
                Log("WARN", "Could not detect the public IP (no internet or service blocked). Check it at https://api.ipify.org");
                return;
            }
            PublicAddress = $"{pub}:{port}";
            Log("INFO", $"PUBLIC address: {PublicAddress}   <- give this to players over the internet");
            Log("INFO", $"Internet players need UDP {port} open in the Windows Firewall and forwarded on your router" +
                        (routerTarget != null ? $" to {routerTarget}." : "."));
            Log("INFO", "If it still fails, your ISP may use CGNAT (no public IP of your own) - use a VPN such as Tailscale/ZeroTier/Radmin.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log("WARN", "Address detection failed: " + ex.Message);
        }
    }

    public string? PublicAddress { get; private set; }

    private static readonly List<string> ServerFeatures = new() { Protocol.Features.ModCheck, Protocol.Features.UcsVersionCheck, Protocol.Features.FlightMirror,
        Protocol.Features.ExternalState };

    /// <summary>Tells the admin when a newer UCS-Server is published (never updates by itself).</summary>
    private async Task UpdateNoticeLoopAsync(CancellationToken ct)
    {
        if (!_cfg.CheckForUpdates) return;
        var current = SemVersion.Parse(Protocol.Version);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var newer = await GitHubReleases.FindNewerAsync("DCS-UCS-Server", current, current.IsPreRelease, ct).ConfigureAwait(false);
                if (newer != null)
                    Log("UPDATE", $"UCS-Server {newer.Version} is available (running {Protocol.Version}): {newer.HtmlUrl}");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (_cfg.Verbose) Log("DEBUG", "Update check failed: " + ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromHours(12), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Handle(UcsMessage msg, IPEndPoint from)
    {
        switch (msg.Type)
        {
            case Protocol.State: HandleState(msg, from); break;
            case Protocol.Args: HandleArgs(msg, from); break;
            case Protocol.Flight: HandleArgs(msg, from); break;
            case Protocol.Ext: HandleArgs(msg, from); break;
            case Protocol.Leave: HandleLeave(msg, from); break;
        }
    }

    private string BuildSessionKey(UcsMessage msg)
    {
        var room = Clean(msg.Room, 128);
        if (room.Length > 0) return "ROOM|" + room;
        var server = Clean(msg.Server, 128);
        var group = Clean(msg.Group, 128);
        var unit = Clean(msg.Unit, 128);
        if (server.Length == 0 || (group.Length == 0 && unit.Length == 0)) return "";
        return _cfg.SessionMatch.Equals("Group", StringComparison.OrdinalIgnoreCase) && group.Length > 0
            ? $"{server}|{group}"
            : $"{server}|{group}|{unit}";
    }

    private static string Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        return s.Length > max ? s[..max] : s;
    }

    private void HandleState(UcsMessage msg, IPEndPoint from)
    {
        var id = msg.ClientId!;
        var key = BuildSessionKey(msg);
        var aircraft = Clean(msg.Aircraft, 64);
        var standby = msg.Standby ?? true;
        ConnectedClient? notifyTarget = null;
        ConnectedClient client;
        bool becameEligible;

        lock (_lock)
        {
            if (!_clients.TryGetValue(id, out client!))
            {
                if (_clients.Count >= _cfg.MaxClients)
                {
                    Send(new UcsMessage { Type = Protocol.Error, ClientId = id, Message = "Server full" }, from);
                    return;
                }
                client = new ConnectedClient { Id = id, EndPoint = from, JoinedAt = NowMs };
                _clients[id] = client;
                Log("JOIN", $"{Clean(msg.Name, 32)} ({from})");
            }

            var wasEligible = client.Eligible;
            var oldKey = client.SessionKey;
            var oldAircraft = client.Aircraft;
            var oldVersion = client.ModVersion;
            var oldHash = client.ProfileHash;
            var oldFormat = client.ArgsFormat;

            client.EndPoint = from;
            client.LastSeen = NowMs;
            client.Name = Clean(msg.Name, 32) is { Length: > 0 } n ? n : "Pilot";
            client.Version = Clean(msg.Version, 24);
            client.SessionKey = key;
            client.Aircraft = aircraft;
            client.Standby = standby;
            client.ModVersion = Clean(msg.ModVersion, 32);
            client.ProfileHash = Clean(msg.ProfileHash, 32);
            client.ArgsFormat = msg.ArgsFormat is > 0 ? msg.ArgsFormat.Value : 1; // old clients: 1

            var changed = oldKey != key || !string.Equals(oldAircraft, aircraft, StringComparison.OrdinalIgnoreCase) ||
                          oldVersion != client.ModVersion || oldHash != client.ProfileHash || oldFormat != client.ArgsFormat;
            becameEligible = client.Eligible && (!wasEligible || changed);

            if (changed || wasEligible != client.Eligible)
            {
                Log("SESSION", $"{client.Name}: {(key.Length > 0 ? key : "<unpaired>")} | " +
                               $"{(aircraft.Length > 0 ? aircraft : "-")} | mod {(client.ModVersion.Length > 0 ? client.ModVersion : "?")} | " +
                               $"profile {(client.ProfileHash.Length > 0 ? client.ProfileHash : "-")} | {(client.Standby ? "Standby" : "Active")}");
                foreach (var other in _clients.Values.Where(c => c != client && c.Eligible && client.Eligible && c.SameCockpit(client) && !c.Compatible(client)))
                {
                    Log("MISMATCH", $"{client.Name} (UCS {client.Version}, mod {client.ModVersion}, {client.ProfileHash}) vs {other.Name} " +
                                    $"(UCS {other.Version}, mod {other.ModVersion}, {other.ProfileHash}) - not synced");
                }
            }

            if (becameEligible)
            {
                // Oldest crew member in the cockpit sends the full cockpit state to the newcomer
                notifyTarget = _clients.Values
                    .Where(c => client.CanSyncWith(c))
                    .OrderBy(c => c.JoinedAt)
                    .FirstOrDefault();
            }
        }

        if (notifyTarget != null)
        {
            Send(new UcsMessage { Type = Protocol.PeerJoined, ClientId = client.Id, Name = client.Name }, notifyTarget.EndPoint);
        }

        SendWelcome(client);
    }

    private void SendWelcome(ConnectedClient client)
    {
        List<string> names;
        List<PeerInfo> incompatible;
        List<PeerInfo> crew;
        lock (_lock)
        {
            var synced = client.Eligible
                ? _clients.Values.Where(c => client.CanSyncWith(c)).ToList()
                : new List<ConnectedClient>();
            names = synced.Select(c => c.Name).ToList();
            crew = synced.Select(c => c.ToPeerInfo()).ToList();
            incompatible = client.Eligible
                ? _clients.Values.Where(c => c != client && c.Eligible && c.SameCockpit(client) && !c.Compatible(client))
                    .Select(c => c.ToPeerInfo()).ToList()
                : new List<PeerInfo>();
        }
        Send(new UcsMessage
        {
            Type = Protocol.Welcome,
            ClientId = client.Id,
            Version = Protocol.Version,
            SessionKey = client.SessionKey,
            Peers = names.Count,
            PeerNames = names,
            Incompatible = incompatible.Count > 0 ? incompatible : null,
            Protocol = UCS.Common.Protocol.Level,
            ArgsFormat = UCS.Common.Protocol.ArgsFormat,
            Features = ServerFeatures,
            Crew = crew,
        }, client.EndPoint);
    }

    private void HandleArgs(UcsMessage msg, IPEndPoint from)
    {
        if (msg.Values == null || msg.Values.Count == 0) return;
        List<IPEndPoint> targets;
        ConnectedClient? sender;
        lock (_lock)
        {
            if (!_clients.TryGetValue(msg.ClientId!, out sender)) return;
            if (!sender.EndPoint.Equals(from)) return;               // anti-spoofing
            if (!sender.Eligible) return;                            // standby / unpaired
            if (!string.Equals(sender.Aircraft, msg.Aircraft, StringComparison.OrdinalIgnoreCase)) return;

            var now = NowMs;
            sender.LastSeen = now;
            if (msg.Type == Protocol.Flight)
            {
                // Separate budget: a ~30 Hz stick stream must never starve switch changes.
                if (now - sender.FlightWindowStart >= 1000)
                {
                    sender.FlightWindowStart = now;
                    sender.FlightCount = 0;
                }
                if (++sender.FlightCount > 90) return;
            }
            else
            {
                if (now - sender.RateWindowStart >= 1000)
                {
                    sender.RateWindowStart = now;
                    sender.RateCount = 0;
                }
                if (++sender.RateCount > _cfg.MaxPacketsPerSecond) return;
            }

            var query = _clients.Values.Where(c => sender.CanSyncWith(c));
            if (!string.IsNullOrEmpty(msg.Target)) query = query.Where(c => c.Id == msg.Target);
            targets = query.Select(c => c.EndPoint).ToList();
        }
        if (targets.Count == 0) return;

        var outMsg = new UcsMessage
        {
            Type = msg.Type is Protocol.Flight or Protocol.Ext ? msg.Type : Protocol.Args,
            ClientId = sender.Id,
            From = sender.Name,
            Aircraft = sender.Aircraft,
            Seq = msg.Seq,
            Values = msg.Values,
        };
        var bytes = Protocol.Serialize(outMsg);
        foreach (var ep in targets) SendRaw(bytes, ep);
        if (_cfg.Verbose && msg.Type != Protocol.Flight) Log("DEBUG", $"{sender.Name}: {msg.Values.Count} args -> {targets.Count} peer(s)");
    }

    private void HandleLeave(UcsMessage msg, IPEndPoint from)
    {
        lock (_lock)
        {
            if (_clients.TryGetValue(msg.ClientId!, out var c) && c.EndPoint.Equals(from))
            {
                _clients.Remove(c.Id);
                Log("LEAVE", $"{c.Name} ({from})");
            }
        }
    }

    private async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        var nextStatus = NowMs + _cfg.StatusIntervalSeconds * 1000L;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            var now = NowMs;
            lock (_lock)
            {
                foreach (var c in _clients.Values.Where(c => now - c.LastSeen > _cfg.TimeoutSeconds * 1000L).ToList())
                {
                    _clients.Remove(c.Id);
                    Log("TIMEOUT", $"{c.Name} ({c.EndPoint})");
                }
            }
            if (now >= nextStatus)
            {
                nextStatus = now + _cfg.StatusIntervalSeconds * 1000L;
                PrintStatus();
            }
        }
    }

    public void PrintStatus()
    {
        lock (_lock)
        {
            if (PublicAddress != null) Log("STATUS", $"Public address: {PublicAddress}");
            Log("STATUS", $"{_clients.Count} client(s), packets in/out: {Interlocked.Read(ref _packetsIn)}/{Interlocked.Read(ref _packetsOut)}");
            foreach (var g in _clients.Values.Where(c => c.SessionKey.Length > 0).GroupBy(c => c.SessionKey + " | " + c.Aircraft))
            {
                Log("STATUS", $"  {g.Key}: {string.Join(", ", g.Select(c => $"{c.Name} [mod {c.ModVersion}/{c.ProfileHash}]" + (c.Standby ? " (standby)" : "")))}");
            }
        }
    }

    private void Send(UcsMessage msg, IPEndPoint ep) => SendRaw(Protocol.Serialize(msg), ep);

    private void SendRaw(byte[] bytes, IPEndPoint ep)
    {
        try
        {
            _udp?.Send(bytes, bytes.Length, ep);
            Interlocked.Increment(ref _packetsOut);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            if (_cfg.Verbose) Log("DEBUG", $"send to {ep} failed: {ex.Message}");
        }
    }
}
