# RAMWatch — Architecture Document

**Version:** 1.0 (Claude Code Handoff)
**Date:** 14 April 2026
**Author:** Repro + Claude
**Status:** Architecture complete. Ready for Phase 1 implementation.

---

## 1. What This Is

A lightweight, open-source Windows application for enthusiasts who tune DRAM timings. It combines three functions that don't exist together in any current tool:

1. **System health monitor** — persistent, event-driven tracking of WHEA errors, MCEs, filesystem corruption, and application crashes. Runs as a Windows service, starts at boot, never misses an event.
2. **Tuning journal** — timestamped log of every timing configuration, every stability test result, and every config change, with automatic drift detection for auto-trained values. Tracks which timings you set manually vs. what AGESA trained.
3. **Shareable history** — git-backed version control of your tuning journey with phone-readable BIOS checklists, AI-friendly digest exports, and optional anonymous community data pooling.

RAMWatch does not change hardware settings, run stress tests, or benchmark. It watches, logs, correlates, and makes your tuning history portable and reviewable.

---

## 2. Design Principles

1. **Zero-cost when idle.** No polling loops burning cycles. Event-driven where possible, low-frequency polling (≥30s) where not. Target: <0.1% CPU, <25MB RAM steady state for the service. 0% CPU / 0MB when the GUI is closed.
2. **Silent admin via service.** The monitoring service runs as `LocalSystem`, starts at boot, never prompts UAC. Full hardware register access, full event log access, full SFC/DISM capability — always. The GUI is an unprivileged client that connects to the service. No elevation dance.
3. **Offline-first, local-only.** No telemetry, no network calls, no update checker unless the user enables it. All data stays on disk.
4. **Two-EXE install, clean uninstall.** Service EXE + GUI EXE. No installer framework. XCOPY-deployable plus one `sc.exe create` command. Uninstall = `sc.exe delete` + delete the folder.
5. **BIFL.** No Electron. No web frameworks. No Node.js. Native code, native GUI, ships with the runtime or AOT compiles to remove the dependency entirely.
6. **Open source.** MIT license. All code, docs, and community schemas are public. No proprietary dependencies except the optional InpOutx64 driver (freeware, user-provided).
7. **Privacy by default.** Three-tier data model: local (everything), private repo (anonymized), public repo (opt-in, hardware + timings only). Error details and timestamps never leave the machine without explicit action.

---

## 3. Technology Stack

### Runtime: .NET 8+ with Native AOT

**Why:** Native AOT produces a single self-contained EXE with no runtime dependency. Startup is near-instant. Memory footprint is minimal. P/Invoke to native drivers (InpOutx64) is first-class. The ecosystem has mature libraries for everything we need.

**Build target:** `win-x64` Native AOT, single-file, self-contained. Two output EXEs.

**Estimated sizes:** Service ~15MB, GUI ~25MB (includes WPF runtime). Total ~40MB.

### GUI: WPF (Windows Presentation Foundation)

**Why not WinUI 3:** WinUI 3 has a mandatory dependency on the Windows App SDK runtime (~80MB), doesn't support Native AOT cleanly, and has known issues with system tray integration. It's the "modern" choice but it's not the lightweight choice.

**Why not MAUI:** Cross-platform abstraction we don't need. Adds layers and weight for zero benefit on a Windows-only tool.

**Why WPF:** Ships with .NET. Full system tray support via NotifyIcon. Mature data binding. Full control over rendering. Hardware-accelerated. Supports custom dark themes natively. AOT-compatible with some constraints (see Section 10). Every Windows system monitoring tool that matters (HWiNFO, ZenTimings, OCCT) uses either WPF or raw Win32. WPF is the right weight class.

**Theme:** Custom dark theme (SGH Dark adjacent — dark navy background, high-contrast status colors, Consolas/IBM Plex Mono typography). No system theme inheritance.

### Hardware Access: InpOutx64 (existing) + custom UMC register reader

**Why:** InpOutx64 is a signed kernel driver that exposes physical memory read/write and PCI config space access from userspace via DLL exports. ZenTimings already installs it. We P/Invoke against the same DLL. If the driver isn't installed, hardware reads degrade gracefully (show "driver not available" instead of timing values).

**Alternative considered:** WinRing0 — similar capability but unsigned driver, requires test-signing mode on modern Windows. Not acceptable for a daily-driver tool.

---

## 4. Architecture Overview — Two-Process Model

RAMWatch runs as two processes: a Windows service (always running, admin, headless) and a GUI client (on-demand, unprivileged, user-facing). They communicate over a named pipe.

```
┌─────────────────────────────────────────────────────────────────────┐
│                    PROCESS 1: RAMWatch.Service.exe                  │
│                    (Windows Service, LocalSystem)                   │
│                    Starts at boot, runs forever                     │
│                                                                     │
│  ┌─────────────┐ ┌─────────────┐ ┌───────────────┐                 │
│  │ EventLog    │ │ Hardware    │ │ Integrity     │                 │
│  │ Monitor     │ │ Reader      │ │ Checker       │                 │
│  ├─────────────┤ ├─────────────┤ ├───────────────┤                 │
│  │ EventLog    │ │ InpOutx64   │ │ SFC/DISM      │                 │
│  │ Watcher     │ │ P/Invoke    │ │ CBS.log parse │                 │
│  │ (push-based)│ │ UMC decode  │ │ Background    │                 │
│  │             │ │ SVI2 decode │ │ process       │                 │
│  └──────┬──────┘ └──────┬──────┘ └───────┬───────┘                 │
│         │               │                │                          │
│  ┌──────┴───────────────┴────────────────┴───────┐                 │
│  │              State Aggregator                  │                 │
│  │  In-memory current state (error counts,        │                 │
│  │  latest timing snapshot, integrity status)     │                 │
│  └──────────────────┬────────────────────────────┘                 │
│                     │                                               │
│  ┌──────────────────┴────────────────────────────┐                 │
│  │              Data Layer                        │                 │
│  │  ┌──────────┐ ┌──────────┐ ┌──────────────┐   │                 │
│  │  │ Settings │ │ CSV      │ │ Snapshot     │   │                 │
│  │  │ (JSON)   │ │ Logger   │ │ Store (JSON) │   │                 │
│  │  └──────────┘ └──────────┘ └──────────────┘   │                 │
│  └───────────────────────────────────────────────┘                 │
│                     │                                               │
│              Named Pipe Server                                      │
│              \\.\pipe\RAMWatch                                      │
└─────────────────────┬───────────────────────────────────────────────┘
                      │ IPC (JSON messages)
┌─────────────────────┴───────────────────────────────────────────────┐
│                    PROCESS 2: RAMWatch.exe                          │
│                    (WPF GUI, user session, unprivileged)            │
│                    Launched by user or at logon, closeable          │
│                                                                     │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌────────┐                │
│  │ Main     │ │ Settings │ │ Snapshot │ │ Tray   │                │
│  │ Window   │ │ Window   │ │ Compare  │ │ Icon   │                │
│  └────┬─────┘ └────┬─────┘ └────┬─────┘ └───┬────┘                │
│       └─────────────┴────────────┴────────────┘                    │
│                          │                                          │
│  ┌───────────────────────┴───────────────────────┐                 │
│  │  MainViewModel (MVVM, INotifyPropertyChanged)  │                │
│  │  Receives state updates from service via pipe  │                │
│  │  Sends commands (snapshot, SFC, settings)      │                │
│  └───────────────────────────────────────────────┘                 │
│                          │                                          │
│              Named Pipe Client                                      │
│              \\.\pipe\RAMWatch                                      │
└─────────────────────────────────────────────────────────────────────┘
```

### 4.1 Why This Split

| Concern | Single-process | Two-process (service + GUI) |
|---|---|---|
| Monitoring before logon | No | Yes — service starts at boot |
| UAC prompt | Every launch (if admin) | Never — service is already admin |
| GUI crash kills monitoring | Yes | No — service keeps running |
| Survives logoff | No | Yes |
| RDP / fast user switch | Dies on disconnect | Service survives |
| Resource cost when GUI closed | N/A (always running) | ~15MB service only |
| Complexity | Lower | Moderate (IPC layer) |

### 4.2 IPC Protocol — Named Pipe

**Pipe name:** `\\.\pipe\RAMWatch`

**Transport:** JSON-over-newline. Each message is a single JSON object terminated by `\n`. No framing headers, no binary protocol. Simplicity over performance — the message rate is <1/second.

**Message types, service → client:**

