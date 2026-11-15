using System.Globalization;
using System.Text.RegularExpressions;
using UCS.Common;

namespace UCS.ProfileCore;

/// <summary>
/// Finds the cockpit animation arguments of the flight controls (cyclic / stick, pedals,
/// collective, throttle, trim) in a mod's mainpanel_init.lua. These are not clickable: the
/// mod animates them from its flight model ("parameter" gauges or controllers), so they are
/// mirrored on a separate channel. Only numbers are kept, no text from the mod.
///
/// Recognised patterns:
///   X = CreateGauge("parameter") ... X.arg_number = 214 ... X.parameter_name = "CYCLIC_PITCH_COCKPIT"
///   X.controller = controllers.base_gauge_StickPitchPosition
///   helper(214, "PITCH_INPUT", {-1, 1}, {-1, 1})          (UH-60L style helper functions)
/// </summary>
public static partial class FlightGaugeParser
{
    [GeneratedRegex(@"^\s*([A-Za-z_][\w\.\[\]""']*?)\s*\.\s*(arg_number|parameter_name|controller|input|output)\s*=\s*(.+?)\s*(?:--.*)?$", RegexOptions.Multiline)]
    private static partial Regex FieldRegex();

    [GeneratedRegex(@"^\s*(?:local\s+)?([A-Za-z_]\w*)?\s*=?\s*[A-Za-z_]\w*\s*\(\s*(\d+)\s*,\s*[""']([A-Za-z_][\w]*)[""']\s*(?:,\s*(\{[^}]*\}))?\s*(?:,\s*(\{[^}]*\}))?", RegexOptions.Multiline)]
    private static partial Regex HelperCallRegex();

    [GeneratedRegex(@"-?\d+(?:\.\d+)?")]
    private static partial Regex NumberRegex();

    public static List<FlightArg> Parse(string scriptsDir, List<string>? warnings = null)
    {
        var path = Path.Combine(scriptsDir, "mainpanel_init.lua");
        var result = new SortedDictionary<int, FlightArg>();
        if (!File.Exists(path))
        {
            warnings?.Add("mainpanel_init.lua not found: flight controls cannot be mirrored for this aircraft.");
            return new();
        }
        var text = File.ReadAllText(path);
        // Drop block comments --[[ ... ]] so disabled code is ignored
        text = Regex.Replace(text, @"--\[(=*)\[.*?\]\1\]", "", RegexOptions.Singleline);
        // Drop commented-out lines
        text = Regex.Replace(text, @"^\s*--.*$", "", RegexOptions.Multiline);

        // 1) gauge objects configured field by field
        var gauges = new Dictionary<string, (int? Arg, string Param, string Controller, double[]? Out)>(StringComparer.Ordinal);
        foreach (Match m in FieldRegex().Matches(text))
        {
            var name = m.Groups[1].Value;
            var field = m.Groups[2].Value;
            var value = m.Groups[3].Value.Trim().TrimEnd(';');
            gauges.TryGetValue(name, out var g);
            switch (field)
            {
                case "arg_number":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)) g.Arg = a;
                    break;
                case "parameter_name":
                    g.Param = value.Trim('"', '\'');
                    break;
                case "controller":
                    g.Controller = value;
                    break;
                case "output":
                    g.Out = Range(value);
                    break;
            }
            g.Param ??= "";
            g.Controller ??= "";
            gauges[name] = g;
        }
        foreach (var (name, g) in gauges)
        {
            if (g.Arg is not int arg) continue;
            Add(result, arg, name + " " + g.Param + " " + g.Controller, g.Out);
        }

        // 2) helper calls: f(arg, "PARAM", {in}, {out})
        foreach (Match m in HelperCallRegex().Matches(text))
        {
            if (!int.TryParse(m.Groups[2].Value, out var arg)) continue;
            var outRange = m.Groups[5].Success ? Range(m.Groups[5].Value) : m.Groups[4].Success ? Range(m.Groups[4].Value) : null;
            Add(result, arg, m.Groups[1].Value + " " + m.Groups[3].Value, outRange);
        }
        return result.Values.ToList();
    }

    private static double[]? Range(string lua)
    {
        var nums = NumberRegex().Matches(lua).Select(n => double.Parse(n.Value, CultureInfo.InvariantCulture)).ToArray();
        return nums.Length >= 2 ? new[] { nums[0], nums[1] } : null;
    }

    private static void Add(SortedDictionary<int, FlightArg> result, int arg, string hint, double[]? output)
    {
        var axis = Classify(hint);
        if (axis == null || arg < 0) return;
        if (result.TryGetValue(arg, out var existing) && existing.Axis != FlightAxes.Other) return;
        result[arg] = new FlightArg
        {
            Arg = arg,
            Axis = axis,
            Min = output?[0] ?? (axis is FlightAxes.Collective or FlightAxes.Throttle ? 0 : -1),
            Max = output?[1] ?? 1,
        };
    }

    [GeneratedRegex(@"cyclic|stick|yoke|control_?column|pedal|rudder|collective|throttle|thrust|trim|pitch_?input|roll_?input|yaw_?input", RegexOptions.IgnoreCase)]
    private static partial Regex ControlRegex();

    // Things that contain a control word but are instruments / lights / switches
    [GeneratedRegex(@"light|lamp|indicat|needle|warn|caution|lock|switch|cover|button|btn|knob|guard|sight|hud|display|cap_|cmd|command_?bar|vsi|adi|fail|hide|visib|flag", RegexOptions.IgnoreCase)]
    private static partial Regex ExcludeRegex();

    /// <summary>Axis of a gauge from its variable / parameter / controller names, or null when it is not a flight control.</summary>
    public static string? Classify(string hint)
    {
        if (!ControlRegex().IsMatch(hint) || ExcludeRegex().IsMatch(hint)) return null;
        var h = hint.ToLowerInvariant();
        bool Has(params string[] words) => words.Any(h.Contains);
        if (Has("trim", "brake")) return FlightAxes.Other;
        if (Has("pitch_input", "pitchinput", "stickpitch", "stick_pitch", "cyclic_pitch", "cyclicpitch", "pitch_cyclic", "pitchcyclic",
                "yoke_pitch", "column")) return FlightAxes.Pitch;
        if (Has("roll_input", "rollinput", "stickroll", "stick_roll", "cyclic_roll", "cyclicroll", "roll_cyclic", "rollcyclic", "yoke_roll"))
            return FlightAxes.Roll;
        if (Has("pedal", "rudder", "yaw_input", "yawinput")) return FlightAxes.Yaw;
        if (Has("collective"))
            return Has("throttle", "idle", "twist", "grip", "switch") ? FlightAxes.Other : FlightAxes.Collective;
        if (Has("throttle", "thrust")) return FlightAxes.Throttle;
        return FlightAxes.Other; // e.g. a stick without a recognisable axis
    }
}
