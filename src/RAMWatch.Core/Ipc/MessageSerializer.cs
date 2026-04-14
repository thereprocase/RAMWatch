using System.Text;
using System.Text.Json;
using RAMWatch.Core.Models;

namespace RAMWatch.Core.Ipc;

/// <summary>
/// JSON-over-newline framing. Each message is a single JSON object on one line.
/// Embedded newlines in string fields are escaped by the JSON serializer,
/// so the only raw newline in the output is the delimiter.
/// </summary>
public static class MessageSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = RamWatchJsonContext.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize<T>(T message) where T : IpcMessage
    {
        // System.Text.Json escapes \n in strings as \\n in the JSON output,
        // so the only literal newline is our delimiter.
        string json = JsonSerializer.Serialize(message, typeof(T), Options);
        return json + "\n";
    }

    public static byte[] SerializeToBytes<T>(T message) where T : IpcMessage
    {
        return Encoding.UTF8.GetBytes(Serialize(message));
    }

    /// <summary>
    /// Deserialize a single line (without trailing newline) into a message.
    /// Returns null if the line is empty or deserialization fails.
    /// </summary>
    public static IpcMessage? Deserialize(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            // Peek at the "type" field to determine the concrete message type.
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("type", out var typeProp))
                return null;

            string? type = typeProp.GetString();
            return type switch
            {
                "state" => JsonSerializer.Deserialize(line, typeof(StateMessage), Options) as IpcMessage,
                "event" => JsonSerializer.Deserialize(line, typeof(EventMessage), Options) as IpcMessage,
                "response" => JsonSerializer.Deserialize(line, typeof(ResponseMessage), Options) as IpcMessage,
                "getState" => JsonSerializer.Deserialize(line, typeof(GetStateMessage), Options) as IpcMessage,
                "runIntegrity" => JsonSerializer.Deserialize(line, typeof(RunIntegrityMessage), Options) as IpcMessage,
                "updateSettings" => JsonSerializer.Deserialize(line, typeof(UpdateSettingsMessage), Options) as IpcMessage,
                // Phase 3 — client → service
                "logValidation" => JsonSerializer.Deserialize(line, typeof(LogValidationMessage), Options) as IpcMessage,
                "getSnapshots" => JsonSerializer.Deserialize(line, typeof(GetSnapshotsMessage), Options) as IpcMessage,
                "getDigest" => JsonSerializer.Deserialize(line, typeof(GetDigestMessage), Options) as IpcMessage,
                // Phase 3 — service → client
                "snapshotsResponse" => JsonSerializer.Deserialize(line, typeof(SnapshotsResponseMessage), Options) as IpcMessage,
                "digestResponse" => JsonSerializer.Deserialize(line, typeof(DigestResponseMessage), Options) as IpcMessage,
                _ => null // Unknown message type — silently drop (B6: forward compatibility)
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
