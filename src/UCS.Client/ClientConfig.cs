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
