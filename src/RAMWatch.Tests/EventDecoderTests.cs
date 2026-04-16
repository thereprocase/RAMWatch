using Xunit;
using RAMWatch.Core.Decode;
using RAMWatch.Core.Models;

namespace RAMWatch.Tests;

/// <summary>
/// Tests for the deterministic, heuristic event decoder.
/// Decoders are pure functions: same MonitoredEvent → same DecodedEvent.
/// </summary>
public class EventDecoderTests
{
    // ── WHEA ─────────────────────────────────────────────────

    [Fact]
    public void Whea_DataFabric_DescribesFclkAdvice()
    {
        var mca = new McaDetails
        {
            BankNumber = 27,
            MciStatus = "0x982000000002080b",
            MciAddr = null,
            MciMisc = "0xd01a0ffe00000000",
            ApicId = 0,
            Component = "Data Fabric (PIE)",
            Classification = McaClassification.DataFabric,
            IsUncorrectable = false,
            IsOverflow = false,
            IsContextCorrupted = false,
            WheaErrorType = 10
        };
        var evt = MakeEvent("WHEA Hardware Errors", 19, EventCategory.Hardware, mca: mca);

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("Data Fabric", decoded.Title);
        Assert.Contains("Corrected", decoded.Title);
        Assert.Contains("FCLK", decoded.Why);
        Assert.Contains("VSOC", decoded.WhatToDo);
        Assert.Contains(decoded.Facts, kv => kv.Key == "MCA bank" && kv.Value == "27");
        Assert.Contains(decoded.Facts, kv => kv.Key == "MCI_STATUS" && kv.Value == "0x982000000002080b");
        Assert.Contains(decoded.Facts, kv => kv.Key == "Uncorrectable" && kv.Value == "no");
    }

    [Fact]
    public void Whea_Umc_AdvisesTimingsAndVdimm()
    {
        var mca = new McaDetails
        {
            BankNumber = 16,
            MciStatus = "0x9c20000000010135",
            ApicId = 0,
            Component = "UMC Channel 0",
            Classification = McaClassification.Umc,
            IsUncorrectable = false,
            IsOverflow = false,
            IsContextCorrupted = false,
            WheaErrorType = 3
        };
        var evt = MakeEvent("WHEA Hardware Errors", 17, EventCategory.Hardware, mca: mca);

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("UMC", decoded.Title);
        Assert.Contains("VDIMM", decoded.WhatToDo);
        Assert.Contains("ProcODT", decoded.WhatToDo);
    }

    [Fact]
    public void Whea_Uncorrectable_ChangesTitlePrefix()
    {
        var mca = new McaDetails
        {
            BankNumber = 27,
            MciStatus = "0xbc20000000002080b",
            ApicId = 0,
            Component = "Data Fabric (PIE)",
            Classification = McaClassification.DataFabric,
            IsUncorrectable = true,
            IsOverflow = false,
            IsContextCorrupted = false,
            WheaErrorType = 10
        };
        var evt = MakeEvent("Machine Check Exception", 1, EventCategory.Hardware, mca: mca);

        var decoded = EventDecoder.Decode(evt);

        Assert.StartsWith("Uncorrectable", decoded.Title);
        Assert.Contains(decoded.Facts, kv => kv.Key == "Uncorrectable" && kv.Value == "yes");
    }

    [Fact]
    public void Whea_NoMca_FallsBackToSummary()
    {
        var evt = MakeEvent("WHEA Hardware Errors", 47, EventCategory.Hardware,
                            summary: "WHEA internal error 0x4");

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("WHEA", decoded.Title);
        Assert.Contains("WHEA internal error", decoded.What);
        Assert.DoesNotContain(decoded.Facts, kv => kv.Key == "MCA bank");
    }

    // ── Bugcheck ─────────────────────────────────────────────

