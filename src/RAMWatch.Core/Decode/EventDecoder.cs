using System.Globalization;
using RAMWatch.Core.Models;

namespace RAMWatch.Core.Decode;

/// <summary>
/// Deterministic, heuristic decoder for monitored events.
/// Pure function: a given MonitoredEvent always produces the same DecodedEvent.
///
/// Each WatchedSource gets its own per-EventId decoder. Decoders parse the raw
/// event XML, extract relevant fields, and produce four prose sections plus a
/// facts list. Heuristics prefer hardcoded mappings (well-known stop codes,
/// status fields, MCA bank classifications) over anything probabilistic.
/// </summary>
public static class EventDecoder
{
    public static DecodedEvent Decode(MonitoredEvent evt)
    {
        return evt.Source switch
        {
            "WHEA Hardware Errors"     => DecodeWhea(evt),
            "Machine Check Exception"  => DecodeWhea(evt),
            "Kernel WHEA Errors"       => DecodeWhea(evt),
            "PCIe Bus Errors"          => DecodePcie(evt),
            "Kernel Bugcheck"          => DecodeBugcheck(evt),
            "Unexpected Shutdown"      => DecodeUnexpectedShutdown(evt),
            "Disk Error"               => DecodeDisk(evt),
            "NTFS Error"               => DecodeNtfs(evt),
            "Volume Shadow Copy"       => DecodeVolSnap(evt),
            "Code Integrity"           => DecodeCodeIntegrity(evt),
            "Filter Manager"           => DecodeFilterManager(evt),
            "Application Crash"        => DecodeAppCrash(evt),
            "Application Hang"         => DecodeAppHang(evt),
            "Memory Diagnostics"       => DecodeMemoryDiagnostics(evt),
            _                          => DecodeGeneric(evt)
        };
    }

    // ── WHEA ─────────────────────────────────────────────────

