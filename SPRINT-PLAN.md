# SPRINT-PLAN.md

RAMWatch combined sprint plan and TODO tracker. Updated after full War Council review (2026-04-14).

---

## War Council Summary

Nine characters reviewed the architecture docs before implementation. Key stats:

| Reviewer | Role | Findings | Critical |
|----------|------|----------|----------|
| Sauron | Data model & IPC protocol | 3C 10W 8N | Missing IPC messages, field mismatches, privacy leak |
| Gandalf | Architecture | 2C 7W 5N | File concurrency, protocol versioning, .NET 8 EOL |
| Frodo | UX & workflow | 1C 5W 7N | BIOS checklist layout, phantom features, no quick-log |
| Aragorn | Security | 4C 5W 3N | Pipe ACL, DLL injection via settings, path traversal |
| Legolas | Performance | 2C 4W 4N | WPF cold start, git threading, mirror logger |
| Gimli | Build & dependencies | 3C 6W 3N | WPF AOT impossible, wrong tray package, DLL loading |
| Ents | Test plan | 5C 12W 5N | 9 untested components, framing vuln, ~258 tests needed |
| Uruk-Hai | Edge cases & crashes | 4H 5M 2L | Boot ID collision, reconnect, midnight rotation |
| Gollum | Document consistency | 5 inconsistencies | Tab count mismatch, tRFC4 value, tray menu |

**ZenTimings research** found: GPL-3.0 license (not MIT as stated), driver layer shifted to PawnIO, architecture references nonexistent file paths.

---

## Blockers — Resolve Before Writing Code

### B1. ZenTimings License is GPL-3.0, Not MIT
The architecture doc claims MIT. Both ZenTimings and ZenStates-Core are GPL-3.0. Options:
- [ ] **Option A:** License RAMWatch as GPL-3.0
- [ ] **Option B:** Clean-room register decode from AMD PPRs (register offsets are hardware facts, not copyrightable)
- [ ] **Decision needed** — this affects the LICENSE file and every file header

### B2. WPF Native AOT Does Not Work
`PublishAot=true` on a WPF project produces build errors. The SDK blocks it. Architecture Section 10's "fallback" is the only valid path.
- [x] **Resolution:** Service → Native AOT. GUI → Self-contained single-file (`PublishSingleFile=true`, `PublishTrimmed=false`). Update size estimates: GUI will be ~80-120MB, not ~25MB.

### B3. Target .NET 10 LTS, Not .NET 8
.NET 8 LTS EOL is November 2026 — 7 months from now. .NET 10 LTS (shipped Nov 2025) supports through Nov 2028.
- [ ] Verify WPF self-contained + Service AOT both work on `net10.0-windows`
- [ ] Update all .csproj TFMs and dependency versions

### B4. Named Pipe Security (Aragorn Critical)
Default ACLs on `\\.\pipe\RAMWatch` allow ANY local process to connect and send commands (including `runIntegrity` which runs SFC/DISM as LocalSystem). This is a local privilege escalation vector.
- [ ] Set explicit `PipeSecurity` DACL restricting access to interactive user SID + SYSTEM
- [ ] Phase 1 blocking — must be correct from the first line of pipe code

### B5. settings.json Privilege Escalation (Aragorn Critical ×2)
The GUI (unprivileged) writes settings.json. The service (LocalSystem) reads it and acts on it. Three attack paths:
- `inpOutDllPath` → load arbitrary DLL as LocalSystem
- `mirrorDirectory` / `logDirectory` → write files to arbitrary paths as LocalSystem
- [ ] **Resolution:** Service determines all paths internally. Remove `inpOutDllPath` from user-writable settings. Validate/canonicalize paths before use. OR: service owns all file writes; GUI sends changes via pipe; settings.json writable only by service (Administrators ACL).

