using System.Text;
using MoonSharp.Interpreter;

namespace UCS.ProfileCore;

/// <summary>
/// Executes the mod's clickabledata.lua (and everything it dofile()s: devices.lua,
/// command_defs.lua, ...) inside a sandboxed MoonSharp Lua interpreter, emulating the
/// minimum of the DCS cockpit environment. Unknown globals become harmless "stubs";
/// unknown helper functions (default_button, default_2_position_tumb, ...) are recorded
/// so the element can still be decoded.
/// </summary>
public sealed class LuaEvaluator
{
    private const string CommonMarker = "__UCS_COMMON__/";

    private readonly string _scriptsDir;
    private readonly string _modDir;
    private readonly string? _dcsRoot;
    private readonly bool _verbose;
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
    private Script _script = null!;

    public List<string> Warnings { get; } = new();
    public int Skipped { get; private set; }

    public LuaEvaluator(string scriptsDir, string modDir, string? dcsRoot, bool verbose)
    {
        _scriptsDir = Path.GetFullPath(scriptsDir);
        _modDir = Path.GetFullPath(modDir);
        _dcsRoot = dcsRoot;
        _verbose = verbose;
    }

    private const string Prelude = @"
_ = function(s, ...) if type(s) == 'string' then return s end return tostring(s) end
class_type = { NULL = 0, BTN = 1, TUMB = 2, SNGBTN = 3, LEV = 4, MOVABLE_LEV = 5 }
start_command = 3000
local __ucs_count = 0
function counter() __ucs_count = __ucs_count + 1 return __ucs_count end
function loadfile(p) return function(...) return dofile(p) end end

local __ucs_make_stub
local function __ucs_num() return 0 end
__ucs_make_stub = function(name)
    local s = {}
    setmetatable(s, {
        __index = function(t, k)
            local c = __ucs_make_stub(name .. '.' .. tostring(k))
            rawset(t, k, c)
            return c
        end,
        __call = function(self, ...) return __ucs_build(name, ...) end,
        __tostring = function() return name end,
        __concat = function(a, b) return tostring(a) .. tostring(b) end,
        __add = __ucs_num, __sub = __ucs_num, __mul = __ucs_num, __div = __ucs_num,
        __mod = __ucs_num, __pow = __ucs_num, __unm = __ucs_num,
        __lt = function() return false end, __le = function() return false end,
    })
    rawset(s, '__ucs_stub', name)
    return s
end
__ucs_stub_factory = __ucs_make_stub

-- Generic decoder for helper functions that are not available (ED common files).
-- ED helpers follow (hint, device, command[, command2], arg, ...).
function __ucs_build(name, ...)
    local a = { ... }
    local el = { __ucs_func = name }
    local i = 1
    if type(a[1]) == 'string' then el.hint = a[1]; i = 2 end
    local nums = {}
    for j = i, #a do
        if type(a[j]) == 'number' then table.insert(nums, a[j]) else table.insert(nums, false) end
    end
    el.device = nums[1]
    el.command = nums[2]
    if type(nums[2]) == 'number' and nums[2] >= 3000 then
        -- skip any further command ids (>= 3000) and take the first plausible argument id
        for j = 3, #nums do
            local n = nums[j]
            if type(n) == 'number' and n < 3000 and n >= 0 and math.floor(n) == n then el.arg = n break end
        end
    else
        el.arg = nums[3]
    end
    return el
end

setmetatable(_G, { __index = function(t, k)
    if type(k) == 'string' and string.sub(k, 1, 6) == '__ucs_' then return nil end
    local s = __ucs_make_stub(tostring(k))
    rawset(t, k, s)
    return s
end })
";

    public List<ClickableElement> Evaluate(string clickablePath)
    {
        _script = new Script(CoreModules.Preset_SoftSandbox);
        _script.Options.DebugPrint = s => { if (_verbose) Console.WriteLine("  [lua] " + s); };
        _script.Globals["dofile"] = DynValue.NewCallback((ctx, args) => DoFile(args.Count > 0 ? args[0].CastToString() : null));
        _script.Globals["require"] = DynValue.NewCallback((ctx, args) =>
            _script.Call(_script.Globals.Get("__ucs_stub_factory"), DynValue.NewString("require:" + (args.Count > 0 ? args[0].CastToString() : ""))));

        var opts = new Table(_script);
        var dirWithSlash = _scriptsDir.Replace('\\', '/').TrimEnd('/') + "/";
        opts["script_path"] = dirWithSlash;
        opts["common_script_path"] = CommonMarker;
        opts["mount_path"] = _modDir.Replace('\\', '/').TrimEnd('/') + "/";
        opts["init_conditions"] = new Table(_script);
        opts["flight"] = new Table(_script);
        opts["cockpit"] = new Table(_script);
        opts["mission"] = new Table(_script);
        opts["date"] = new Table(_script);
        opts["measurement_system"] = "imperial";
        var screen = new Table(_script);
        screen["width"] = 1920;
        screen["height"] = 1080;
        screen["aspect"] = 1920.0 / 1080.0;
        opts["screen"] = screen;
        _script.Globals["LockOn_Options"] = opts;

        // The global stub metatable is installed last so the above stay real values
        _script.DoString(Prelude, null, "ucs_prelude");

        var result = DoFile(clickablePath);
        if (result.IsNil() && !_executedMain) throw new InvalidOperationException("clickabledata.lua could not be executed.");

        var elements = _script.Globals.RawGet("elements");
        if (elements == null || elements.Type != DataType.Table)
            throw new InvalidOperationException("clickabledata.lua did not define an 'elements' table.");

        var list = new List<ClickableElement>();
        foreach (var pair in elements.Table.Pairs)
        {
            if (pair.Value.Type != DataType.Table) continue;
            var key = pair.Key.Type == DataType.String ? pair.Key.String : pair.Key.ToPrintString();
            var el = Decode(key, pair.Value.Table);
            if (el != null) list.Add(el);
            else Skipped++;
        }
        return list;
    }

