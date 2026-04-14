namespace RAMWatch.Core.Models;

/// <summary>
/// Base for all IPC messages. Every message carries a protocol version
/// so mismatched service/GUI versions can be detected (B6).
/// </summary>
public abstract class IpcMessage
{
    public const int CurrentProtocolVersion = 1;

    public required string Type { get; init; }
    public int ProtocolVersion { get; init; } = CurrentProtocolVersion;
}

// ── Service → Client ─────────────────────────────────────────────

public sealed class StateMessage : IpcMessage
{
    public required ServiceState State { get; init; }
}

public sealed class EventMessage : IpcMessage
{
    public required MonitoredEvent Event { get; init; }
}

public sealed class ResponseMessage : IpcMessage
{
    public required string RequestId { get; init; }
    public required string Status { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
}

// ── Client → Service ─────────────────────────────────────────────

public sealed class GetStateMessage : IpcMessage
{
    public required string RequestId { get; init; }
}

public sealed class RunIntegrityMessage : IpcMessage
{
    public required string RequestId { get; init; }
    public required string Check { get; init; }
}

public sealed class UpdateSettingsMessage : IpcMessage
{
    public required AppSettings Settings { get; init; }
    public required string RequestId { get; init; }
}