### B6. IPC Protocol Versioning (Gandalf + Sauron Critical)
No version field means Phase 3 GUI + Phase 1 service = cryptic deserialization failures.
- [ ] Add `"protocolVersion": 1` to initial state push and every client message
- [ ] Service ignores unknown message types, returns `{"type":"response","status":"error","code":"unsupported_message"}`
- [ ] Phase 1 blocking — costs 3 lines, prevents a class of failures forever

### B7. File Concurrency Strategy (Gandalf Critical)
Zero discussion of file locking for settings.json, snapshots.json, or CSV files.
- [ ] Define single-writer principle for each file
- [ ] All JSON writes use write-to-temp-then-rename (atomic on NTFS)
- [ ] CSV appends use `FileShare.Read` so mirror logger can copy
- [ ] Phase 1 blocking

---

## Architecture Corrections Required

### IPC Protocol Gaps (Sauron)
- [ ] Add `integrityProgress` push message type (SFC/DISM progress via pipe)
- [ ] Add `getSnapshots` request/response (Snapshots tab dropdown has nothing to populate)
- [ ] Add `getFullExport` request/response (Export All button)
- [ ] Add `annotateChange` message (ConfigChange.UserNotes editing)
- [ ] Add `ready: false` flag in state message during startup scan (prevents misleading "CLEAN" display)
- [ ] Define error response shape: `{"type":"response","status":"error","code":"...","message":"..."}`
- [ ] Add version handshake on connect

### Missing Data Models (Sauron + Frodo)
- [ ] Define `HardwareInfo`/`SystemProfile` model (CPU, board, BIOS, AGESA, RAM — needed by digest and public contribution but has no home)
- [ ] Define `PublicContribution` as explicit C# record (currently described only as a table)
- [ ] Add `TimingSnapshot` field to `ConfigChange` record (Section 6.7 says "full snapshot" but the record has only `Changes` dict)
- [ ] Define per-timing direction map for comparison view (tREFI is better when HIGHER, not lower)
- [ ] Add `schemaVersion` field to every persisted JSON file

