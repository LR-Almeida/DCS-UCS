using System.Text.Json;
using System.Text.Json.Serialization;

namespace UCS.Common;

/// <summary>
/// Network protocol between UCS-Client.exe and UCS-Server.exe (JSON over UDP).
/// A single envelope type keeps the wire format simple and forward compatible.
/// </summary>
public static class Protocol
{
    public const string Version = "0.1.4-alpha";
    public const int DefaultServerPort = 5055;
    public const int ExportSendPort = 5051;   // Export.lua -> Client
    public const int ExportListenPort = 5052; // Client -> Export.lua
    public const int HookPort = 5053;         // GameGUI hook -> Client
    public const int MaxDatagram = 60000;

    /// <summary>
    /// Wire protocol level. 1 = 0.1.0-alpha (no field sent). Raised when messages gain fields;
    /// old peers simply ignore unknown JSON fields, so newer clients keep working with old servers.
    /// 3 = 0.1.2-alpha ("flight" messages).
    /// </summary>
    public const int Level = 3;

    /// <summary>
    /// Format of "args" values. Only a change here breaks sync between crew members, so the
    /// server refuses to pair clients with different formats. Missing field = 1.
    /// </summary>
    public const int ArgsFormat = 1;

    /// <summary>Optional server features, announced in "welcome.features" (level 2+).</summary>
    public static class Features
    {
        /// <summary>Server compares mod version + profile fingerprint (0.1.0-alpha already does it).</summary>
        public const string ModCheck = "mod_check";
        /// <summary>Server reports each crew member's UCS version and blocks incompatible args formats.</summary>
        public const string UcsVersionCheck = "ucs_version_check";
        /// <summary>Server relays "flight" messages (stick / pedals / collective positions).</summary>
        public const string FlightMirror = "flight_mirror";

        /// <summary>Feature -> (minimum server version, what the user loses without it).</summary>
        public static readonly IReadOnlyDictionary<string, (string MinServer, string Description)> Known =
            new Dictionary<string, (string, string)>
            {
                [ModCheck] = ("0.1.0-alpha", "mod version / profile compatibility check"),
                [UcsVersionCheck] = ("0.1.1-alpha", "UCS version check between crew members (warning when a crew member runs another UCS version)"),
                [FlightMirror] = ("0.1.2-alpha", "flight controls mirror (stick, pedals and collective positions of the crew member flying)"),
            };

        /// <summary>Features of a server that does not announce them (0.1.0-alpha).</summary>
        public static readonly string[] Level1 = { ModCheck };
    }

    // Message types
    public const string State = "state";            // C->S join + heartbeat
    public const string Args = "args";              // C->S->C argument changes
    public const string Flight = "flight";          // C->S->C flight-control positions (level 3+, ~30 Hz)
    public const string Leave = "leave";            // C->S
    public const string Welcome = "welcome";        // S->C heartbeat ack + session info
    public const string PeerJoined = "peer_joined"; // S->C ask for a snapshot for the new peer
    public const string Error = "error";            // S->C

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static byte[] Serialize(UcsMessage msg) => JsonSerializer.SerializeToUtf8Bytes(msg, Json);

    public static UcsMessage? Deserialize(ReadOnlySpan<byte> data)
    {
        try
        {
            return JsonSerializer.Deserialize<UcsMessage>(data, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class UcsMessage
{
    public string Type { get; set; } = "";
    public string? ClientId { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }

    // Session identification (state)
    public string? Server { get; set; }
    public string? Group { get; set; }
    public string? Unit { get; set; }
    public string? Room { get; set; }
    public string? Aircraft { get; set; }
    public bool? Standby { get; set; }
    /// <summary>Mod version from entry.lua ("" when the mod does not declare one).</summary>
    public string? ModVersion { get; set; }
    /// <summary>AircraftProfile.Fingerprint() of the loaded profile.</summary>
    public string? ProfileHash { get; set; }
    /// <summary>Sender protocol level (missing = 1).</summary>
    public int? Protocol { get; set; }
    /// <summary>Sender args format (missing = 1).</summary>
    public int? ArgsFormat { get; set; }
    /// <summary>Optional features supported by the server (welcome, level 2+).</summary>
    public List<string>? Features { get; set; }

    // Argument sync (args)
    public Dictionary<string, double>? Values { get; set; }
    public string? Target { get; set; }
    public string? From { get; set; }
    public long? Seq { get; set; }

    // Server answers (welcome / error)
    public string? SessionKey { get; set; }
    public int? Peers { get; set; }
    public List<string>? PeerNames { get; set; }
    /// <summary>Crew members in the same cockpit that cannot sync (different mod version / profile).</summary>
    public List<PeerInfo>? Incompatible { get; set; }
    /// <summary>All synced crew members with their UCS versions (level 2+).</summary>
    public List<PeerInfo>? Crew { get; set; }
    public string? Message { get; set; }
}

public sealed class PeerInfo
{
    public string Name { get; set; } = "";
    public string ModVersion { get; set; } = "";
    public string ProfileHash { get; set; } = "";
    /// <summary>UCS client version of that crew member ("" when unknown / old client).</summary>
    public string UcsVersion { get; set; } = "";
    public int ArgsFormat { get; set; } = 1;
}
