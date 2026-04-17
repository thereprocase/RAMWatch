using Xunit;
using RAMWatch.Core.Ipc;
using RAMWatch.Core.Models;

namespace RAMWatch.Tests;

public class IpcRoundtripTests
{
    [Fact]
    public void StateMessage_RoundTrips()
    {
        var msg = CreateStateMessage();
        string json = MessageSerializer.Serialize(msg);
        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));

        var state = Assert.IsType<StateMessage>(result);
        Assert.Equal("state", state.Type);
        Assert.Equal(IpcMessage.CurrentProtocolVersion, state.ProtocolVersion);
        Assert.True(state.State.Ready);
        Assert.Equal(2, state.State.Errors.Count);
    }

    [Fact]
    public void EventMessage_RoundTrips()
    {
        var msg = new EventMessage
        {
            Type = "event",
            Event = new MonitoredEvent(
                DateTime.UtcNow,
                "WHEA Hardware Errors",
                EventCategory.Hardware,
                17,
                EventSeverity.Warning,
                "Corrected hardware error on component Memory")
        };

        string json = MessageSerializer.Serialize(msg);
        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));

        var evt = Assert.IsType<EventMessage>(result);
        Assert.Equal("WHEA Hardware Errors", evt.Event.Source);
        Assert.Equal(17, evt.Event.EventId);
        Assert.False(evt.IsCritical);
    }

    [Fact]
    public void EventMessage_Critical_SerializesIsCriticalFlag()
    {
        // RAMBurn integration §2.1: EventMessage carries an is_critical flag
        // so clients can filter critical events without re-classifying.
        var mce = new MonitoredEvent(
            DateTime.UtcNow,
            "Machine Check Exception",
            EventCategory.Hardware,
            1,
            EventSeverity.Critical,
            "Machine Check Exception");

        var msg = new EventMessage
        {
            Type = "event",
            Event = mce,
            IsCritical = mce.Severity == EventSeverity.Critical
        };

        string json = MessageSerializer.Serialize(msg);
        Assert.Contains("\"is_critical\":true", json);

        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));
        var evt = Assert.IsType<EventMessage>(result);
        Assert.True(evt.IsCritical);
        Assert.Equal(EventSeverity.Critical, evt.Event.Severity);
    }

    [Fact]
    public void ResponseMessage_RoundTrips()
    {
        var msg = new ResponseMessage
        {
            Type = "response",
            RequestId = "req-123",
            Status = "ok"
        };

        string json = MessageSerializer.Serialize(msg);
        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));

        var resp = Assert.IsType<ResponseMessage>(result);
        Assert.Equal("req-123", resp.RequestId);
        Assert.Equal("ok", resp.Status);
        Assert.Null(resp.Code);
    }

    [Fact]
    public void ErrorResponse_RoundTrips()
    {
        var msg = new ResponseMessage
        {
            Type = "response",
            RequestId = "req-456",
            Status = "error",
            Code = "unsupported_message",
            Message = "Unknown message type: futureFeature"
        };

        string json = MessageSerializer.Serialize(msg);
        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));

        var resp = Assert.IsType<ResponseMessage>(result);
        Assert.Equal("error", resp.Status);
        Assert.Equal("unsupported_message", resp.Code);
    }

    [Fact]
    public void GetStateMessage_RoundTrips()
    {
        var msg = new GetStateMessage
        {
            Type = "getState",
            RequestId = "req-789"
        };

        string json = MessageSerializer.Serialize(msg);
        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));

        var get = Assert.IsType<GetStateMessage>(result);
        Assert.Equal("req-789", get.RequestId);
    }

    [Fact]
    public void RunIntegrityMessage_RoundTrips()
    {
        var msg = new RunIntegrityMessage
        {
            Type = "runIntegrity",
            RequestId = "req-sfc",
            Check = "sfc"
        };

        string json = MessageSerializer.Serialize(msg);
        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));

        var run = Assert.IsType<RunIntegrityMessage>(result);
        Assert.Equal("sfc", run.Check);
    }

    [Fact]
    public void UpdateSettingsMessage_RoundTrips()
    {
        var settings = new AppSettings { RefreshIntervalSeconds = 30, MinimizeToTray = false };
        var msg = new UpdateSettingsMessage
        {
            Type = "updateSettings",
            RequestId = "req-settings",
            Settings = settings
        };

        string json = MessageSerializer.Serialize(msg);
        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));

        var upd = Assert.IsType<UpdateSettingsMessage>(result);
        Assert.Equal(30, upd.Settings.RefreshIntervalSeconds);
        Assert.False(upd.Settings.MinimizeToTray);
    }

    /// <summary>
    /// Ents critical finding: newlines in string fields must not break
    /// JSON-over-newline framing. System.Text.Json escapes \n as \\n
    /// in JSON output, so the only literal newline is the delimiter.
    /// </summary>
    [Fact]
    public void NewlineInStringField_DoesNotBreakFraming()
    {
        var evt = new EventMessage
        {
            Type = "event",
            Event = new MonitoredEvent(
                DateTime.UtcNow,
                "Application Error",
                EventCategory.Application,
                1000,
                EventSeverity.Warning,
                "Line one\nLine two\nLine three")
        };

        string json = MessageSerializer.Serialize(evt);

        // The serialized output should be exactly one line of JSON + one trailing newline
        var lines = json.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);

        // Should round-trip correctly with the newlines preserved
        var result = MessageSerializer.Deserialize(lines[0]);
        var roundTripped = Assert.IsType<EventMessage>(result);
        Assert.Equal("Line one\nLine two\nLine three", roundTripped.Event.Summary);
    }

    [Fact]
    public void UnknownMessageType_ReturnsNull()
    {
        string json = """{"type":"futureFeature","protocolVersion":1,"data":"test"}""";
        var result = MessageSerializer.Deserialize(json);
        Assert.Null(result);
    }

    [Fact]
    public void MalformedJson_ReturnsNull()
    {
        var result = MessageSerializer.Deserialize("{not valid json");
        Assert.Null(result);
    }

    [Fact]
    public void EmptyLine_ReturnsNull()
    {
        Assert.Null(MessageSerializer.Deserialize(""));
        Assert.Null(MessageSerializer.Deserialize("   "));
    }

    [Fact]
    public void ProtocolVersion_IsPresent()
    {
        var msg = new GetStateMessage { Type = "getState", RequestId = "r1" };
        string json = MessageSerializer.Serialize(msg);

        Assert.Contains("protocolVersion", json);
        Assert.Contains($"{IpcMessage.CurrentProtocolVersion}", json);
    }

    // ── Phase 3 message roundtrip tests ──────────────────────────────────────

    [Fact]
    public void LogValidationMessage_RoundTrips()
    {
        var msg = new LogValidationMessage
        {
            Type = "logValidation",
            RequestId = "req-val-1",
            TestTool = "Karhu",
            MetricName = "coverage",
            MetricValue = 1200.0,
            MetricUnit = "%",
            Passed = true,
            ErrorCount = 0,
            DurationMinutes = 90,
            ActiveSnapshotId = "snap-abc",
            Notes = "Ran overnight, no errors"
        };

        string json = MessageSerializer.Serialize(msg);
        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));

        var log = Assert.IsType<LogValidationMessage>(result);
        Assert.Equal("req-val-1", log.RequestId);
        Assert.Equal("Karhu", log.TestTool);
        Assert.Equal(1200.0, log.MetricValue);
        Assert.True(log.Passed);
        Assert.Equal(0, log.ErrorCount);
        Assert.Equal(90, log.DurationMinutes);
        Assert.Equal("snap-abc", log.ActiveSnapshotId);
    }

    [Fact]
    public void LogValidationMessage_OptionalFieldsOmittedWhenNull()
    {
        var msg = new LogValidationMessage
        {
            Type = "logValidation",
            RequestId = "req-val-2",
            TestTool = "TM5",
            MetricName = "cycles",
            MetricValue = 30.0,
            MetricUnit = "cycles",
            Passed = true
        };

        string json = MessageSerializer.Serialize(msg);

        // Notes and ActiveSnapshotId are null — WhenWritingNull suppresses them.
        Assert.DoesNotContain("\"notes\"", json);
        Assert.DoesNotContain("\"activeSnapshotId\"", json);

        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));
        var log = Assert.IsType<LogValidationMessage>(result);
        Assert.Null(log.Notes);
        Assert.Null(log.ActiveSnapshotId);
    }

    [Fact]
    public void GetSnapshotsMessage_RoundTrips()
    {
        var msg = new GetSnapshotsMessage
        {
            Type = "getSnapshots",
            RequestId = "req-snaps-1"
        };

        string json = MessageSerializer.Serialize(msg);
        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));

        var get = Assert.IsType<GetSnapshotsMessage>(result);
        Assert.Equal("req-snaps-1", get.RequestId);
    }

    [Fact]
    public void SnapshotsResponseMessage_RoundTrips_EmptyList()
    {
        var msg = new SnapshotsResponseMessage
        {
            Type = "snapshotsResponse",
            RequestId = "req-snaps-1",
            Snapshots = new List<TimingSnapshot>()
        };

        string json = MessageSerializer.Serialize(msg);
        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));

        var resp = Assert.IsType<SnapshotsResponseMessage>(result);
        Assert.Equal("req-snaps-1", resp.RequestId);
        Assert.Empty(resp.Snapshots);
    }

    [Fact]
    public void SnapshotsResponseMessage_RoundTrips_WithSnapshot()
    {
        var snap = new TimingSnapshot
        {
            SnapshotId = "snap-xyz",
            Timestamp = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc),
            BootId = "boot-001",
            CL = 36,
            MemClockMhz = 2000
        };

        var msg = new SnapshotsResponseMessage
        {
            Type = "snapshotsResponse",
            RequestId = "req-snaps-2",
            Snapshots = new List<TimingSnapshot> { snap }
        };

        string json = MessageSerializer.Serialize(msg);
        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));

        var resp = Assert.IsType<SnapshotsResponseMessage>(result);
        Assert.Single(resp.Snapshots);
        Assert.Equal("snap-xyz", resp.Snapshots[0].SnapshotId);
        Assert.Equal(36, resp.Snapshots[0].CL);
        Assert.Equal(2000, resp.Snapshots[0].MemClockMhz);
    }

    [Fact]
    public void GetDigestMessage_RoundTrips()
    {
        var msg = new GetDigestMessage
        {
            Type = "getDigest",
            RequestId = "req-digest-1",
            HistoryCount = 5
        };

        string json = MessageSerializer.Serialize(msg);
        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));

        var get = Assert.IsType<GetDigestMessage>(result);
        Assert.Equal("req-digest-1", get.RequestId);
        Assert.Equal(5, get.HistoryCount);
    }

    [Fact]
    public void GetDigestMessage_DefaultHistoryCount()
    {
        var msg = new GetDigestMessage
        {
            Type = "getDigest",
            RequestId = "req-digest-2"
        };

        Assert.Equal(10, msg.HistoryCount);
    }

    [Fact]
    public void DigestResponseMessage_RoundTrips_WithText()
    {
        var msg = new DigestResponseMessage
        {
            Type = "digestResponse",
            RequestId = "req-digest-1",
            DigestText = "Boot 1: CL36-36-36-76 2000MHz\nBoot 2: CL36-36-36-76 2000MHz"
        };

        string json = MessageSerializer.Serialize(msg);
        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));

        var resp = Assert.IsType<DigestResponseMessage>(result);
        Assert.Equal("req-digest-1", resp.RequestId);
        Assert.NotNull(resp.DigestText);
        Assert.Contains("CL36", resp.DigestText);
    }

    [Fact]
    public void DigestResponseMessage_RoundTrips_NullText()
    {
        var msg = new DigestResponseMessage
        {
            Type = "digestResponse",
            RequestId = "req-digest-2",
            DigestText = null
        };

        string json = MessageSerializer.Serialize(msg);

        // Null field suppressed by WhenWritingNull.
        Assert.DoesNotContain("\"digestText\"", json);

        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));
        var resp = Assert.IsType<DigestResponseMessage>(result);
        Assert.Null(resp.DigestText);
    }

    // ── New Phase 3 messages ──────────────────────────────────────────────────

    [Fact]
    public void DeleteValidationMessage_RoundTrips()
    {
        var msg = new DeleteValidationMessage
        {
            Type         = "deleteValidation",
            RequestId    = "req-del-1",
            ValidationId = "abc123"
        };

        string json = MessageSerializer.Serialize(msg);
        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));

        var del = Assert.IsType<DeleteValidationMessage>(result);
        Assert.Equal("req-del-1", del.RequestId);
        Assert.Equal("abc123", del.ValidationId);
    }

    [Fact]
    public void GetDesignationsMessage_RoundTrips()
    {
        var msg = new GetDesignationsMessage
        {
            Type      = "getDesignations",
            RequestId = "req-des-1"
        };

        string json = MessageSerializer.Serialize(msg);
        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));

        var get = Assert.IsType<GetDesignationsMessage>(result);
        Assert.Equal("req-des-1", get.RequestId);
    }

    [Fact]
    public void UpdateDesignationsMessage_RoundTrips()
    {
        var msg = new UpdateDesignationsMessage
        {
            Type         = "updateDesignations",
            RequestId    = "req-des-2",
            Designations = new Dictionary<string, string>
            {
                ["CL"]    = "Manual",
                ["RCDRD"] = "Auto",
                ["tRFC"]  = "Unknown"
            }
        };

        string json = MessageSerializer.Serialize(msg);
        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));

        var upd = Assert.IsType<UpdateDesignationsMessage>(result);
        Assert.Equal("req-des-2", upd.RequestId);
        Assert.Equal(3, upd.Designations.Count);
        Assert.Equal("Manual",  upd.Designations["CL"]);
        Assert.Equal("Auto",    upd.Designations["RCDRD"]);
        Assert.Equal("Unknown", upd.Designations["tRFC"]);
    }

    [Fact]
    public void DesignationsResponseMessage_RoundTrips()
    {
        var msg = new DesignationsResponseMessage
        {
            Type         = "designationsResponse",
            RequestId    = "req-des-1",
            Designations = new Dictionary<string, string>
            {
                ["CL"]    = "Manual",
                ["RCDRD"] = "Auto"
            }
        };

        string json = MessageSerializer.Serialize(msg);
        var result = MessageSerializer.Deserialize(json.TrimEnd('\n'));

        var resp = Assert.IsType<DesignationsResponseMessage>(result);
        Assert.Equal("req-des-1", resp.RequestId);
        Assert.Equal(2, resp.Designations.Count);
        Assert.Equal("Manual", resp.Designations["CL"]);
    }

    [Fact]
    public void ProtocolMismatch_MessageWithWrongVersion_StillDeserializes()
    {
        // MessageSerializer.Deserialize is unaware of version — it hands the
        // message to RamWatchService which performs the version check.
        // The deserializer must not throw or return null for a structurally valid
        // message whose protocolVersion happens to be wrong.
        string json = """{"type":"getState","requestId":"r1","protocolVersion":999}""";
        var result = MessageSerializer.Deserialize(json);

        var msg = Assert.IsType<GetStateMessage>(result);
        Assert.Equal(999, msg.ProtocolVersion);
    }

    private static StateMessage CreateStateMessage()
    {
        return new StateMessage
        {
            Type = "state",
            State = new ServiceState
            {
                Timestamp = DateTime.UtcNow,
                BootTime = DateTime.UtcNow.AddHours(-3),
                Ready = true,
                DriverStatus = "not_found",
                ServiceUptime = TimeSpan.FromHours(3),
                Errors =
                [
                    new ErrorSource("WHEA Hardware Errors", EventCategory.Hardware, 0, null),
                    new ErrorSource("Application Error", EventCategory.Application, 2, DateTime.UtcNow)
                ],
                Integrity = new IntegrityState(0, IntegrityCheckStatus.NotRun, IntegrityCheckStatus.NotRun)
            }
        };
    }
}
