using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UCS.Common;

namespace UCS.Client;

/// <summary>
/// Local UDP link with DCS:
///   127.0.0.1:5051  &lt;- UCS-Export.lua (text protocol)
///   127.0.0.1:5052  -&gt; UCS-Export.lua
///   127.0.0.1:5053  &lt;- UCS-Hook.lua (JSON)
/// </summary>
public sealed class DcsBridge : IDisposable
{
    private readonly IPEndPoint _exportTarget = new(IPAddress.Loopback, Protocol.ExportListenPort);
    private UdpClient? _exportUdp;
    private UdpClient? _hookUdp;
    private readonly CancellationTokenSource _cts = new();

    public event Action<string>? ExportMessage;
    public event Action<JsonElement>? HookMessage;
    public event Action<string>? Log;

    public void Start()
    {
        _exportUdp = Bind(Protocol.ExportSendPort, "UCS-Export.lua");
        _hookUdp = Bind(Protocol.HookPort, "UCS-Hook.lua");
        if (_exportUdp != null) _ = ExportLoopAsync(_exportUdp, _cts.Token);
        if (_hookUdp != null) _ = HookLoopAsync(_hookUdp, _cts.Token);
    }

    private UdpClient? Bind(int port, string who)
    {
        try
        {
            return UdpHelper.Create(new IPEndPoint(IPAddress.Loopback, port));
        }
        catch (SocketException ex)
        {
            Log?.Invoke($"ERROR: could not open local port {port} ({who}): {ex.SocketErrorCode}. " +
                        "Is another UCS-Client already running?");
            return null;
        }
    }

    private async Task ExportLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                ExportMessage?.Invoke(Encoding.UTF8.GetString(res.Buffer));
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { }
            catch (Exception ex) { Log?.Invoke("Export: " + ex.Message); }
        }
    }

    private async Task HookLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(res.Buffer);
                HookMessage?.Invoke(doc.RootElement.Clone());
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (JsonException) { }
            catch (SocketException) { }
            catch (Exception ex) { Log?.Invoke("Hook: " + ex.Message); }
        }
    }

    public void SendToExport(string message)
    {
        var udp = _exportUdp;
        if (udp == null) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            udp.Send(bytes, bytes.Length, _exportTarget);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Sends PREFIX + entries joined by ';' split into datagrams the Lua side can read (&lt; 8 KB).</summary>
    public void SendChunked(string prefix, IEnumerable<string> entries, int maxBytes = 3500)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            if (sb.Length > 0 && sb.Length + e.Length + 1 > maxBytes)
            {
                SendToExport(prefix + sb);
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append(';');
            sb.Append(e);
        }
        if (sb.Length > 0) SendToExport(prefix + sb);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _exportUdp?.Dispose();
        _hookUdp?.Dispose();
        _cts.Dispose();
    }
}
