using UCS.Common;
using UCS.Server;

Console.Title = "UCS-Server - DCS Universal Control Sync";
using var logFile = ServerLogFile.Install();
Console.WriteLine("==============================================================");
Console.WriteLine($"  DCS-UCS Server {Protocol.Version}  -  Universal Control Sync");
Console.WriteLine("  Free & open-source (MIT). Ctrl+C to stop.");
Console.WriteLine("==============================================================");

string? configPath = null;
int? portOverride = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--config" or "-c" when i + 1 < args.Length:
            configPath = args[++i];
            break;
        case "--port" or "-p" when i + 1 < args.Length:
            if (int.TryParse(args[++i], out var p)) portOverride = p;
            break;
        case "--help" or "-h" or "/?":
            Console.WriteLine("Usage: UCS-Server.exe [--port 5055] [--config server_config.json]");
            Console.WriteLine(@"Log: Saved Games\DCS\Logs\UCS\UCS-Server.log (or %APPDATA%\DCS-UCS\logs without DCS).");
            return 0;
    }
}

configPath ??= AppPaths.ConfigFile("server_config.json");
var cfg = ServerConfig.LoadOrCreate(configPath);
Console.WriteLine(logFile?.FilePath is { } logPath ? $"[INFO] Log file: {logPath}" : "[INFO] Log file: not available (console only)");
if (portOverride is > 0 and < 65536) cfg.Port = portOverride.Value;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var server = new SyncServer(cfg);
try
{
    await server.RunAsync(cts.Token);
}
catch (System.Net.Sockets.SocketException ex)
{
    Console.WriteLine($"[FATAL] Could not open UDP port {cfg.Port}: {ex.Message}");
    Console.WriteLine("Is another UCS-Server already running? Press any key to exit.");
    if (!Console.IsInputRedirected) Console.ReadKey(true);
    return 1;
}
return 0;