    private static DecodedEvent DecodeWhea(MonitoredEvent evt)
    {
        var mca = evt.Mca;
        var facts = new List<KeyValuePair<string, string>>
        {
            new("Event ID", evt.EventId.ToString(CultureInfo.InvariantCulture)),
            new("Logged at", evt.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
        };

        if (mca is null)
        {
            // Some WHEA events (e.g., id 47/48) carry no MCA bank — fall back to summary.
            return new DecodedEvent(
                Title:    WheaIdName(evt.EventId),
                What:     evt.Summary,
                Where:    "Reported by Windows Hardware Error Architecture (WHEA).",
                Why:      "WHEA events without MCA payload are still hardware-level signals from the platform.",
                WhatToDo: "Check the raw event in Event Viewer for vendor-specific detail. Repeat hits without obvious cause are still worth treating as instability.",
                Facts:    facts);
        }

        facts.Add(new("MCA bank", mca.BankNumber.ToString(CultureInfo.InvariantCulture)));
        facts.Add(new("Component", mca.Component));
        facts.Add(new("Classification", mca.Classification.ToString()));
        facts.Add(new("MCI_STATUS", mca.MciStatus));
        if (mca.MciAddr is not null) facts.Add(new("MCI_ADDR", mca.MciAddr));
        if (mca.MciMisc is not null) facts.Add(new("MCI_MISC", mca.MciMisc));
        facts.Add(new("APIC ID", mca.ApicId.ToString(CultureInfo.InvariantCulture)));
        facts.Add(new("Uncorrectable", mca.IsUncorrectable ? "yes" : "no"));
        if (mca.IsOverflow) facts.Add(new("Overflow", "yes — additional errors lost"));
        if (mca.IsContextCorrupted) facts.Add(new("Context corrupted", "yes — PCC bit set"));
        if (mca.WheaErrorType > 0) facts.Add(new("WHEA error type", WheaErrorTypeName(mca.WheaErrorType)));

        string severity = mca.IsUncorrectable ? "Uncorrectable" : "Corrected";
        string title = $"{severity} MCE — {mca.Component}";

        string what = mca.IsUncorrectable
            ? $"An uncorrectable machine check exception fired in {mca.Component}. Windows survived to log it but this is the same family of error that BSODs the system if it reaches the wrong code path."
            : $"A corrected machine check exception fired in {mca.Component}. Hardware caught and corrected the error before it reached software, but the underlying instability is real.";

        string where = $"MCA bank {mca.BankNumber} on the processor with APIC ID {mca.ApicId}. Component classification: {mca.Classification}.";

        string why = mca.Classification switch
        {
            McaClassification.DataFabric => "Data Fabric / PIE errors are the canonical FCLK instability signal on Zen. They appear when the Infinity Fabric clock is too high or the SoC voltage rail is too low to sustain link training.",
            McaClassification.Umc        => "UMC errors point at the memory controller. Causes include too-tight memory timings, marginal VDIMM, weak ProcODT, or RTT settings that can't drive the bus cleanly.",
            McaClassification.L3Cache    => "L3 cache errors typically track core clock or core voltage stability. On Zen 3 V-Cache parts, also check VDDG_CCD which feeds the cache and IF link.",
            McaClassification.Core       => "Per-core errors usually mean the affected core is being clocked or undervolted past its limit. Check Curve Optimizer offsets or PBO settings for that core.",
            McaClassification.Pcie       => "PCIe / NBIO errors share a fabric with the memory controller on Zen. They can correlate with FCLK instability or with PCIe Gen4 signal integrity on the GPU/NVMe link.",
            McaClassification.IoHub      => "IO Hub / IOMMU errors usually involve DMA remapping. Check for buggy drivers or aggressive RAM-adjacent settings before assuming hardware failure.",
            _                             => "MCA errors are platform-level reports. The bank-to-component mapping is best-effort; check the raw MCI_STATUS for vendor-specific decode."
        };

        string whatToDo = mca.Classification switch
        {
            McaClassification.DataFabric => "Raise VSOC by 25 mV (cap at 1.2 V) or drop FCLK by 33-67 MHz and retest. On Zen 3, VDDG IOD must stay below VSOC by ~50 mV or the LDO clamps the rail.",
            McaClassification.Umc        => "Loosen RAM timings (start with tRFC, then tCL/tRCD), bump VDIMM 20-40 mV, or revisit ProcODT and RTT. Test with a memory burner before declaring stable.",
            McaClassification.L3Cache    => "Reduce CO offsets on the affected core or raise VCORE / LLC. On X3D parts this can also indicate VDDG_CCD too low for the L3/IF interconnect.",
            McaClassification.Core       => "Identify the affected core via APIC ID, ease its CO offset, and rerun a single-thread stress test (Y-cruncher, Prime95 small FFTs).",
            McaClassification.Pcie       => "Check PCIe lane bifurcation, GPU/NVMe seating, and recent driver updates. If clustered with Data Fabric errors, treat as FCLK instability.",
            _                             => "Repeat the workload to see if the error reproduces. Single isolated correctable errors are common; bursts or uncorrectable hits demand action."
        };

        return new DecodedEvent(title, what, where, why, whatToDo, facts);
    }

    private static string WheaIdName(int id) => id switch
    {
        1  => "WHEA Machine Check Exception",
        17 => "WHEA Memory Error",
        18 => "WHEA Cache Hierarchy Error",
        19 => "WHEA Bus/Interconnect Error",
        20 => "WHEA PCIe Error",
        46 => "WHEA Corrected Error (PMC)",
        47 => "WHEA Internal Error",
        48 => "WHEA Persistent Hardware Error",
        _  => $"WHEA Event {id}"
    };

    private static string WheaErrorTypeName(int t) => t switch
    {
        0  => "Internal",
        1  => "Bus",
        2  => "Memory access",
        3  => "Memory hierarchy",
        4  => "Micro-architectural",
        5  => "TLB",
        6  => "Cache hierarchy",
        7  => "Functional unit",
        8  => "Self-test",
        10 => "Bus/Interconnect",
        _  => $"Type {t}"
    };

    // ── PCIe (Kernel-PCI) ────────────────────────────────────

    private static DecodedEvent DecodePcie(MonitoredEvent evt)
    {
        var data = EventXml.ReadNamedData(evt.RawXml);
        var facts = new List<KeyValuePair<string, string>> { new("Event ID", evt.EventId.ToString(CultureInfo.InvariantCulture)) };
        foreach (var kv in data) facts.Add(new(kv.Key, kv.Value));

        string severity = evt.EventId is 1 or 3 or 5 or 7 ? "Fatal" : "Correctable";

        return new DecodedEvent(
            Title:    $"{severity} PCIe Bus Error (id {evt.EventId})",
            What:     $"{severity} PCIe Advanced Error Reporting event. {evt.Summary}",
            Where:    "PCI Express root complex or device. Check the fields below for the device path and error register dump.",
            Why:      "On Zen, the PCIe controllers share the Infinity Fabric. AER bursts can correlate with FCLK instability, GPU PCIe Gen4 signal issues, or a marginal NVMe link.",
            WhatToDo: "Cross-reference with WHEA events at the same timestamp. If they cluster, treat as fabric instability (raise VSOC, drop FCLK). Otherwise reseat the device or drop the link to Gen3.",
            Facts:    facts);
    }

    // ── Bugcheck (WER 1001) ──────────────────────────────────

    private static DecodedEvent DecodeBugcheck(MonitoredEvent evt)
    {
        var data = EventXml.ReadNamedData(evt.RawXml);
        var facts = new List<KeyValuePair<string, string>>();

        string? bugcheckHex = data.TryGetValue("BugcheckCode", out var bc) ? FormatHex(ParseUlong(bc)) : null;
        string? p1 = data.TryGetValue("BugcheckParameter1", out var v1) ? v1 : null;
        string? p2 = data.TryGetValue("BugcheckParameter2", out var v2) ? v2 : null;
        string? p3 = data.TryGetValue("BugcheckParameter3", out var v3) ? v3 : null;
        string? p4 = data.TryGetValue("BugcheckParameter4", out var v4) ? v4 : null;
        string? overrideRaw = data.TryGetValue("BugcheckOverride", out var bo) ? bo : null;

        var (codeName, codeWhy, codeAction) = ClassifyBugcheck(bugcheckHex);

        if (bugcheckHex is not null) facts.Add(new("Stop code", $"{bugcheckHex}  ({codeName})"));
        if (p1 is not null) facts.Add(new("Parameter 1", FormatHexParam(p1)));
        if (p2 is not null) facts.Add(new("Parameter 2", FormatHexParam(p2)));
        if (p3 is not null) facts.Add(new("Parameter 3", FormatHexParam(p3)));
        if (p4 is not null) facts.Add(new("Parameter 4", FormatHexParam(p4)));
        if (overrideRaw is not null) facts.Add(new("Override", overrideRaw));

        bool anyParam = p1 is not null || p2 is not null || p3 is not null || p4 is not null;
        string whereText = anyParam
            ? "Kernel-mode crash. The parameters above are stop-code-specific — for memory errors they're often the faulting address, an access type, and a probe context."
            : "Kernel-mode crash. This event payload did not include the four BugcheckParameter fields; the dump file is the only source for them.";

        return new DecodedEvent(
            Title:    bugcheckHex is null ? "Kernel Bugcheck (BSOD)" : $"BSOD — {codeName}",
            What:     bugcheckHex is null
                        ? "Windows recorded a kernel bugcheck (BSOD). The stop code wasn't extracted from this event payload."
                        : $"The system bugchecked with stop code {bugcheckHex} ({codeName}). The crash dump (if enabled) is in C:\\Windows\\MEMORY.DMP or \\Minidump\\.",
            Where:    whereText,
            Why:      codeWhy,
            WhatToDo: codeAction,
            Facts:    facts);
    }

    private static (string Name, string Why, string Action) ClassifyBugcheck(string? codeHex)
    {
        if (codeHex is null)
            return ("UNKNOWN", "Bugchecks indicate a kernel-mode failure that Windows could not recover from.", "Check the dump file with WinDbg or BlueScreenView.");

        return codeHex.ToLowerInvariant() switch
        {
            "0x0000009c" or "0x9c" => ("MACHINE_CHECK_EXCEPTION",
                "Direct escalation from a hardware MCE that exceeded WHEA's correction capability. On Zen this is almost always FCLK / UMC / SoC voltage instability.",
                "Treat as a hardware error: drop FCLK by 33-67 MHz, raise VSOC by 25 mV, loosen RAM timings, or all three. Cross-reference WHEA events at the same timestamp."),

            "0x00000124" or "0x124" => ("WHEA_UNCORRECTABLE_ERROR",
                "An uncorrectable hardware error fired. Parameter 1 identifies the source (0x0 = MCE, 0x1 = corrected platform, 0x2 = NMI, 0x4 = BOOT). Parameter 2 points at a WHEA_ERROR_RECORD.",
                "Capture the full dump and decode the WHEA_ERROR_RECORD with !whea in WinDbg. Same tuning advice as 0x9C — assume hardware-level instability."),

            "0x00000101" or "0x101" => ("CLOCK_WATCHDOG_TIMEOUT",
                "A processor failed to send a clock interrupt within the expected interval. Common cause: core clock instability under heavy load, often Curve Optimizer or PBO too aggressive.",
                "Reduce CO offsets, lower the boost ceiling, or raise VCORE LLC. Can also indicate L3 / VDDG_CCD margin on V-Cache parts."),

            "0x0000001a" or "0x1a" => ("MEMORY_MANAGEMENT",
                "The memory manager detected a corrupt page table or invalid physical page reference. Strongly correlates with bad RAM, marginal VDIMM, or unstable memory timings.",
                "Run a memory stress test (TM5 absolut, OCCT, MemTest86). If a single core triggers it under load, suspect IOD/CCD voltage. Otherwise loosen tRFC/tCL and bump VDIMM."),

            "0x0000004e" or "0x4e" => ("PFN_LIST_CORRUPT",
                "The kernel found the page frame number list damaged. Usually means RAM is silently flipping bits — a tuning/voltage problem more often than a dead DIMM.",
                "Loosen primary timings (tCL, tRCD, tRP) and tRFC. Bump VDIMM 20-40 mV. Run a memory stress test before re-tightening."),

            "0x00000050" or "0x50" => ("PAGE_FAULT_IN_NONPAGED_AREA",
                "A driver or the kernel referenced memory that wasn't paged in. Top causes: bad RAM, bad driver, or — under tuning — a bit flip in a kernel structure.",
                "If recurring after a memory tweak, revert and stress-test. If after a driver install/update, roll back the driver."),

            "0x0000007e" or "0x7e" => ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED",
                "A system thread hit an unhandled exception. Parameter 1 is the exception code, parameter 2 is the faulting address. Most often a driver bug, but RAM corruption can mimic this.",
                "Identify the faulting module from the dump. If it's a recently updated driver, roll it back. If memory tuning is in flight, revert and retest."),

            "0x0000003b" or "0x3b" => ("SYSTEM_SERVICE_EXCEPTION",
                "An exception was raised while executing a system service routine. Like 0x7E, usually a driver bug or RAM corruption.",
                "Same approach as 0x7E. If the dump points at a kernel module, treat as RAM-suspect; pin the cause to driver vs. tuning before changing voltages."),

            "0x0000007f" or "0x7f" => ("UNEXPECTED_KERNEL_MODE_TRAP",
                "The CPU raised a trap the kernel didn't expect. Parameter 1 = trap number; double-fault (0x8) is common with marginal CPU stability.",
                "Reduce CO offsets and PBO ceiling. Test single-core (Y-cruncher) and all-core (Prime95) loads separately."),

            "0x000000d1" or "0xd1" => ("DRIVER_IRQL_NOT_LESS_OR_EQUAL",
                "A driver tried to access pageable memory at a too-high IRQL. Driver bug — but bad RAM can corrupt the IRQL value on the stack and produce identical symptoms.",
                "Identify the faulting module from the dump. If RAM tuning is in flight, revert as a control test."),

            "0x00000139" or "0x139" => ("KERNEL_SECURITY_CHECK_FAILURE",
                "A kernel data structure failed an integrity check. Causes: driver bug, malware, or — under aggressive tuning — RAM bit flips that corrupt structure cookies.",
                "If recurring with RAM tuning, loosen primary timings and bump VDIMM as a control test. Otherwise track the faulting module."),

            "0x000000c5" or "0xc5" => ("DRIVER_CORRUPTED_EXPOOL",
                "Pool memory was overwritten by a driver — but the same symptom appears when RAM corrupts pool entries. On a tuning-active system, this is usually memory-side.",
                "Loosen RAM timings and bump VDIMM. If it persists with conservative RAM settings, investigate the faulting driver."),

            "0x000000c2" or "0xc2" => ("BAD_POOL_CALLER",
                "A driver made an invalid pool allocation call, or pool metadata was corrupted. RAM instability commonly produces this on overclocked systems.",
                "Same as 0xC5 — start with RAM-side as a control."),

            "0x00000119" or "0x119" => ("VIDEO_SCHEDULER_INTERNAL_ERROR",
                "GPU scheduler hit an unexpected condition. Often driver-side, but can correlate with PCIe or fabric instability on Zen platforms.",
                "Update or roll back GPU drivers. If clustered with WHEA Bus/Interconnect events, treat as fabric instability."),

            "0x000000ef" or "0xef" => ("CRITICAL_PROCESS_DIED",
                "A required system process exited unexpectedly. Causes range from corrupted system files (run SFC) to RAM-induced bit flips in process pages.",
                "Run sfc /scannow and DISM RestoreHealth. If memory tuning is in flight, revert as a control."),

            _ => ($"STOP {codeHex.ToUpperInvariant()}",
                "This stop code is not in the local lookup table. The four parameters are code-specific — see Microsoft's bug check reference for decode.",
                "Dump analysis with WinDbg gives the most reliable next step. If RAM tuning is in flight, revert as a control test before chasing drivers.")
        };
    }

