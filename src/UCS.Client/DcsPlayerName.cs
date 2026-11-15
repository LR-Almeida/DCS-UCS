using System.Text.RegularExpressions;
using UCS.ProfileCore;

namespace UCS.Client;

/// <summary>
/// Best guess of the DCS player name before DCS reports it: the first multiplayer nickname in
/// Saved Games\DCS*\Config\nicknames.lua. The exact name arrives later from UCS-Hook.lua.
/// </summary>
public static partial class DcsPlayerName
{
    [GeneratedRegex(@"\[\s*1\s*\]\s*=\s*""(?<n>(?:[^""\\]|\\.)*)""", RegexOptions.CultureInvariant)]
    private static partial Regex FirstNicknameRegex();

    public static string? FromNicknames()
    {
        try
        {
            var sg = ModLocator.SavedGamesDir();
            foreach (var profile in new[] { "DCS", "DCS.openbeta" })
            {
                var file = Path.Combine(sg, profile, "Config", "nicknames.lua");
                if (!File.Exists(file)) continue;
                var m = FirstNicknameRegex().Match(File.ReadAllText(file));
                if (!m.Success) continue;
                var name = Regex.Unescape(m.Groups["n"].Value).Trim();
                if (name.Length > 0) return name.Length > 32 ? name[..32] : name;
            }
        }
        catch
        {
            // no guess
        }
        return null;
    }
}