### Project Structure Fix (Gandalf)
- [ ] Move `Hardware/` from `RAMWatch.Core` to `RAMWatch.Service` (GUI doesn't use hardware; transitive P/Invoke dependency creates problems)
- [ ] Core contains only: Models + Ipc (things both sides use)

### Dependency Corrections (Gimli)
- [ ] Replace `Hardcodet.NotifyIcon.Wpf` (CPOL, unmaintained, no AOT) → `H.NotifyIcon.Wpf` v2.4.1 (MIT, AOT-annotated, maintained)
- [ ] Add `Microsoft.Extensions.Hosting.WindowsServices` (required for `.UseWindowsService()` — omitted from architecture)
- [ ] Add `System.Management` NuGet package (WMI queries for VDIMM/VTT/AGESA — not inbox on .NET 8+)
- [ ] Evaluate PawnIO as InpOutx64 replacement (ZenTimings has migrated; PawnIO is signed and properly secured)

### Document Inconsistencies (Gollum)
- [ ] Architecture Section 7.1 shows 4 tabs; UI Guide Section 5.1 shows 5 (missing "Timeline"). Add Timeline to architecture wireframes.
- [ ] tRFC4 is 260 in Section 7.2 but 270 in Section 14.3. Pick one canonical example value.
- [ ] Tray menu: Architecture has "Refresh", UI Guide has "Copy Digest". Reconcile.
- [ ] Driver status: IPC schema uses `"not_found"`, narrative uses "driver not available". Standardize.

---

## Corrected Dependency Catalog

| Package | NuGet ID | Version | AOT Status | Project |
|---------|----------|---------|------------|---------|
| Generic Host | `Microsoft.Extensions.Hosting` | 10.0.x | Compatible | Service |
| Windows Service Support | `Microsoft.Extensions.Hosting.WindowsServices` | 10.0.x | Compatible | Service |
| MVVM Source Gen | `CommunityToolkit.Mvvm` | 8.4.x | Compatible | GUI |
| System Tray | `H.NotifyIcon.Wpf` | 2.4.x | Compatible | GUI |
| WMI Access | `System.Management` | 10.0.x | Partial | Service |
| Test Framework | `xunit` | 3.x | Compatible | Tests |
| Test SDK | `Microsoft.NET.Test.Sdk` | 17.x | Compatible | Tests |
| xUnit VS Runner | `xunit.runner.visualstudio` | 3.x | Compatible | Tests |

**Inbox (no NuGet needed):** System.Text.Json, System.IO.Pipes, System.Diagnostics.Eventing.Reader, Microsoft.Win32.Registry

**Not needed (remove from plan):** Hardcodet.NotifyIcon.Wpf, any mocking framework (use manual fakes for AOT safety)

---

## Phase 1 Sprint — Service + IPC + Minimal GUI

### Pre-implementation Checklist
- [x] War Council review complete
- [x] Workspace scaffolded (git init, directory structure)
- [x] ZenTimings reference cached (reference/zentimings/, reference/zenstates-core/)
- [ ] **B1** License decision (GPL-3.0 vs clean-room)
- [ ] **B3** .NET 10 verification
- [ ] **B4** Pipe security design
- [ ] **B5** Settings privilege model
- [ ] **B6** Protocol version field
- [ ] **B7** File concurrency strategy

### Sprint Tasks (ordered by dependency)

#### 1. Solution & Project Files
- [ ] Create `RAMWatch.sln`
- [ ] `src/RAMWatch.Core/RAMWatch.Core.csproj` — `net10.0-windows`, class library
- [ ] `src/RAMWatch.Service/RAMWatch.Service.csproj` — `net10.0-windows`, `PublishAot=true`, worker service
- [ ] `src/RAMWatch/RAMWatch.csproj` — `net10.0-windows`, WPF, `PublishSingleFile=true`, `PublishTrimmed=false`
- [ ] `src/RAMWatch.Tests/RAMWatch.Tests.csproj` — `net10.0-windows`, xUnit v3
- [ ] Verify `dotnet build` succeeds with empty projects

#### 2. Core — Models
- [ ] `IpcMessages.cs` — all message types with `protocolVersion` field
- [ ] `MonitoredEvent.cs`
- [ ] `AppSettings.cs` with schema version
- [ ] `RamWatchJsonContext.cs` — single `[JsonSerializable]` context for all types

#### 3. Core — IPC
- [ ] `MessageSerializer.cs` — JSON-over-newline framing (escape embedded newlines!)
- [ ] `PipeServer.cs` — with explicit `PipeSecurity` DACL (B4)
- [ ] `PipeClient.cs` — with reconnection logic (exponential backoff)
- [ ] Unit tests: `IpcRoundtripTests.cs` including newline-in-notes test

#### 4. Service — Foundation
- [ ] `Program.cs` — `UseWindowsService()`, host setup
- [ ] `RamWatchService.cs` — BackgroundService entry point
- [ ] Settings load/save with atomic write (write-temp-rename pattern) (B7)
- [ ] Unit tests: `SettingsTests.cs` (missing file, corrupt file, round-trip)

#### 5. Service — Event Log Monitor
- [ ] `EventLogMonitor.cs` — push-based EventLogWatcher subscriptions
- [ ] Historical scan on startup (events since last boot) with dedup
- [ ] Unit tests: `EventClassificationTests.cs` including dedup test

#### 6. Service — State Aggregator
- [ ] `StateAggregator.cs` — combines sources, builds state message
- [ ] Handles `ready: false` during startup scan
- [ ] Unit tests: `StateAggregatorTests.cs` (null hardware, event accumulation)

#### 7. Service — CSV Logger
- [ ] `CsvLogger.cs` — append-only, daily rotation, `FileShare.Read`
- [ ] Handle midnight rotation atomically
- [ ] Log retention on startup
- [ ] Unit tests: `CsvLoggerTests.cs` (rotation, partial line tolerance, retention)

#### 8. Service — Mirror Logger
- [ ] `MirrorLogger.cs` — fire-and-forget background Task with 5s timeout
- [ ] Never block primary write path
- [ ] Unit tests: `MirrorLoggerTests.cs`

#### 9. Service — Integrity Checker (CBS.log only for Phase 1)
- [ ] `IntegrityChecker.cs` — CBS.log tail parsing (extractable pure function)
- [ ] SFC/DISM stubs that respond "not available" (full implementation Phase 5)
- [ ] Unit tests: `IntegrityCheckerTests.cs`

#### 10. GUI — Main Window
- [ ] `App.xaml` + `Dark.xaml` theme (color tokens as ResourceDictionary)
- [ ] `MainWindow.xaml` — status header + tab strip + action bar
- [ ] `MainViewModel.cs` — receives state from pipe, owns UI state
- [ ] Pipe connection with reconnection and "Connecting..." state
- [ ] Show stale data immediately, update on fresh state arrival

#### 11. GUI — Monitor Tab
- [ ] `MonitorTab.xaml` — error table (DataGrid), integrity panel
- [ ] "Service not installed" banner with Install button (invokes Install-RAMWatch.bat via runas)

#### 12. GUI — System Tray
- [ ] `H.NotifyIcon.Wpf` integration
- [ ] Green/red/gray icon states
- [ ] Context menu: Show, status line, Save Snapshot, Copy Digest, Quit
- [ ] Close button → minimize to tray (first-time tooltip)

#### 13. GUI — Settings Tab
- [ ] `SettingsTab.xaml` — settings controls
- [ ] Settings sent via pipe (`updateSettings`), service writes the file
- [ ] Input validation (minimum refresh interval, path validation)

#### 14. Export — Clipboard
- [ ] One-click clipboard export (formatted text block per Section 13.1)

#### 15. Scripts
- [ ] `Install-RAMWatch.bat` — admin check, data dir, service install, ACLs
- [ ] `Uninstall-RAMWatch.bat` — service stop/delete, autostart removal

#### 16. Integration Smoke Test
- [ ] `IpcSmokeTests.cs` — in-process real pipe, full state round-trip

### Phase 1 Definition of Done
- Service starts at boot, survives logoff/RDP disconnect
- GUI connects to service, displays error table with live event counts
- System tray with colored status icon
- CSV event logging with daily rotation and mirror support
- Settings persist and apply via IPC
- Clipboard export works
- Pipe is secured (interactive user only)
- All unit tests pass
- Replaces the PowerShell prototype

---

## Security Model (from Aragorn)

**Principle: service owns all file writes. GUI is a view-only pipe client.**

| Resource | Writer | Reader | ACL |
|----------|--------|--------|-----|
| `settings.json` | Service (via pipe command) | Service | Administrators:RW, Users:R |
| `snapshots.json` | Service | Service | Administrators:RW, Users:R |
| `events_*.csv` | Service | Mirror logger, external tools | Administrators:RW, Users:R |
| `\\.\pipe\RAMWatch` | Service (server) | GUI (client) | SYSTEM + Interactive User |
| InpOutx64 DLL path | Service (hardcoded scan) | Service | Not configurable via settings |

**DLL loading:** Service scans only admin-owned paths (own directory, System32). Never loads from PATH or user-writable locations. Verify DLL signature if feasible.

**Command injection prevention:** All subprocess invocations use `Process.Start` with `ArgumentList` (array form), never string interpolation through cmd.exe. Enum-valued IPC fields validated against allowlist.

**Privacy filter:** Deny-by-default. `PublicContribution` is constructed by explicit field mapping, not by copying everything and removing sensitive fields.

---

## Test Plan Summary (from Ents)

~258 tests across 21 test files. 7 existed in the architecture plan; 14 are new (identified by Ents as coverage gaps).

**Phase 1 priority tests:** IpcRoundtripTests, SettingsTests, EventClassificationTests, CsvLoggerTests, StateAggregatorTests, IpcSmokeTests

**Phase 2 priority tests:** UmcDecodeTests, Svi2DecodeTests, HardwareReaderTests, CpuDetectTests

**Phase 3 priority tests:** ConfigChangeDetectorTests, DriftDetectorTests, LkgTrackerTests, ValidationTestLoggerTests, SnapshotStoreTests, PrivacyFilterTests

**Framework:** xUnit v3 + manual fakes (no Moq/NSubstitute — runtime code generation is AOT-incompatible). Hardware tests gated with `[Trait("Category", "Hardware")]`, skipped in CI.

---

## ZenTimings Reference

Cloned to `reference/` (gitignored, local cache only):
- `reference/zentimings/` — ZenTimings GUI (irusanov/ZenTimings, GPL-3.0)
- `reference/zenstates-core/` — ZenStates-Core library (irusanov/ZenStates-Core, GPL-3.0)

**Developer verified:** Ivan Rusanov (irusanov on GitHub, same handle on overclock.net TechPowerUp). Not 1usmus (different person).

**Key reference files for Phase 2:**
- `zenstates-core/Dictionaries/DDR4Dictionary.cs` — UMC register offset→timing map
- `zenstates-core/DRAM/BaseDramTimings.cs` — Generic decode loop, all timing properties
- `zenstates-core/DRAM/Ddr4Timings.cs` — DDR4-specific: tRFC decode, FGR detection
- `zenstates-core/Constants.cs` — SVI2 register addresses per Zen generation
- `zenstates-core/Cpu.cs` — CPUID parsing, codename mapping
- `zenstates-core/SMU.cs` + `Mailbox/*.cs` — SMU mailbox protocol

**Architecture doc errors:**
- References `ZenTimings/Zen2/UMC.cs` and `ZenTimings/Zen3/UMC.cs` — these don't exist. Actual decode is in ZenStates-Core with dictionary-based architecture.
- ZenTimings has migrated from InpOutx64 to PawnIO (signed driver). Evaluate PawnIO for RAMWatch.

---

## Parking Lot (deferred decisions)

| # | Question | Blocking | Notes |
|---|----------|----------|-------|
| 1 | InpOutx64 vs PawnIO | Phase 2 | PawnIO is signed, modern, what ZenTimings uses now. Also GPL. |
| 2 | SMU mailbox scope | Phase 2 | Complex. Use WMI for voltages initially; defer SMU to Phase 2 optional. |
| 3 | Zen 4 / DDR5 | Phase 5 | Different register map. ZenStates-Core has DDR5Dictionary.cs for reference. |
| 4 | Auto-update mechanism | Phase 5 | Opt-in, GitHub releases API. |
| 5 | Code signing | Pre-release | ~$200-400/year. SmartScreen without it. |
| 6 | Community data aggregation | Phase 4 | Central index vs shared repo vs API. |
| 7 | AIDA64 CSV import format | Phase 3 | Undocumented, version-dependent. Fault-tolerant parser. |
| 8 | "Next planned" field | Phase 3 | Frodo found: digest shows it but no UI input exists. Need IPC message. |
| 9 | Designation override UX | Phase 3 | Frodo found: write-once-per-boot lock has no escape hatch in Settings UI. |
| 10 | Timeline tab wireframe | Phase 3 | Missing from architecture Section 7. Needs design before implementation. |
| 11 | Boot ID format | Phase 1 | Uruk-Hai: `boot_MMDD_HHMM` collides at minute granularity. Use UUID or sequential. |
| 12 | Comparison direction map | Phase 3 | tREFI is better HIGHER. ProcODT, voltages are context-dependent. Need per-field map. |