    // ── Unexpected Shutdown (Kernel-Power 41) ────────────────

    private static DecodedEvent DecodeUnexpectedShutdown(MonitoredEvent evt)
    {
        var data = EventXml.ReadNamedData(evt.RawXml);
        var facts = new List<KeyValuePair<string, string>> { new("Event ID", "41 (Kernel-Power)") };

        string? bcCode = data.TryGetValue("BugcheckCode", out var bc) ? bc : null;
        string? sleepInProgress = data.TryGetValue("SleepInProgress", out var sip) ? sip : null;
        string? powerButton = data.TryGetValue("PowerButtonTimestamp", out var pbt) ? pbt : null;

        bool hasBugcheck = bcCode is not null && ParseUlong(bcCode) != 0;
        bool cleanPowerButton = powerButton is not null && powerButton != "0";

        foreach (var kv in data) facts.Add(new(kv.Key, kv.Value));

        string what;
        string why;
        string whatToDo;

        if (hasBugcheck)
        {
            string codeHex = FormatHex(ParseUlong(bcCode!));
            // Reuse the bugcheck classifier so the user sees the symbolic name
            // and a one-line "why" without having to cross-reference a separate
            // 1001 event.
            var (codeName, _, _) = ClassifyBugcheck(codeHex);
            what = $"The system rebooted following a kernel bugcheck. Stop code: {codeHex} ({codeName}). See the matching Kernel Bugcheck event (id 1001) for full parameter detail.";
            why = "A bugcheck-triggered Kernel-Power 41 means the BSOD path completed and the system reset. The stop code is the real signal — chase that, not the 41 itself.";
            whatToDo = "Open the matching WER bugcheck event (id 1001) for the same timestamp. If memory tuning is active, revert and retest before changing anything else.";
        }
        else if (cleanPowerButton)
        {
            what = "The system was powered off by a long press of the physical power button (or equivalent reset).";
            why = "This is the cleanest of the bad shutdown reasons — the OS knows the user (or BIOS) forced it. Often happens after a hang where the user gave up waiting.";
            whatToDo = "If this followed a hang, treat as instability: capture what the system was doing and check WHEA / driver events at the same timestamp.";
        }
        else if (IsTruthy(sleepInProgress))
        {
            what = "Power was lost while the system was entering or leaving sleep.";
            why = "Sleep transitions exercise SoC voltage rails and the IF retraining path — instability here often points at marginal VSOC or VDDG.";
            whatToDo = "Disable sleep as a control test. If the issue resolves, suspect SoC voltage or VDDP/VDDG margin during S-state transitions.";
        }
        else
        {
            what = "The system shut down without warning — no bugcheck, no power button, no clean OS shutdown.";
            why = "A 'pure' Kernel-Power 41 (no bugcheck, no power button) means the platform died too fast to log anything. Common causes: PSU drop-out, hard hang, thermal trip past the silicon limit, or motherboard VRM event.";
            whatToDo = "Check temperatures (Tjmax trip would NOT log a bugcheck), PSU rails under load, and PBO/CO settings. If memory tuning is in flight, revert as a control.";
        }

        return new DecodedEvent(
            Title:    "Unexpected Shutdown (Kernel-Power 41)",
            What:     what,
            Where:    "Kernel-Power subsystem. The fields below reflect what Windows could record before going down.",
            Why:      why,
            WhatToDo: whatToDo,
            Facts:    facts);
    }

