namespace UCS.Common;

/// <summary>
/// Choices made in the installer about network connections (install-options.ini next to the
/// executables, written by Setup). A connection turned off there stays off: the settings cannot
/// re-enable it (run the installer again to change it). Missing file = everything allowed.
/// </summary>
public static class InstallOptions
{
    public const string FileName = "install-options.ini";

    private static readonly Lazy<Dictionary<string, string>> Values = new(Read);

    /// <summary>Check GitHub for DCS-UCS updates (client and server).</summary>
    public static bool CheckForUpdatesAllowed => Allowed("CheckForUpdates");

    /// <summary>Look up this PC's public IP (api.ipify.org and fallbacks).</summary>
    public static bool DetectPublicIpAllowed => Allowed("DetectPublicIp");

    private static bool Allowed(string key) =>
        !Values.Value.TryGetValue(key, out var v) ||
        !(v.Equals("0") || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("no", StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, string> Read()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(AppPaths.ExeDir, FileName);
            if (!File.Exists(path)) return result;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';' or '[') continue;
                var eq = line.IndexOf('=');
                if (eq > 0) result[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        catch
        {
            // unreadable file: keep the defaults
        }
        return result;
    }
}
