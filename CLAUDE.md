# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

RAMWatch is a Windows-only DRAM tuning monitor: a system health tracker + tuning journal + shareable history for enthusiasts who tune RAM timings. It reads hardware registers, watches event logs, tracks timing drift across boots, and maintains a git-backed tuning diary. Read-only — it never modifies hardware.

**Status:** Phases 1-3 substantially complete. Solution builds, 576 tests passing, service installed and running. All hardware reads operational: UMC timings, SVI2 voltages, SMU PM table (FCLK/UCLK, VDDP/VDDG, thermal/power), BIOS WMI (VDimm/Vtt/Vpp, ProcODT, Rtt, drive strengths), DIMMs, UMC address map. Three-tier polling: hot (3s thermals+SVI2), warm (30-60s full state), cold (boot+trigger timings). IPC protocol v2 with ThermalUpdateMessage and RequestTimingRefreshMessage for external clients (RAMBurn integration). WHEA monitoring with MCA bank decode, per-CCD temps, reactive vitals capture on hardware events. Two War Council code reviews completed with all findings addressed.

## Architecture Blockers (Resolved)

`SPRINT-PLAN.md` lists seven blockers (B1–B7) that were resolved before Phase 1 implementation. Keep these constraints in mind when modifying code:

- **B1** — License decision (ZenTimings/ZenStates-Core are GPL-3.0, not MIT; either GPL this project or clean-room the UMC decode from AMD PPRs)
- **B2** — WPF Native AOT is blocked by the SDK; service uses AOT, GUI uses self-contained single-file (resolved, documented below)
- **B3** — Target `net10.0-windows`, not .NET 8 (.NET 8 EOL Nov 2026)
- **B4** — Named pipe must have explicit DACL restricting to interactive user SID + SYSTEM (default ACLs allow local privilege escalation via `runIntegrity`)
- **B5** — Settings privilege escalation: `inpOutDllPath` and path fields let unprivileged GUI write values the LocalSystem service will act on. Service owns all paths internally; never load DLL paths from settings
- **B6** — IPC protocol version field on every message; service rejects unknown types with a structured error
- **B7** — File concurrency: single-writer per file, write-to-temp-then-rename for JSON, `FileShare.Read` on CSV appends

B4–B7 were Phase 1 blocking and are implemented. Verify these constraints are maintained when modifying IPC, settings, or file I/O code.

## Architecture — Two-Process Model

Two executables communicating over a secured named pipe (`\\.\pipe\RAMWatch`):

- **RAMWatch.Service.exe** — Windows service, LocalSystem, starts at boot. **Native AOT** (`PublishAot=true`). Owns all monitoring (EventLogWatcher push-based subscriptions, InpOutx64 P/Invoke for UMC register reads, CBS.log tail, SFC/DISM runner), state aggregation, CSV logging, git commits, and the pipe server. Target: <0.1% CPU, <25MB RAM steady state.
- **RAMWatch.exe** — WPF GUI client, unprivileged, on-demand. **Self-contained single-file** (`PublishSingleFile=true`, NOT Native AOT — WPF does not support AOT). ~80-120MB. Connects to pipe, receives JSON state pushes, sends commands. MVVM with CommunityToolkit.Mvvm source generators. Tabs: Monitor, Timings, Timeline, Snapshots, Settings. Custom dark theme (navy background, semantic status colors, Cascadia Mono for numbers).

**Pipe security:** DACL restricts access to SYSTEM + interactive user SID. Not open to all local users.

IPC is JSON-over-newline (protocol v2). Service → client: `state` (full push on connect + warm-tier periodic), `event` (real-time with optional vitals), `thermalUpdate` (hot-tier 3s push with ThermalPowerSnapshot + SVI2 VCore/VSoC). Client → service: `getState`, `runIntegrity`, `updateSettings`, `logValidation`, `updateDesignations`, `getDigest`, `saveSnapshot`, `requestTimingRefresh` (triggers immediate cold-tier re-read). Also: era management, boot fail logging, snapshot CRUD.

## Technology Stack

