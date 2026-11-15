using UCS.Common;
using UCS.ProfileGen;

const string Usage = """
DCS-UCS Profile Generator

Usage:
  UCS-ProfileGen.exe <mod folder> <aircraft name> [options]

Example:
  UCS-ProfileGen.exe "C:\Users\me\Saved Games\DCS\Mods\aircraft\UH-60L" "UH-60L"

Options:
  --out <dir|file.json>   Output folder or file (default: profiles\ next to the exe,
                          or %APPDATA%\DCS-UCS\profiles if that is read-only)
  --dcs <DCS folder>      DCS World install folder, used to load ED common scripts
                          (auto-detected from the registry when omitted)
  --no-axes               Drop every continuous control (knobs/levers/axes)
  --keep-flight-axes      Do not filter flight controls (cyclic, collective, throttle...)
  --text-parser           Skip Lua evaluation and use the text parser only
  --verbose               Print details

The aircraft name must match LoGetSelfData().Name in DCS (usually the unit type).
""";

if (args.Length < 2 || args.Any(a => a is "-h" or "--help" or "/?"))
{
    Console.WriteLine(Usage);
    return args.Length < 2 ? 1 : 0;
}

var modDir = args[0];
var aircraft = args[1].Trim();
string? outArg = null, dcsArg = null;
bool noAxes = false, keepFlight = false, textOnly = false, verbose = false;
for (var i = 2; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--out" when i + 1 < args.Length: outArg = args[++i]; break;
        case "--dcs" when i + 1 < args.Length: dcsArg = args[++i]; break;
        case "--no-axes": noAxes = true; break;
        case "--keep-flight-axes": keepFlight = true; break;
        case "--text-parser": textOnly = true; break;
        case "--verbose" or "-v": verbose = true; break;
        default:
            Console.Error.WriteLine($"Unknown option: {args[i]}");
            return 1;
    }
}

if (!Directory.Exists(modDir))
{
    Console.Error.WriteLine($"Mod folder not found: {modDir}");
    return 2;
}
if (aircraft.Length == 0)
{
    Console.Error.WriteLine("Aircraft name is empty.");
    return 1;
}

var clickable = FindFile(modDir, "clickabledata.lua");
if (clickable == null)
{
    Console.Error.WriteLine("clickabledata.lua not found inside the mod folder.");
    return 3;
}
var scriptsDir = Path.GetDirectoryName(clickable)!;
var devices = File.Exists(Path.Combine(scriptsDir, "devices.lua")) ? Path.Combine(scriptsDir, "devices.lua") : FindFile(modDir, "devices.lua");
var dcsRoot = dcsArg ?? DetectDcs();

Console.WriteLine($"Mod:            {Path.GetFullPath(modDir)}");
Console.WriteLine($"clickabledata:  {clickable}");
Console.WriteLine($"devices:        {devices ?? "(not found)"}");
Console.WriteLine($"DCS install:    {dcsRoot ?? "(not found - ED common helpers emulated)"}");

List<ClickableElement> elements = new();
var warnings = new List<string>();
var method = "lua";
if (!textOnly)
{
    try
    {
        var eval = new LuaEvaluator(scriptsDir, modDir, dcsRoot, verbose);
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
    Console.WriteLine(textOnly ? "Using text parser." : "Lua evaluation produced no elements, falling back to the text parser.");
    try
    {
        elements = RegexParser.Parse(clickable, devices, scriptsDir, warnings);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Text parser failed: " + ex.Message);
        return 4;
    }
}

var profile = new AircraftProfile
{
    AircraftName = aircraft,
    Description = $"Auto-generated profile for {aircraft}",
    GeneratedBy = $"UCS-ProfileGen {Protocol.Version} ({method})",
    GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
    Source = Path.GetFileName(Path.GetFullPath(modDir).TrimEnd('\\', '/')),
};

int filteredFlight = 0, filteredAxes = 0, duplicates = 0;
foreach (var e in elements.OrderBy(e => e.ArgId))
{
    if (e.Kind == ElementKinds.Axis)
    {
        if (noAxes) { filteredAxes++; continue; }
        if (!keepFlight && ElementKinds.FlightAxisRegex().IsMatch(e.Hint + " " + e.Key)) { filteredFlight++; continue; }
    }
    else if (!keepFlight && ElementKinds.FlightAxisRegex().IsMatch(e.Hint) && e.Kind == ElementKinds.Unknown)
    {
        filteredFlight++;
        continue;
    }
    if (profile.Args.ContainsKey(e.ArgId)) { duplicates++; continue; }
    profile.Args[e.ArgId] = new ProfileArg
    {
        DeviceId = e.DeviceId,
        CommandId = e.CommandId,
        Description = e.Hint,
        Type = e.Kind,
    };
}

string outPath;
if (outArg != null && outArg.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) outPath = outArg;
else outPath = Path.Combine(outArg ?? AppPaths.WritableProfileDir(), ProfileFileName(aircraft));

if (profile.Args.Count == 0)
{
    Console.Error.WriteLine("No usable clickable elements found. Profile not written.");
    foreach (var w in warnings) Console.Error.WriteLine("  warn: " + w);
    return 5;
}

try
{
    profile.Save(outPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Could not write {outPath}: {ex.Message}");
    return 6;
}

Console.WriteLine();
Console.WriteLine($"Elements found:          {elements.Count}");
Console.WriteLine($"Flight controls removed: {filteredFlight}");
if (noAxes) Console.WriteLine($"Axes removed:            {filteredAxes}");
Console.WriteLine($"Duplicate args ignored:  {duplicates}");
Console.WriteLine($"Arguments in profile:    {profile.Args.Count}");
foreach (var w in warnings.Take(verbose ? int.MaxValue : 10)) Console.WriteLine("  warn: " + w);
if (!verbose && warnings.Count > 10) Console.WriteLine($"  ... {warnings.Count - 10} more warning(s) (use --verbose)");
Console.WriteLine();
Console.WriteLine($"Profile written: {Path.GetFullPath(outPath)}");
return 0;

static string ProfileFileName(string aircraft)
{
    var invalid = Path.GetInvalidFileNameChars();
    return new string(aircraft.Select(c => invalid.Contains(c) ? '_' : c).ToArray()) + ".json";
}

static string? FindFile(string root, string name)
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
        // Prefer the cockpit script folder
        return all.OrderByDescending(p => p.Contains(Path.Combine("Cockpit", "Scripts"), StringComparison.OrdinalIgnoreCase))
                  .ThenBy(p => p.Length)
                  .FirstOrDefault();
    }
    catch
    {
        return null;
    }
}

static string? DetectDcs()
{
    if (OperatingSystem.IsWindows())
    {
        foreach (var key in new[] { @"Software\Eagle Dynamics\DCS World", @"Software\Eagle Dynamics\DCS World OpenBeta" })
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key);
                if (k?.GetValue("Path") is string p && Directory.Exists(p)) return p;
            }
            catch
            {
                // ignore
            }
        }
    }
    foreach (var p in new[]
             {
                 @"C:\Program Files\Eagle Dynamics\DCS World",
                 @"C:\Program Files\Eagle Dynamics\DCS World OpenBeta",
                 @"C:\Program Files (x86)\Steam\steamapps\common\DCSWorld",
             })
    {
        if (Directory.Exists(p)) return p;
    }
    return null;
}
