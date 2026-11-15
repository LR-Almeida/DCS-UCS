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
