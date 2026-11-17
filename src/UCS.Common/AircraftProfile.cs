using System.Text.Json;
using System.Text.Json.Serialization;

namespace UCS.Common;

/// <summary>
/// profiles/&lt;aircraft_name&gt;.json — maps cockpit argument ids to the clickable
/// device/command used to reproduce them on the other cockpit.
/// </summary>
public sealed class AircraftProfile
{
    public string AircraftName { get; set; } = "";
    /// <summary>Version declared by the mod's entry.lua when the profile was generated ("" if unknown).</summary>
    public string ModVersion { get; set; } = "";
    public string? Description { get; set; }
    public string? GeneratedBy { get; set; }
    public string? GeneratedAt { get; set; }
    public string? Source { get; set; }
    public SortedDictionary<int, ProfileArg> Args { get; set; } = new();
    /// <summary>
    /// Profile layout. 1 = 0.1.x switches only (missing field). 2 = also lists the flight-control
    /// animation arguments (<see cref="Flight"/>). Older generated profiles are regenerated.
    /// </summary>
    public int Schema { get; set; } = 1;
    /// <summary>3 = trim commands (<see cref="Trim"/>); 4 = axis commands from the mod's bindings;
    /// 5 = external states (<see cref="External"/>); 6 = external states with per-arg commands;
    /// 7 = cargo doors no longer excluded.</summary>
    public const int CurrentSchema = 7;
    /// <summary>Flight-control animation args (stick, pedals, collective...), mirrored from whoever flies.</summary>
    public List<FlightArg>? Flight { get; set; }
    /// <summary>The mod's cyclic trim commands (null when the mod declares none).</summary>
    public TrimCommands? Trim { get; set; }
    /// <summary>
    /// Input command ids the mod binds to each flight axis (pitch, roll, yaw, collective, throttle), from its
    /// Input/joystick/default.lua. First candidates of the flight mirror calibration.
    /// </summary>
    public Dictionary<string, List<int>>? FlightCommands { get; set; }
    /// <summary>External-model states the mod changes only from the keyboard (doors, landing light...).</summary>
    public List<ExternalGroup>? External { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AircraftProfile Load(string path)
    {
        var profile = JsonSerializer.Deserialize<AircraftProfile>(File.ReadAllText(path), Json)
                      ?? throw new InvalidDataException("Empty profile");
        if (string.IsNullOrWhiteSpace(profile.AircraftName))
            profile.AircraftName = Path.GetFileNameWithoutExtension(path);
        return profile;
    }

    /// <summary>
    /// Short hash of the arg → device/command mapping. Two crew members can only sync when
    /// their fingerprints match (same mapping = same mod cockpit). Descriptions are ignored.
    /// </summary>
    public string Fingerprint()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (arg, a) in Args) sb.Append(arg).Append(':').Append(a.DeviceId).Append(':').Append(a.CommandId).Append(';');
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}

public sealed class ProfileArg
{
    public int DeviceId { get; set; }
    public int CommandId { get; set; }
    public string Description { get; set; } = "";
    /// <summary>button, toggle, multiposition, axis, unknown (informative only).</summary>
    public string? Type { get; set; }
}

/// <summary>
/// Trim commands of a mod (EFM command ids from command_defs.lua). Used to hand the trim over with
/// the controls: the seat that loses the controls resets its trim, the seat that takes them captures
/// the mirrored stick position as its trim (verified on the OH-6A).
/// </summary>
public sealed class TrimCommands
{
    /// <summary>"Trim release" pressed (force trim: trim follows the stick while held).</summary>
    public int? Release { get; set; }
    /// <summary>"Trim release" released (trim stored at the current stick position).</summary>
    public int? ReleaseEnd { get; set; }
    /// <summary>Single "trim set" command (alternative to Release/ReleaseEnd).</summary>
    public int? Set { get; set; }
    public int? Reset { get; set; }

    public bool CanHandOver => Reset != null && ((Release != null && ReleaseEnd != null) || Set != null);
}

/// <summary>
/// A mod script that animates external draw arguments and listens to key commands: when a crew member's
/// settled value of one of <see cref="Args"/> differs, the other seat presses the mod's own command (learning
/// which one of <see cref="Commands"/> changes that argument).
/// </summary>
public sealed class ExternalGroup
{
    public List<int> Args { get; set; } = new();
    public List<int> Commands { get; set; } = new();
    /// <summary>
    /// Per argument, the commands that (from the script itself) change the variable animating it, e.g.
    /// arg 115 = taxi_light, set by Keys.LandingLight. Tried first; the other commands only as a fallback.
    /// </summary>
    public Dictionary<int, List<int>>? Prefer { get; set; }
}

/// <summary>Axis names used in <see cref="FlightArg.Axis"/>.</summary>
public static class FlightAxes
{
    public const string Pitch = "pitch";
    public const string Roll = "roll";
    public const string Yaw = "yaw";
    public const string Collective = "collective";
    public const string Throttle = "throttle";
    /// <summary>Trim indicators, collective throttle twist, idle stop... (display only).</summary>
    public const string Other = "other";
}

/// <summary>A cockpit animation argument driven by the flight model (not clickable).</summary>
public sealed class FlightArg
{
    public int Arg { get; set; }
    /// <summary>pitch, roll, yaw, collective, throttle, other.</summary>
    public string Axis { get; set; } = FlightAxes.Other;
    /// <summary>Range of the argument value (gauge output), used to convert to a -1..1 input axis.</summary>
    public double Min { get; set; } = -1;
    public double Max { get; set; } = 1;
}