    [Fact]
    public void Bugcheck_0x9C_MapsToMachineCheck()
    {
        const string xml = """
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>
              <System><EventID>1001</EventID></System>
              <EventData>
                <Data Name='BugcheckCode'>156</Data>
                <Data Name='BugcheckParameter1'>0x0</Data>
                <Data Name='BugcheckParameter2'>0x0</Data>
                <Data Name='BugcheckParameter3'>0x0</Data>
                <Data Name='BugcheckParameter4'>0x0</Data>
              </EventData>
            </Event>
            """;
        var evt = MakeEvent("Kernel Bugcheck", 1001, EventCategory.Hardware, rawXml: xml);

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("MACHINE_CHECK_EXCEPTION", decoded.Title);
        Assert.Contains("FCLK", decoded.Why);
        Assert.Contains(decoded.Facts, kv => kv.Key == "Stop code" && kv.Value.Contains("0x0000009c"));
    }

    [Fact]
    public void Bugcheck_0x124_WheaUncorrectable()
    {
        const string xml = """
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>
              <System><EventID>1001</EventID></System>
              <EventData>
                <Data Name='BugcheckCode'>292</Data>
                <Data Name='BugcheckParameter1'>0x0</Data>
                <Data Name='BugcheckParameter2'>0xfffff80012345678</Data>
                <Data Name='BugcheckParameter3'>0x0</Data>
                <Data Name='BugcheckParameter4'>0x0</Data>
              </EventData>
            </Event>
            """;
        var evt = MakeEvent("Kernel Bugcheck", 1001, EventCategory.Hardware, rawXml: xml);

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("WHEA_UNCORRECTABLE_ERROR", decoded.Title);
        Assert.Contains(decoded.Facts, kv => kv.Key == "Stop code");
    }

    [Fact]
    public void Bugcheck_0x1A_MemoryManagement()
    {
        const string xml = """
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>
              <System><EventID>1001</EventID></System>
              <EventData>
                <Data Name='BugcheckCode'>26</Data>
                <Data Name='BugcheckParameter1'>0x41790</Data>
                <Data Name='BugcheckParameter2'>0xfffff7800abc</Data>
                <Data Name='BugcheckParameter3'>0x0</Data>
                <Data Name='BugcheckParameter4'>0x0</Data>
              </EventData>
            </Event>
            """;
        var evt = MakeEvent("Kernel Bugcheck", 1001, EventCategory.Hardware, rawXml: xml);

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("MEMORY_MANAGEMENT", decoded.Title);
        Assert.Contains("RAM", decoded.Why);
    }

    [Fact]
    public void Bugcheck_UnknownStopCode_FallsBackGracefully()
    {
        const string xml = """
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>
              <System><EventID>1001</EventID></System>
              <EventData>
                <Data Name='BugcheckCode'>999999</Data>
                <Data Name='BugcheckParameter1'>0x0</Data>
                <Data Name='BugcheckParameter2'>0x0</Data>
                <Data Name='BugcheckParameter3'>0x0</Data>
                <Data Name='BugcheckParameter4'>0x0</Data>
              </EventData>
            </Event>
            """;
        var evt = MakeEvent("Kernel Bugcheck", 1001, EventCategory.Hardware, rawXml: xml);

        var decoded = EventDecoder.Decode(evt);

        // Should not throw, should still produce a useful decode.
        Assert.False(string.IsNullOrEmpty(decoded.Title));
        Assert.False(string.IsNullOrEmpty(decoded.WhatToDo));
        Assert.Contains(decoded.Facts, kv => kv.Key == "Stop code");
    }

    [Fact]
    public void Bugcheck_NoXml_ReturnsUnknownStopCodeNote()
    {
        var evt = MakeEvent("Kernel Bugcheck", 1001, EventCategory.Hardware, rawXml: null);

        var decoded = EventDecoder.Decode(evt);

        Assert.Equal("Kernel Bugcheck (BSOD)", decoded.Title);
    }

    // ── Unexpected Shutdown (Kernel-Power 41) ────────────────

    [Fact]
    public void UnexpectedShutdown_WithBugcheck_PointsAtBugcheck()
    {
        const string xml = """
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>
              <System><EventID>41</EventID></System>
              <EventData>
                <Data Name='BugcheckCode'>156</Data>
                <Data Name='SleepInProgress'>0</Data>
                <Data Name='PowerButtonTimestamp'>0</Data>
              </EventData>
            </Event>
            """;
        var evt = MakeEvent("Unexpected Shutdown", 41, EventCategory.Hardware, rawXml: xml);

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("bugcheck", decoded.What.ToLowerInvariant());
        Assert.Contains("0x0000009c", decoded.What);
    }

