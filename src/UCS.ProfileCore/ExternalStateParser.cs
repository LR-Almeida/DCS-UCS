using System.Text.RegularExpressions;
using UCS.Common;

namespace UCS.ProfileCore;

/// <summary>
/// Finds the external-model states a mod changes only from the keyboard (doors, landing light, sun visor...):
/// in each cockpit script, the external draw arguments it animates (set_aircraft_draw_argument_value) and the
/// key commands it listens to (dev:listen_command(Keys.X)). Switch-driven states are already synced by the
/// switch channel, so device_commands are not used. Weapons, engine, fuel, cargo and similar scripts and
/// commands are excluded on purpose: replaying them on the other seat would be wrong or dangerous.
/// Only numbers are kept in the profile.
/// </summary>
public static partial class ExternalStateParser
{
    [GeneratedRegex(@"set_aircraft_draw_argument_value\s*\(\s*(\d+)\s*,")]
    private static partial Regex DrawArgRegex();

    [GeneratedRegex(@"listen_command\s*\(\s*(Keys\.\w+)\s*\)")]
    private static partial Regex ListenRegex();

    // Scripts that must never drive key presses on the other seat
    [GeneratedRegex(@"weapon|gun|sight|rocket|missile|rwr|radar|cargo|sling|hoist|kneeboard|radio|comm|nav|fuel|engine|electric|damage|hud|mfd|efm|ffb|fm_|avionic|ins|gps|flir|tads|laser",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExcludedFileRegex();

    // Commands that look like an on/off or open/close state of something visible outside
    [GeneratedRegex(@"door|canopy|hatch|window|visor|light|lamp|beacon|strobe|search|landing|taxi|toggle|open|close|_on\b|_off\b|On$|Off$",
        RegexOptions.IgnoreCase)]
    private static partial Regex StateCommandRegex();

    // Not an on/off state (brightness steps, cycling, movement) or not harmless to replay on the other seat
    [GeneratedRegex(@"throttle|cutoff|engine|start|fire|weapon|gun|rocket|missile|bomb|jettison|eject|fuel|batt|gener|gov|trim|rotor|brake|cargo|release|drop|arm|master|sling|hoist|winch|flare|chaff|smoke|grenade|roe|burst|increase|decrease|slew|axis|mode|lock|zoom|move|gear|flap|spoiler|speedbrake|hook|wing|probe|refuel|pilot|show|stick|cycle|intensity|dim|bright|(inc|dec)(_|$|[A-Z])|_(inc|dec)|(up|down|left|right)$|_(up|down|left|right)",
        RegexOptions.IgnoreCase)]
    private static partial Regex DangerousCommandRegex();

    [GeneratedRegex(@"command\s*==\s*(Keys\.\w+)")]
    private static partial Regex CommandTestRegex();

    [GeneratedRegex(@"(?<![\w\.])([A-Za-z_]\w*)\s*=(?!=)\s*([^\n]*)")]
    private static partial Regex AssignmentRegex();

    [GeneratedRegex(@"[A-Za-z_]\w*")]
    private static partial Regex IdentRegex();

    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "local", "if", "then", "else", "elseif", "end", "and", "or", "not", "nil", "true", "false", "return", "function",
        "math", "min", "max", "abs", "floor", "value", "command", "dev", "self", "get_param_handle", "set", "get",
    };

    private static HashSet<string> Idents(string text) =>
        IdentRegex().Matches(text).Select(m => m.Value).Where(v => !Noise.Contains(v)).ToHashSet(StringComparer.Ordinal);

    public static List<ExternalGroup> Parse(string scriptsDir, List<string>? warnings = null)
    {
        var groups = new List<ExternalGroup>();
        if (!Directory.Exists(scriptsDir)) return groups;
        Dictionary<string, int>? keys = null;
        foreach (var file in Directory.EnumerateFiles(scriptsDir, "*.lua", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(scriptsDir, file);
            if (ExcludedFileRegex().IsMatch(rel)) continue;
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            if (!text.Contains("set_aircraft_draw_argument_value", StringComparison.Ordinal)) continue;
            // ignore commented-out lines
            text = Regex.Replace(text, @"^\s*--.*$", "", RegexOptions.Multiline);

            var args = DrawArgRegex().Matches(text).Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(a => a).ToList();
            var names = ListenRegex().Matches(text).Select(m => m.Groups[1].Value).Distinct()
                .Where(n => StateCommandRegex().IsMatch(n) && !DangerousCommandRegex().IsMatch(n)).ToList();
            if (args.Count == 0 || names.Count == 0) continue;

            // What each command does: the code from "command == Keys.X" to the next command test.
            // Commands that only move a cockpit switch (performClickableAction) are keyboard shortcuts of a
            // switch that the switch channel already syncs: never replay them (field test: acol_light_on).
            var tests = CommandTestRegex().Matches(text).ToList();
            var assigns = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var aliases = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < tests.Count; i++)
            {
                var name = tests[i].Groups[1].Value;
                var end = i + 1 < tests.Count ? tests[i + 1].Index : Math.Min(text.Length, tests[i].Index + 2000);
                var block = text[tests[i].Index..end];
                if (block.Contains("performClickableAction", StringComparison.Ordinal)) { aliases.Add(name); continue; }
                var set = assigns.TryGetValue(name, out var existing) ? existing : assigns[name] = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match m in AssignmentRegex().Matches(block)) if (!Noise.Contains(m.Groups[1].Value)) set.Add(m.Groups[1].Value);
            }
            names = names.Where(n => !aliases.Contains(n)).ToList();
            if (names.Count == 0) continue;

            // Variable dependencies (a = f(b)): the draw argument may use a smoothed copy of the variable
            var deps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (Match m in AssignmentRegex().Matches(text))
            {
                var lhs = m.Groups[1].Value;
                if (Noise.Contains(lhs)) continue;
                if (!deps.TryGetValue(lhs, out var d)) deps[lhs] = d = new HashSet<string>(StringComparer.Ordinal);
                d.UnionWith(Idents(m.Groups[2].Value));
            }

            keys ??= AxisCommandParser.EvaluateCommandDefs(scriptsDir, warnings);
            int Id(string n) => keys.TryGetValue(n, out var id) ? id : -1;
            var commands = names.Select(Id).Where(id => id > 0).Distinct().Take(8).ToList();
            if (commands.Count == 0) continue;

            var prefer = new Dictionary<int, List<int>>();
            foreach (var arg in args)
            {
                var vars = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match m in Regex.Matches(text, @"set_aircraft_draw_argument_value\s*\(\s*" + arg + @"\s*,([^\n]*)"))
                    vars.UnionWith(Idents(m.Groups[1].Value));
                for (var hop = 0; hop < 2; hop++)
                    foreach (var v in vars.ToList())
                        if (deps.TryGetValue(v, out var d)) vars.UnionWith(d);
                var hits = names.Where(n => assigns.TryGetValue(n, out var set) && set.Overlaps(vars)).Select(Id).Where(id => id > 0).Distinct().ToList();
                if (hits.Count > 0) prefer[arg] = hits;
            }
            groups.Add(new ExternalGroup { Args = args, Commands = commands, Prefer = prefer.Count > 0 ? prefer : null });
        }
        return groups;
    }
}