```jsonc
// Full state push (sent on connect + every refresh interval)
{
  "type": "state",
  "timestamp": "2026-04-14T12:15:33",
  "bootTime": "2026-04-14T09:01:00",
  "errors": [
    { "source": "WHEA Hardware Errors", "category": "Hardware", "count": 0, "lastSeen": null },
    // ... all sources
  ],
  "timings": { /* full TimingSnapshot or null if driver unavailable */ },
  "designations": { "CL": "manual", "RCDRD": "manual", "RRDS": "auto" /* ... */ },
  "lkg": { /* TimingSnapshot of last known good, or null */ },
  "lkgValidation": { "tool": "Karhu", "coverage": 8000, "date": "2026-04-14" },
  "recentValidations": [ /* last 5 ValidationResult records */ ],
  "recentChanges": [ /* last 5 ConfigChange records */ ],
  "driftEvents": [ /* any drift detected this boot */ ],
  "integrity": {
    "cbsCorruptionCount": 0,
    "sfcStatus": "not_run",
    "dismStatus": "not_run"
  },
  "driverStatus": "loaded",  // "loaded" | "not_found" | "access_denied"
  "serviceUptime": "12:14:33"
}

// Real-time event notification (sent immediately when EventLogWatcher fires)
{
  "type": "event",
  "timestamp": "2026-04-14T12:16:01",
  "source": "WHEA Hardware Errors",
  "category": "Hardware",
  "severity": "Warning",
  "eventId": 17,
  "summary": "Corrected hardware error on component Memory..."
}

// Command response
{
  "type": "response",
  "requestId": "abc123",
  "status": "ok",
  "data": { /* varies by command */ }
}
```

**Message types, client → service:**

```jsonc
// Request current state (also sent implicitly on connect)
{ "type": "getState", "requestId": "abc123" }

// Take a named snapshot
{ "type": "snapshot", "requestId": "abc124", "name": "CL16 baseline", "notes": "Karhu 8000%", "karhuPercent": 8000, "aida64": { "read": 53200, "write": 28400, "copy": 48900, "latency": 63.2 } }

// Run SFC or DISM
{ "type": "runIntegrity", "requestId": "abc125", "check": "sfc" }  // or "dism_check" or "dism_scan"

// Update settings
{ "type": "updateSettings", "requestId": "abc126", "settings": { "refreshIntervalSeconds": 30 } }

// Log a validation test result
{ "type": "logValidation", "requestId": "abc127", "tool": "Karhu", "metricName": "Coverage", "metricValue": 8000, "metricUnit": "%", "passed": true, "errorCount": 0, "durationMinutes": 240, "notes": "Overnight soak" }

// Update manual/auto timing designations
{ "type": "updateDesignations", "requestId": "abc128", "designations": { "CL": "manual", "RCDRD": "manual", "RRDS": "auto" } }

// Request AI helper digest
{ "type": "getDigest", "requestId": "abc129", "historyCount": 5 }

// Request LKG diff against current
{ "type": "getLkgDiff", "requestId": "abc130" }
```

**Connection lifecycle:**
1. GUI opens pipe connection on launch
2. Service sends full state immediately
3. Service pushes incremental events and periodic state refreshes
4. GUI sends commands as needed
5. GUI disconnect = no impact on service
6. Multiple GUI instances allowed (service broadcasts to all connected clients)

### 4.3 Performance Budget

| Component | CPU (idle) | CPU (per event) | RAM | Disk I/O |
|---|---|---|---|---|
| Service: EventLogWatcher | 0.00% (kernel callback, thread suspended) | <0.1ms per event | ~2MB for subscriptions | None |
| Service: Hardware read (60s timer) | 0.00% between ticks | <1ms PCI reads | ~1MB decoded state | None |
| Service: CSV write (60s timer) | 0.00% between ticks | <1ms file append | Negligible | ~1KB/min |
| Service: CBS.log tail (60s timer) | 0.00% between ticks | <5ms string scan | ~1MB for tail buffer | Read only |
| Service: Named pipe server | 0.00% (async I/O) | <0.1ms per message | ~1MB per client | None |
| **Service total** | **0.00%** | **<7ms per 60s tick** | **~15–20MB** | **~1KB/min** |
| GUI: WPF idle | 0.00% (no render without input) | N/A | ~30–50MB | None |
| GUI: State update (60s) | <0.01% | <2ms rebind | Negligible | None |
| **GUI total** | **0.00%** | **<2ms per update** | **~30–50MB** | **None** |

**For context:** The Windows Event Log service itself uses ~15MB. HWiNFO in background mode uses ~40MB. RAMWatch's service is lighter than both.

---

## 5. Service Layer Detail

### 5.1 EventLogMonitor

**Responsibility:** Watch Windows Event Log for RAM/CPU/OS-relevant errors.

**Implementation:** Use `System.Diagnostics.Eventing.Reader.EventLogWatcher` for push-based event notification. No polling. The OS notifies us when a matching event is written. This is the correct zero-cost approach — CPU usage is exactly zero between events.

**Watched sources:**

| Category | Provider | Event IDs | Requires Admin |
|---|---|---|---|
| WHEA correctable | Microsoft-Windows-WHEA-Logger | 17, 18, 19, 20, 47 | No |
| Machine Check Exception | Microsoft-Windows-WHEA-Logger | 1 | No |
| Kernel bugcheck | Microsoft-Windows-WER-SystemErrorReporting | 1001 | No |
| Unexpected shutdown | Microsoft-Windows-Kernel-Power | 41 | No |
| Disk/IO | disk | 7, 11, 15, 51, 52 | No |
| NTFS | Ntfs | 55, 98, 137, 140 | No |
| Volume Shadow Copy | volsnap | 14, 25, 35, 36 | No |
| Code Integrity | Microsoft-Windows-CodeIntegrity | 3001–3004, 3033 | No |
| Filter Manager | Microsoft-Windows-FilterManager | 3, 6 | No |
| Application crash | Application Error | 1000 | No |
| Application hang | Application Hang | 1002 | No |
| Memory diagnostics | Microsoft-Windows-MemoryDiagnostics-Results | 1001, 1002 | No |

**Key design decision:** We subscribe to live events AND do a one-time historical scan on startup (events since last boot). This gives us both the running count and real-time notification without double-counting.

**Data model per event:**

```csharp
public record MonitoredEvent(
    DateTime Timestamp,
    string Source,         // e.g. "WHEA Hardware Errors"
    string Category,       // "Hardware", "Filesystem", "Integrity", "Application"
    int EventId,
    EventSeverity Severity,  // Info, Warning, Error, Critical
    string Summary,        // First 200 chars of message
    string RawXml          // Full event XML for export
);
```

### 5.2 HardwareReader

**Responsibility:** Read DRAM timings, voltages, and clocks directly from CPU/memory controller registers.

**Implementation:** P/Invoke against InpOutx64.dll for PCI config space reads. Decode UMC (Unified Memory Controller) registers using known offsets for Zen 2/3/4.

**Register map source:** ZenTimings open source (MIT license). The relevant decode logic lives in `ZenTimings/Zen2/UMC.cs` and `ZenTimings/Zen3/UMC.cs` in the irusanov/ZenTimings GitHub repo. We port these register offset tables and bitfield decoders, not the GUI.

**Capability detection:**

```
1. Check CPU family (CPUID) → select register map
2. Check InpOutx64.dll presence → if missing, disable hardware reads
3. Check admin privileges → if not admin, PCI config reads will fail
4. Probe UMC base address → if inaccessible, disable gracefully
```

**Read frequency:** On-demand only (user clicks "Snapshot" or on a configurable timer, default 60s). Register reads are fast (<1ms per full timing dump) but there's no reason to do them continuously.

**Data model:**

```csharp
public record TimingSnapshot(
    DateTime Timestamp,
    // Clocks
    int MemoryClockMHz,      // MCLK
    int FabricClockMHz,      // FCLK
    int UclkMHz,             // UCLK
    // Primaries
    int CL, int RCDRD, int RCDWR, int RP, int RAS, int RC,
    // Key secondaries
    int RFC, int RFC2, int RFC4,
    int RRDS, int RRDL, int FAW,
    int WTRS, int WTRL, int WR,
    int RDRDSCL, int WRWRSCL,
    int CWL, int RTP, int RDWR_val, int WRRD,
    int REFI,                // tREFI — refresh interval
    int CKE,                 // tCKE
    int STAG,
    // Tertiaries / turn-around
    int RDRDSC, int RDRDSD, int RDRDDD,
    int WRWRSC, int WRWRSD, int WRWRDD,
    int MOD, int MODPDA, int MRD, int MRDPDA,
    // PHY timings (per-channel, logged but not user-tunable)
    int PHYWRD, int PHYWRL,
    int PHYRDL_A, int PHYRDL_B,  // per-channel, may differ (training artifact)
    // Controller config
    bool GDM, bool BGS, bool BGSAlt, bool PowerDown,
    string CommandRate,      // "1T" or "2T"
    string RefreshMode,      // "Normal" or "Fine"
    int ProcODT,
    // Rtt / drive strengths (auto-trained, logged for drift detection)
    string RttNom, string RttWr, string RttPark,
    string ClkDrvStr, string AddrCmdDrvStr,
    string CsOdtDrvStr, string CkeDrvStr,
    int AddrCmdSetup, int CsOdtSetup, int CkeSetup,
    // Voltages (from SVI2 telemetry + BIOS WMI)
    double VSOC, double VDDG_CCD, double VDDG_IOD,
    double CLDO_VDDP, double VDIMM, double VTT
);
```

