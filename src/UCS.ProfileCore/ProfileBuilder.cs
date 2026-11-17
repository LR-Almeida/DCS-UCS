using UCS.Common;

namespace UCS.ProfileCore;

public sealed class ProfileGenOptions
{
    public bool NoAxes { get; init; }
    public bool KeepFlightAxes { get; init; }
    public bool TextParserOnly { get; init; }
    public bool Verbose { get; init; }
    public string? DcsRoot { get; init; }
    /// <summary>Explicit clickabledata.lua (otherwise searched inside the mod folder).</summary>
    public string? ClickablePath { get; init; }
}

public sealed class ProfileGenResult
{
    public required AircraftProfile Profile { get; init; }
    public required string Method { get; init; }
    public required string ClickablePath { get; init; }
    public string? DevicesPath { get; init; }
    public List<string> Warnings { get; } = new();
    public int ElementsFound { get; set; }
    public int FlightControlsRemoved { get; set; }
    public int AxesRemoved { get; set; }
    public int Duplicates { get; set; }
    public int FlightArgs => Profile.Flight?.Count ?? 0;
}

/// <summary>
/// Builds an aircraft profile from a mod's cockpit scripts. Descriptions are always
/// replaced by neutral labels ("toggle 17"): no text from the mod is copied.
/// </summary>
public static class ProfileBuilder
{
    public static ProfileGenResult Generate(string modDir, string aircraft, ProfileGenOptions? options = null)
    {
        var o = options ?? new ProfileGenOptions();
        if (!Directory.Exists(modDir)) throw new DirectoryNotFoundException($"Mod folder not found: {modDir}");
        var clickable = o.ClickablePath ?? ModLocator.FindFile(modDir, "clickabledata.lua")
                        ?? throw new FileNotFoundException("clickabledata.lua not found inside the mod folder.");
        var scriptsDir = Path.GetDirectoryName(clickable)!;
        var devicesCandidate = Path.Combine(scriptsDir, "devices.lua");
        var devices = File.Exists(devicesCandidate) ? devicesCandidate : ModLocator.FindFile(modDir, "devices.lua");

        var warnings = new List<string>();
        List<ClickableElement> elements = new();
        var method = "lua";
        if (!o.TextParserOnly)
        {
            try
            {
                var eval = new LuaEvaluator(scriptsDir, modDir, o.DcsRoot, o.Verbose);
                elements = eval.Evaluate(clickable);
                warnings.AddRange(eval.Warnings);
                if (eval.Skipped > 0) warnings.Add($"{eval.Skipped} element(s) without a resolvable device/command/arg were skipped.");
            }
            catch (Exception ex)
            {
                warnings.Add("Lua evaluation failed: " + ex.Message);
            }
        }
        if (elements.Count == 0)
        {
            method = "text";
            elements = RegexParser.Parse(clickable, devices, scriptsDir, warnings);
        }

        var profile = new AircraftProfile
        {
            AircraftName = aircraft,
            ModVersion = ModLocator.ReadVersion(modDir),
            Description = "Generated locally by UCS-ProfileGen. Do not redistribute.",
            GeneratedBy = $"UCS-ProfileGen {Protocol.Version} ({method})",
            GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Schema = AircraftProfile.CurrentSchema,
        };
        var result = new ProfileGenResult
        {
            Profile = profile,
            Method = method,
            ClickablePath = clickable,
            DevicesPath = devices,
            ElementsFound = elements.Count,
        };
        result.Warnings.AddRange(warnings);

        foreach (var e in elements.OrderBy(e => e.ArgId))
        {
            // The hint is only used for filtering; it is never written to the profile.
            var isFlight = ElementKinds.FlightAxisRegex().IsMatch(e.Hint + " " + e.Key);
            if (e.Kind == ElementKinds.Axis)
            {
                if (o.NoAxes) { result.AxesRemoved++; continue; }
                if (!o.KeepFlightAxes && isFlight) { result.FlightControlsRemoved++; continue; }
            }
            else if (!o.KeepFlightAxes && isFlight && e.Kind == ElementKinds.Unknown)
            {
                result.FlightControlsRemoved++;
                continue;
            }
            if (profile.Args.ContainsKey(e.ArgId)) { result.Duplicates++; continue; }
            profile.Args[e.ArgId] = new ProfileArg
            {
                DeviceId = e.DeviceId,
                CommandId = e.CommandId,
                Description = $"{e.Kind} {e.ArgId}",
                Type = e.Kind,
            };
        }

        // Trim commands (EFM command ids): hand the trim over with the controls.
        try
        {
            profile.Trim = ReadTrimCommands(scriptsDir);
        }
        catch (Exception ex)
        {
            result.Warnings.Add("Trim command scan failed: " + ex.Message);
        }

        // Flight-control animations (stick, pedals, collective): mirrored on their own channel.
        try
        {
            var flight = FlightGaugeParser.Parse(scriptsDir, result.Warnings);
            // A clickable arg is synced as a switch: never mirror it as a flight control too.
            flight.RemoveAll(f => profile.Args.ContainsKey(f.Arg));
            profile.Flight = flight;
        }
        catch (Exception ex)
        {
            result.Warnings.Add("Flight control scan failed: " + ex.Message);
            profile.Flight = new();
        }
        return result;
    }

    private static readonly System.Text.RegularExpressions.Regex TrimRegex = new(
        @"^\s*(trimReleaseRelease|trimRelease|trimSet|trimReset)\s*=\s*(\d+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>Reads trimRelease / trimReleaseRelease / trimSet / trimReset from command_defs.lua (numbers only).</summary>
    public static TrimCommands? ReadTrimCommands(string scriptsDir)
    {
        var path = Path.Combine(scriptsDir, "command_defs.lua");
        if (!File.Exists(path)) return null;
        var t = new TrimCommands();
        foreach (System.Text.RegularExpressions.Match m in TrimRegex.Matches(File.ReadAllText(path)))
        {
            var id = int.Parse(m.Groups[2].Value);
            switch (m.Groups[1].Value.ToLowerInvariant())
            {
                case "trimrelease": t.Release ??= id; break;
                case "trimreleaserelease": t.ReleaseEnd ??= id; break;
                case "trimset": t.Set ??= id; break;
                case "trimreset": t.Reset ??= id; break;
            }
        }
        return t.Release == null && t.ReleaseEnd == null && t.Set == null && t.Reset == null ? null : t;
    }

    public static string FileNameFor(string aircraft)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(aircraft.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim() + ".json";
    }
}