    // ── Disk ─────────────────────────────────────────────────

    private static DecodedEvent DecodeDisk(MonitoredEvent evt)
    {
        var data = EventXml.ReadNamedData(evt.RawXml);
        var facts = new List<KeyValuePair<string, string>> { new("Event ID", evt.EventId.ToString(CultureInfo.InvariantCulture)) };
        foreach (var kv in data) facts.Add(new(kv.Key, kv.Value));

        string device = data.TryGetValue("DeviceName", out var dn) ? dn
                      : data.TryGetValue("Device", out var d2) ? d2
                      : "(device not in event payload)";

        var (what, why, action) = evt.EventId switch
        {
            7  => ("A disk reported a bad block (read or write retry exceeded).",
                   "Bad block events are early-warning signals from the drive's bad-block remapping pool. One per year is noise; bursts are not.",
                   "Check SMART attributes (Reallocated Sectors, Pending, Uncorrectable). If reallocations are climbing, plan replacement."),
            11 => ("The storage driver detected a controller error on the listed device.",
                   "Controller errors usually indicate cable, port, or PSU issues for SATA/SAS, or PCIe link errors for NVMe. Can also be triggered by aggressive PCIe Gen4 tuning.",
                   "Reseat data and power cables. For NVMe, check whether PCIe Gen4 is stable (drop to Gen3 as a control test)."),
            15 => ("The device wasn't ready for access at the time of the request — usually a cold-start or sleep-resume race.",
                   "Common at boot if the drive enumerates after the OS asks for it. Persistent 15s under steady-state are abnormal.",
                   "Check whether the device powers on within the BIOS POST budget. For external drives, the cause is almost always the connection."),
            51 => ("A paging operation failed. The OS couldn't read or write a page to the swap file.",
                   "Paging errors strongly suggest hardware-level storage trouble. Combined with NTFS or disk events, they indicate a failing drive or controller.",
                   "Run the manufacturer's diagnostic tool. Capture SMART data. Treat as drive-suspect until proven otherwise."),
            52 => ("The disk reported a warning — typically write cache disabled, or low-level diagnostic info from the drive firmware.",
                   "Less severe than a hard error. Persistent 52s often track a drive that has degraded but not yet failed.",
                   "Check SMART for the listed device. Confirm with the manufacturer's diagnostic tool."),
            _  => ($"Disk event {evt.EventId} from {device}.",
                   "Disk-class events vary widely — see the fields below for specifics.",
                   "If clustered with NTFS / paging events, escalate to drive replacement or controller troubleshooting.")
        };

        return new DecodedEvent(
            Title:    $"Disk Event {evt.EventId}",
            What:     what,
            Where:    $"Device: {device}",
            Why:      why,
            WhatToDo: action,
            Facts:    facts);
    }

