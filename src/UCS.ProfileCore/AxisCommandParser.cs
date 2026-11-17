using System.Text.RegularExpressions;
using MoonSharp.Interpreter;
using UCS.Common;

namespace UCS.ProfileCore;

/// <summary>
/// Reads, from the mod's own joystick bindings (Input/joystick/default.lua), which input command each
/// flight axis uses: e.g. OH-6A collective = iCommandPlaneThrustCommon (2004), throttle = Keys.throttleAxis;
/// UH-60L collective = iCommandPlaneCollective (2087). These become the first candidates of the flight
/// mirror calibration, so no aircraft needs hand-made settings. Only numbers are kept.
/// </summary>
public static partial class AxisCommandParser
{
    /// <summary>DCS input command ids (read from the DCS input environment, Input.getEnvTable().Actions).</summary>
    private static readonly Dictionary<string, int> DcsCommands = new(StringComparer.Ordinal)
    {
        ["iCommandPlanePitch"] = 2001,
        ["iCommandPlaneRoll"] = 2002,
        ["iCommandPlaneRudder"] = 2003,
        ["iCommandPlaneThrustCommon"] = 2004,
        ["iCommandPlaneThrustLeft"] = 2005,
        ["iCommandPlaneThrustRight"] = 2006,
        ["iCommandPlaneCollective"] = 2087,
    };

    // {combos = defaultDeviceAssignmentFor("roll"), action = iCommandPlaneRoll, name = _('Roll Cyclic')}
    // Axis bindings of a cockpit device (cockpit_device_id = ...) are not input commands and are skipped.
    [GeneratedRegex(@"defaultDeviceAssignmentFor\(\s*[""'](?<kind>\w+)[""']\s*\)[^{}\n]*?action\s*=\s*(?<action>[\w\.]+)(?<rest>[^{}\n]*)", RegexOptions.IgnoreCase)]
    private static partial Regex BindingRegex();

    [GeneratedRegex(@"name\s*=\s*_\(\s*[""'](?<name>[^""']*)[""']")]
    private static partial Regex NameRegex();

    public static Dictionary<string, List<int>> Parse(string modDir, string scriptsDir, string aircraft, List<string>? warnings = null)
    {
        var result = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var bindings = ModLocator.FindFile(modDir, "default.lua", "joystick", aircraft);
        if (bindings == null) return result;
        Dictionary<string, int>? modCommands = null;

        foreach (Match m in BindingRegex().Matches(File.ReadAllText(bindings)))
        {
            if (m.Groups["rest"].Value.Contains("cockpit_device_id", StringComparison.Ordinal)) continue;
            var name = NameRegex().Match(m.Value).Groups["name"].Value.ToLowerInvariant();
            var axis = m.Groups["kind"].Value.ToLowerInvariant() switch
            {
                "pitch" => FlightAxes.Pitch,
                "roll" => FlightAxes.Roll,
                "rudder" => FlightAxes.Yaw,
                "thrust" => name.Contains("collective") ? FlightAxes.Collective : FlightAxes.Throttle,
                "throttle" => name.Contains("collective") ? FlightAxes.Collective : FlightAxes.Throttle,
                "collective" => FlightAxes.Collective,
                _ => null,
            };
            if (axis == null) continue;

            var action = m.Groups["action"].Value;
            int? id = null;
            if (DcsCommands.TryGetValue(action, out var known)) id = known;
            else if (action.Contains('.'))
            {
                modCommands ??= EvaluateCommandDefs(scriptsDir, warnings);
                if (modCommands.TryGetValue(action, out var modId)) id = modId;
            }
            if (id == null) continue;
            if (!result.TryGetValue(axis, out var list)) result[axis] = list = new List<int>();
            if (!list.Contains(id.Value)) list.Add(id.Value);
        }
        return result;
    }

    /// <summary>Runs command_defs.lua in a sandbox and returns "Table.name" -> number (Keys.*, EFM_commands.*...).</summary>
    public static Dictionary<string, int> EvaluateCommandDefs(string scriptsDir, List<string>? warnings)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = Path.Combine(scriptsDir, "command_defs.lua");
        if (!File.Exists(path)) return map;
        try
        {
            var script = new Script(CoreModules.Preset_SoftSandbox);
            script.DoString(File.ReadAllText(path), null, "command_defs.lua");
            foreach (var pair in script.Globals.Pairs)
            {
                if (pair.Key.Type != DataType.String || pair.Value.Type != DataType.Table) continue;
                foreach (var entry in pair.Value.Table.Pairs)
                {
                    if (entry.Key.Type == DataType.String && entry.Value.Type == DataType.Number)
                        map[pair.Key.String + "." + entry.Key.String] = (int)entry.Value.Number;
                }
            }
        }
        catch (Exception ex)
        {
            warnings?.Add("command_defs.lua evaluation failed (axis commands): " + ex.Message);
        }
        return map;
    }
}