- **.NET 10 LTS** (`net10.0-windows`), `win-x64`, self-contained. (.NET 8 EOL Nov 2026 — insufficient runway)
- **Service:** Native AOT (`PublishAot=true`). **GUI:** Self-contained single-file, not AOT (WPF incompatible — see Key Design Constraints)
- **WPF** for GUI (not WinUI 3, not MAUI — weight class decision)
- **PawnIO** for PCI config space / MSR / MMIO reads (signed driver, replaces InpOutx64). ZenTimings has migrated to PawnIO. GPL-2+ with IOCTL linking exception.
- **System.Text.Json** with source generators — single `RamWatchJsonContext.cs` in Core for all serializable types
- `[LibraryImport]` for P/Invoke in service (source-generated, not `[DllImport]`). Wrap every call in try/catch — missing DLL throws `DllNotFoundException` at call site, not load time.
- Use `NativeLibrary.Load(path)` + `NativeLibrary.TryGetExport` for runtime path scanning (the `[LibraryImport]` fixed name can't implement multi-path scan)
- CommunityToolkit.Mvvm `[ObservableProperty]` for MVVM bindings
- **H.NotifyIcon.Wpf** for system tray (NOT Hardcodet.NotifyIcon.Wpf — unmaintained, no AOT, CPOL license)
- **Microsoft.Extensions.Hosting.WindowsServices** required for `.UseWindowsService()` (not just Hosting)
- **xUnit v3** for tests, manual fakes for mocking (Moq/NSubstitute use runtime codegen, AOT-incompatible)
- No DI container, no logging framework, no ORM, no libgit2 — flat files and shell-out to git/gh

## Build and Run

```bash
# Build everything
dotnet build RAMWatch.sln

# Publish service (Native AOT, ~15MB)
dotnet publish src/RAMWatch.Service -c Release -r win-x64

# Publish GUI (self-contained single-file, ~80-120MB — NOT AOT)
dotnet publish src/RAMWatch -c Release -r win-x64

# Run all tests
dotnet test src/RAMWatch.Tests

# Run a single test or test class by name (xUnit filter)
dotnet test src/RAMWatch.Tests --filter "FullyQualifiedName~UmcDecode"

# Dev: build + run GUI from source (no admin)
scripts/Dev.ps1

# Install: publish + service + shortcuts (admin)
scripts/Install.ps1

# Update: rebuild + hot-swap installed binaries (admin)
scripts/Update.ps1

# Uninstall: remove service + binaries (admin)
scripts/Uninstall.ps1
```

**WSL / Linux caveat:** this repo lives on WSL but the target is `net10.0-windows` with WPF — you cannot produce a runnable artifact from the Linux side (no WPF, no win-x64 host on Linux). Real builds, test runs, and the service install happen on Windows (PowerShell or cmd). From WSL you can still compile-check the Core + Service projects with an installed .NET 10 SDK, but the GUI project and anything touching WPF will not build on Linux. Treat Linux-side `dotnet` as a syntax/unit-test aid for the non-WPF code, not a full build.

## Project Structure

```
RAMWatch.sln
src/
  RAMWatch.Core/          # Shared: models + IPC only (both sides use these)
    Models/               # MonitoredEvent, TimingSnapshot, ConfigChange, DriftEvent, etc.
    Ipc/                  # PipeServer, PipeClient, MessageSerializer
  RAMWatch.Service/       # Windows service (BackgroundService, Native AOT)
    Hardware/             # InpOut P/Invoke, CpuDetect, Zen2/3 UMC decode, SVI2, SMU
    Services/             # EventLogMonitor, HardwareReader, IntegrityChecker,
                          # ConfigChangeDetector, DriftDetector, CsvLogger,
                          # StateAggregator, LkgTracker, GitCommitter, etc.
  RAMWatch/               # WPF GUI
    ViewModels/           # MainViewModel, SettingsViewModel, SnapshotViewModel
    Views/                # MonitorTab, TimingsTab, TimelineTab, SnapshotsTab, etc.
    Themes/Dark.xaml      # Color tokens as ResourceDictionary entries
    Converters/           # SeverityToColor, BoolToVisibility
  RAMWatch.Tests/         # UMC decode, CSV logger, IPC roundtrip, drift detection, privacy filter
scripts/                  # Install/Uninstall batch files
```

## Data Location

All runtime data lives in `%ProgramData%\RAMWatch\`:

- `settings.json`, `snapshots.json`, `designations.json`, `tests.json`, `changes.json` — JSON state files (atomic write-to-temp-then-rename, service is sole writer)
- `logs/` — daily CSVs, `FileShare.Read` on append so the mirror logger can copy concurrently
- The service owns every write into this directory. The GUI never touches it directly; it sends changes via pipe. Settings ACLed to Administrators so unprivileged users can't tamper with what the LocalSystem service will read.

## Implementation Phases

1. **Service + IPC + Minimal GUI (MVP):** Event log monitoring, CBS.log scan, named pipe, error table GUI, tray icon, CSV logging, dual logging (mirror dir), settings, clipboard export. No hardware reads.
2. **Hardware Reads + Timings:** InpOutx64 detection, Zen 3 UMC register decode, SVI2 voltages, Timings tab, timing CSV logging, tRFC1 readback bug warning.
3. **Tuning Journal:** Config change detection, manual/auto designations, drift detection (20-boot rolling window), validation test logger, LKG tracking, Timeline tab, snapshot comparison, AI digest export, full history export/import.
4. **Git + Community:** Local git repo with auto-commit, CURRENT.md/LKG.md phone-readable checklists, GitHub push with isolated GH_CONFIG_DIR, three-tier privacy model, public contribution records.
5. **Polish:** SFC/DISM integration, toast notifications, log rotation, Zen 2/4 register maps, FCLK WHEA classifier.

## Key Design Constraints

- **WPF does NOT support Native AOT.** GUI uses self-contained single-file, `PublishTrimmed=false`. No `XamlReader.Load()` from strings (compiled BAML only). Service uses full Native AOT. Use `System.Text.Json` source generators with `[JsonSerializable]` everywhere.
- **Zero-cost idle:** Event-driven where possible (EventLogWatcher is kernel-callback, zero CPU between events). Low-frequency polling (≥30s) where not. No polling loops.
- **Graceful degradation:** If InpOutx64 not found → disable hardware reads, show "driver not available". If service not running → GUI shows banner, falls back to direct event log reads. If git not on PATH → disable git integration. Never crash on missing optional dependency.
- **Privacy by default:** Three tiers — local (everything), private repo (anonymized, no serial numbers/timestamps/event XML), public repo (opt-in, hardware+timings+validation only). Error details never leave the machine without explicit action.
- **Settings never crash:** If settings.json missing → create with defaults. If corrupt → log warning, use defaults.

## UMC Register Decode Notes

**CAUTION: ZenTimings and ZenStates-Core are GPL-3.0, not MIT as the architecture doc claims.** Either license RAMWatch as GPL-3.0, or clean-room the decode from AMD PPRs. Register offsets (hardware facts) are not copyrightable, but code structure and decode implementations are GPL-covered.

Register maps derived from AMD PPRs / ZenTimings reference. Offsets can shift between AGESA releases — include a validation step (read known-constant fields, verify before trusting decode). Known issues:
- tRFC1 readback bug on ComboAM4v2PI 1.2.0.x — detect and warn in UI
- VDIMM/VTT on MSI boards: BIOS WMI (AMD_ACPI), not hardware registers — static values, label accordingly
- PHYRDL mismatch between channels is normal (PHY training artifact), not an error

## Critical Implementation Notes

- **File concurrency:** All JSON writes use write-to-temp-then-rename (atomic on NTFS). Single-writer principle per file. Service owns all data file writes; GUI sends changes via pipe.
- **IPC protocol version:** Every message includes `protocolVersion` field. Service returns error for unknown message types. GUI checks version on connect.
- **Driver loading:** PawnIO is installed system-wide with its own signed driver. Service detects availability via IOCTL, not a user-supplied DLL path. No `inpOutDllPath` in settings (B5 fix).
- **Subprocess execution:** Always use `Process.Start` with `ArgumentList` (array form). Never string-interpolate into shell commands. Validate IPC enum fields against allowlist.
- **Git operations:** Must run on a dedicated background thread/Task. Never block the service's main event loop. Enqueue commit requests, drain asynchronously.

## Reference Documents

These are comprehensive and authoritative — read on demand, don't memorize:
- `RAMWatch-Architecture.md` — Full architecture, data models, IPC protocol, register maps, all service/data layer detail. **Note:** Contains several errors corrected by War Council review — see SPRINT-PLAN.md for the correction list.
- `RAMWatch-UI-Guide.md` — UI/UX design: layout grids, color palette, typography, component patterns, accessibility, tray behavior
- `SPRINT-PLAN.md` — Combined sprint plan, TODO tracker, War Council findings, dependency catalog, and architecture corrections
- `reference/zentimings/` — ZenTimings GUI source (GPL-3.0, cache only, gitignored)
- `reference/zenstates-core/` — ZenStates-Core library source (GPL-3.0, cache only, gitignored)