    // ── NTFS ─────────────────────────────────────────────────

    private static DecodedEvent DecodeNtfs(MonitoredEvent evt)
    {
        var data = EventXml.ReadNamedData(evt.RawXml);
        var facts = new List<KeyValuePair<string, string>> { new("Event ID", evt.EventId.ToString(CultureInfo.InvariantCulture)) };
        foreach (var kv in data) facts.Add(new(kv.Key, kv.Value));

        var (what, why, action) = evt.EventId switch
        {
            55  => ("NTFS detected file system corruption on a volume.",
                    "55 is the canonical 'something is wrong with this volume' signal. Causes: bad RAM (corrupted writes), bad sectors, or a driver writing garbage.",
                    "Run chkdsk on the affected volume. If RAM tuning is active, revert as a control. If the volume keeps re-corrupting after chkdsk, suspect drive."),
            98  => ("NTFS encountered a transactional or volume-wide warning (often related to the NTFS log file).",
                    "98s usually surface during heavy I/O on a stressed volume. Not always a hardware signal but worth noting.",
                    "Check whether disk events fire at the same time. If so, treat as drive-class trouble."),
            137 => ("NTFS prevented an inconsistency that would have damaged the volume.",
                    "137 is NTFS catching itself — the data on disk is still consistent but the situation that triggered it is abnormal.",
                    "Check for concurrent disk events. If recurring, run chkdsk and review SMART data."),
            140 => ("NTFS detected corruption during a metadata update (transactional log replay).",
                    "Similar to 55 in severity. Can also indicate an unsafe shutdown / power event corrupted the journal.",
                    "Run chkdsk. If correlated with Kernel-Power 41 events, the root cause is the prior crash, not NTFS."),
            _   => ($"NTFS event {evt.EventId}.",
                    "NTFS events vary — see the fields below.",
                    "Run chkdsk if corruption is alleged. Cross-reference disk events for the same volume.")
        };

        return new DecodedEvent(
            Title:    $"NTFS Event {evt.EventId}",
            What:     what,
            Where:    "NTFS file system driver. The fields below identify the volume and the offending operation.",
            Why:      why,
            WhatToDo: action,
            Facts:    facts);
    }