**Known issues to handle:**
- tRFC1 readback bug on ComboAM4v2PI 1.2.0.x — the SMU register returns stale/default values. Document this in the UI when detected.
- VDIMM/VTT on MSI boards come from BIOS WMI (AMD_ACPI class), not from hardware registers. These are static values (what BIOS set, not real-time). Label them accordingly.
- PHYRDL mismatch between channels is normal (PHY training artifact). Don't flag it as an error.

### 5.3 IntegrityChecker

**Responsibility:** Check Windows component store and system file integrity.

**Implementation:**

| Check | Method | Duration | Admin Required |
|---|---|---|---|
| CBS.log scan | Parse `%SystemRoot%\Logs\CBS\CBS.log` tail | <1s | No |
| SFC verify | Shell `sfc /verifyonly` in background process | 2–10 min | Yes |
| DISM check | Shell `DISM /Online /Cleanup-Image /CheckHealth` | 1–5 min | Yes |
| DISM scan | Shell `DISM /Online /Cleanup-Image /ScanHealth` | 5–20 min | Yes |

**Key design decisions:**
- SFC and DISM run only on explicit user request (button press). Never automatic — they're expensive.
- CBS.log scan runs on each refresh cycle. It's a file tail, costs nothing.
- Background process management: use `System.Diagnostics.Process` with async output capture, not `Start-Job`. Report progress via line-count heuristic.
- Parse SFC/DISM stdout for known result strings. Map to enum: `Clean | CorruptionFound | CorruptionRepaired | Failed | Unknown`.

### 5.4 ConfigChangeDetector

**Responsibility:** Detect when DRAM timings or clocks change between boots (or within a boot if Ryzen Master is used).

**Implementation:** On each timing snapshot, compare to the previous snapshot. If any value differs, emit a `ConfigChange` event with before/after values for every changed field. This is the backbone of the tuning timeline — the user doesn't have to tell RAMWatch they changed something, it notices.

**Data model:**

```csharp
public record ConfigChange(
    DateTime Timestamp,
    string BootId,
    Dictionary<string, (string OldValue, string NewValue)> Changes,
    string? UserNotes          // optional, user can annotate via GUI
);
```

### 5.5 DriftDetector

**Responsibility:** Identify when auto-trained timings change across boots without the user changing any manual settings.

**Implementation:** Maintains a rolling window of the last 20 boot sessions. For any timing marked "auto" in the manual/auto designation map, compares the current boot's trained value against the mode (most common value) from the window. If an auto timing trains to a different value, emit a `DriftEvent`.

**Why this matters:** AGESA memory training is non-deterministic. The same BIOS settings can produce different auto-trained secondary/tertiary timings depending on temperature, voltage ripple, or training seed. Drift in tRRDL from 11 to 12 across boots can cause intermittent instability that's invisible without this tracking.

**Data model:**

```csharp
public record DriftEvent(
    DateTime Timestamp,
    string BootId,
    string TimingName,         // e.g. "tRRDL"
    int ExpectedValue,         // mode from window
    int ActualValue,           // this boot's trained value
    int BootsAtExpected,       // how many of last 20 matched expected
    int BootsAtActual          // how many trained to this new value
);
```

### 5.6 ValidationTestLogger

**Responsibility:** Record stability test results and link them to the timing snapshot that was active during the test.

**Implementation:** The GUI provides a "Log Test Result" form. The service receives the test data via pipe, captures the current timing snapshot, and writes a `ValidationResult` to the test log. Supports built-in test types (Karhu, TM5+config, AIDA64) and user-defined custom tests.

**Built-in test types:**

| Tool | Metric | Unit | Pass Threshold (default) |
|---|---|---|---|
| Karhu RAMTest | Coverage | % | 1000% |
| TM5 + 1usmus_v3 | Cycles | count | 25 |
| TM5 + anta777 | Cycles | count | 3 |
| AIDA64 | Benchmark | scores | N/A (informational) |
| OCCT Memory | Duration | minutes | 60 |
| Boot test | POST | pass/fail | N/A |
| Custom | User-defined | User-defined | User-defined |

**Data model:**

```csharp
public record ValidationResult(
    DateTime Timestamp,
    string BootId,
    string TestTool,           // "Karhu", "TM5_1usmus_v3", "Custom:MyTest"
    string MetricName,         // "Coverage", "Cycles", "Duration"
    double MetricValue,        // 8000, 25, 60
    string MetricUnit,         // "%", "cycles", "minutes"
    bool Passed,
    int ErrorCount,
    TimeSpan Duration,
    TimingSnapshot ActiveTimings,  // full snapshot at time of test
    string? Notes
);
```

**Last Known Good (LKG):** The most recent timing snapshot with a passing validation test above a configurable threshold (default: Karhu 1000% or TM5 25 cycles). The service tracks this automatically. The GUI always shows the LKG prominently — it's your "safe to revert to" config.

### 5.7 ZenTimings Detection

**Responsibility:** Detect presence of InpOutx64 driver on each service start.

