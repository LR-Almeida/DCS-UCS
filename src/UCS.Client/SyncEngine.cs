using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using UCS.Common;
using UCS.ProfileCore;

namespace UCS.Client;

public sealed record EngineStatus(
    bool ExportAlive,
    bool ExportActive,
    string Aircraft,
    string? ProfileName,
    int ProfileArgs,
    string DcsServer,
    string Group,
    string Unit,
    string ManualRoom,
    string SessionKey,
    bool ConnectionRequested,
    bool Connected,
    string ServerAddress,
    string ServerVersion,
    IReadOnlyList<string> Peers,
    IReadOnlyList<PeerInfo> Incompatible,
    string ModVersion,
    string ProfileHash,
    bool GeneratingProfile,
    string PilotName,
    string DetectedPilot,
    long SentArgs,
    long ReceivedArgs,
    int FlightArgs,
    bool FlightServerSupport,
    long FlightSent,
    long FlightReceived,
    int Seat,
    bool FlightInjectActive,
    bool? HasControls);

/// <summary>
/// Core of UCS-Client: glues the DCS bridge (Export + Hook) to the UCS-Server
/// connection and keeps the Export script informed about the active profile.
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private const int HeartbeatMs = 2000;
    private const int ExportAliveMs = 5000;
    private const int WelcomeTimeoutMs = 6000;
    private const int ProfileResendMs = 3000;
    private const int MaxArgsPerPacket = 300;

    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DcsBridge _bridge = new();
    private readonly ProfileManager _profiles = new();
    private readonly System.Threading.Timer _timer;
    private readonly string _clientId = Guid.NewGuid().ToString("N");

    private ClientConfig _cfg;

    // DCS state
    private string _hookServer = "", _hookGroup = "", _hookUnit = "", _hookAircraft = "";
    private string _detectedPilot = DcsPlayerName.FromNicknames() ?? "";
    private string _exportAircraft = "";
    private bool _exportActive;
    private long _exportLastSeen = -100000;

    // Profile state
    private AircraftProfile? _profile;
    private string _profileAircraft = "";   // DCS name the profile was sent for
    private long _profileSentAt = -100000;
    private long _lastProfileSearch = -100000;
    private const int ProfileSearchMs = 5000;
    private bool _standbySent;
    private readonly Dictionary<int, double> _localValues = new();
    private string _modVersion = "";        // installed mod version (or profile's when the mod is not found)
    private string _profileHash = "";
    private bool _generating;
    private readonly HashSet<string> _autoGenTried = new(StringComparer.OrdinalIgnoreCase);

    // Server state
    private UdpClient? _serverUdp;
    private IPEndPoint? _serverEp;
    private CancellationTokenSource? _serverCts;
    private bool _connectRequested;
    private long _lastWelcome = -100000;
    private string _sessionKey = "";
    private List<string> _peers = new();
    private List<PeerInfo> _incompatible = new();
    private string _serverVersion = "";
    private bool _serverChecked;
    private readonly HashSet<string> _crewVersionWarned = new(StringComparer.OrdinalIgnoreCase);
    private bool _scriptVersionWarned;
    private long _seq;
    private long _sentArgs, _receivedArgs;
    private long _flightSent, _flightReceived;
    private bool _flightSupported;
    private bool _flightWarned;
    private int _seat = 1;              // multicrew seat reported by the hook (1 = first seat)

    private bool? _hasControls;         // DCS "master" flag reported by the Export (null = unknown)
    private bool _mirroring;            // the Export is mirroring the crew's flight controls in this seat
    private bool _controlsReported;

    /// <summary>FMODE inject field: 0 off, 1 automatic (only without the controls), 2 forced (tests only).</summary>
    private static int InjectMode(ClientConfig cfg) => !cfg.FlightMirrorInject ? 0 : cfg.FlightInjectAnySeat ? 2 : 1;
    private int _connectGeneration;

    public event Action<string>? Log;
    public event Action<ClientConfig>? ConfigChanged;

    public SyncEngine(ClientConfig cfg)
    {
        _cfg = cfg.Clone();
        _bridge.Log += s => Log?.Invoke(s);
        _bridge.ExportMessage += OnExportMessage;
        _bridge.HookMessage += OnHookMessage;
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private long Now => _clock.ElapsedMilliseconds;

    public void Start()
    {
        _bridge.Start();
        _timer.Change(500, 500);
        Log?.Invoke($"UCS-Client {Protocol.Version} started. Waiting for DCS (UDP 5051/5053)...");
        if (_cfg.ConnectOnStartup) _ = ConnectAsync();
    }

    public void UpdateConfig(ClientConfig cfg)
    {
        bool roomChanged, flightChanged, injectChanged;
        lock (_gate)
        {
            roomChanged = _cfg.ManualRoom != cfg.ManualRoom || _cfg.PilotName != cfg.PilotName ||
                          _cfg.AutoDetectPilotName != cfg.AutoDetectPilotName;
            injectChanged = _cfg.FlightMirrorInject != cfg.FlightMirrorInject;
            flightChanged = injectChanged || _cfg.FlightMirrorSend != cfg.FlightMirrorSend ||
                            _cfg.FlightDiagnostics != cfg.FlightDiagnostics || _cfg.FlightAxisCommands != cfg.FlightAxisCommands;
            _cfg = cfg.Clone();
        }
        if (roomChanged) SendState();
        if (injectChanged)
            Log?.Invoke(cfg.FlightMirrorInject
                ? "Flight mirror ON (automatic): while you do not have the controls, your stick, pedals and collective follow the crew member flying."
                : "Flight mirror OFF.");
        if (flightChanged) SendFlightMode();
    }

    // ------------------------------------------------------------------ status

    public EngineStatus GetStatus()
    {
        lock (_gate)
        {
            var now = Now;
            return new EngineStatus(
                ExportAlive: now - _exportLastSeen < ExportAliveMs,
                ExportActive: _exportActive && now - _exportLastSeen < ExportAliveMs,
                Aircraft: EffectiveAircraft,
                ProfileName: _profile?.AircraftName,
                ProfileArgs: _profile?.Args.Count ?? 0,
                DcsServer: _hookServer,
                Group: _hookGroup,
                Unit: _hookUnit,
                ManualRoom: _cfg.ManualRoom,
                SessionKey: _sessionKey,
                ConnectionRequested: _connectRequested,
                Connected: _connectRequested && now - _lastWelcome < WelcomeTimeoutMs,
                ServerAddress: $"{_cfg.ServerHost}:{_cfg.ServerPort}",
                ServerVersion: _serverVersion,
                Peers: _peers.ToList(),
                Incompatible: _incompatible.ToList(),
                ModVersion: _modVersion,
                ProfileHash: _profileHash,
                GeneratingProfile: _generating,
                PilotName: EffectivePilotName,
                DetectedPilot: _detectedPilot,
                SentArgs: _sentArgs,
                ReceivedArgs: _receivedArgs,
                FlightArgs: _profile?.Flight?.Count ?? 0,
                FlightServerSupport: _flightSupported,
                FlightSent: _flightSent,
                FlightReceived: _flightReceived,
                Seat: _seat,
                FlightInjectActive: _mirroring && _exportActive,
                HasControls: _hasControls);
        }
    }

    private string EffectiveAircraft
    {
        get
        {
            var exportAlive = Now - _exportLastSeen < ExportAliveMs;
            if (exportAlive && _exportAircraft.Length > 0) return _exportAircraft;
            return _hookAircraft;
        }
    }

    /// <summary>Name sent to the crew: the DCS player name when auto-detection is on.</summary>
    private string EffectivePilotName =>
        _cfg.AutoDetectPilotName && _detectedPilot.Length > 0 ? _detectedPilot : _cfg.PilotName;

    private bool IsStandby => !(_profile != null && _exportActive && Now - _exportLastSeen < ExportAliveMs);

    // ------------------------------------------------------------- server link

    public async Task<bool> ConnectAsync()
    {
        string host;
        int port;
        int generation;
        lock (_gate)
        {
            host = _cfg.ServerHost;
            port = _cfg.ServerPort;
            generation = ++_connectGeneration;
        }
        DisconnectInternal(sendLeave: true, log: false);

        if (string.IsNullOrWhiteSpace(host))
        {
            Log?.Invoke("Enter the UCS server IP/host.");
            return false;
        }
        Log?.Invoke($"Connecting to {host}:{port}...");
        var ep = await UdpHelper.ResolveAsync(host.Trim(), port).ConfigureAwait(false);
        if (ep == null)
        {
            Log?.Invoke($"Could not resolve '{host}'.");
            return false;
        }

        lock (_gate)
        {
            if (generation != _connectGeneration) return false; // superseded by a newer connect
            _serverUdp = UdpHelper.Create(new IPEndPoint(IPAddress.Any, 0));
            _serverEp = ep;
            _serverCts = new CancellationTokenSource();
            _connectRequested = true;
            _lastWelcome = -100000;
            _serverChecked = false;
            _serverVersion = "";
            _flightSupported = false;
            _peers = new List<string>();
            _ = ServerReceiveLoopAsync(_serverUdp, _serverCts.Token);
        }
        SendState();
        return true;
    }

    public void Disconnect()
    {
        lock (_gate) _connectGeneration++;
        DisconnectInternal(sendLeave: true, log: true);
    }

    private void DisconnectInternal(bool sendLeave, bool log)
    {
        UdpClient? udp;
        IPEndPoint? ep;
        CancellationTokenSource? cts;
        bool wasConnected;
        lock (_gate)
        {
            udp = _serverUdp;
            ep = _serverEp;
            cts = _serverCts;
            wasConnected = _connectRequested;
            _serverUdp = null;
            _serverEp = null;
            _serverCts = null;
            _connectRequested = false;
            _sessionKey = "";
            _peers = new List<string>();
            _incompatible = new List<PeerInfo>();
        }
        if (udp != null && ep != null && sendLeave)
        {
            try
            {
                var bytes = Protocol.Serialize(new UcsMessage { Type = Protocol.Leave, ClientId = _clientId });
                udp.Send(bytes, bytes.Length, ep);
            }
            catch { }
        }
        cts?.Cancel();
        udp?.Dispose();
        cts?.Dispose();
        if (log && wasConnected) Log?.Invoke("Disconnected from the UCS server.");
    }

    private void SendToServer(UcsMessage msg)
    {
        UdpClient? udp;
        IPEndPoint? ep;
        lock (_gate)
        {
            udp = _serverUdp;
            ep = _serverEp;
        }
        if (udp == null || ep == null) return;
        try
        {
            var bytes = Protocol.Serialize(msg);
            udp.Send(bytes, bytes.Length, ep);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }

    private void SendState()
    {
        UcsMessage msg;
        lock (_gate)
        {
            if (!_connectRequested) return;
            msg = new UcsMessage
            {
                Type = Protocol.State,
                ClientId = _clientId,
                Name = EffectivePilotName,
                Version = Protocol.Version,
                Server = _hookServer,
                Group = _hookGroup,
                Unit = _hookUnit,
                Room = string.IsNullOrWhiteSpace(_cfg.ManualRoom) ? null : _cfg.ManualRoom.Trim(),
                Aircraft = _profile != null ? _profileAircraft : EffectiveAircraft,
                Standby = IsStandby,
                ModVersion = _profile != null ? _modVersion : null,
                ProfileHash = _profile != null ? _profileHash : null,
                Protocol = UCS.Common.Protocol.Level,
                ArgsFormat = UCS.Common.Protocol.ArgsFormat,
            };
        }
        SendToServer(msg);
    }

    private async Task ServerReceiveLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try
            {
                res = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            var msg = Protocol.Deserialize(res.Buffer);
            if (msg == null) continue;
            try
            {
                HandleServerMessage(msg);
            }
            catch (Exception ex)
            {
                Log?.Invoke("Error processing server packet: " + ex.Message);
            }
        }
    }

    private void HandleServerMessage(UcsMessage msg)
    {
        switch (msg.Type)
        {
            case Protocol.Welcome:
            {
                bool first;
                var incompatibleMsgs = new List<string>();
                var serverNotes = new List<string>();
                List<string> newPeers = msg.PeerNames ?? new List<string>();
                string joinedNames = "";
                lock (_gate)
                {
                    first = Now - _lastWelcome >= WelcomeTimeoutMs;
                    _lastWelcome = Now;
                    _sessionKey = msg.SessionKey ?? "";
                    var added = newPeers.Except(_peers).ToList();
                    var removed = _peers.Except(newPeers).ToList();
                    if (added.Count > 0) joinedNames += "Joined the cockpit: " + string.Join(", ", added) + ". ";
                    if (removed.Count > 0) joinedNames += "Left the cockpit: " + string.Join(", ", removed) + ".";
                    _peers = newPeers;

                    var inc = msg.Incompatible ?? new List<PeerInfo>();
                    var oldKeys = _incompatible.Select(i => $"{i.Name}|{i.ModVersion}|{i.ProfileHash}").ToHashSet();
                    foreach (var i in inc.Where(i => !oldKeys.Contains($"{i.Name}|{i.ModVersion}|{i.ProfileHash}")))
                        incompatibleMsgs.Add(DescribeIncompatibility(i, _modVersion, _profileHash));
                    _incompatible = inc;

                    if (!_serverChecked)
                    {
                        _serverChecked = true;
                        _serverVersion = msg.Version ?? "";
                        serverNotes.AddRange(DescribeServerCapabilities(msg));
                        _flightSupported = (msg.Features ?? UCS.Common.Protocol.Features.Level1.ToList())
                            .Contains(UCS.Common.Protocol.Features.FlightMirror);
                    }
                    if (msg.Crew != null)
                    {
                        foreach (var p in msg.Crew.Where(p => p.UcsVersion.Length > 0 && p.UcsVersion != UCS.Common.Protocol.Version))
                        {
                            if (_crewVersionWarned.Add($"{p.Name}|{p.UcsVersion}"))
                                serverNotes.Add($"NOTE: {p.Name} uses UCS {p.UcsVersion} (you: {UCS.Common.Protocol.Version}). " +
                                                "Sync works, but using the same version is recommended (Update tab).");
                        }
                    }
                }
                if (first) Log?.Invoke($"Connected to the UCS server{(msg.Version is { Length: > 0 } v ? $" (version {v})" : "")}.");
                foreach (var n in serverNotes) Log?.Invoke(n);
                if (joinedNames.Length > 0) Log?.Invoke(joinedNames.Trim());
                foreach (var m in incompatibleMsgs) Log?.Invoke("COMPATIBILITY ERROR: " + m);
                break;
            }
            case Protocol.Error:
                Log?.Invoke("Server: " + msg.Message);
                break;
            case Protocol.PeerJoined:
                if (!string.IsNullOrEmpty(msg.ClientId)) SendSnapshotTo(msg.ClientId, msg.Name);
                break;
            case Protocol.Args:
                ApplyRemoteArgs(msg);
                break;
            case Protocol.Flight:
                ApplyRemoteFlight(msg);
                break;
        }
    }

    /// <summary>
    /// Backward compatibility: newer clients work with older servers; features the server does not
    /// support are disabled and listed in the log.
    /// </summary>
    private static List<string> DescribeServerCapabilities(UcsMessage welcome)
    {
        var notes = new List<string>();
        var features = welcome.Features ?? UCS.Common.Protocol.Features.Level1.ToList();
        var serverVersion = welcome.Version is { Length: > 0 } v ? v : "unknown";
        foreach (var (feature, info) in UCS.Common.Protocol.Features.Known)
        {
            if (!features.Contains(feature))
                notes.Add($"Server {serverVersion}: '{info.Description}' disabled (requires UCS-Server {info.MinServer} or newer).");
        }
        if (welcome.ArgsFormat is { } fmt && fmt != UCS.Common.Protocol.ArgsFormat)
            notes.Add($"WARNING: server uses args format {fmt}, this client uses {UCS.Common.Protocol.ArgsFormat}. Update UCS-Server.");
        return notes;
    }

    /// <summary>Human readable reason why a crew member cannot be synced.</summary>
    public static string DescribeIncompatibility(PeerInfo peer, string myVersion, string myHash)
    {
        static string V(string v) => v.Length > 0 ? v : "no version";
        if (peer.ModVersion != myVersion)
            return $"{peer.Name} has mod version {V(peer.ModVersion)} and you have {V(myVersion)}. Both must install the same version.";
        return $"{peer.Name} has the same mod ({V(myVersion)}) but a different profile ({peer.ProfileHash} vs {myHash}). " +
               @"Both delete this aircraft profile in %APPDATA%\DCS-UCS\profiles so it is generated again.";
    }

    private void SendSnapshotTo(string targetId, string? name)
    {
        List<KeyValuePair<int, double>> values;
        string aircraft;
        lock (_gate)
        {
            if (IsStandby || _profile == null) return;
            values = _localValues.ToList();
            aircraft = _profileAircraft;
        }
        if (values.Count == 0) return;
        Log?.Invoke($"Sending cockpit state to {name ?? "new crew member"} ({values.Count} args).");
        foreach (var chunk in values.Chunk(MaxArgsPerPacket))
        {
            SendToServer(new UcsMessage
            {
                Type = Protocol.Args,
                ClientId = _clientId,
                Aircraft = aircraft,
                Target = targetId,
                Seq = Interlocked.Increment(ref _seq),
                Values = chunk.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value),
            });
        }
    }

    private void ApplyRemoteArgs(UcsMessage msg)
    {
        if (msg.Values == null) return;
        var entries = new List<string>();
        lock (_gate)
        {
            if (IsStandby || _profile == null) return;
            if (!string.Equals(msg.Aircraft, _profileAircraft, StringComparison.OrdinalIgnoreCase)) return;
            foreach (var (key, val) in msg.Values)
            {
                if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var arg)) continue;
                if (!_profile.Args.ContainsKey(arg) || double.IsNaN(val) || double.IsInfinity(val)) continue;
                if (_localValues.TryGetValue(arg, out var cur) && Math.Abs(cur - val) <= 0.01) continue;
                _localValues[arg] = val;
                entries.Add($"{arg}={val.ToString("0.###", CultureInfo.InvariantCulture)}");
            }
            _receivedArgs += entries.Count;
        }
        if (entries.Count > 0) _bridge.SendChunked("SET|", entries);
    }

    /// <summary>Flight-control positions of the crew member flying -> Export (display / experimental injection).</summary>
    private void ApplyRemoteFlight(UcsMessage msg)
    {
        if (msg.Values == null) return;
        var entries = new List<string>();
        lock (_gate)
        {
            if (IsStandby || _profile?.Flight == null) return;
            if (!string.Equals(msg.Aircraft, _profileAircraft, StringComparison.OrdinalIgnoreCase)) return;
            foreach (var (key, val) in msg.Values)
            {
                if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var arg)) continue;
                if (double.IsNaN(val) || double.IsInfinity(val) || !_profile.Flight.Any(f => f.Arg == arg)) continue;
                entries.Add($"{arg}={val.ToString("0.####", CultureInfo.InvariantCulture)}");
            }
            if (entries.Count > 0) _flightReceived++;
        }
        if (entries.Count > 0) _bridge.SendToExport("FSET|" + string.Join(";", entries));
    }

    /// <summary>Tells the Export which flight args to watch (sent after the profile).</summary>
    private void SendFlightArgs(AircraftProfile profile)
    {
        var list = profile.Flight ?? new List<FlightArg>();
        static string F(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);
        _bridge.SendToExport("FLIGHT|" + string.Join(";", list.Select(f => $"{f.Arg}:{f.Axis}:{F(f.Min)}:{F(f.Max)}")));
        SendFlightMode();
    }

    private void SendFlightMode()
    {
        ClientConfig cfg;
        lock (_gate) cfg = _cfg.Clone();
        var cmds = new List<string>();
        // "axis=cmd[:-1],cmd...;axis=..." -> candidates tried in order by the Export (calibration)
        foreach (var item in (cfg.FlightAxisCommands ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = item.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;
            var cands = new List<string>();
            foreach (var cand in kv[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var cs = cand.Split(':', StringSplitOptions.TrimEntries);
                if (!int.TryParse(cs[0], out var cmd)) continue;
                cands.Add($"{cmd}:{(cs.Length > 1 && cs[1].StartsWith('-') ? "-1" : "1")}");
            }
            if (cands.Count > 0) cmds.Add($"{kv[0].ToLowerInvariant()}={string.Join(",", cands)}");
        }
        int seat;
        lock (_gate) seat = _seat;
        _bridge.SendToExport($"FMODE|{(cfg.FlightMirrorSend ? 1 : 0)}|{InjectMode(cfg)}|{(cfg.FlightDiagnostics ? 1 : 0)}|{string.Join(";", cmds)}|{seat}");
    }

    // ---------------------------------------------------------- DCS messages

    private void OnExportMessage(string text)
    {
        var parts = text.Split('|');
        switch (parts[0])
        {
            case "HELLO":
                lock (_gate)
                {
                    _exportLastSeen = Now;
                    _exportActive = false;
                    _profileSentAt = -100000;
                    _standbySent = false;
                    _localValues.Clear();
                }
                Log?.Invoke($"UCS-Export.lua connected (version {(parts.Length > 1 ? parts[1] : "?")}).");
                CheckScriptVersion("UCS-Export.lua", parts.Length > 1 ? parts[1] : "");
                EvaluateProfile();
                break;

            case "SELF":
            {
                var name = parts.Length > 1 ? parts[1] : "";
                var active = parts.Length > 2 && parts[2] == "1";
                bool activated, changed;
                lock (_gate)
                {
                    _exportLastSeen = Now;
                    changed = name != _exportAircraft;
                    activated = active && !_exportActive;
                    if (!active && _exportActive) _localValues.Clear();
                    _exportAircraft = name;
                    _exportActive = active;
                }
                if (changed) Log?.Invoke(name.Length > 0 ? $"DCS: aircraft '{name}'." : "DCS: no aircraft / menu.");
                if (activated)
                {
                    // Ask for a full snapshot so we know the current cockpit state
                    _bridge.SendToExport("SNAPSHOT");
                    string shown;
                    lock (_gate) shown = name.Length > 0 ? name : EffectiveAircraft;
                    Log?.Invoke($"Sync ACTIVE ({shown}).");
                    SendState();
                }
                else if (changed)
                {
                    EvaluateProfile();
                    SendState();
                }
                break;
            }

            case "ARG":
            case "SNAP":
            {
                var broadcast = parts[0] == "ARG";
                var values = new Dictionary<string, double>();
                string aircraft;
                lock (_gate)
                {
                    _exportLastSeen = Now;
                    if (_profile == null) return;
                    aircraft = _profileAircraft;
                    foreach (var entry in (parts.Length > 1 ? parts[1] : "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = entry.Split('=');
                        if (kv.Length != 2) continue;
                        if (!int.TryParse(kv[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var arg)) continue;
                        if (!double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var val)) continue;
                        _localValues[arg] = val;
                        values[kv[0]] = val;
                    }
                    if (broadcast) _sentArgs += values.Count;
                }
                if (broadcast && values.Count > 0 && !IsStandbyLocked())
                {
                    foreach (var chunk in values.Chunk(MaxArgsPerPacket))
                    {
                        SendToServer(new UcsMessage
                        {
                            Type = Protocol.Args,
                            ClientId = _clientId,
                            Aircraft = aircraft,
                            Seq = Interlocked.Increment(ref _seq),
                            Values = chunk.ToDictionary(kv => kv.Key, kv => kv.Value),
                        });
                    }
                }
                break;
            }

            case "FLT":
            {
                var values = new Dictionary<string, double>();
                string aircraft;
                bool supported, warn;
                lock (_gate)
                {
                    _exportLastSeen = Now;
                    if (_profile == null || IsStandby || !_connectRequested) return;
                    aircraft = _profileAircraft;
                    supported = _flightSupported;
                    warn = !supported && _serverChecked && !_flightWarned;
                    if (warn) _flightWarned = true;
                    foreach (var entry in (parts.Length > 1 ? parts[1] : "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = entry.Split('=');
                        if (kv.Length != 2) continue;
                        if (!double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var val)) continue;
                        values[kv[0]] = val;
                    }
                    if (supported && values.Count > 0) _flightSent++;
                }
                if (warn) Log?.Invoke("Flight controls are not mirrored: the UCS server is older than 0.1.2-alpha.");
                if (supported && values.Count > 0)
                {
                    SendToServer(new UcsMessage
                    {
                        Type = Protocol.Flight,
                        ClientId = _clientId,
                        Aircraft = aircraft,
                        Seq = Interlocked.Increment(ref _seq),
                        Values = values,
                    });
                }
                break;
            }

            case "CTRL":
            {
                // CTRL|<master 1/0/?>|<seat 0-based/?>|<mirroring 0/1>
                bool? master = parts.Length > 1 && parts[1] == "1" ? true : parts.Length > 1 && parts[1] == "0" ? false : null;
                var mirroring = parts.Length > 3 && parts[3] == "1";
                bool controlsChanged, mirrorChanged, firstReport, mirrorEnabled;
                lock (_gate)
                {
                    _exportLastSeen = Now;
                    firstReport = !_controlsReported;
                    _controlsReported = true;
                    controlsChanged = master != _hasControls;
                    mirrorChanged = mirroring != _mirroring;
                    _hasControls = master;
                    _mirroring = mirroring;
                    mirrorEnabled = _cfg.FlightMirrorInject;
                }
                if (controlsChanged || firstReport)
                {
                    if (master == true)
                        Log?.Invoke("You have the controls." + (mirrorEnabled ? " Flight mirror inactive." : ""));
                    else if (master == false)
                        Log?.Invoke("A crew member has the controls." + (mirrorEnabled ? " Your stick, pedals and collective follow theirs." : ""));
                    else
                        Log?.Invoke("DCS does not report who has the controls (single player, or the server restricts ownship export): " +
                                    "the flight mirror only runs outside the first seat.");
                }
                if (mirrorChanged && !controlsChanged)
                    Log?.Invoke(mirroring ? "Flight mirror active." : "Flight mirror inactive.");
                break;
            }

            case "DIAG":
                lock (_gate) _exportLastSeen = Now;
                Log?.Invoke("[diag] " + (parts.Length > 1 ? string.Join("|", parts.Skip(1)) : ""));
                break;

            case "BYE":
                lock (_gate)
                {
                    _exportActive = false;
                    _exportAircraft = "";
                    _exportLastSeen = -100000;
                    _localValues.Clear();
                }
                Log?.Invoke("UCS-Export.lua stopped (mission ended).");
                EvaluateProfile();
                SendState();
                break;
        }
    }

    private bool IsStandbyLocked()
    {
        lock (_gate) return IsStandby;
    }

    private void OnHookMessage(JsonElement root)
    {
        var type = Str(root, "type");
        if (type == "autoconnect")
        {
            HandleAutoConnect(Str(root, "addr"));
            return;
        }
        if (type != "slot") return;

        var server = Str(root, "server");
        var group = Str(root, "group");
        var unit = Str(root, "unit");
        var aircraft = Str(root, "aircraft");
        var player = Str(root, "player").Trim();
        var seat = root.TryGetProperty("seat", out var seatEl) && seatEl.ValueKind == JsonValueKind.Number && seatEl.TryGetInt32(out var sv) ? sv : 1;
        bool seatChanged;
        lock (_gate)
        {
            seatChanged = seat != _seat;
            _seat = seat;
        }
        if (seatChanged)
        {
            Log?.Invoke($"Multicrew seat: {seat}.");
            SendFlightMode();
        }
        CheckScriptVersion("UCS-Hook.lua", Str(root, "version"));
        bool nameChanged = false;
        if (player.Length > 0)
        {
            lock (_gate)
            {
                if (player.Length > 32) player = player[..32];
                nameChanged = player != _detectedPilot;
                _detectedPilot = player;
            }
            if (nameChanged) Log?.Invoke($"DCS player name detected: {player}");
        }
        bool changed;
        lock (_gate)
        {
            changed = server != _hookServer || group != _hookGroup || unit != _hookUnit || aircraft != _hookAircraft || nameChanged;
            _hookServer = server;
            _hookGroup = group;
            _hookUnit = unit;
            _hookAircraft = aircraft;
        }
        if (changed)
        {
            Log?.Invoke(unit.Length > 0
                ? $"Slot: {server} | {group} | {unit} ({aircraft})"
                : "Slot: none (spectator / menu).");
            EvaluateProfile();
            SendState();
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private void HandleAutoConnect(string addr)
    {
        ClientConfig snapshot;
        lock (_gate)
        {
            if (!_cfg.AcceptAutoConnect)
            {
                Log?.Invoke($"Auto-Connect received ({addr}) but it is disabled in the settings.");
                return;
            }
            if (!UdpHelper.TryParseAddress(addr, Protocol.DefaultServerPort, out var host, out var port))
            {
                Log?.Invoke($"Auto-Connect: invalid address '{addr}'.");
                return;
            }
            var same = string.Equals(host, _cfg.ServerHost, StringComparison.OrdinalIgnoreCase) && port == _cfg.ServerPort;
            if (same && _connectRequested) return;
            _cfg.ServerHost = host;
            _cfg.ServerPort = port;
            snapshot = _cfg.Clone();
        }
        Log?.Invoke($"Auto-Connect from DCS server: {snapshot.ServerHost}:{snapshot.ServerPort}");
        try { snapshot.Save(); } catch (Exception ex) { Log?.Invoke("Could not save settings: " + ex.Message); }
        ConfigChanged?.Invoke(snapshot);
        _ = ConnectAsync();
    }

    // ----------------------------------------------------------- profiles

    /// <summary>Decides whether the Export must be active (profile exists) or in standby.</summary>
    private readonly object _evalLock = new();

    private void EvaluateProfile()
    {
        // Export, Hook and timer threads may all trigger an evaluation at once
        lock (_evalLock) EvaluateProfileCore();
    }

    private void EvaluateProfileCore()
    {
        string aircraft;
        AircraftProfile? current;
        bool exportAlive;
        lock (_gate)
        {
            aircraft = EffectiveAircraft;
            current = _profile;
            exportAlive = Now - _exportLastSeen < ExportAliveMs;
        }

        if (aircraft.Length == 0)
        {
            if (current != null) Log?.Invoke("Standby: no aircraft.");
            SetStandby();
            return;
        }

        if (current != null && string.Equals(_profileAircraft, aircraft, StringComparison.OrdinalIgnoreCase))
        {
            bool resend;
            lock (_gate) resend = exportAlive && !_exportActive && Now - _profileSentAt > ProfileResendMs;
            if (resend) SendProfile(current, aircraft);
            return;
        }

        bool alreadyStandby;
        lock (_gate)
        {
            alreadyStandby = _profile == null && _standbySent && _profileAircraft == aircraft;
            // Without a profile, look for a newly generated one only every few seconds
            if (alreadyStandby && Now - _lastProfileSearch < ProfileSearchMs) return;
            _lastProfileSearch = Now;
        }

        bool autoGen;
        lock (_gate) autoGen = _cfg.AutoGenerateProfiles;
        var mod = ModLocator.Find(aircraft);
        var profile = _profiles.Find(aircraft);

        if (profile == null)
        {
            if (!alreadyStandby)
            {
                Log?.Invoke(_profiles.LastError != null
                    ? $"Invalid profile for '{aircraft}': {_profiles.LastError}. Standby."
                    : mod == null
                        ? $"No profile for '{aircraft}' (official module or mod not found in Saved Games). Standby."
                        : $"No profile for '{aircraft}'. Standby.");
            }
            SetStandby();
            lock (_gate) _profileAircraft = aircraft;
            if (autoGen && mod != null) StartAutoGenerate(mod, "first time in this aircraft");
            return;
        }

        // Profile generated by an older UCS (no flight controls): regenerate it once
        if (mod != null && autoGen && profile.Schema < AircraftProfile.CurrentSchema &&
            (profile.GeneratedBy ?? "").StartsWith("UCS-ProfileGen", StringComparison.Ordinal) &&
            string.Equals(mod.Version, profile.ModVersion, StringComparison.Ordinal) &&
            StartAutoGenerate(mod, "profile from an older UCS version, adding flight controls"))
        {
            SetStandby();
            lock (_gate) _profileAircraft = aircraft;
            return;
        }

        // Profile generated for another version of the mod: regenerate it (mod updated)
        if (mod != null && !string.Equals(mod.Version, profile.ModVersion, StringComparison.Ordinal))
        {
            if (autoGen && StartAutoGenerate(mod, $"mod updated: profile {Fmt(profile.ModVersion)} -> installed {Fmt(mod.Version)}"))
            {
                SetStandby();
                lock (_gate) _profileAircraft = aircraft;
                return;
            }
            if (!autoGen)
                Log?.Invoke($"WARNING: the '{aircraft}' profile was generated for mod {Fmt(profile.ModVersion)}, but {Fmt(mod.Version)} is installed. " +
                            "Regenerate it.");
        }

        lock (_gate)
        {
            _profile = profile;
            _profileAircraft = aircraft;
            _modVersion = mod?.Version ?? profile.ModVersion;
            _profileHash = profile.Fingerprint();
            _localValues.Clear();
        }
        Log?.Invoke($"Profile loaded: {profile.AircraftName} ({profile.Args.Count} controls, {profile.Flight?.Count ?? 0} flight-control animations, " +
                    $"mod {Fmt(_modVersion)}, profile {_profileHash}).");
        if (exportAlive) SendProfile(profile, aircraft);
        SendState();
    }

    private static string Fmt(string version) => version.Length > 0 ? version : "no version";

    /// <summary>
    /// Generates the profile in the background from the installed mod (local only, neutral labels).
    /// Returns false when a generation for this mod version was already attempted.
    /// </summary>
    private bool StartAutoGenerate(ModInfo mod, string reason)
    {
        var key = $"{mod.Aircraft}|{mod.Version}|{AircraftProfile.CurrentSchema}";
        lock (_gate)
        {
            if (_generating || !_autoGenTried.Add(key)) return false;
            _generating = true;
        }
        Log?.Invoke($"Generating the '{mod.Aircraft}' profile from mod {Path.GetFileName(mod.ModDir)} ({reason})...");
        _ = Task.Run(() =>
        {
            try
            {
                if (mod.ClickablePath == null)
                {
                    Log?.Invoke($"Mod {Path.GetFileName(mod.ModDir)} has no readable clickabledata.lua. Standby.");
                    return;
                }
                var result = ProfileBuilder.Generate(mod.ModDir, mod.Aircraft, new ProfileGenOptions { ClickablePath = mod.ClickablePath });
                if (result.Profile.Args.Count == 0)
                {
                    Log?.Invoke($"Could not generate the '{mod.Aircraft}' profile (no controls found).");
                    return;
                }
                var dir = Path.Combine(AppPaths.UserDir, "profiles");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, ProfileBuilder.FileNameFor(mod.Aircraft));
                result.Profile.Save(path);
                Log?.Invoke($"Profile generated: {result.Profile.Args.Count} controls, {result.FlightArgs} flight-control animations, " +
                            $"mod {Fmt(result.Profile.ModVersion)} ({result.Method}).");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Failed to generate the '{mod.Aircraft}' profile: {ex.Message}");
            }
            finally
            {
                lock (_gate)
                {
                    _generating = false;
                    _lastProfileSearch = -100000; // pick the new profile up on the next tick
                    _standbySent = true;
                }
            }
        });
        return true;
    }

    private void SetStandby()
    {
        bool send;
        lock (_gate)
        {
            send = !_standbySent || _profile != null;
            _profile = null;
            _exportActive = false;
            _localValues.Clear();
            _standbySent = true;
        }
        if (send) _bridge.SendToExport("STANDBY");
    }

    private void SendProfile(AircraftProfile profile, string aircraft)
    {
        lock (_gate)
        {
            _profileSentAt = Now;
            _standbySent = false;
        }
        var entries = profile.Args.Select(kv => $"{kv.Key}:{kv.Value.DeviceId}:{kv.Value.CommandId}").ToList();
        var parts = new List<string>();
        var sb = new System.Text.StringBuilder();
        foreach (var e in entries)
        {
            if (sb.Length > 0 && sb.Length + e.Length + 1 > 3000)
            {
                parts.Add(sb.ToString());
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append(';');
            sb.Append(e);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());

        var safeName = aircraft.Replace("|", "");
        _bridge.SendToExport($"PROFILE_BEGIN|{safeName}|{parts.Count}");
        for (var i = 0; i < parts.Count; i++)
            _bridge.SendToExport($"PROFILE_PART|{safeName}|{i}|{parts[i]}");
        _bridge.SendToExport($"PROFILE_END|{safeName}");
        SendFlightArgs(profile);
    }

    // ----------------------------------------------------------- periodic

    private long _nextHeartbeat;

    private void Tick()
    {
        try
        {
            var now = Now;
            bool exportTimedOut;
            lock (_gate)
            {
                exportTimedOut = _exportActive && now - _exportLastSeen >= ExportAliveMs;
                if (exportTimedOut) _exportActive = false;
            }
            if (exportTimedOut) Log?.Invoke("UCS-Export.lua not responding. Standby.");

            EvaluateProfile();

            if (now >= _nextHeartbeat)
            {
                _nextHeartbeat = now + HeartbeatMs;
                SendState();
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("Internal error: " + ex.Message);
        }
    }

    /// <summary>DCS keeps the scripts it loaded at startup: warn when they do not match this client.</summary>
    private void CheckScriptVersion(string script, string version)
    {
        if (version.Length == 0 || version == UCS.Common.Protocol.Version) return;
        lock (_gate)
        {
            if (_scriptVersionWarned) return;
            _scriptVersionWarned = true;
        }
        Log?.Invoke($"WARNING: DCS is running {script} {version} but this client is {UCS.Common.Protocol.Version}. " +
                    "Restart DCS to load the updated scripts (if it persists, run the installer again).");
    }

    public void Dispose()
    {
        _timer.Dispose();
        DisconnectInternal(sendLeave: true, log: false);
        _bridge.Dispose();
    }
}
