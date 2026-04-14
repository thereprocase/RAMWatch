# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

RAMWatch is a Windows-only DRAM tuning monitor: a system health tracker + tuning journal + shareable history for enthusiasts who tune RAM timings. It reads hardware registers, watches event logs, tracks timing drift across boots, and maintains a git-backed tuning diary. Read-only — it never modifies hardware.

**Status:** Architecture complete (see reference docs below). Ready for Phase 1 implementation.

## Architecture — Two-Process Model

Two Native AOT executables communicating over a named pipe (`\\.\pipe\RAMWatch`):

- **RAMWatch.Service.exe** — Windows service, LocalSystem, starts at boot. Owns all monitoring (EventLogWatcher push-based subscriptions, InpOutx64 P/Invoke for UMC register reads, CBS.log tail, SFC/DISM runner), state aggregation, CSV logging, git commits, and the pipe server. Target: <0.1% CPU, <25MB RAM steady state.
- **RAMWatch.exe** — WPF GUI client, unprivileged, on-demand. Connects to pipe, receives JSON state pushes, sends commands. MVVM with CommunityToolkit.Mvvm source generators. Tabs: Monitor, Timings, Timeline, Snapshots, Settings. Custom dark theme (navy background, semantic status colors, Cascadia Mono for numbers).

IPC is JSON-over-newline. Message types: `state` (full push on connect + periodic), `event` (real-time), `getState`, `snapshot`, `runIntegrity`, `updateSettings`, `logValidation`, `updateDesignations`, `getDigest`, `getLkgDiff`.

## Technology Stack

- **.NET 8+**, Native AOT, `win-x64`, single-file self-contained
- **WPF** for GUI (not WinUI 3, not MAUI — weight class decision)
- **InpOutx64** for PCI config space / physical memory reads (user-provided, not bundled)
- **System.Text.Json** with source generators (no reflection-based serialization)
- `[LibraryImport]` for P/Invoke (source-generated, not `[DllImport]`)
- CommunityToolkit.Mvvm `[ObservableProperty]` for MVVM bindings
- No DI container, no logging framework, no ORM, no libgit2 — flat files and shell-out to git/gh

## Build and Run

```bash
# Build everything
dotnet build RAMWatch.sln

# Publish service (Native AOT)
dotnet publish src/RAMWatch.Service -c Release -r win-x64

# Publish GUI (Native AOT + WPF)
dotnet publish src/RAMWatch -c Release -r win-x64

# Run tests
dotnet test src/RAMWatch.Tests

# Install service (admin required, one-time)
scripts/Install-RAMWatch.bat
```

## Project Structure

```
RAMWatch.sln
src/
  RAMWatch.Core/          # Shared: models, hardware decode, IPC protocol
    Models/               # MonitoredEvent, TimingSnapshot, ConfigChange, DriftEvent, etc.
    Hardware/             # InpOut P/Invoke, CpuDetect, Zen2/3 UMC decode, SVI2, SMU
    Ipc/                  # PipeServer, PipeClient, MessageSerializer
  RAMWatch.Service/       # Windows service (BackgroundService)
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

Data lives in `%ProgramData%\RAMWatch\` — settings.json, snapshots.json, designations.json, tests.json, changes.json, and `logs/` (daily CSVs).

## Implementation Phases

1. **Service + IPC + Minimal GUI (MVP):** Event log monitoring, CBS.log scan, named pipe, error table GUI, tray icon, CSV logging, dual logging (mirror dir), settings, clipboard export. No hardware reads.
2. **Hardware Reads + Timings:** InpOutx64 detection, Zen 3 UMC register decode, SVI2 voltages, Timings tab, timing CSV logging, tRFC1 readback bug warning.
3. **Tuning Journal:** Config change detection, manual/auto designations, drift detection (20-boot rolling window), validation test logger, LKG tracking, Timeline tab, snapshot comparison, AI digest export, full history export/import.
4. **Git + Community:** Local git repo with auto-commit, CURRENT.md/LKG.md phone-readable checklists, GitHub push with isolated GH_CONFIG_DIR, three-tier privacy model, public contribution records.
5. **Polish:** SFC/DISM integration, toast notifications, log rotation, Zen 2/4 register maps, FCLK WHEA classifier.

## Key Design Constraints

- **Native AOT + WPF:** No `XamlReader.Load()` from strings. All XAML compiled (BAML). Use `[LibraryImport]` not `[DllImport]`. Use `System.Text.Json` source generators with `[JsonSerializable]`. If WPF AOT proves too fragile, fall back to standard self-contained for GUI only.
- **Zero-cost idle:** Event-driven where possible (EventLogWatcher is kernel-callback, zero CPU between events). Low-frequency polling (≥30s) where not. No polling loops.
- **Graceful degradation:** If InpOutx64 not found → disable hardware reads, show "driver not available". If service not running → GUI shows banner, falls back to direct event log reads. If git not on PATH → disable git integration. Never crash on missing optional dependency.
- **Privacy by default:** Three tiers — local (everything), private repo (anonymized, no serial numbers/timestamps/event XML), public repo (opt-in, hardware+timings+validation only). Error details never leave the machine without explicit action.
- **Settings never crash:** If settings.json missing → create with defaults. If corrupt → log warning, use defaults.

## UMC Register Decode Notes

Register maps ported from ZenTimings (MIT). Offsets can shift between AGESA releases — include a validation step (read known-constant fields, verify before trusting decode). Known issues:
- tRFC1 readback bug on ComboAM4v2PI 1.2.0.x — detect and warn in UI
- VDIMM/VTT on MSI boards: BIOS WMI (AMD_ACPI), not hardware registers — static values, label accordingly
- PHYRDL mismatch between channels is normal (PHY training artifact), not an error

## Reference Documents

These are comprehensive and authoritative — read on demand, don't memorize:
- `RAMWatch-Architecture.md` — Full architecture, data models, IPC protocol, register maps, all service/data layer detail
- `RAMWatch-UI-Guide.md` — UI/UX design: layout grids, color palette, typography, component patterns, accessibility, tray behavior