    [Fact]
    public void UnexpectedShutdown_PowerButton_DescribesForceOff()
    {
        const string xml = """
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>
              <System><EventID>41</EventID></System>
              <EventData>
                <Data Name='BugcheckCode'>0</Data>
                <Data Name='SleepInProgress'>0</Data>
                <Data Name='PowerButtonTimestamp'>132894751234567890</Data>
              </EventData>
            </Event>
            """;
        var evt = MakeEvent("Unexpected Shutdown", 41, EventCategory.Hardware, rawXml: xml);

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("power button", decoded.What.ToLowerInvariant());
    }

    [Fact]
    public void UnexpectedShutdown_NothingLogged_TreatedAsHardKill()
    {
        const string xml = """
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>
              <System><EventID>41</EventID></System>
              <EventData>
                <Data Name='BugcheckCode'>0</Data>
                <Data Name='SleepInProgress'>0</Data>
                <Data Name='PowerButtonTimestamp'>0</Data>
              </EventData>
            </Event>
            """;
        var evt = MakeEvent("Unexpected Shutdown", 41, EventCategory.Hardware, rawXml: xml);

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("without warning", decoded.What);
        Assert.Contains("PSU", decoded.Why);
    }

    // ── Disk ─────────────────────────────────────────────────

    [Fact]
    public void Disk_Event7_BadBlock()
    {
        const string xml = """
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>
              <System><EventID>7</EventID></System>
              <EventData>
                <Data Name='DeviceName'>\Device\Harddisk0\DR0</Data>
              </EventData>
            </Event>
            """;
        var evt = MakeEvent("Disk Error", 7, EventCategory.Filesystem, rawXml: xml);

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("bad block", decoded.What.ToLowerInvariant());
        Assert.Contains("SMART", decoded.WhatToDo);
        Assert.Contains("\\Device\\Harddisk0\\DR0", decoded.Where);
    }

    [Fact]
    public void Disk_Event51_PagingError()
    {
        var evt = MakeEvent("Disk Error", 51, EventCategory.Filesystem);

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("paging", decoded.What.ToLowerInvariant());
        Assert.Contains("drive", decoded.Why.ToLowerInvariant());
    }

    // ── NTFS ─────────────────────────────────────────────────

    [Fact]
    public void Ntfs_Event55_RecommendsChkdsk()
    {
        var evt = MakeEvent("NTFS Error", 55, EventCategory.Filesystem);

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("corruption", decoded.What.ToLowerInvariant());
        Assert.Contains("chkdsk", decoded.WhatToDo.ToLowerInvariant());
    }

    [Fact]
    public void Ntfs_Event137_PreventedDamage()
    {
        var evt = MakeEvent("NTFS Error", 137, EventCategory.Filesystem);

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("prevented", decoded.What.ToLowerInvariant());
    }

    // ── Code Integrity ───────────────────────────────────────

    [Fact]
    public void CodeIntegrity_3001_UnsignedBinary()
    {
        const string xml = """
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>
              <System><EventID>3001</EventID></System>
              <EventData>
                <Data Name='FileName'>\??\C:\Drivers\sketchy.sys</Data>
              </EventData>
            </Event>
            """;
        var evt = MakeEvent("Code Integrity", 3001, EventCategory.Integrity, rawXml: xml);

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("unsigned", decoded.What.ToLowerInvariant());
        Assert.Contains("sketchy.sys", decoded.Where);
    }

    [Fact]
    public void CodeIntegrity_3033_SigningLevel()
    {
        var evt = MakeEvent("Code Integrity", 3033, EventCategory.Integrity);

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("HVCI", decoded.What);
    }

    // ── Application Error / Hang ─────────────────────────────

