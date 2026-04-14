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