**Implementation:** On every service start (not just first run), scan for `inpoutx64.dll` in:
1. Same directory as RAMWatch.Service.exe
2. ZenTimings install directory (check registry + common paths)
3. `C:\Windows\System32\drivers\` (driver sys file)
4. System PATH

If found: load it, enable hardware reads, log "InpOutx64 driver detected at {path}."
If not found: disable hardware reads, report status to GUI via pipe. GUI shows a one-time dismissable notification with "Don't show again" checkbox and a link to ZenTimings download. Re-check on every service restart — user may install ZenTimings between reboots.

---

## 6. Data Layer Detail

### 6.1 Settings (JSON)

**Location:** `%ProgramData%\RAMWatch\settings.json`

If the file doesn't exist, create with defaults. If it's corrupt, log a warning and use defaults. Never crash on bad config.

```jsonc
{
  // General
  "startMinimized": false,
  "minimizeToTray": true,
  "alwaysOnTop": false,
  "launchAtLogon": false,

  // Monitoring
  "refreshIntervalSeconds": 60,
  "enableHardwareReads": true,
  "enableTimingSnapshot": true,
  "timingSnapshotIntervalSeconds": 300,

  // Logging
  "enableCsvLogging": true,
  "logDirectory": "%ProgramData%\\RAMWatch\\logs",
  "logRetentionDays": 90,
  "maxLogSizeMB": 100,
  "mirrorDirectory": "",              // empty = disabled. e.g. "D:\\Dropbox\\RAMWatch"

  // Notifications
  "enableToastNotifications": true,
  "notifyOnWHEA": true,
  "notifyOnBSOD": true,
  "notifyOnDrift": true,
  "notifyOnCodeIntegrity": false,
  "notifyOnAppCrash": false,
  "notifyCooldownSeconds": 300,

  // Display
  "theme": "dark",
  "showTimingsPanel": true,
  "showVoltagesPanel": true,

  // Validation
  "lkgThresholdKarhu": 1000,          // min Karhu % to qualify as LKG
  "lkgThresholdTm5Cycles": 25,        // min TM5 cycles to qualify as LKG
  "promptDesignationsOnChange": true,  // ask manual/auto after config change

  // Git integration
  "git": {
    "enabled": false,
    "provider": "github",             // "github" | "gitlab" | "manual"
    "username": "",
    "repoName": "ramwatch-log",
    "private": true,
    "ghConfigDir": "%ProgramData%\\RAMWatch\\.gh",
    "commitOnConfigChange": true,
    "commitOnValidation": true,
    "pushAfterCommit": true,
    "pushRetryMinutes": 15
  },

  // Community sharing
  "sharing": {
    "enabled": false,
    "publicRepoName": "ramwatch-public",
    "shareOnValidation": true,         // auto-share passing test results
    "shareFields": {
      "cpu": true,
      "board": true,
      "biosVersion": true,
      "agesa": true,
      "ramPartNumber": true,
      "dieType": true,
      "timings": true,
      "voltages": true,
      "validationResults": true,
      "benchmarks": true,
      "errorHistory": false            // never shared by default
    }
  },

  // Advanced
  "inpOutDllPath": "",                // empty = auto-detect
  "cpuFamily": "auto",
  "driftWindowBoots": 20,             // how many boots to track for drift
  "debugLogging": false
}
```

### 6.2 CSV Logger

**Location:** `%ProgramData%\RAMWatch\logs\`

**File naming:** `ramwatch_YYYY-MM-DD.csv` — one file per calendar day. Append-only. Never modify existing rows.

**Two log files per day:**

**Event log (`events_YYYY-MM-DD.csv`):**

```csv
timestamp,boot_id,source,category,severity,event_id,summary
2026-04-14T09:15:33,boot_0414_0901,WHEA Hardware Errors,Hardware,Warning,17,"Corrected hardware error on component Memory"
```

**Timing snapshots (`timings_YYYY-MM-DD.csv`):**

```csv
timestamp,boot_id,mclk,fclk,uclk,cl,rcdrd,rcdwr,rp,ras,rc,rfc,rfc2,rfc4,rrds,rrdl,faw,wtrs,wtrl,wr,rdrdscl,wrwrscl,cwl,vsoc,vddg_ccd,vddg_iod,vdimm
2026-04-14T09:15:33,boot_0414_0901,1800,1800,1800,16,22,22,22,42,64,577,375,260,7,11,38,5,14,26,5,5,16,1.0875,0.9976,0.9976,1.4000
```

**Boot ID format:** `boot_MMDD_HHMM` derived from `LastBootUpTime`. Groups all data from one boot session for easy filtering.

**Rotation:** On startup, delete log files older than `logRetentionDays`. Check total log directory size against `maxLogSizeMB` and delete oldest files if exceeded.

### 6.3 Snapshot Store

**Purpose:** Named snapshots that persist across sessions. The user clicks "Save Snapshot" and gives it a name like "CL16 baseline" or "tRFC 540 attempt". This is the tuning diary.

**Location:** `%ProgramData%\RAMWatch\snapshots.json`

**Schema:**

```jsonc
{
  "snapshots": [
    {
      "name": "CL16 baseline",
      "timestamp": "2026-04-14T09:15:33",
      "notes": "First CL16 boot, Karhu 8000% clean",
      "timings": { /* full TimingSnapshot */ },
      "errorCounts": { /* error counts at time of snapshot */ },
      "karhuPercent": 8000,
      "aida64": {
        "read": 53200,
        "write": 28400,
        "copy": 48900,
        "latency": 63.2
      }
    }
  ]
}
```

**AIDA64 fields:** Manual entry via the GUI. RAMWatch can also import from AIDA64's CSV export if the user points it at the file. This avoids manual transcription errors without creating a fragile automation dependency.

### 6.4 Manual/Auto Timing Designations

**Purpose:** Track which timings the user set manually in BIOS vs. which AGESA auto-trained. This metadata is critical for drift detection and for generating accurate BIOS checklists.

**Location:** `%ProgramData%\RAMWatch\designations.json`

**Schema:**

```jsonc
{
  "designations": {
    "CL": "manual",
    "RCDRD": "manual",
    "RCDWR": "manual",
    "RP": "manual",
    "RAS": "manual",
    "RC": "manual",
    "CWL": "manual",
    "RFC": "manual",
    "RFC2": "manual",
    "RFC4": "manual",
    "RRDS": "auto",
    "RRDL": "auto",
    "FAW": "auto",
    "WTRS": "auto",
    "WTRL": "auto",
    "WR": "auto",
    "RDRDSCL": "auto",
    "WRWRSCL": "auto"
    // ... all timings, "manual" | "auto" | "unknown"
  },
  "lastUpdated": "2026-04-14",
  "promptOnConfigChange": true
}
```

**GUI interaction:** Settings → Timing Sources shows a checklist grouped by category (Primaries, tRFC group, Secondaries, Turn-around, PHY). Each timing has a toggle: Manual / Auto. Defaults to "unknown" (gray) on first install. After a reboot where ConfigChangeDetector fires, the GUI prompts once: "Timings changed since last boot. Update manual/auto designations?" Pre-filled from last time. One interaction per boot, only when something changed, completely optional to dismiss.

**Write-once per boot:** Designations can only be changed once per boot session (after the post-boot prompt). This prevents confusion from mid-session edits and keeps the log clean. If the user needs to correct a designation, they reboot or use an override in Settings.

### 6.5 Validation Test Log

**Location:** `%ProgramData%\RAMWatch\tests.json`

**Schema:** Array of `ValidationResult` records (see Section 5.6). Each entry includes the full timing snapshot at time of test, so you can always see exactly what config was validated.

**GUI integration:** The Monitor tab has a "Log Test Result" button that opens a form: select test tool (dropdown), enter coverage/cycles, pass/fail, error count, notes. The service captures the current timing snapshot automatically.

### 6.6 Dual Logging

**Primary log:** `%ProgramData%\RAMWatch\logs\` — always written, non-negotiable. This is the authoritative local copy.

**Mirror log:** A user-configurable second path (Settings → Logging → Mirror Directory). Intended for Dropbox, Synology Drive, OneDrive, or any synced folder.

**Implementation:**
- On every primary log write, async file copy to mirror path.
- If mirror target is unavailable (network drive offline, Dropbox paused), log a warning, skip the copy, retry next cycle.
- Never block the primary log write waiting for the mirror.
- Never lose data because a sync service is busy.
- Mirror includes all CSVs, snapshots.json, tests.json, and designations.json. Does NOT mirror settings.json (machine-specific).

### 6.7 Config Change Log

**Location:** `%ProgramData%\RAMWatch\changes.json`

**Schema:** Array of `ConfigChange` records (see Section 5.4). Each entry captures before/after values for every timing that changed, plus the full timing snapshot after the change.

This is the backbone of the timeline view — combined with the validation test log and error event log, you can reconstruct the complete tuning journey.

---

## 7. GUI Design

### 7.1 Window Structure

Single main window with a tab strip or collapsible panel layout. No MDI. No floating windows.

```
┌──────────────────────────────────────────────────┐
│ RAMWatch                              _ □ ✕      │
├──────────────────────────────────────────────────┤
│ ● CLEAN — 0 errors since boot                   │
│ Boot: 04/14 09:01  |  Up: 12h 14m  |  12:15:33  │
├──────────────────────────────────────────────────┤
│ [Monitor]  [Timings]  [Snapshots]  [Settings]    │
├──────────────────────────────────────────────────┤
│                                                  │
│  ┌─ Error Monitor ─────────────────────────────┐ │
│  │ Source                    Count    Last      │ │
│  │ WHEA Hardware Errors        0       -        │ │
│  │ Machine Check Exception     0       -        │ │
│  │ Kernel Bugcheck             0       -        │ │
│  │ ...                                          │ │
│  └──────────────────────────────────────────────┘ │
│                                                  │
│  ┌─ Integrity ─────────────────────────────────┐ │
│  │ ● CBS.log: Clean                            │ │
│  │ ● SFC: Not run this session                 │ │
│  │ [Run SFC]  [Run DISM Check]                 │ │
│  └──────────────────────────────────────────────┘ │
│                                                  │
├──────────────────────────────────────────────────┤
│ [Save Snapshot]  [Copy to Clipboard]  [Refresh]  │
└──────────────────────────────────────────────────┘
```

### 7.2 Timings Tab

```
┌──────────────────────────────────────────────────┐
│ [Monitor]  [Timings]  [Snapshots]  [Settings]    │
├──────────────────────────────────────────────────┤
│                                                  │
│  ┌─ Clocks ────────────────────────────────────┐ │
│  │ MCLK  1800    FCLK  1800    UCLK  1800     │ │
│  │ Speed  3600 MT/s    Ratio  1:1:1            │ │
│  └──────────────────────────────────────────────┘ │
│                                                  │
│  ┌─ Primaries ─────────────────────────────────┐ │
│  │ CL    16    tRCDRD  22    tRCDWR  22        │ │
│  │ tRP   22    tRAS    42    tRC     64         │ │
│  │ CWL   16    GDM     On    Cmd     1T        │ │
│  └──────────────────────────────────────────────┘ │
│                                                  │
│  ┌─ Secondaries ───────────────────────────────┐ │
│  │ tRFC   577 (320ns)   tRFC2  375   tRFC4 260 │ │
│  │ tRRDS    7    tRRDL   11    tFAW   38       │ │
│  │ tWTRS    5    tWTRL   14    tWR    26        │ │
│  │ RDRDSCL  5    WRWRSCL  5                    │ │
│  └──────────────────────────────────────────────┘ │
│                                                  │
│  ┌─ Voltages ──────────────────────────────────┐ │
│  │ VDIMM  1.400V   VSOC   1.088V              │ │
│  │ VTT    0.700V   VDDP   1.048V              │ │
│  │ VDDG CCD 0.998V   VDDG IOD 0.998V         │ │
│  └──────────────────────────────────────────────┘ │
│                                                  │
│  ┌─ tRFC1 readback note ──────────────────────┐  │
│  │ ⚠ tRFC1 SMU readback unreliable on this    │  │
│  │   AGESA. Value shown may not reflect BIOS  │  │
│  │   setting. tRFC2/4 readback is accurate.   │  │
│  └─────────────────────────────────────────────┘  │
│                                                  │
│  ● Driver: InpOutx64 loaded                     │
│  ● Last read: 12:15:33                          │
│                                                  │
├──────────────────────────────────────────────────┤
│ [Save Snapshot]  [Copy to Clipboard]  [Refresh]  │
└──────────────────────────────────────────────────┘
```

### 7.3 Snapshots Tab

A comparison table. Select two snapshots from a dropdown and see them side by side with deltas highlighted. Changed values in green (improved) or red (regressed), based on whether lower is better for that timing.

Also shows the benchmark baseline table from the handoff doc — read/write/copy/latency per phase, manually entered.

### 7.4 Settings Tab

All `settings.json` values exposed as labeled controls. Toggle switches for booleans, numeric spinners for intervals, folder picker for log directory. Changes apply immediately and auto-save. No "OK/Cancel" dialog pattern — settings are live.

### 7.5 System Tray

When minimized to tray:
- Green icon: 0 errors since boot
- Yellow icon: non-critical events (app crash, hang)
- Red icon: WHEA, MCE, bugcheck, or BSOD event detected

Double-click tray icon: restore window.
Right-click context menu: `Show | Refresh | Save Snapshot | Quit`

Toast notification on new error (respecting cooldown setting).

---

## 8. Service Installation and Admin Strategy

The service runs as `LocalSystem` — full admin, no UAC, no user session dependency. This is the standard model for hardware monitoring (HWiNFO, NZXT CAM, iCUE all do this).

### 8.1 Service Install (one-time, requires admin)

```
sc.exe create RAMWatch binPath= "C:\RAMWatch\RAMWatch.Service.exe" start= auto DisplayName= "RAMWatch Monitor"
sc.exe description RAMWatch "RAM stability and system integrity monitor"
sc.exe start RAMWatch
```

Or wrapped in a `Install-RAMWatch.bat` that checks for admin and runs both commands. The GUI Settings tab also exposes an "Install Service" button that does this via `Process.Start("sc.exe", ...)` with `runas` verb — one UAC prompt, once, ever.

### 8.2 Service Recovery

Configure automatic restart on failure:

```
sc.exe failure RAMWatch reset= 86400 actions= restart/5000/restart/10000/restart/30000
```

Three restart attempts (5s, 10s, 30s delays), counter resets after 24 hours. If the service crashes three times in a day, something is fundamentally wrong — stop retrying.

### 8.3 Service Account

`LocalSystem` is the simplest path. It has:
- Full access to event logs
- Full access to PCI config space (via InpOutx64)
- Full access to run SFC/DISM
- Write access to `%ProgramData%\RAMWatch\` for shared logs/config

**Why not a custom service account:** Adds install complexity for no security benefit. RAMWatch doesn't access network resources, user profiles, or anything that benefits from a restricted account. The attack surface is a read-only hardware monitor that writes CSV files.

### 8.4 Data Directory

Shared data lives in `%ProgramData%\RAMWatch\` (typically `C:\ProgramData\RAMWatch\`). This is accessible to both the service (LocalSystem) and the GUI (user session).

```
C:\ProgramData\RAMWatch\
├── settings.json          # Shared config (service reads, GUI writes)
├── snapshots.json         # Named snapshots
└── logs/
    ├── events_2026-04-14.csv
    └── timings_2026-04-14.csv
