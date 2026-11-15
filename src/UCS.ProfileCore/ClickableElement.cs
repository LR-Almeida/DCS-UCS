using System.Text.RegularExpressions;

namespace UCS.ProfileCore;

/// <summary>A clickable cockpit element extracted from clickabledata.lua.</summary>
public sealed record ClickableElement(string Key, string Hint, int DeviceId, int CommandId, int ArgId, string Kind);

public static partial class ElementKinds
{
    public const string Button = "button";
    public const string Toggle = "toggle";
    public const string Multi = "multiposition";
    public const string Axis = "axis";
    public const string Unknown = "unknown";

    // ED class_type values
    public const int ClassBtn = 1, ClassTumb = 2, ClassSngBtn = 3, ClassLev = 4, ClassMovableLev = 5;

    public static string FromFunctionName(string func)
    {
        var f = func.ToLowerInvariant();
        if (f.Contains("axis") || f.Contains("lever") || f.Contains("wheel") || f.Contains("rotary") || f.Contains("potentiometer"))
            return Axis;
        if (f.Contains("multiposition") || f.Contains("multi_pos") || f.Contains("selector") || f.Contains("3_pos") || f.Contains("3_position"))
            return Multi;
        if (f.Contains("button") || f.Contains("push") || f.Contains("btn"))
            return Button;
        if (f.Contains("tumb") || f.Contains("switch") || f.Contains("toggle") || f.Contains("cover"))
            return Toggle;
        return Unknown;
    }

    public static string FromClass(int cls) => cls switch
    {
        ClassBtn or ClassSngBtn => Button,
        ClassTumb => Toggle,
        ClassLev or ClassMovableLev => Axis,
        _ => Unknown,
    };

    /// <summary>Primary flight controls / engine axes that must never be synced by clicks.</summary>
    [GeneratedRegex(@"\b(cyclic|collective|pedals?|rudder|stick|yoke|control column|throttles?|thrust lever|power levers?|condition lever|toe brakes?|wheel brakes?|trim wheel|flight control)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex FlightAxisRegex();
}