    [Fact]
    public void AppCrash_PositionalDataExtracted()
    {
        const string xml = """
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>
              <System><EventID>1000</EventID></System>
              <EventData>
                <Data>chrome.exe</Data>
                <Data>123.0.6312.86</Data>
                <Data>0x65f7ab12</Data>
                <Data>libfaulty.dll</Data>
                <Data>1.2.3.4</Data>
                <Data>0x65f7ab33</Data>
                <Data>0xc0000005</Data>
                <Data>0x000000000004f234</Data>
                <Data>4567</Data>
                <Data>0x01da7c1e</Data>
              </EventData>
            </Event>
            """;
        var evt = MakeEvent("Application Crash", 1000, EventCategory.Application, rawXml: xml);

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("chrome.exe", decoded.Title);
        Assert.Contains("libfaulty.dll", decoded.What);
        Assert.Contains("ACCESS_VIOLATION", decoded.What);
        Assert.Contains(decoded.Facts, kv => kv.Key == "Application" && kv.Value == "chrome.exe");
        Assert.Contains(decoded.Facts, kv => kv.Key == "Faulting module" && kv.Value == "libfaulty.dll");
    }

    [Fact]
    public void AppHang_BasicFields()
    {
        const string xml = """
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>
              <System><EventID>1002</EventID></System>
              <EventData>
                <Data>slack.exe</Data>
                <Data>4.36.140</Data>
                <Data>0x65f7ab12</Data>
                <Data>1234</Data>
                <Data>0x01da7c1e</Data>
                <Data>abc123-hang-sig</Data>
              </EventData>
            </Event>
            """;
        var evt = MakeEvent("Application Hang", 1002, EventCategory.Application, rawXml: xml);

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("slack.exe", decoded.Title);
        Assert.Contains(decoded.Facts, kv => kv.Key == "Hang signature" && kv.Value == "abc123-hang-sig");
    }

    // ── Memory Diagnostics ───────────────────────────────────

    [Fact]
    public void MemoryDiagnostics_PassEvent_ReadsAsClean()
    {
        var evt = MakeEvent("Memory Diagnostics", 1001, EventCategory.Hardware);

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("Pass", decoded.Title);
        Assert.Contains("not sufficient", decoded.Why);
    }

    [Fact]
    public void MemoryDiagnostics_FailEvent_RecommendsLoosen()
    {
        var evt = MakeEvent("Memory Diagnostics", 1002, EventCategory.Hardware);

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("Errors Detected", decoded.Title);
        Assert.Contains("Loosen", decoded.WhatToDo);
        Assert.Contains("VDIMM", decoded.WhatToDo);
    }

    // ── Generic fallback ─────────────────────────────────────

    [Fact]
    public void UnknownSource_FallsBackToGeneric()
    {
        var evt = MakeEvent("Wholly Unknown Source", 9999, EventCategory.Application,
                            summary: "Some plain summary");

        var decoded = EventDecoder.Decode(evt);

        Assert.Contains("Wholly Unknown Source", decoded.Title);
        Assert.Equal("Some plain summary", decoded.What);
    }

    // ── Determinism ──────────────────────────────────────────

    [Fact]
    public void Decoder_IsDeterministic()
    {
        var mca = new McaDetails
        {
            BankNumber = 16,
            MciStatus = "0x9c20000000010135",
            ApicId = 4,
            Component = "UMC Channel 0",
            Classification = McaClassification.Umc,
            IsUncorrectable = false,
            IsOverflow = false,
            IsContextCorrupted = false,
            WheaErrorType = 3
        };
        var evt = MakeEvent("WHEA Hardware Errors", 17, EventCategory.Hardware, mca: mca);

        var first = EventDecoder.Decode(evt);
        var second = EventDecoder.Decode(evt);

        Assert.Equal(first.Title,    second.Title);
        Assert.Equal(first.What,     second.What);
        Assert.Equal(first.Where,    second.Where);
        Assert.Equal(first.Why,      second.Why);
        Assert.Equal(first.WhatToDo, second.WhatToDo);
        Assert.Equal(first.Facts.Count, second.Facts.Count);
    }

    // ── Helpers ──────────────────────────────────────────────

    private static MonitoredEvent MakeEvent(
        string source, int eventId, EventCategory category,
        string? rawXml = null,
        McaDetails? mca = null,
        string summary = "(test summary)")
    {
        return new MonitoredEvent(
            new DateTime(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc),
            source,
            category,
            eventId,
            EventSeverity.Warning,
            summary,
            rawXml,
            mca);
    }
}