    // ── VolSnap (Volume Shadow Copy) ─────────────────────────

    private static DecodedEvent DecodeVolSnap(MonitoredEvent evt)
    {
        var data = EventXml.ReadNamedData(evt.RawXml);
        var facts = new List<KeyValuePair<string, string>> { new("Event ID", evt.EventId.ToString(CultureInfo.InvariantCulture)) };
        foreach (var kv in data) facts.Add(new(kv.Key, kv.Value));

        var (what, action) = evt.EventId switch
        {
            14 => ("Shadow copy storage location is unavailable.", "Verify the shadow copy storage volume exists and has free space."),
            25 => ("A shadow copy was aborted because the volume could not be written.", "Check disk events on the affected volume. May indicate I/O error during snapshot creation."),
            35 => ("Shadow copy storage was unable to grow.", "Free space on the shadow copy storage volume."),
            36 => ("Oldest shadow copy was deleted to make room.", "Informational — shadow copy storage is at its size limit."),
            _  => ($"VolSnap event {evt.EventId}.", "See the fields below for specifics.")
        };

        return new DecodedEvent(
            Title:    $"Volume Shadow Copy Event {evt.EventId}",
            What:     what,
            Where:    "VolSnap (Volume Shadow Copy) driver.",
            Why:      "Shadow copy events are usually administrative (low storage) rather than hardware. Worth noting if they coincide with disk or NTFS errors.",
            WhatToDo: action,
            Facts:    facts);
    }

    // ── Code Integrity ───────────────────────────────────────

    private static DecodedEvent DecodeCodeIntegrity(MonitoredEvent evt)
    {
        var data = EventXml.ReadNamedData(evt.RawXml);
        var facts = new List<KeyValuePair<string, string>> { new("Event ID", evt.EventId.ToString(CultureInfo.InvariantCulture)) };
        foreach (var kv in data) facts.Add(new(kv.Key, kv.Value));

        string fileName = data.TryGetValue("FileNameLength", out _) && data.TryGetValue("FileName", out var fn) ? fn
                        : data.TryGetValue("FileName", out var fn2) ? fn2
                        : "(unspecified file)";

        var (what, why, action) = evt.EventId switch
        {
            3001 => ("An unsigned binary attempted to load into the kernel.",
                     "Code Integrity blocks unsigned drivers by default. The attempt was denied.",
                     "Identify the source. Anti-cheat, hypervisors, and old hardware utilities are common offenders."),
            3002 => ("A binary signed with a revoked certificate attempted to load.",
                     "The driver's signing chain is no longer trusted (typically a vendor cert revocation).",
                     "Update to a current driver from the vendor."),
            3003 => ("A binary failed page-hash verification.",
                     "The on-disk file's hash does not match its signed manifest. Causes: tampered file, corrupted disk write, or RAM bit flip during paging.",
                     "Reinstall the driver. If recurring, run chkdsk on the system volume; if memory tuning is active, revert as a control."),
            3004 => ("A binary's catalog signature could not be verified.",
                     "The catalog file describing this binary's signature is missing or corrupt.",
                     "Run sfc /scannow and DISM RestoreHealth. Reinstall the affected driver."),
            3033 => ("A binary did not meet the system's signing-level requirements (HVCI / kernel-mode CI policy).",
                     "Custom CI policy or HVCI is rejecting drivers that would have loaded on a default install.",
                     "Either update the driver to a HVCI-compatible signed version or relax the CI policy if you control it.")
        ,
            _    => ($"Code Integrity event {evt.EventId}.",
                     "See the fields below for specifics.",
                     "Identify the binary and either update it or trust the source.")
        };

        return new DecodedEvent(
            Title:    $"Code Integrity Event {evt.EventId}",
            What:     what,
            Where:    $"File: {fileName}",
            Why:      why,
            WhatToDo: action,
            Facts:    facts);
    }

    // ── Filter Manager ───────────────────────────────────────

    private static DecodedEvent DecodeFilterManager(MonitoredEvent evt)
    {
        var data = EventXml.ReadNamedData(evt.RawXml);
        var facts = new List<KeyValuePair<string, string>> { new("Event ID", evt.EventId.ToString(CultureInfo.InvariantCulture)) };
        foreach (var kv in data) facts.Add(new(kv.Key, kv.Value));

        var (what, why, action) = evt.EventId switch
        {
            3 => ("A filter driver failed to attach to a volume during initialisation.",
                  "Common when an antivirus or file-system filter driver loads before the volume it filters is ready. Usually transient.",
                  "If recurring on a specific volume, investigate the listed filter driver."),
            6 => ("A filter driver failed to attach during volume mount.",
                  "Same family as event 3 — filter ordering or load failure.",
                  "Update the filter driver (most often AV / EDR software).")
        ,
            _ => ($"Filter Manager event {evt.EventId}.",
                  "See the fields below.",
                  "Filter Manager events are rarely tuning-related.")
        };

        return new DecodedEvent(
            Title:    $"Filter Manager Event {evt.EventId}",
            What:     what,
            Where:    "Filter Manager (FltMgr) — Windows file system filter framework.",
            Why:      why,
            WhatToDo: action,
            Facts:    facts);
    }