    private bool _executedMain;

    private DynValue DoFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return DynValue.Nil;
        var resolved = Resolve(path);
        if (resolved == null)
        {
            if (!path.StartsWith(CommonMarker, StringComparison.Ordinal))
                Warn($"dofile: file not found '{path}' (ignored)");
            else if (_verbose)
                Console.WriteLine($"  [info] ED common script not available, emulated: {path[CommonMarker.Length..]}");
            return DynValue.Nil;
        }
        if (!_loading.Add(resolved))
        {
            Warn($"dofile: recursive include ignored '{resolved}'");
            return DynValue.Nil;
        }
        try
        {
            if (_verbose) Console.WriteLine($"  [lua] dofile {resolved}");
            var code = ReadLua(resolved);
            var res = _script.DoString(code, null, Path.GetFileName(resolved));
            _executedMain = true;
            return res;
        }
        catch (InterpreterException ex)
        {
            Warn($"{Path.GetFileName(resolved)}: {ex.DecoratedMessage ?? ex.Message} (continuing)");
            _executedMain = true;
            return DynValue.Nil;
        }
        finally
        {
            _loading.Remove(resolved);
        }
    }

    private string? Resolve(string path)
    {
        var p = path.Replace('\\', '/');
        var candidates = new List<string>();
        if (p.StartsWith(CommonMarker, StringComparison.Ordinal))
        {
            var rel = p[CommonMarker.Length..];
            if (_dcsRoot != null) candidates.Add(Path.Combine(_dcsRoot, "Scripts", "Aircrafts", "_Common", "Cockpit", rel));
        }
        else if (Path.IsPathRooted(p))
        {
            candidates.Add(p);
        }
        else
        {
            candidates.Add(Path.Combine(_scriptsDir, p));
            candidates.Add(Path.Combine(_modDir, p));
            if (_dcsRoot != null) candidates.Add(Path.Combine(_dcsRoot, p));
        }
        foreach (var c in candidates)
        {
            try
            {
                var full = Path.GetFullPath(c);
                if (File.Exists(full)) return full;
            }
            catch
            {
                // invalid path characters
            }
        }
        return null;
    }

    private static string ReadLua(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        // Lua 5.1 allows a leading shebang line
        if (text.StartsWith("#")) text = "--" + text;
        return text;
    }

    private void Warn(string msg)
    {
        Warnings.Add(msg);
        if (_verbose) Console.WriteLine("  [warn] " + msg);
    }

    private static int? Num(DynValue? v)
    {
        if (v == null || v.Type != DataType.Number) return null;
        var d = v.Number;
        if (double.IsNaN(d) || d < 0 || d > int.MaxValue || Math.Floor(d) != d) return null;
        return (int)d;
    }

    private static DynValue? First(Table t, string field)
    {
        var v = t.Get(field);
        if (v.Type == DataType.Table) return v.Table.Get(1);
        return v.IsNil() ? null : v;
    }

    private static string Text(DynValue v) => v.Type switch
    {
        DataType.String => v.String,
        DataType.Table => v.Table.RawGet("__ucs_stub")?.CastToString() ?? "",
        DataType.Nil or DataType.Void => "",
        _ => v.ToPrintString(),
    };

    private ClickableElement? Decode(string key, Table t)
    {
        var hint = Text(t.Get("hint"));
        var func = t.RawGet("__ucs_func");
        if (func != null && func.Type == DataType.String)
        {
            // Produced by the generic helper decoder
            var dev = Num(t.Get("device"));
            var cmd = Num(t.Get("command"));
            var arg = Num(t.Get("arg"));
            if (dev == null || cmd == null || arg == null) return null;
            return new ClickableElement(key, hint.Length > 0 ? hint : key, dev.Value, cmd.Value, arg.Value, ElementKinds.FromFunctionName(func.String));
        }

        // Real element table (from the mod's own helpers or ED clickable_defs.lua)
        var device = Num(t.Get("device"));
        var action = First(t, "action");
        var argV = First(t, "arg");
        var command = Num(action);
        var argId = Num(argV);
        if (device == null || command == null || argId == null) return null;

        var kind = ElementKinds.Unknown;
        var cls = First(t, "class");
        var clsNum = Num(cls);
        if (clsNum != null) kind = ElementKinds.FromClass(clsNum.Value);
        if (kind == ElementKinds.Toggle)
        {
            // TUMB with a fractional step is a multi-position selector
            var argValue = First(t, "arg_value");
            if (argValue != null && argValue.Type == DataType.Number && Math.Abs(argValue.Number) is > 0 and < 0.99)
                kind = ElementKinds.Multi;
        }
        return new ClickableElement(key, hint.Length > 0 ? hint : key, device.Value, command.Value, argId.Value, kind);
    }
}
