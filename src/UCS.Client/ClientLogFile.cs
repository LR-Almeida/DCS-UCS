using UCS.ProfileCore;

namespace UCS.Client;

/// <summary>
/// Writes the Log tab to Saved Games\DCS\Logs\UCS\UCS-Client.log (like dcs.log: the previous
/// session is kept as UCS-Client.old.log). Never throws: logging must not break the client.
/// </summary>
public sealed class ClientLogFile : IDisposable
{
    private readonly object _lock = new();
    private StreamWriter? _writer;

    /// <summary>Log folder; the UCS_LOG_DIR environment variable overrides it (tests, portable setups).</summary>
    public string Directory { get; } =
        Environment.GetEnvironmentVariable("UCS_LOG_DIR") is { Length: > 0 } custom ? custom : ModLocator.UcsLogDir();
    public string? FilePath { get; private set; }

    public ClientLogFile()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(Directory, "UCS-Client.log");
            var old = Path.Combine(Directory, "UCS-Client.old.log");
            if (File.Exists(path)) File.Copy(path, old, overwrite: true);
            _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            FilePath = path;
            _writer.WriteLine($"=== UCS-Client {UCS.Common.Protocol.Version} - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        }
        catch
        {
            _writer = null; // e.g. another UCS-Client instance holds the file
        }
    }

    public void Write(string line)
    {
        lock (_lock)
        {
            try { _writer?.WriteLine(line); }
            catch { _writer = null; }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}
