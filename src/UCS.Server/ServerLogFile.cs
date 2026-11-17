using System.Text;
using UCS.Common;

namespace UCS.Server;

/// <summary>
/// Copies everything the server prints to UCS-Server.log (previous run kept as UCS-Server.old.log), so the
/// session can be followed or shared without touching the console window. Folder: Saved Games\DCS\Logs\UCS
/// when DCS is installed on this PC (dedicated servers too), otherwise %APPDATA%\DCS-UCS\logs.
/// Never throws: a log problem must not stop the server.
/// </summary>
internal sealed class ServerLogFile : TextWriter
{
    private readonly TextWriter _console;
    private readonly object _lock = new();
    private StreamWriter? _file;

    public string? FilePath { get; }

    private ServerLogFile(TextWriter console, StreamWriter file, string path)
    {
        _console = console;
        _file = file;
        FilePath = path;
    }

    public override Encoding Encoding => _console.Encoding;

    /// <summary>Installs the tee on Console.Out; returns null (console only) when the file cannot be opened.</summary>
    public static ServerLogFile? Install()
    {
        try
        {
            var dir = Directory.Exists(AppPaths.DcsWriteDir()) ? AppPaths.UcsLogDir() : Path.Combine(AppPaths.UserDir, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "UCS-Server.log");
            if (File.Exists(path)) File.Copy(path, Path.Combine(dir, "UCS-Server.old.log"), overwrite: true);
            var file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            var tee = new ServerLogFile(Console.Out, file, path);
            Console.SetOut(tee);
            return tee;
        }
        catch
        {
            return null; // e.g. a second server instance holds the file
        }
    }

    public override void Write(char value)
    {
        lock (_lock)
        {
            _console.Write(value);
            try { _file?.Write(value); } catch { _file = null; }
        }
    }

    public override void Write(string? value)
    {
        lock (_lock)
        {
            _console.Write(value);
            try { _file?.Write(value); } catch { _file = null; }
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_lock)
        {
            _console.WriteLine(value);
            try { _file?.WriteLine(value); } catch { _file = null; }
        }
    }

    public override void Flush()
    {
        lock (_lock)
        {
            _console.Flush();
            try { _file?.Flush(); } catch { _file = null; }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            lock (_lock)
            {
                try { _file?.Dispose(); } catch { }
                _file = null;
            }
        base.Dispose(disposing);
    }
}
