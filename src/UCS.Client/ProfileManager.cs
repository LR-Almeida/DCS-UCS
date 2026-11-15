using UCS.Common;

namespace UCS.Client;

/// <summary>Finds and caches profiles/&lt;aircraft_name&gt;.json files.</summary>
public sealed class ProfileManager
{
    private readonly Dictionary<string, (DateTime stamp, AircraftProfile profile)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string? LastError { get; private set; }

    public static string SanitizeFileName(string aircraft)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(aircraft.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    public AircraftProfile? Find(string aircraft)
    {
        LastError = null;
        if (string.IsNullOrWhiteSpace(aircraft)) return null;
        var fileName = SanitizeFileName(aircraft) + ".json";

        foreach (var dir in AppPaths.ProfileDirs())
        {
            if (!Directory.Exists(dir)) continue;
            var path = Path.Combine(dir, fileName);
            if (!File.Exists(path))
            {
                // Fallback: any profile whose "aircraft_name" matches
                path = Directory.EnumerateFiles(dir, "*.json")
                    .FirstOrDefault(f => string.Equals(TryLoad(f)?.AircraftName, aircraft, StringComparison.OrdinalIgnoreCase)) ?? "";
                if (path.Length == 0) continue;
            }
            var p = TryLoad(path);
            if (p != null && p.Args.Count > 0) return p;
        }
        return null;
    }

    private AircraftProfile? TryLoad(string path)
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (_cache.TryGetValue(path, out var c) && c.stamp == stamp) return c.profile;
            var p = AircraftProfile.Load(path);
            _cache[path] = (stamp, p);
            return p;
        }
        catch (Exception ex)
        {
            LastError = $"{Path.GetFileName(path)}: {ex.Message}";
            return null;
        }
    }
}
