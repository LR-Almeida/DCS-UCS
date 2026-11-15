using System.Text.Json;
using System.Text.Json.Serialization;

namespace UCS.Common;

/// <summary>
/// Network protocol between UCS-Client.exe and UCS-Server.exe (JSON over UDP).
/// A single envelope type keeps the wire format simple and forward compatible.
/// </summary>
public static class Protocol
{
    public const string Version = "1.0.0";
    public const int DefaultServerPort = 5055;
    public const int ExportSendPort = 5051;   // Export.lua -> Client
    public const int ExportListenPort = 5052; // Client -> Export.lua
    public const int HookPort = 5053;         // GameGUI hook -> Client
    public const int MaxDatagram = 60000;

    // Message types
    public const string State = "state";            // C->S join + heartbeat
    public const string Args = "args";              // C->S->C argument changes
    public const string Leave = "leave";            // C->S
    public const string Welcome = "welcome";        // S->C heartbeat ack + session info
    public const string PeerJoined = "peer_joined"; // S->C ask for a snapshot for the new peer
    public const string Error = "error";            // S->C

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static byte[] Serialize(UcsMessage msg) => JsonSerializer.SerializeToUtf8Bytes(msg, Json);

    public static UcsMessage? Deserialize(ReadOnlySpan<byte> data)
    {
        try
        {
            return JsonSerializer.Deserialize<UcsMessage>(data, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class UcsMessage
{
    public string Type { get; set; } = "";
    public string? ClientId { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }

    // Session identification (state)
    public string? Server { get; set; }
    public string? Group { get; set; }
    public string? Unit { get; set; }
    public string? Room { get; set; }
    public string? Aircraft { get; set; }
    public bool? Standby { get; set; }

    // Argument sync (args)
    public Dictionary<string, double>? Values { get; set; }
    public string? Target { get; set; }
    public string? From { get; set; }
    public long? Seq { get; set; }

    // Server answers (welcome / error)
    public string? SessionKey { get; set; }
    public int? Peers { get; set; }
    public List<string>? PeerNames { get; set; }
    public string? Message { get; set; }
}