```

**Permissions:** The install script grants `Users` group read/write on this directory. The service creates it with appropriate ACLs on first run.

### 8.5 GUI Without Service

If the GUI launches and can't connect to the service pipe, it shows a banner: "RAMWatch service not running. Monitoring is limited." Falls back to direct event log reads (unprivileged, no hardware reads, no SFC). This preserves the "works without admin" escape hatch from the original design.

---

## 9. Installation and Lifecycle

### 9.1 Distribution

```
RAMWatch/
├── RAMWatch.Service.exe    # Service (Native AOT, ~15MB)
├── RAMWatch.exe            # GUI client (Native AOT + WPF, ~25MB)
├── Install-RAMWatch.bat    # One-click service install (run as admin)
├── Uninstall-RAMWatch.bat  # Service remove + cleanup
└── README.md
```

No MSI, no NSIS, no installer framework. ZIP distribution.

### 9.2 Install-RAMWatch.bat

```batch
@echo off
net session >nul 2>&1 || (echo Run as Administrator & pause & exit /b 1)

set INSTALL_DIR=%~dp0
set DATA_DIR=%ProgramData%\RAMWatch

:: Create data directory
mkdir "%DATA_DIR%\logs" 2>nul
icacls "%DATA_DIR%" /grant Users:(OI)(CI)RW >nul

:: Install and start service
sc.exe create RAMWatch binPath= "\"%INSTALL_DIR%RAMWatch.Service.exe\"" start= auto DisplayName= "RAMWatch Monitor"
sc.exe description RAMWatch "RAM stability and system integrity monitor"
sc.exe failure RAMWatch reset= 86400 actions= restart/5000/restart/10000/restart/30000
sc.exe start RAMWatch

echo RAMWatch service installed and started.
pause
```

### 9.3 First Launch (Service)

1. Create `%ProgramData%\RAMWatch\` if it doesn't exist
2. Write default `settings.json`
3. Detect InpOutx64 driver — check known paths
4. Open named pipe server
5. Start event log watchers
6. Start hardware read timer (if driver available)
7. Start CSV logging
8. Wait for clients or events (idle)

### 9.4 First Launch (GUI)

1. Connect to `\\.\pipe\RAMWatch`
2. If connection fails: show "service not installed" banner with "Install" button
3. Receive full state from service
4. Render UI
5. Optionally create logon startup entry (`HKCU\...\Run`) for the GUI only

### 9.5 Uninstall-RAMWatch.bat

```batch
@echo off
net session >nul 2>&1 || (echo Run as Administrator & pause & exit /b 1)

sc.exe stop RAMWatch
sc.exe delete RAMWatch

:: Remove GUI autostart
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v RAMWatch /f 2>nul