    // ── Application Crash (Application Error 1000) ───────────

    private static DecodedEvent DecodeAppCrash(MonitoredEvent evt)
    {
        // Application Error event uses positional Data nodes (no Name attribute).
        // Standard layout: AppName, AppVersion, AppTimestamp, ModName, ModVersion,
        //                  ModTimestamp, ExceptionCode, Offset, ProcessId, ProcessStartTime, ...
        var values = EventXml.ReadUnnamedData(evt.RawXml);

        string Get(int i) => i < values.Count ? values[i] : "";
        string appName   = Get(0);
        string appVer    = Get(1);
        string modName   = Get(3);
        string modVer    = Get(4);
        string excCode   = Get(6);
        string offset    = Get(7);
        string processId = Get(8);

        var facts = new List<KeyValuePair<string, string>>
        {
            new("Event ID", "1000 (Application Error)"),
            new("Application", string.IsNullOrEmpty(appName) ? "(unknown)" : appName),
            new("App version", string.IsNullOrEmpty(appVer) ? "(unknown)" : appVer),
            new("Faulting module", string.IsNullOrEmpty(modName) ? "(unknown)" : modName),
            new("Module version", string.IsNullOrEmpty(modVer) ? "(unknown)" : modVer),
            new("Exception code", FormatHexParam(excCode)),
            new("Fault offset", FormatHexParam(offset)),
            new("Process ID", string.IsNullOrEmpty(processId) ? "(unknown)" : processId),
        };

        string title = $"App Crash — {(string.IsNullOrEmpty(appName) ? "(unknown app)" : appName)}";

        // When EventData is missing the essential fields, build a plain summary
        // rather than concatenating broken sentences with leading spaces and
        // double gaps. Truncated WER payloads from third-party providers are the
        // common case here.
        if (string.IsNullOrEmpty(appName) || string.IsNullOrEmpty(modName))
        {
            return new DecodedEvent(
                Title:    title,
                What:     "An application crash was recorded. Standard EventData fields (application name, faulting module, exception code) were missing or truncated.",
                Where:    "Application Error provider. Open Event Viewer for the raw event payload.",
                Why:      "Without the faulting-module and exception-code fields there is no signal to chase from this row alone.",
                WhatToDo: "Cross-reference with WER (Windows Error Reporting) folder under %LOCALAPPDATA%\\CrashDumps for the dump file.",
                Facts:    facts);
        }

        string excName = ExceptionCodeName(excCode);

        return new DecodedEvent(
            Title:    title,
            What:     $"{appName} faulted in module {modName} with exception {FormatHexParam(excCode)} ({excName}) at offset {FormatHexParam(offset)}.",
            Where:    $"Process {processId}, app version {appVer}, module version {modVer}.",
            Why:      "User-mode app crashes are usually app or driver bugs, not RAM. But a single faulting module that crashes after RAM tuning, and never before, is a control-test signal.",
            WhatToDo: "If reproducible: bisect against a known-good RAM profile. If isolated: report to the application vendor with the exception code and offset.",
            Facts:    facts);
    }

    private static string ExceptionCodeName(string codeHex) => codeHex.ToLowerInvariant() switch
    {
        "0xc0000005" => "ACCESS_VIOLATION",
        "0xc000001d" => "ILLEGAL_INSTRUCTION",
        "0xc0000094" => "INTEGER_DIVIDE_BY_ZERO",
        "0xc0000096" => "PRIVILEGED_INSTRUCTION",
        "0xc00000fd" => "STACK_OVERFLOW",
        "0xc0000409" => "STACK_BUFFER_OVERRUN",
        "0xc0000374" => "HEAP_CORRUPTION",
        "0xc0000142" => "DLL_INIT_FAILED",
        "0xe06d7363" => "C++ exception",
        _            => "see exception_code reference"
    };

    // ── Application Hang (Application Hang 1002) ─────────────

