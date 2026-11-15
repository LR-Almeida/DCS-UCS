using System.Text.Json;
using System.Text.Json.Serialization;

namespace UCS.Server;

public sealed class ServerConfig
{
    /// <summary>IP to bind (0.0.0.0 = every interface).</summary>
    public string BindAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 5055;
    /// <summary>Clients without heartbeat for this long are dropped.</summary>
    public int TimeoutSeconds { get; set; } = 15;
    /// <summary>"Unit" = {DcsServer}|{Group}|{Unit} (default). "Group" = {DcsServer}|{Group}
    /// (useful for mods without native multicrew, where each crew member uses a different unit
    /// of the same group).</summary>
    public string SessionMatch { get; set; } = "Unit";
    public int MaxClients { get; set; } = 256;
    /// <summary>Per-client flood protection.</summary>
    public int MaxPacketsPerSecond { get; set; } = 200;
    public int StatusIntervalSeconds { get; set; } = 60;
    public bool Verbose { get; set; } = false;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static ServerConfig LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), Options);
                if (cfg != null) return cfg.Validate();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Invalid {Path.GetFileName(path)} ({ex.Message}). Using defaults.");
                return new ServerConfig();
            }
        }
        var def = new ServerConfig();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(def, Options));
            Console.WriteLine($"[INFO] Created default config: {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Could not write {path}: {ex.Message}");
        }
        return def;
    }

    private ServerConfig Validate()
    {
        if (Port is < 1 or > 65535) Port = 5055;
        if (TimeoutSeconds < 3) TimeoutSeconds = 15;
        if (MaxClients < 2) MaxClients = 256;
        if (MaxPacketsPerSecond < 10) MaxPacketsPerSecond = 200;
        if (StatusIntervalSeconds < 5) StatusIntervalSeconds = 60;
        if (!SessionMatch.Equals("Group", StringComparison.OrdinalIgnoreCase)) SessionMatch = "Unit";
        return this;
    }
}