echo RAMWatch service removed.
echo Data preserved in %ProgramData%\RAMWatch (delete manually if desired).
pause
```

### 9.6 Uninstall

Uninstall-RAMWatch.bat removes the service and autostart entry. Log data in `%ProgramData%\RAMWatch\` is preserved (user deletes manually if desired). The application folder itself is just EXEs — delete it.

---

## 10. Native AOT Considerations

WPF + Native AOT has constraints. Key issues and mitigations:

| Issue | Mitigation |
|---|---|
| XAML runtime compilation not supported in AOT | Use compiled XAML (BAML) only. No `XamlReader.Load()` from strings at runtime. All XAML in .xaml files compiled at build time. |
| Reflection-based binding limited | Use source generators for INotifyPropertyChanged (e.g., CommunityToolkit.Mvvm `[ObservableProperty]`). Explicit binding paths only. |
| Some WPF controls use reflection internally | Test all controls in AOT mode early. DataGrid is known to work. Avoid third-party control libraries unless AOT-verified. |
| P/Invoke to InpOutx64 | Works natively in AOT. Declare with `[LibraryImport]` (source-generated) instead of `[DllImport]`. |
| JSON serialization | Use `System.Text.Json` source generators (`[JsonSerializable]`). No reflection-based serialization. |

**Fallback:** If AOT proves too constraining during development, fall back to standard self-contained single-file publish (includes .NET runtime, ~60MB EXE). Still no install required, just larger.

---

## 11. InpOutx64 Integration Detail

### 11.1 DLL Interface

```csharp
internal static partial class InpOut
{
    [LibraryImport("inpoutx64.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsInpOutDriverOpen();

    [LibraryImport("inpoutx64.dll")]
    internal static partial uint ReadPciConfigDword(uint bus, uint device, uint func, uint offset);

    [LibraryImport("inpoutx64.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReadPhysicalMemory(
        IntPtr address, IntPtr buffer, uint length);
}
```

### 11.2 UMC Register Map (Zen 3, AM4)

The Unified Memory Controller registers live in PCI config space. ZenTimings reads them via config space access at known BDF (Bus/Device/Function) addresses.

**UMC instances on AM4:** Two instances (one per channel), at device addresses `0x18:0x1` and `0x18:0x2` (approximate — exact BDF varies by platform and must be probed).

**Key register groups to port from ZenTimings source:**

| Register Group | Offset Range | Contents |
|---|---|---|
| UMC_DRAM_TIMING1 | 0x200–0x204 | CL, RAS, RCDRD, RCDWR, RC, RP |
| UMC_DRAM_TIMING2 | 0x204–0x208 | RTP, RRDS, RRDL, RRD |
| UMC_DRAM_TIMING3 | 0x208–0x20C | WR, WTRS, WTRL, FAW |
| UMC_DRAM_TIMING4 | 0x20C–0x210 | CWL, additional |
| UMC_DRAM_TIMING5 | 0x210–0x214 | RFC, RFC2, RFC4 |
| UMC_DRAM_TIMING6 | 0x214–0x218 | RDRDSCL, WRWRSCL |
| UMC_DRAM_TIMING7+ | 0x218–0x230 | Turn-around timings |
| UMC_DRAM_CONFIG | 0x240–0x250 | GDM, BGS, Cmd rate |

**Important:** These offsets are from the ZenTimings Zen3 implementation and need verification against the specific AGESA version. The register layout can shift between AGESA releases. RAMWatch should include a validation step: read known-constant fields and verify they match expected values before trusting the rest of the decode.

### 11.3 Voltage Reads

| Voltage | Source | Method |
|---|---|---|
| VSOC | SVI2 telemetry plane | PCI config read, Core::1::0x5A address, specific bitfield |
| VDDG CCD | SMU power table | Read via SMU mailbox protocol |
| VDDG IOD | SMU power table | Read via SMU mailbox protocol |
| CLDO VDDP | SMU power table | Read via SMU mailbox protocol |
| VDIMM | BIOS WMI (AMD_ACPI) | WMI query (no driver needed, but static value) |
| VTT | BIOS WMI (AMD_ACPI) | WMI query (MSI/Gigabyte only) |

**SMU mailbox protocol:** This is the most complex part. Communicating with the SMU requires writing a message ID to a mailbox register, writing arguments, triggering execution, and reading results. ZenTimings uses the Ryzen SMU library for this. We should evaluate whether to bundle a port of this or treat SMU reads as optional/advanced.

---

## 12. Error Classification and Alerting

Not all errors are equal. The UI and notifications should distinguish severity.

### Severity Tiers

| Tier | Color | Toast | Examples |
|---|---|---|---|
| Critical | Red, pulsing dot | Immediate | MCE, BSOD, Kernel Bugcheck |
| Warning | Red, static dot | Yes (with cooldown) | WHEA correctable, NTFS error, unexpected shutdown |
| Notice | Yellow | Optional (off by default) | Code integrity, app crash, app hang |
| Info | Gray | Never | CBS.log clean, SFC clean |

### Alert Cooldown

After a toast notification, suppress further toasts from the same source for `notifyCooldownSeconds` (default 300). This prevents notification storms during a bad boot where WHEA events might fire every few seconds. The running count in the UI still updates in real-time.

---

## 13. Export System

### 13.1 Clipboard Export

One-click "Copy to Clipboard" produces a formatted text block for forums and chat:

```
RAMWatch — 2026-04-14 12:15:33
Boot: 04/14 09:01 | Uptime: 3h 14m
Status: CLEAN — 0 errors since boot

Clocks: DDR4-3600 | MCLK 1800 | FCLK 1800 | UCLK 1800 | 1:1:1
Primaries: 16-22-22-22-42-64 | CWL 16 | GDM On | 1T
tRFC: 577/375/260 (320ns)
Secondaries: RRDS 7 | RRDL 11 | FAW 38 | WTRS 5 | WTRL 14 | WR 26
SCL: RDRD 5 | WRWR 5
Voltages: VDIMM 1.400 | VSOC 1.088 | VDDG 0.998/0.998 | VDDP 1.048 | VTT 0.700
```

### 13.2 AI Helper Digest

A context-window-friendly export designed for pasting into a conversation with an AI tuning assistant. Target: under 2000 tokens. Includes everything the AI needs to pick up where it left off, nothing it doesn't.

**Includes:**
- All timings (every value ZenTimings exposes), with manual/auto designations
- Drift warnings on any auto-trained timing that changed across boots
- Current config vs. LKG config (if different), with diff
- Last N validation results with tool, coverage, pass/fail
- Error summary (counts only, no message text)
- Next planned change (user-editable note field)
- Hardware summary (CPU, board, BIOS, AGESA, RAM, die, ranks)

**Format:**

```
RAMWatch Digest — 2026-04-14

Hardware: 5800X3D | B550 TOMAHAWK E7C91AMS.2A0 | AGESA 1.2.0.E
RAM: CMH64GX4M2Z4000C18 | Micron Rev.B | 2×32GB DR | 4 ranks

Current: DDR4-3600 | FCLK 1800 | VDIMM 1.400V | 1:1:1

Primaries (manual): CL 16 | RCDRD 22 | RCDWR 22 | RP 22 | RAS 42 | RC 64
CWL (manual): 16 | GDM: On | Cmd: 1T
tRFC (manual): 577/375/260 (320ns)
Secondaries (auto): RRDS 7 | RRDL 11 | FAW 38 | WTRS 5 | WTRL 14 | WR 26
  ⚠ tRRDL drifted: 11→12 on boot 04/15 08:30 (reverted on reboot)
SCL (auto): RDRD 5 | WRWR 5
Misc (auto): RTP 14 | RDWR 9 | WRRD 2 | CKE 0 | REFI 14029 | STAG 255
Turn-around (auto): RDRDSC 1 | RDRDSD 5 | RDRDDD 4 | WRWRSC 1 | WRWRSD 7 | WRWRDD 6
MOD (auto): MOD 27 | MODPDA 27 | MRD 8 | MRDPDA 18
PHY (auto, read-only): PHYWRD 2 | PHYWRL 13 | PHYRDL 28/26
Rtt (auto): Nom Dis | Wr RZQ/3 | Park RZQ/1 | ProcODT 48Ω
DrvStr (auto): Clk 24Ω | AddrCmd 24Ω | CsOdt 24Ω | Cke 24Ω
Voltages: VSOC 1.088 | VDDG 0.998/0.998 | VDDP 1.048 | VTT 0.700

LKG: Same as current (validated 04/13 22:30)

Validation History (last 5):
  04/13 22:30  Karhu 12400%  PASS  16-22-22-42 tRFC577
  04/13 21:15  Karhu  2000%  PASS  18-22-22-42 tRFC577
  04/13 18:45  Boot   FAIL   18-22-22-42 tRFC480 (no POST)
  04/13 18:20  Karhu  8000%  PASS  18-22-22-42 tRFC600
  04/13 18:03  Boot   PASS   18-22-22-42 tRFC600

Errors (this boot): 0
Errors (all time): 0 WHEA, 0 MCE, 0 BSOD

Next planned: tRCDRD 22→21
```

### 13.3 Full History Export

"Export All" button produces a single JSON file containing everything: all snapshots, all validation results, all config changes, all drift events, all error logs, all designations. Importable into a fresh RAMWatch install for backup or migration.

---

## 14. Git Integration

### 14.1 Overview

The service maintains a local git repo at `%ProgramData%\RAMWatch\history\`. On every config change or validation test, it auto-commits a set of flat, phone-readable files. Optional push to a private GitHub/GitLab repo.

### 14.2 Repo Structure

```
history/
├── CURRENT.md          # Running config, formatted for phone reading in BIOS
├── LKG.md              # Last Known Good — your BIOS revert checklist
├── CHANGELOG.md        # Append-only, newest first, one-line per event
├── timings.json        # Machine-readable current snapshot
├── snapshots.json      # All named snapshots
├── tests.json          # All validation results
└── designations.json   # Manual/auto map
```

### 14.3 CURRENT.md (Phone-Readable BIOS Checklist)

```markdown
# RAMWatch — Current Config
Updated: 2026-04-14 22:30

## BIOS Settings (enter these manually)
DRAM Frequency: DDR4-3600
FCLK: 1800 MHz
VDIMM: 1.400V

### Primaries
CL: 16
tRCDRD: 22
tRCDWR: 22
tRP: 22
tRAS: 42
tRC: 64
CWL: 16

### tRFC
tRFC1: 577
tRFC2: 375
tRFC4: 270

## Auto-trained (leave on Auto in BIOS)
tRRDS: 7  tRRDL: 11  tFAW: 38
tWTRS: 5  tWTRL: 14  tWR: 26
RDRDSCL: 5  WRWRSCL: 5

## Last validated
Karhu 12400% PASS — 2026-04-14 06:30
```

### 14.4 Commit Behavior

Commits are automatic and descriptive:

```
Config change: CL 18→16, CWL 18→16
Karhu 12400% PASS @ 16-22-22-42 tRFC577
Config change: tRCDRD 22→21
Boot FAIL — no POST (reverted to LKG)
Drift detected: tRRDL 11→12 (auto-trained)
```

### 14.5 Account Isolation

The service's git repo uses repo-scoped config that never touches global git/gh state. User's work repos (SGH, NTH/Scribe, etc.) are completely unaffected.

**Implementation via isolated `GH_CONFIG_DIR`:**

```
# RAMWatch gets its own gh auth state
set GH_CONFIG_DIR=%ProgramData%\RAMWatch\.gh
gh auth login --hostname github.com
gh auth setup-git
```

**Repo-level git config (no global side effects):**

```
git -C <repo_path> config user.name "RAMWatch"
git -C <repo_path> config user.email "<username>@users.noreply.github.com"
```

**Setup flow (one-time, via GUI):**
1. Settings → Git → Enable
2. Enter GitHub username and repo name
3. Click "Authenticate" → opens browser for OAuth or accepts PAT
4. Service creates private repo via `gh repo create` if it doesn't exist
5. Done. Never touches global auth again.

**Dependencies:** `git` and `gh` CLI on PATH. If not found, disable git integration with a notification. No libgit2, no embedded git — shell out to the user's installed tools.

---

## 15. Three-Tier Privacy Model

Data exists at three visibility levels. Each tier is a strict subset of the one below it.

### 15.1 Local (machine only)

**Location:** `%ProgramData%\RAMWatch\`

**Contains everything:** Full timestamps with time-of-day, boot IDs, Windows event XML with error messages, file paths, serial numbers, PHY training values, drift event details, SFC/DISM output, all forensic detail.

**Never leaves the machine** unless the user manually copies it.

### 15.2 Private Repo

**Location:** GitHub/GitLab private repo

**Contains:** Hardware config (CPU, board, BIOS, AGESA, RAM part number, die, ranks), all timings with manual/auto designations, validation results (tool, coverage, pass/fail), config change history, LKG, CURRENT.md, CHANGELOG.md, benchmark scores.

**Excludes:** Serial numbers, error message text (counts only), boot timestamps with time-of-day (dates only), file paths, Windows event XML, SFC/DISM output, drift forensic details (drift is noted in changelog but without boot-time correlation).

**Principle:** Enough to reconstruct your tuning journey and share with a tuning helper (human or AI). Not enough to fingerprint your specific machine or correlate with other data sources.

### 15.3 Public Repo (opt-in)

**Location:** User's public GitHub repo (e.g., `thereprocase/ramwatch-public`)

**Contains:** Anonymized, structured contribution records. One record per validated stable config. Required fields:

| Field | Example | Required |
|---|---|---|
| CPU model | AMD Ryzen 7 5800X3D | Yes |
| Motherboard model | MSI MAG B550 TOMAHAWK | Yes |
| BIOS version | E7C91AMS.2A0 | Yes |
| AGESA version | ComboAM4v2PI 1.2.0.E | Yes |
| RAM part number | CMH64GX4M2Z4000C18 | Yes |
| Die type | Micron 16Gbit Rev.B | Yes |
| Rank config | 2×32GB, dual-rank, 4 ranks total | Yes |
| Frequency / FCLK | 3600 / 1800 | Yes |
| All timings | Full snapshot | Yes |
| Voltages | VDIMM, VSOC, VDDG, VDDP | Yes |
| Validation tool + result | Karhu 12400% PASS | Yes |
| Benchmark scores | AIDA64 read/write/copy/latency | Optional |
| Error history | — | Never |
| Boot timestamps | — | Never |
| Serial numbers | — | Never |

**Excludes:** Everything personal. No usernames tied to error logs, no timestamps beyond the submission date, no boot session data.

**Contribution format:** One JSON file per validated config, appended to a `contributions/` directory in the public repo. File named by random ID, not username.

**Community value:** The killer query is "Show me every validated stable config for Micron Rev.B, 2×32GB, dual rank, DDR4-3600, on B550 boards." This data doesn't exist in centralized form today.

---

## 16. Build and Dependency Summary

| Dependency | Purpose | License | Bundle? |
|---|---|---|---|
| .NET 8 SDK | Build toolchain | MIT | No (build only) |
| Microsoft.Extensions.Hosting | Windows service hosting (BackgroundService) | MIT | Yes (NuGet, compiled in) |
| CommunityToolkit.Mvvm | Source-gen MVVM (GUI only) | MIT | Yes (NuGet, compiled in) |
| System.Text.Json | Settings/snapshot/IPC serialization | MIT | Yes (part of .NET) |
| System.IO.Pipes | Named pipe IPC | MIT | Yes (part of .NET) |
| Hardcodet.NotifyIcon.Wpf | System tray support (GUI only) | CPOL | Yes (NuGet) |
| InpOutx64.dll | Hardware register access | Freeware | No (runtime dependency, user provides) |
| git | Version control for tuning history | GPL-2.0 | No (runtime, optional, user provides) |
| gh CLI | GitHub API for repo create/push | MIT | No (runtime, optional, user provides) |

**No other dependencies.** No logging framework (we write CSV directly). No DI container (manual construction, the app is small). No ORM (we write flat files). No message bus (named pipe with JSON is the IPC). No libgit2 (shell out to user's git).

---

## 17. Project Structure

```
RAMWatch/
├── RAMWatch.sln
├── src/
│   ├── RAMWatch.Core/                    # Shared library (models, hardware, IPC)
│   │   ├── Models/
│   │   │   ├── MonitoredEvent.cs
│   │   │   ├── TimingSnapshot.cs
│   │   │   ├── TimingDesignation.cs     # Manual/auto per timing
│   │   │   ├── ConfigChange.cs          # Before/after diff
│   │   │   ├── DriftEvent.cs            # Auto-trained timing drift
│   │   │   ├── ValidationResult.cs      # Stability test record
│   │   │   ├── PublicContribution.cs    # Anonymized community record
│   │   │   ├── AppSettings.cs
│   │   │   ├── Snapshot.cs
│   │   │   └── IpcMessages.cs           # All message types for pipe protocol
│   │   │
│   │   ├── Hardware/
│   │   │   ├── InpOut.cs                 # P/Invoke declarations
│   │   │   ├── CpuDetect.cs             # CPUID family/model detection
│   │   │   ├── Zen3Umc.cs               # Zen 3 UMC register map + decode
│   │   │   ├── Zen2Umc.cs               # Zen 2 register map (if needed)
│   │   │   ├── Svi2.cs                  # SVI2 voltage telemetry decode
│   │   │   └── SmuMailbox.cs            # SMU communication protocol
│   │   │
│   │   ├── Ipc/
│   │   │   ├── PipeServer.cs            # Named pipe server (service side)
│   │   │   ├── PipeClient.cs            # Named pipe client (GUI side)
│   │   │   └── MessageSerializer.cs     # JSON-over-newline framing
│   │   │
│   │   └── RAMWatch.Core.csproj
│   │
│   ├── RAMWatch.Service/                 # Windows service (headless)
│   │   ├── Program.cs                   # Service entry point
│   │   ├── RamWatchService.cs           # BackgroundService implementation
│   │   ├── Services/
│   │   │   ├── EventLogMonitor.cs       # EventLogWatcher subscriptions
│   │   │   ├── HardwareReader.cs        # InpOutx64 P/Invoke + UMC decode
│   │   │   ├── IntegrityChecker.cs      # SFC/DISM/CBS runner
│   │   │   ├── ConfigChangeDetector.cs  # Timing change detection across boots
│   │   │   ├── DriftDetector.cs         # Auto-trained timing drift analysis
│   │   │   ├── ValidationTestLogger.cs  # Stability test result recording
│   │   │   ├── CsvLogger.cs             # Append-only CSV writer
│   │   │   ├── MirrorLogger.cs          # Async copy to mirror directory
│   │   │   ├── SnapshotStore.cs         # Named snapshot persistence
│   │   │   ├── StateAggregator.cs       # Combines all sources into current state
│   │   │   ├── LkgTracker.cs            # Last Known Good config tracking
│   │   │   ├── GitCommitter.cs          # Git repo management + commit + push
│   │   │   ├── PublicContributor.cs     # Anonymized public data submission
│   │   │   └── ZenTimingsDetector.cs    # InpOutx64 driver detection
│   │   │
│   │   └── RAMWatch.Service.csproj
│   │
│   ├── RAMWatch/                         # WPF GUI client
│   │   ├── App.xaml
│   │   ├── App.xaml.cs
│   │   ├── MainWindow.xaml
│   │   ├── MainWindow.xaml.cs
│   │   │
│   │   ├── ViewModels/
│   │   │   ├── MainViewModel.cs         # Receives state from pipe, owns UI state
│   │   │   ├── SettingsViewModel.cs
│   │   │   └── SnapshotViewModel.cs
│   │   │
│   │   ├── Views/
│   │   │   ├── MonitorTab.xaml          # Error monitor panel
│   │   │   ├── TimingsTab.xaml          # Hardware timings + manual/auto indicators
│   │   │   ├── TimelineTab.xaml         # Chronological config changes + tests + errors
│   │   │   ├── SnapshotsTab.xaml        # Snapshot comparison + LKG display
│   │   │   ├── ValidationDialog.xaml    # Log test result form
│   │   │   ├── DesignationsDialog.xaml  # Manual/auto timing checklist
│   │   │   └── SettingsTab.xaml         # Settings + Git + Sharing controls
│   │   │
│   │   ├── Converters/
│   │   │   ├── SeverityToColorConverter.cs
│   │   │   └── BoolToVisibilityConverter.cs
│   │   │
│   │   ├── Themes/
│   │   │   └── Dark.xaml                # Color resources, control templates
│   │   │
│   │   └── RAMWatch.csproj
│   │
│   └── RAMWatch.Tests/
│       ├── UmcDecodeTests.cs            # Register decode unit tests
│       ├── CsvLoggerTests.cs
│       ├── EventClassificationTests.cs
│       ├── IpcRoundtripTests.cs         # Serialize/deserialize message tests
│       ├── DriftDetectorTests.cs        # Drift detection logic
│       ├── ConfigChangeDiffTests.cs     # Before/after comparison
│       └── PrivacyFilterTests.cs        # Verify private/public exclusions
│
├── scripts/
│   ├── Install-RAMWatch.bat             # Service install (run as admin)
│   └── Uninstall-RAMWatch.bat           # Service remove
│
├── docs/
│   ├── ARCHITECTURE.md                  # This document
│   └── REGISTER_MAP.md                  # UMC register reference
│
└── README.md
```

---

## 18. Development Phases

### Phase 1 — Service + IPC + Minimal GUI (MVP)
- Windows service with event log monitoring (push-based)
- CBS.log scan
- Named pipe server with JSON protocol
- GUI client: connects to pipe, displays error table
- System tray with status icon
- CSV event logging (service-side)
- Dual logging (primary + configurable mirror directory)
- Settings (JSON, shared via %ProgramData%)
- Install/uninstall batch scripts
- Clipboard export
- No hardware reads. No timings tab.
- **Ship when:** Replaces the PowerShell prototype. Service survives logoff.

### Phase 2 — Hardware Reads + Timings
- ZenTimings/InpOutx64 detection (scan on each service start)
- Zen 3 UMC register decode (all timings ZenTimings exposes)
- SVI2 voltage reads
- Timings tab in GUI with all values
- Timing snapshot CSV logging
- tRFC1 readback bug detection and warning
- **Ship when:** Matches ZenTimings output for Zen 3

### Phase 3 — Tuning Journal
- Config change detection + change log
- Manual/auto timing designations (per-boot checklist prompt, write-once per boot)
- Drift detection across boots (rolling 20-boot window)
- Validation test logger (Karhu, TM5, OCCT, boot pass/fail, custom user-defined tests)
- Last Known Good (LKG) tracking with configurable pass thresholds
- Timeline tab (chronological view: config changes + test results + errors interleaved)
- Snapshot comparison with LKG diff (green = improved, red = regressed)
- AIDA64 manual entry + CSV import path
- AI helper digest export (<2000 tokens, all timings, manual/auto labels, drift flags)
- Full history JSON export/import for backup and migration
- **Ship when:** Complete tuning diary. Replaces handoff docs and session notes.

### Phase 4 — Git + Community
- Local git repo with auto-commit on config change / validation
- CURRENT.md + LKG.md phone-readable BIOS checklists (auto-generated)
- CHANGELOG.md auto-generated with descriptive commit messages
- GitHub/GitLab push with isolated account config (GH_CONFIG_DIR, repo-scoped creds)
- Three-tier privacy filtering (local → private repo → public repo)
- Public contribution records (anonymized, one per validated config)
- Community sharing opt-in with per-field visibility controls
- **Ship when:** Private repo works end-to-end. Public sharing functional.

### Phase 5 — Polish
- SFC/DISM integration (service runs, GUI shows progress via pipe)
- Toast notifications with cooldown (including drift alerts)
- Log retention and rotation
- Theme refinement (SGH Dark adjacent)
- Zen 2 / Zen 4 register maps (stretch)
- FCLK stability WHEA classifier (distinguish correctable FCLK errors from other WHEA)
- Community data aggregation / query tool (stretch — "show all Rev.B configs on B550")
- **Ship when:** Ready for open-source public release

---

## 19. Open Questions

1. **InpOutx64 licensing and distribution.** The DLL is freeware but redistribution terms are unclear. Can we bundle it, or must the user install ZenTimings first? Need to check the highrez license.

2. **SMU mailbox protocol complexity.** Reading VDDG/VDDP from the SMU power table requires a multi-step mailbox protocol that varies by SMU firmware version. ZenTimings handles this via the Ryzen SMU library. Do we port that library, depend on it as a DLL, or skip SMU reads entirely and rely on WMI for voltages?

3. **Zen 4 / AM5 / DDR5 support.** The UMC register layout changes significantly for DDR5 platforms. Phase 1–4 target Zen 3 AM4 only. Zen 4 support would require a separate register map module.

4. **Auto-update mechanism.** The design principle says "no network calls unless user enables it." If we add an update checker, it should be opt-in, check a GitHub releases API endpoint, and never auto-download.

5. **Code signing.** An unsigned EXE will trigger SmartScreen warnings on first launch. A code signing certificate costs ~$200–400/year. Worth it for public distribution, not for personal use.

6. **Name.** RAMWatch is descriptive but generic. Consider whether a more distinctive name serves the project better if it ever goes public.

7. **Named pipe security.** The pipe is created by LocalSystem and connected to by an unprivileged user. Default ACLs allow any local user to connect. For a single-user desktop this is fine. If the tool ever targets multi-user systems, the pipe should restrict access to the interactive user or a specific group.

8. **Service + AOT for WPF.** The service (no GUI) is straightforward for Native AOT. WPF + Native AOT is newer and has constraints (Section 10). If WPF AOT proves fragile, the fallback is AOT for the service only and standard self-contained for the GUI. The service is where zero-overhead matters most.

9. **Community data aggregation.** How to aggregate public contributions across repos. Options: (a) central index repo that scrapes user repos, (b) users submit PRs to a shared repo, (c) lightweight API that collects contributions. Option (a) is most decentralized. Option (c) violates "no phone home." Decision deferred to Phase 4.

10. **Git credential storage.** PAT-in-URL vs. GH_CONFIG_DIR credential helper vs. Windows Credential Manager. GH_CONFIG_DIR is cleanest for isolation. Windows Credential Manager is most secure. PAT-in-URL is simplest but stores secret in plaintext settings.json. Recommend GH_CONFIG_DIR as default, document alternatives.

11. **AIDA64 CSV import format.** AIDA64's export format is not officially documented and may change between versions. The import parser should be fault-tolerant and fall back to manual entry if the CSV doesn't match expected columns.

---

## 20. Non-Goals

Things this application explicitly does NOT do:

- **Change any hardware settings.** Read-only. No writing to PCI config space, no SMU commands that modify state, no voltage changes. This is a monitor, not a tuner.
- **Replace Karhu, TM5, or AIDA64.** No memory stress testing. No benchmarking. Those tools exist and are good at their jobs.
- **Run on Linux or macOS.** Windows-only. The entire monitoring stack is Windows-specific.
- **Phone home.** No analytics, no crash reporting, no update pings unless explicitly enabled.
- **Look like every other AI-generated app.** Custom dark theme, purpose-built layout, no rounded-corner card soup.

---

## 21. License

MIT. Simplest, most permissive, no contributor friction. The full codebase, architecture docs, and community contribution schemas are open source. InpOutx64 is a third-party freeware dependency, not bundled.
