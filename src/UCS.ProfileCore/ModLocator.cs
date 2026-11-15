using System.Text.RegularExpressions;

namespace UCS.ProfileCore;

/// <summary>An installed community aircraft mod that declares a flyable aircraft type.</summary>
public sealed record ModInfo(string ModDir, string Aircraft, string Version, string? ScriptsDir, string? ClickablePath);

/// <summary>
/// Finds the community mod that provides a given aircraft type by reading the mods'
/// entry.lua (make_flyable('TYPE', current_mod_path..'/Cockpit/Scripts/', ...)).
/// Only Saved Games\DCS*\Mods\aircraft is searched: official ED modules live in the
/// DCS install folder and are never touched.
/// </summary>
public static partial class ModLocator
{
    [GeneratedRegex(@"make_flyable\s*\(\s*(?:(?<q>['""])(?<type>[^'""]+)\k<q>|(?<ident>[A-Za-z_]\w*))\s*,\s*(?<cockpit>[^,]*)", RegexOptions.CultureInvariant)]
    private static partial Regex MakeFlyableRegex();

    [GeneratedRegex(@"(?:current_mod_path\s*\.\.\s*|mod_file\s*\(\s*)(?<q>['""])/?(?<path>[^'""]+)\k<q>", RegexOptions.CultureInvariant)]
    private static partial Regex CockpitPathRegex();

    [GeneratedRegex(@"\bversion\s*=\s*(?<q>['""])(?<v>[^'""]*)\k<q>", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    private static readonly object CacheLock = new();
    private static List<ModInfo>? _cache;
    private static DateTime _cacheTime;

    /// <summary>Saved Games folder (honors a relocated folder via the registry).</summary>
    public static string SavedGamesDir()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
                if (k?.GetValue("{4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4}", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) is string raw)
                {
                    var p = Environment.ExpandEnvironmentVariables(raw);
                    if (Directory.Exists(p)) return p;
                }
            }
            catch
            {
                // fall back below
            }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
    }

    public static IEnumerable<string> ModRoots()
    {
        var sg = SavedGamesDir();
        foreach (var name in new[] { "DCS", "DCS.openbeta" })
        {
            var dir = Path.Combine(sg, name, "Mods", "aircraft");
            if (Directory.Exists(dir)) yield return dir;
        }
    }

    /// <summary>All flyable aircraft declared by installed mods (cached for 30 s).</summary>
    public static IReadOnlyList<ModInfo> ScanAll(bool refresh = false)
    {
        lock (CacheLock)
        {
            if (!refresh && _cache != null && DateTime.UtcNow - _cacheTime < TimeSpan.FromSeconds(30)) return _cache;
            var list = new List<ModInfo>();
            foreach (var root in ModRoots())
            {
                foreach (var modDir in Directory.EnumerateDirectories(root))
                {
                    try { list.AddRange(ReadMod(modDir)); }
                    catch { /* unreadable mod: skip */ }
                }
            }
            _cache = list;
            _cacheTime = DateTime.UtcNow;
            return list;
        }
    }

    public static ModInfo? Find(string aircraft, bool refresh = false) =>
        ScanAll(refresh).FirstOrDefault(m => string.Equals(m.Aircraft, aircraft, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads one mod folder (entry.lua) and returns the aircraft it declares.</summary>
    public static List<ModInfo> ReadMod(string modDir)
    {
        var result = new List<ModInfo>();
        var entry = Path.Combine(modDir, "entry.lua");
        if (!File.Exists(entry)) return result;
        var code = File.ReadAllText(entry);
        var version = VersionRegex().Match(code) is { Success: true } vm ? vm.Groups["v"].Value.Trim() : "";

        foreach (Match m in MakeFlyableRegex().Matches(code))
        {
            var type = m.Groups["type"].Success ? m.Groups["type"].Value : ResolveIdentifier(code, m.Groups["ident"].Value);
            if (string.IsNullOrWhiteSpace(type)) continue;
            if (result.Any(r => string.Equals(r.Aircraft, type, StringComparison.OrdinalIgnoreCase))) continue;

            string? scriptsDir = null, clickable = null;
            var cp = CockpitPathRegex().Match(m.Groups["cockpit"].Value);
            if (cp.Success)
            {
                var candidate = Path.GetFullPath(Path.Combine(modDir, cp.Groups["path"].Value.Replace('/', Path.DirectorySeparatorChar)));
                var file = Path.Combine(candidate, "clickabledata.lua");
                if (File.Exists(file)) { scriptsDir = candidate; clickable = file; }
            }
            if (clickable == null)
            {
                clickable = FindFile(modDir, "clickabledata.lua");
                scriptsDir = clickable != null ? Path.GetDirectoryName(clickable) : null;
            }
            result.Add(new ModInfo(modDir, type.Trim(), version, scriptsDir, clickable));
        }
        return result;
    }

    /// <summary>Version declared in entry.lua of the mod folder ("" when absent).</summary>
    public static string ReadVersion(string modDir)
    {
        var entry = Path.Combine(modDir, "entry.lua");
        if (!File.Exists(entry)) return "";
        return VersionRegex().Match(File.ReadAllText(entry)) is { Success: true } vm ? vm.Groups["v"].Value.Trim() : "";
    }

    private static string? ResolveIdentifier(string code, string ident)
    {
        if (string.IsNullOrEmpty(ident)) return null;
        var m = Regex.Match(code, @"\b" + Regex.Escape(ident) + @"\s*=\s*(?<q>['""])(?<v>[^'""]+)\k<q>");
        return m.Success ? m.Groups["v"].Value : null;
    }

    public static string? FindFile(string root, string name)
    {
        try
        {
            var all = Directory.EnumerateFiles(root, name, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 8,
            }).ToList();
            return all.OrderByDescending(p => p.Contains(Path.Combine("Cockpit", "Scripts"), StringComparison.OrdinalIgnoreCase))
                      .ThenBy(p => p.Length)
                      .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
