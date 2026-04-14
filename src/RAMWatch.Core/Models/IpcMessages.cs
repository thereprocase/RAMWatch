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

// ── Phase 3 — Client → Service ────────────────────────────────────

/// <summary>
/// Log a stability test result. Service appends to tests.json and
/// re-evaluates the LKG snapshot.
/// </summary>
public sealed class LogValidationMessage : IpcMessage
{
    public required string RequestId { get; init; }
    public required string TestTool { get; init; }
    public required string MetricName { get; init; }
    public required double MetricValue { get; init; }
    public required string MetricUnit { get; init; }
    public required bool Passed { get; init; }
    public int ErrorCount { get; init; }
    public int DurationMinutes { get; init; }
    public string? ActiveSnapshotId { get; init; }
    public string? Notes { get; init; }
}

/// <summary>
/// Trigger a manual snapshot save with an optional user-supplied label.
/// Service uses the current timing reading as the snapshot source.
/// </summary>
public sealed class SaveSnapshotMessage : IpcMessage
{
    public required string RequestId { get; init; }
    /// <summary>
    /// Optional label for the snapshot. When null or empty, the service
    /// uses a timestamp-based default label.
    /// </summary>
    public string? Label { get; init; }
}

/// <summary>
/// Request the full list of timing snapshots.
/// </summary>
public sealed class GetSnapshotsMessage : IpcMessage
{
    public required string RequestId { get; init; }
}

/// <summary>
/// Request an AI-readable digest of the last N boots.
/// </summary>
public sealed class GetDigestMessage : IpcMessage
{
    public required string RequestId { get; init; }
    /// <summary>Number of boot history entries to include. Defaults to 10.</summary>
    public int HistoryCount { get; init; } = 10;
}

/// <summary>
/// Delete a validation result by ID. Service removes the entry from
/// tests.json, re-evaluates the LKG snapshot, and broadcasts updated state.
/// </summary>
public sealed class DeleteValidationMessage : IpcMessage
{
    public required string RequestId { get; init; }
    public required string ValidationId { get; init; }
}

/// <summary>
/// Request the current timing designation map.
/// </summary>
public sealed class GetDesignationsMessage : IpcMessage
{
    public required string RequestId { get; init; }
}

/// <summary>
/// Update the timing designation map. Values must be "Manual", "Auto", or "Unknown".
/// Service persists to designations.json and broadcasts updated state.
/// </summary>
public sealed class UpdateDesignationsMessage : IpcMessage
{
    public required string RequestId { get; init; }
    /// <summary>
    /// Full replacement map. Values: "Manual", "Auto", "Unknown".
    /// </summary>
    public required Dictionary<string, string> Designations { get; init; }
}

// ── Phase 3 — Service → Client ────────────────────────────────────

/// <summary>
/// Response to GetDesignationsMessage.
/// </summary>
public sealed class DesignationsResponseMessage : IpcMessage
{
    public required string RequestId { get; init; }
    /// <summary>
    /// Current designation map. Values: "Manual", "Auto", "Unknown".
    /// </summary>
    public required Dictionary<string, string> Designations { get; init; }
}

/// <summary>
/// Response to GetSnapshotsMessage.
/// </summary>
public sealed class SnapshotsResponseMessage : IpcMessage
{
    public required string RequestId { get; init; }
    public required List<TimingSnapshot> Snapshots { get; init; }
}

/// <summary>
/// Response to GetDigestMessage.
/// </summary>
public sealed class DigestResponseMessage : IpcMessage
{
    public required string RequestId { get; init; }
    /// <summary>
    /// Plain-text digest suitable for pasting into an AI prompt.
    /// Null when no snapshot history exists yet.
    /// </summary>
    public string? DigestText { get; init; }
}

// ── Phase 3 — Snapshot management (Client → Service) ──────────────

/// <summary>
/// Permanently delete a snapshot from the journal.
/// Service removes the entry, persists, and broadcasts updated state.
/// </summary>
public sealed class DeleteSnapshotMessage : IpcMessage
{
    public required string RequestId { get; init; }
    public required string SnapshotId { get; init; }
}

/// <summary>
/// Rename a snapshot in the journal.
/// Service updates the label, persists, and broadcasts updated state.
/// </summary>
public sealed class RenameSnapshotMessage : IpcMessage
{
    public required string RequestId { get; init; }
    public required string SnapshotId { get; init; }
    public required string NewLabel { get; init; }
}
