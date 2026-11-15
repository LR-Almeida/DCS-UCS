using System.Text.RegularExpressions;

namespace UCS.ProfileGen;

/// <summary>
/// Best-effort text parser used when the Lua evaluation fails. Understands the common
/// patterns:
///   devices["NAME"] = counter()   /  devices.NAME = 12
///   device_commands = { Button_1 = counter(); Button_2 = start_command + 2, ... }
///   elements["KEY"] = helper(_("Hint"), devices.NAME, device_commands.Button_1, 123, ...)
/// </summary>
internal static partial class RegexParser
{
    [GeneratedRegex(@"devices\s*(?:\[\s*[""'](?<name>\w+)[""']\s*\]|\.(?<name>\w+))\s*=\s*(?<value>counter\s*\(\s*\)|\d+)")]
    private static partial Regex DeviceRegex();

    [GeneratedRegex(@"(?<name>\w+)\s*=\s*(?<value>counter\s*\(\s*\)|\d+|start_command\s*\+\s*\d+|\w+\s*\+\s*\d+)\s*[,;]?")]
    private static partial Regex CommandEntryRegex();

    [GeneratedRegex(@"(?:local\s+)?(?:count|__count|start_command|command_start)\s*=\s*(?<base>\d+)")]
    private static partial Regex CounterBaseRegex();

    [GeneratedRegex(
        @"elements\s*\[\s*[""'](?<key>[^""']+)[""']\s*\]\s*=\s*(?<func>[\w\.]+)\s*\(\s*(?:_\(\s*)?[""'](?<hint>[^""']*)[""']\s*\)?\s*,\s*(?<dev>devices\.\w+|\d+)\s*,\s*(?<cmd>(?:device_commands|\w+)\.\w+|\d+)\s*,\s*(?<rest>[^\r\n]*)")]
    private static partial Regex ElementRegex();

    [GeneratedRegex(@"-?\d+(?:\.\d+)?|[\w\.]+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"--\[(?<eq>=*)\[.*?\]\k<eq>\]", RegexOptions.Singleline)]
    private static partial Regex BlockCommentRegex();

    [GeneratedRegex(@"--[^\r\n]*")]
    private static partial Regex LineCommentRegex();

    private static string StripComments(string code) =>
        LineCommentRegex().Replace(BlockCommentRegex().Replace(code, ""), "");

    public static Dictionary<string, int> ParseDevices(string? devicesPath)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (devicesPath == null || !File.Exists(devicesPath)) return map;
        var count = 0;
        foreach (Match m in DeviceRegex().Matches(StripComments(File.ReadAllText(devicesPath))))
        {
            var v = m.Groups["value"].Value;
            int id = v.StartsWith("counter", StringComparison.Ordinal) ? ++count : int.Parse(v);
            if (!v.StartsWith("counter", StringComparison.Ordinal)) count = id;
            map[m.Groups["name"].Value] = id;
        }
        return map;
    }

    /// <summary>Parses "device_commands = { ... }" tables in every Lua file of the folder.</summary>
    public static Dictionary<string, int> ParseCommands(string scriptsDir)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(scriptsDir, "*.lua", SearchOption.TopDirectoryOnly))
        {
            var code = StripComments(File.ReadAllText(file));
            var idx = code.IndexOf("device_commands", StringComparison.Ordinal);
            if (idx < 0) continue;
            var open = code.IndexOf('{', idx);
            if (open < 0) continue;
            var close = MatchBrace(code, open);
            if (close < 0) continue;

            var before = code[..idx];
            var baseMatch = CounterBaseRegex().Matches(before).LastOrDefault();
            var counterBase = baseMatch != null ? int.Parse(baseMatch.Groups["base"].Value) : 3000;
            // "local count = 3000; counter() returns count+1" is the usual idiom
            var count = counterBase;
            var body = code[(open + 1)..close];
            foreach (Match m in CommandEntryRegex().Matches(body))
            {
                var name = m.Groups["name"].Value;
                var v = m.Groups["value"].Value.Replace(" ", "");
                int id;
                if (v.StartsWith("counter", StringComparison.Ordinal)) id = ++count;
                else if (v.Contains('+')) id = counterBase + int.Parse(v[(v.IndexOf('+') + 1)..]);
                else id = int.Parse(v);
                count = Math.Max(count, id);
                map[name] = id;
            }
        }
        return map;
    }

    private static int MatchBrace(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    public static List<ClickableElement> Parse(string clickablePath, string? devicesPath, string scriptsDir, List<string> warnings)
    {
        var devices = ParseDevices(devicesPath);
        var commands = ParseCommands(scriptsDir);
        if (devices.Count == 0) warnings.Add("devices.lua: no devices found by the text parser.");
        if (commands.Count == 0) warnings.Add("device_commands: not found, assuming Button_N = 3000 + N.");

        var list = new List<ClickableElement>();
        var code = StripComments(File.ReadAllText(clickablePath));
        foreach (Match m in ElementRegex().Matches(code))
        {
            var devTok = m.Groups["dev"].Value;
            int? dev = int.TryParse(devTok, out var d) ? d
                : devices.TryGetValue(devTok[(devTok.IndexOf('.') + 1)..], out var dd) ? dd : null;

            var cmdTok = m.Groups["cmd"].Value;
            int? cmd = null;
            if (int.TryParse(cmdTok, out var c)) cmd = c;
            else
            {
                var cmdName = cmdTok[(cmdTok.IndexOf('.') + 1)..];
                if (commands.TryGetValue(cmdName, out var cc)) cmd = cc;
                else if (cmdName.StartsWith("Button_", StringComparison.Ordinal) && int.TryParse(cmdName[7..], out var n)) cmd = 3000 + n;
            }

            // First integer argument after the command(s) is the arg id
            int? arg = null;
            foreach (Match t in TokenRegex().Matches(m.Groups["rest"].Value))
            {
                var tok = t.Value;
                if (int.TryParse(tok, out var a))
                {
                    if (a >= 0 && (a < 3000 || cmd is < 3000)) { arg = a; break; }
                }
                else if (tok.Contains('.') && !double.TryParse(tok, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    continue; // second command (device_commands.Button_2)
                }
                else break;
            }

            if (dev == null || cmd == null || arg == null) continue;
            list.Add(new ClickableElement(m.Groups["key"].Value, m.Groups["hint"].Value, dev.Value, cmd.Value, arg.Value,
                ElementKinds.FromFunctionName(m.Groups["func"].Value)));
        }
        return list;
    }
}
