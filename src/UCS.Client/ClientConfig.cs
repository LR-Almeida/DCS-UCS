using System.Text.Json;
using UCS.Common;

namespace UCS.Client;

public sealed class ClientConfig
{
    public string ServerHost { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = Protocol.DefaultServerPort;
    public bool AcceptAutoConnect { get; set; } = true;
    public string ManualRoom { get; set; } = "";
    public string PilotName { get; set; } = "Pilot";
    /// <summary>Use the player name reported by DCS instead of PilotName.</summary>
    public bool AutoDetectPilotName { get; set; } = true;
    public bool ConnectOnStartup { get; set; } = false;
    /// <summary>Generate missing/outdated profiles locally from the installed mod.</summary>
    public bool AutoGenerateProfiles { get; set; } = true;
    /// <summary>Check GitHub for new UCS versions (at startup and every 6 hours).</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Offer alpha/beta versions too (always on while running a pre-release).</summary>
    public bool IncludePreReleases { get; set; } = true;
    /// <summary>Version the user chose to skip in the update pop-up.</summary>
    public string SkippedVersion { get; set; } = "";

    // ---- Flight controls (0.1.2+, experimental)
    /// <summary>Send this seat's stick / pedals / collective positions to the crew (view only).</summary>
    public bool FlightMirrorSend { get; set; } = true;
    /// <summary>
    /// Feed the received positions into this seat as joystick input, so the mod animates the stick
    /// like the pilot's. Only for the seat NOT flying. Never saved: off at every start for safety.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool FlightMirrorInject { get; set; }
    /// <summary>Record flight-control args every frame to Saved Games\DCS\Logs\UCS\UCS-diag-*.csv and report flickering args.</summary>
    public bool FlightDiagnostics { get; set; } = false;
    /// <summary>
    /// Candidate DCS input commands per axis for the injection, tried in order until the cockpit animation
    /// follows (axis=cmd[:-1 inverted],cmd...;...). 2001-2004 = joystick pitch/roll/rudder/thrust,
    /// 2087 = iCommandPlaneCollective. Verified in game (OH-6A 1.7): pitch 2001 inverted, roll 2002, rudder
    /// 2003, collective 2004 inverted (2087 is ignored by that mod).
    /// </summary>
    public string FlightAxisCommands { get; set; } = DefaultFlightAxisCommands;
    public const string DefaultFlightAxisCommands =
        "pitch=2001:-1,2001:1;roll=2002:1,2002:-1;yaw=2003:1,2003:-1;collective=2087:1,2087:-1,2004:-1,2004:1";
    private const string OldDefaultFlightAxisCommands = "pitch=2001;roll=2002;yaw=2003;collective=2087";
    /// <summary>
    /// Test only (not in the UI): allow the injection in seat 1 too. Seat 1 normally has the controls, and
    /// there the injected input really flies the aircraft (verified in single player).
    /// </summary>
    public bool FlightInjectAnySeat { get; set; } = false;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string FilePath => AppPaths.ConfigFile("client_config.json");

    public static ClientConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(FilePath), Options);
                if (cfg != null)
                {
                    if (cfg.ServerPort is < 1 or > 65535) cfg.ServerPort = Protocol.DefaultServerPort;
                    cfg.ManualRoom ??= "";
                    cfg.PilotName = string.IsNullOrWhiteSpace(cfg.PilotName) ? "Pilot" : cfg.PilotName;
                    if (string.IsNullOrWhiteSpace(cfg.FlightAxisCommands) || cfg.FlightAxisCommands == OldDefaultFlightAxisCommands)
                        cfg.FlightAxisCommands = DefaultFlightAxisCommands;
                    return cfg;
                }
            }
        }
        catch
        {
            // fall through to defaults
        }
        return new ClientConfig();
    }

    public void Save()
    {
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }

    public ClientConfig Clone() => (ClientConfig)MemberwiseClone();
}
