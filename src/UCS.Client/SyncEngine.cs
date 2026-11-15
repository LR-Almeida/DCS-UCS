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
    IReadOnlyList<string> Peers,
    IReadOnlyList<PeerInfo> Incompatible,
    string ModVersion,
    string ProfileHash,
    bool GeneratingProfile,
    long SentArgs,
    long ReceivedArgs);

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
    private long _seq;
    private long _sentArgs, _receivedArgs;
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
        Log?.Invoke($"UCS-Client {Protocol.Version} iniciado. Aguardando DCS (UDP 5051/5053)...");
        if (_cfg.ConnectOnStartup) _ = ConnectAsync();
    }

    public void UpdateConfig(ClientConfig cfg)
    {
        bool roomChanged;
        lock (_gate)
        {
            roomChanged = _cfg.ManualRoom != cfg.ManualRoom || _cfg.PilotName != cfg.PilotName;
            _cfg = cfg.Clone();
        }
        if (roomChanged) SendState();
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
                Peers: _peers.ToList(),
                Incompatible: _incompatible.ToList(),
                ModVersion: _modVersion,
                ProfileHash: _profileHash,
                GeneratingProfile: _generating,
                SentArgs: _sentArgs,
                ReceivedArgs: _receivedArgs);
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
            Log?.Invoke("Informe o IP/host do servidor UCS.");
            return false;
        }
        Log?.Invoke($"Conectando a {host}:{port}...");
        var ep = await UdpHelper.ResolveAsync(host.Trim(), port).ConfigureAwait(false);
        if (ep == null)
        {
            Log?.Invoke($"Não foi possível resolver '{host}'.");
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
        if (log && wasConnected) Log?.Invoke("Desconectado do servidor UCS.");
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
                Name = _cfg.PilotName,
                Version = Protocol.Version,
                Server = _hookServer,
                Group = _hookGroup,
                Unit = _hookUnit,
                Room = string.IsNullOrWhiteSpace(_cfg.ManualRoom) ? null : _cfg.ManualRoom.Trim(),
                Aircraft = _profile != null ? _profileAircraft : EffectiveAircraft,
                Standby = IsStandby,
                ModVersion = _profile != null ? _modVersion : null,
                ProfileHash = _profile != null ? _profileHash : null,
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
                Log?.Invoke("Erro ao processar pacote do servidor: " + ex.Message);
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
                List<string> newPeers = msg.PeerNames ?? new List<string>();
                string joinedNames = "";
                lock (_gate)
                {
                    first = Now - _lastWelcome >= WelcomeTimeoutMs;
                    _lastWelcome = Now;
                    _sessionKey = msg.SessionKey ?? "";
                    var added = newPeers.Except(_peers).ToList();
                    var removed = _peers.Except(newPeers).ToList();
                    if (added.Count > 0) joinedNames += "Entrou na cabine: " + string.Join(", ", added) + ". ";
                    if (removed.Count > 0) joinedNames += "Saiu da cabine: " + string.Join(", ", removed) + ".";
                    _peers = newPeers;

                    var inc = msg.Incompatible ?? new List<PeerInfo>();
                    var oldKeys = _incompatible.Select(i => $"{i.Name}|{i.ModVersion}|{i.ProfileHash}").ToHashSet();
                    foreach (var i in inc.Where(i => !oldKeys.Contains($"{i.Name}|{i.ModVersion}|{i.ProfileHash}")))
                        incompatibleMsgs.Add(DescribeIncompatibility(i, _modVersion, _profileHash));
                    _incompatible = inc;
                }
                if (first) Log?.Invoke("Conectado ao servidor UCS.");
                if (joinedNames.Length > 0) Log?.Invoke(joinedNames.Trim());
                foreach (var m in incompatibleMsgs) Log?.Invoke("ERRO DE COMPATIBILIDADE: " + m);
                break;
            }
            case Protocol.Error:
                Log?.Invoke("Servidor: " + msg.Message);
                break;
            case Protocol.PeerJoined:
                if (!string.IsNullOrEmpty(msg.ClientId)) SendSnapshotTo(msg.ClientId, msg.Name);
                break;
            case Protocol.Args:
                ApplyRemoteArgs(msg);
                break;
        }
    }

    /// <summary>Human readable reason why a crew member cannot be synced.</summary>
    public static string DescribeIncompatibility(PeerInfo peer, string myVersion, string myHash)
    {
        static string V(string v) => v.Length > 0 ? v : "sem versão";
        if (peer.ModVersion != myVersion)
            return $"{peer.Name} usa o mod versão {V(peer.ModVersion)} e você {V(myVersion)}. Atualizem para a mesma versão.";
        return $"{peer.Name} tem o mesmo mod ({V(myVersion)}) mas um perfil diferente ({peer.ProfileHash} x {myHash}). " +
               @"Apaguem o perfil desta aeronave em %APPDATA%\DCS-UCS\profiles para ele ser gerado de novo.";
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
        Log?.Invoke($"Enviando estado da cabine para {name ?? "novo tripulante"} ({values.Count} args).");
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
                Log?.Invoke($"UCS-Export.lua conectado (versão {(parts.Length > 1 ? parts[1] : "?")}).");
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
                if (changed) Log?.Invoke(name.Length > 0 ? $"DCS: aeronave '{name}'." : "DCS: fora de aeronave / menu.");
                if (activated)
                {
                    // Ask for a full snapshot so we know the current cockpit state
                    _bridge.SendToExport("SNAPSHOT");
                    Log?.Invoke($"Sincronização ATIVA ({name}).");
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

            case "BYE":
                lock (_gate)
                {
                    _exportActive = false;
                    _exportAircraft = "";
                    _exportLastSeen = -100000;
                    _localValues.Clear();
                }
                Log?.Invoke("UCS-Export.lua encerrado (fim da missão).");
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
        bool changed;
        lock (_gate)
        {
            changed = server != _hookServer || group != _hookGroup || unit != _hookUnit || aircraft != _hookAircraft;
            _hookServer = server;
            _hookGroup = group;
            _hookUnit = unit;
            _hookAircraft = aircraft;
        }
        if (changed)
        {
            Log?.Invoke(unit.Length > 0
                ? $"Slot: {server} | {group} | {unit} ({aircraft})"
                : "Slot: nenhum (espectador / menu).");
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
                Log?.Invoke($"Auto-Connect recebido ({addr}) mas está desativado nas opções.");
                return;
            }
            if (!UdpHelper.TryParseAddress(addr, Protocol.DefaultServerPort, out var host, out var port))
            {
                Log?.Invoke($"Auto-Connect: endereço inválido '{addr}'.");
                return;
            }
            var same = string.Equals(host, _cfg.ServerHost, StringComparison.OrdinalIgnoreCase) && port == _cfg.ServerPort;
            if (same && _connectRequested) return;
            _cfg.ServerHost = host;
            _cfg.ServerPort = port;
            snapshot = _cfg.Clone();
        }
        Log?.Invoke($"Auto-Connect do servidor DCS: {snapshot.ServerHost}:{snapshot.ServerPort}");
        try { snapshot.Save(); } catch (Exception ex) { Log?.Invoke("Não foi possível salvar a configuração: " + ex.Message); }
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
            if (current != null) Log?.Invoke("Standby: fora de aeronave.");
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
                    ? $"Perfil inválido para '{aircraft}': {_profiles.LastError}. Standby."
                    : mod == null
                        ? $"Sem perfil para '{aircraft}' (módulo oficial ou mod não encontrado em Saved Games). Standby."
                        : $"Sem perfil para '{aircraft}'. Standby.");
            }
            SetStandby();
            lock (_gate) _profileAircraft = aircraft;
            if (autoGen && mod != null) StartAutoGenerate(mod, "primeira vez nesta aeronave");
            return;
        }

        // Profile generated for another version of the mod: regenerate it (mod updated)
        if (mod != null && !string.Equals(mod.Version, profile.ModVersion, StringComparison.Ordinal))
        {
            if (autoGen && StartAutoGenerate(mod, $"mod atualizado: perfil {Fmt(profile.ModVersion)} → instalado {Fmt(mod.Version)}"))
            {
                SetStandby();
                lock (_gate) _profileAircraft = aircraft;
                return;
            }
            if (!autoGen)
                Log?.Invoke($"AVISO: perfil de '{aircraft}' foi gerado para o mod {Fmt(profile.ModVersion)}, mas o instalado é " +
                            $"{Fmt(mod.Version)}. Gere-o novamente.");
        }

        lock (_gate)
        {
            _profile = profile;
            _profileAircraft = aircraft;
            _modVersion = mod?.Version ?? profile.ModVersion;
            _profileHash = profile.Fingerprint();
            _localValues.Clear();
        }
        Log?.Invoke($"Perfil carregado: {profile.AircraftName} ({profile.Args.Count} argumentos, mod {Fmt(_modVersion)}, perfil {_profileHash}).");
        if (exportAlive) SendProfile(profile, aircraft);
        SendState();
    }

    private static string Fmt(string version) => version.Length > 0 ? version : "sem versão";

    /// <summary>
    /// Generates the profile in the background from the installed mod (local only, neutral labels).
    /// Returns false when a generation for this mod version was already attempted.
    /// </summary>
    private bool StartAutoGenerate(ModInfo mod, string reason)
    {
        var key = $"{mod.Aircraft}|{mod.Version}";
        lock (_gate)
        {
            if (_generating || !_autoGenTried.Add(key)) return false;
            _generating = true;
        }
        Log?.Invoke($"Gerando perfil de '{mod.Aircraft}' a partir do mod {Path.GetFileName(mod.ModDir)} ({reason})...");
        _ = Task.Run(() =>
        {
            try
            {
                if (mod.ClickablePath == null)
                {
                    Log?.Invoke($"O mod {Path.GetFileName(mod.ModDir)} não tem clickabledata.lua legível. Standby.");
                    return;
                }
                var result = ProfileBuilder.Generate(mod.ModDir, mod.Aircraft, new ProfileGenOptions { ClickablePath = mod.ClickablePath });
                if (result.Profile.Args.Count == 0)
                {
                    Log?.Invoke($"Não foi possível gerar o perfil de '{mod.Aircraft}' (nenhum controle encontrado).");
                    return;
                }
                var dir = Path.Combine(AppPaths.UserDir, "profiles");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, ProfileBuilder.FileNameFor(mod.Aircraft));
                result.Profile.Save(path);
                Log?.Invoke($"Perfil gerado: {result.Profile.Args.Count} controles, mod {Fmt(result.Profile.ModVersion)} ({result.Method}).");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Falha ao gerar o perfil de '{mod.Aircraft}': {ex.Message}");
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
            if (exportTimedOut) Log?.Invoke("UCS-Export.lua sem resposta. Standby.");

            EvaluateProfile();

            if (now >= _nextHeartbeat)
            {
                _nextHeartbeat = now + HeartbeatMs;
                SendState();
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("Erro interno: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        DisconnectInternal(sendLeave: true, log: false);
        _bridge.Dispose();
    }
}