    private static DecodedEvent DecodeAppHang(MonitoredEvent evt)
    {
        // Application Hang 1002 positional layout (per Microsoft Q&A docs):
        //   0: AppName, 1: AppVersion, 2: ProcessId, 3: StartTime,
        //   4: TerminationTime, 5: ExeFileName, 6: ReportId, 7: PackageFullName
        // The earlier shape used here mistook StartTime for ProcessId and read
        // TerminationTime as a "hang signature" field that does not exist.
        var values = EventXml.ReadUnnamedData(evt.RawXml);
        string Get(int i) => i < values.Count ? values[i] : "";

        string appName     = Get(0);
        string appVer      = Get(1);
        string processId   = Get(2);
        string exeFileName = Get(5);
        string reportId    = Get(6);

        var facts = new List<KeyValuePair<string, string>>
        {
            new("Event ID", "1002 (Application Hang)"),
            new("Application", string.IsNullOrEmpty(appName) ? "(unknown)" : appName),
            new("App version", string.IsNullOrEmpty(appVer) ? "(unknown)" : appVer),
            new("Process ID", string.IsNullOrEmpty(processId) ? "(unknown)" : processId),
        };
        if (!string.IsNullOrEmpty(exeFileName)) facts.Add(new("Executable", exeFileName));
        if (!string.IsNullOrEmpty(reportId))    facts.Add(new("Report ID", reportId));

        string title = $"App Hang — {(string.IsNullOrEmpty(appName) ? "(unknown app)" : appName)}";

        if (string.IsNullOrEmpty(appName))
        {
            return new DecodedEvent(
                Title:    title,
                What:     "An application hang was recorded. Standard EventData fields (application name, version) were missing or truncated.",
                Where:    "Application Hang provider. Open Event Viewer for the raw event payload.",
                Why:      "Without the application identity there is no actionable signal in this row alone.",
                WhatToDo: "Cross-reference with WER (Windows Error Reporting) folder under %LOCALAPPDATA%\\CrashDumps for the dump file.",
                Facts:    facts);
        }

        return new DecodedEvent(
            Title:    title,
            What:     $"{appName} stopped responding to window messages and was terminated by Windows Error Reporting.",
            Where:    $"Process {processId}, app version {appVer}.",
            Why:      "Hangs are a UI-thread signal, not a hardware signal — except when they cluster across many apps simultaneously, which can hint at I/O or driver wedge.",
            WhatToDo: "Single hang: report to vendor. Mass hangs: check disk and storage events at the same timestamp.",
            Facts:    facts);
    }

    // ── Memory Diagnostics ───────────────────────────────────

    private static DecodedEvent DecodeMemoryDiagnostics(MonitoredEvent evt)
    {
        var data = EventXml.ReadNamedData(evt.RawXml);
        var facts = new List<KeyValuePair<string, string>> { new("Event ID", evt.EventId.ToString(CultureInfo.InvariantCulture)) };
        foreach (var kv in data) facts.Add(new(kv.Key, kv.Value));

        bool errorsFound = evt.EventId == 1002 ||
                           data.TryGetValue("Result", out var r) && r != "0";

        return new DecodedEvent(
            Title:    errorsFound ? "Windows Memory Diagnostics — Errors Detected" : "Windows Memory Diagnostics — Pass",
            What:     errorsFound
                        ? "Windows Memory Diagnostics found errors during its scheduled scan."
                        : "Windows Memory Diagnostics completed without finding errors.",
            Where:    "Built-in Windows memory test (mdsched.exe).",
            Why:      errorsFound
                        ? "WMD is conservative — by the time it reports errors, something is wrong. But its detection is much weaker than TM5/HCI/Karhu, so a clean WMD result does not mean the RAM is stable for tuning."
                        : "A WMD pass is necessary but not sufficient for tuning stability. Consider it 'no obvious failure' rather than 'stable'.",
            WhatToDo: errorsFound
                        ? "Loosen RAM timings to JEDEC, raise VDIMM 20-40 mV, then run a real stress test (TM5, OCCT, MemTest86). If errors persist at JEDEC, the RAM is failing."
                        : "Run a real memory stress test for at least 1 hour to confirm tuning stability.",
            Facts:    facts);
    }

    // ── Generic fallback ─────────────────────────────────────

    private static DecodedEvent DecodeGeneric(MonitoredEvent evt)
    {
        var data = EventXml.ReadNamedData(evt.RawXml);
        var facts = new List<KeyValuePair<string, string>> { new("Event ID", evt.EventId.ToString(CultureInfo.InvariantCulture)) };
        foreach (var kv in data) facts.Add(new(kv.Key, kv.Value));

        return new DecodedEvent(
            Title:    $"{evt.Source} (Event {evt.EventId})",
            What:     evt.Summary,
            Where:    $"Source: {evt.Source}, category {evt.Category}.",
            Why:      "No specialised decoder for this source. The summary above is what the event provider supplied.",
            WhatToDo: "Open Event Viewer for the full event payload if more detail is needed.",
            Facts:    facts);
    }

    // ── Helpers ──────────────────────────────────────────────

    private static ulong ParseUlong(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
                ? hex : 0;
        }
        return ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : 0;
    }

    private static string FormatHex(ulong value)
    {
        if (value == 0) return "0x0";
        return value <= 0xFFFFFFFF ? $"0x{value:x8}" : $"0x{value:x16}";
    }

    /// <summary>
    /// Format a parameter that may already be hex, decimal, or already prefixed.
    /// Returns the input unchanged if it's already 0x-prefixed, otherwise hex-formats it.
    /// </summary>
    private static string FormatHexParam(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "0x0";
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return raw.ToLowerInvariant();
        return FormatHex(ParseUlong(raw));
    }

    /// <summary>
    /// Boolean-ish flag parser for event log fields. Different Windows builds
    /// emit Kernel-Power 41's SleepInProgress as either "1"/"0" or "true"/"false";
    /// accept either rather than letting the entire branch never fire on the
    /// other variant.
    /// </summary>
    private static bool IsTruthy(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;
        var s = raw.Trim();
        if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase) || ParseUlong(s) != 0;
    }
}
