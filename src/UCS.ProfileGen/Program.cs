using UCS.Common;
using UCS.ProfileCore;

const string Usage = """
DCS-UCS Profile Generator (local use only - generated profiles must not be redistributed)

Usage:
  UCS-ProfileGen.exe <aircraft name>                    find the mod in Saved Games automatically
  UCS-ProfileGen.exe <mod folder> <aircraft name>       use a specific mod folder
  UCS-ProfileGen.exe --list                             list aircraft declared by installed mods

Examples:
  UCS-ProfileGen.exe "UH-60L"
  UCS-ProfileGen.exe "C:\Users\me\Saved Games\DCS\Mods\aircraft\uh60l" "UH-60L"

Options:
  --out <dir|file.json>   Output folder or file (default: %APPDATA%\DCS-UCS\profiles)
  --dcs <DCS folder>      DCS World install folder, used to load ED common scripts (optional)
  --no-axes               Drop every continuous control (knobs/levers/axes)
  --keep-flight-axes      Do not filter flight controls (cyclic, collective, throttle...)
  --text-parser           Skip Lua evaluation and use the text parser only
  --verbose               Print details

Notes:
  * The aircraft name must match the type DCS reports (the first argument of make_flyable
    in the mod's entry.lua, e.g. "UH-60L").
  * UCS-Client.exe generates profiles automatically; this tool is for troubleshooting.
  * Descriptions in the profile are neutral labels ("toggle 17"); no text from the mod is copied.
""";

var positional = new List<string>();
string? outArg = null, dcsArg = null;
bool noAxes = false, keepFlight = false, textOnly = false, verbose = false, list = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "-h" or "--help" or "/?": Console.WriteLine(Usage); return 0;
        case "--out" when i + 1 < args.Length: outArg = args[++i]; break;
        case "--dcs" when i + 1 < args.Length: dcsArg = args[++i]; break;
        case "--no-axes": noAxes = true; break;
        case "--keep-flight-axes": keepFlight = true; break;
        case "--text-parser": textOnly = true; break;
        case "--verbose" or "-v": verbose = true; break;
        case "--list": list = true; break;
        default:
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"Unknown option: {args[i]}");
                return 1;
            }
            positional.Add(args[i]);
            break;
    }
}

if (list)
{
    Console.WriteLine($"Saved Games: {ModLocator.SavedGamesDir()}");
    foreach (var m in ModLocator.ScanAll(refresh: true).OrderBy(m => m.Aircraft))
        Console.WriteLine($"  {m.Aircraft,-24} mod {(m.Version.Length > 0 ? m.Version : "(no version)"),-12} {Path.GetFileName(m.ModDir)}{(m.ClickablePath == null ? "  [no clickabledata.lua]" : "")}");
    return 0;
}

if (positional.Count is < 1 or > 2)
{
    Console.WriteLine(Usage);
    return 1;
}

string modDir, aircraft;
string? clickable = null;
if (positional.Count == 1)
{
    aircraft = positional[0].Trim();
    var mod = ModLocator.Find(aircraft, refresh: true);
    if (mod == null)
    {
        Console.Error.WriteLine($"No mod in {ModLocator.SavedGamesDir()}\\DCS*\\Mods\\aircraft declares '{aircraft}'. Use --list or pass the mod folder.");
        return 2;
    }
    modDir = mod.ModDir;
    clickable = mod.ClickablePath;
}
else
{
    modDir = positional[0];
    aircraft = positional[1].Trim();
    var declared = Directory.Exists(modDir) ? ModLocator.ReadMod(modDir).FirstOrDefault(m => string.Equals(m.Aircraft, aircraft, StringComparison.OrdinalIgnoreCase)) : null;
    clickable = declared?.ClickablePath;
}

if (aircraft.Length == 0)
{
    Console.Error.WriteLine("Aircraft name is empty.");
    return 1;
}

ProfileGenResult result;
try
{
    result = ProfileBuilder.Generate(modDir, aircraft, new ProfileGenOptions
    {
        NoAxes = noAxes,
        KeepFlightAxes = keepFlight,
        TextParserOnly = textOnly,
        Verbose = verbose,
        DcsRoot = dcsArg,
        ClickablePath = clickable,
    });
}
catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
{
    Console.Error.WriteLine(ex.Message);
    return 3;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Generation failed: " + ex.Message);
    return 4;
}

Console.WriteLine($"Mod:            {Path.GetFullPath(modDir)}");
Console.WriteLine($"Mod version:    {(result.Profile.ModVersion.Length > 0 ? result.Profile.ModVersion : "(not declared)")}");
Console.WriteLine($"clickabledata:  {result.ClickablePath}");
Console.WriteLine($"devices:        {result.DevicesPath ?? "(not found)"}");
Console.WriteLine($"Method:         {result.Method}");
Console.WriteLine();
Console.WriteLine($"Elements found:          {result.ElementsFound}");
Console.WriteLine($"Flight controls removed: {result.FlightControlsRemoved}");
if (noAxes) Console.WriteLine($"Axes removed:            {result.AxesRemoved}");
Console.WriteLine($"Duplicate args ignored:  {result.Duplicates}");
Console.WriteLine($"Arguments in profile:    {result.Profile.Args.Count}");
Console.WriteLine($"Profile fingerprint:     {result.Profile.Fingerprint()}");
var axisCmds = result.Profile.FlightCommands;
Console.WriteLine("Axis commands (mod):     " + (axisCmds == null ? "none" :
                  string.Join(", ", axisCmds.Select(kv => $"{kv.Key} {string.Join("/", kv.Value)}"))));
var ext = result.Profile.External;
Console.WriteLine("External states:         " + (ext == null ? "none" :
                  string.Join(" | ", ext.Select(g => $"args {string.Join(",", g.Args)} <- keys {string.Join(",", g.Commands)}" +
                      (g.Prefer == null ? "" : " [" + string.Join(" ", g.Prefer.Select(kv => $"{kv.Key}>{string.Join("/", kv.Value)}")) + "]")))));
var trim = result.Profile.Trim;
Console.WriteLine($"Trim commands:           " + (trim == null ? "none" :
                  $"release {trim.Release?.ToString() ?? "-"} / release-end {trim.ReleaseEnd?.ToString() ?? "-"} / set {trim.Set?.ToString() ?? "-"} / reset {trim.Reset?.ToString() ?? "-"}" +
                  (trim.CanHandOver ? " (hand-over supported)" : "")));
Console.WriteLine($"Flight-control args:     {result.FlightArgs}" +
                  (result.FlightArgs > 0 ? "  (" + string.Join(", ", result.Profile.Flight!.Select(f => $"{f.Arg} {f.Axis}")) + ")" : ""));
foreach (var w in result.Warnings.Take(verbose ? int.MaxValue : 10)) Console.WriteLine("  warn: " + w);
if (!verbose && result.Warnings.Count > 10) Console.WriteLine($"  ... {result.Warnings.Count - 10} more warning(s) (use --verbose)");

if (result.Profile.Args.Count == 0)
{
    Console.Error.WriteLine("No usable clickable elements found. Profile not written.");
    return 5;
}

var outPath = outArg != null && outArg.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
    ? outArg
    : Path.Combine(outArg ?? Path.Combine(AppPaths.UserDir, "profiles"), ProfileBuilder.FileNameFor(aircraft));
try
{
    result.Profile.Save(outPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Could not write {outPath}: {ex.Message}");
    return 6;
}
Console.WriteLine();
Console.WriteLine($"Profile written: {Path.GetFullPath(outPath)}");
return 0;
