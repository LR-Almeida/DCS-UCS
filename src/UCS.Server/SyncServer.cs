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
    public bool Standby { get; set; } = true;
    public long LastSeen { get; set; }
    public long JoinedAt { get; init; }
    public long RateWindowStart { get; set; }
    public int RateCount { get; set; }

    /// <summary>A client relays only when paired to a session, flying a profiled aircraft and not in standby.</summary>
    public bool Eligible => !Standby && SessionKey.Length > 0 && Aircraft.Length > 0;

    public bool SameCockpit(ConnectedClient other) =>
        string.Equals(SessionKey, other.SessionKey, StringComparison.Ordinal) &&
        string.Equals(Aircraft, other.Aircraft, StringComparison.OrdinalIgnoreCase);

    /// <summary>Same mod version and same profile mapping: only then commands are relayed.</summary>
    public bool Compatible(ConnectedClient other) =>
        string.Equals(ModVersion, other.ModVersion, StringComparison.Ordinal) &&
        string.Equals(ProfileHash, other.ProfileHash, StringComparison.Ordinal);

    /// <summary>Crew members that can exchange cockpit commands.</summary>
    public bool CanSyncWith(ConnectedClient other) => other != this && other.Eligible && SameCockpit(other) && Compatible(other);

    public PeerInfo ToPeerInfo() => new() { Name = Name, ModVersion = ModVersion, ProfileHash = ProfileHash };
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
        Log("INFO", "Remember to allow this UDP port in the Windows Firewall / router (port forward) for remote players.");

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

    private void Handle(UcsMessage msg, IPEndPoint from)
    {
        switch (msg.Type)
        {
            case Protocol.State: HandleState(msg, from); break;
            case Protocol.Args: HandleArgs(msg, from); break;
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

            client.EndPoint = from;
            client.LastSeen = NowMs;
            client.Name = Clean(msg.Name, 32) is { Length: > 0 } n ? n : "Pilot";
            client.Version = Clean(msg.Version, 16);
            client.SessionKey = key;
            client.Aircraft = aircraft;
            client.Standby = standby;
            client.ModVersion = Clean(msg.ModVersion, 32);
            client.ProfileHash = Clean(msg.ProfileHash, 32);

            var changed = oldKey != key || !string.Equals(oldAircraft, aircraft, StringComparison.OrdinalIgnoreCase) ||
                          oldVersion != client.ModVersion || oldHash != client.ProfileHash;
            becameEligible = client.Eligible && (!wasEligible || changed);

            if (changed || wasEligible != client.Eligible)
            {
                Log("SESSION", $"{client.Name}: {(key.Length > 0 ? key : "<unpaired>")} | " +
                               $"{(aircraft.Length > 0 ? aircraft : "-")} | mod {(client.ModVersion.Length > 0 ? client.ModVersion : "?")} | " +
                               $"profile {(client.ProfileHash.Length > 0 ? client.ProfileHash : "-")} | {(client.Standby ? "Standby" : "Active")}");
                foreach (var other in _clients.Values.Where(c => c != client && c.Eligible && client.Eligible && c.SameCockpit(client) && !c.Compatible(client)))
                {
                    Log("MISMATCH", $"{client.Name} (mod {client.ModVersion}, {client.ProfileHash}) vs {other.Name} " +
                                    $"(mod {other.ModVersion}, {other.ProfileHash}) - not synced");
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
        lock (_lock)
        {
            names = client.Eligible
                ? _clients.Values.Where(c => client.CanSyncWith(c)).Select(c => c.Name).ToList()
                : new List<string>();
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
            if (now - sender.RateWindowStart >= 1000)
            {
                sender.RateWindowStart = now;
                sender.RateCount = 0;
            }
            if (++sender.RateCount > _cfg.MaxPacketsPerSecond) return;

            var query = _clients.Values.Where(c => sender.CanSyncWith(c));
            if (!string.IsNullOrEmpty(msg.Target)) query = query.Where(c => c.Id == msg.Target);
            targets = query.Select(c => c.EndPoint).ToList();
        }
        if (targets.Count == 0) return;

        var outMsg = new UcsMessage
        {
            Type = Protocol.Args,
            ClientId = sender.Id,
            From = sender.Name,
            Aircraft = sender.Aircraft,
            Seq = msg.Seq,
            Values = msg.Values,
        };
        var bytes = Protocol.Serialize(outMsg);
        foreach (var ep in targets) SendRaw(bytes, ep);
        if (_cfg.Verbose) Log("DEBUG", $"{sender.Name}: {msg.Values.Count} args -> {targets.Count} peer(s)");
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
