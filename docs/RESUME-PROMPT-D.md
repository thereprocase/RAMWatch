# Resume Prompt for RAMWatch (Session E)

Copy everything below this line into a new Claude Code session to resume work.

---

## Context

You're continuing work on RAMWatch, a Windows DRAM tuning monitor at F:\Claude\projects\RAMwatch. Read these files first:

1. `CLAUDE.md` — project rules, architecture, build commands, constraints
2. `docs/RESUME-PROMPT-D.md` — this file (what was built in Session D, bugs, next steps)

## Current State

- **76 commits on main** (17 this session)
- **464 tests**, all passing: `"$HOME/.dotnet/dotnet.exe" test src/RAMWatch.Tests`
- **Build**: `"$HOME/.dotnet/dotnet.exe" build RAMWatch.sln` — zero errors
- **Phases 1-4 complete**, verified on live 5800X3D
- **Install workflow**: `scripts/Publish-Release.ps1` → `scripts/Install-RAMWatch.ps1`
- **Dev iteration**: `scripts/Update-RAMWatch.ps1` (one command, admin PowerShell)

## Session D Changes (17 commits)

### Bugs Fixed
1. **Spurious config changes** — Skip incomplete hardware reads (FCLK=0), ±5 MHz clock jitter tolerance
2. **Auto-snapshots missing clocks** — Deferred until FCLK/UCLK > 0
3. **Snapshot dropdown order** — Strict reverse-chronological by timestamp
4. **Default left selection** — Walk direction fixed for newest-first order
5. **IsManualLabel** — Now excludes "Auto " prefixed labels from dedup bypass
6. **Clock ratio decode** — Fixed 8→7 bits to match ZenTimings reference
7. **Partial-failure snapshots** — Rejected when CL=0 or RAS=0 (register read failed)
8. **FCLK/UCLK jitter** — SnapClockMhz rounds to nearest BCLK/3 multiple (±3 MHz)
9. **GDI handle leak** — Tray icon fallback path now clones + DestroyIcon
10. **app.ico WPF resource** — Added as both ApplicationIcon and Resource
11. **Update script** — Always relaunches GUI, not just when it was running

### Features Added
1. **"Normal" column on Monitor tab** — `~21 ±4`, `rare (1/7)`, `always 0`, `—`
2. **Timing summary on snapshot dropdowns** — `— DDR4-3600 CL16-20-20-42`
3. **Timing context on timeline entries** — Subtle line below each entry
4. **`--minimized` CLI arg** — App starts in tray when launched with --minimized
5. **StartMinimized setting wired** — Also applies on launch
6. **Lucide circuit-board icon** — App icon + tray states (green/red/gray)

### Security Hardening (LOTC Review)
- Path validation at startup for LogDirectory/MirrorDirectory from settings.json
- Designations IPC capped at 500 keys, 64-char key limit
- BuildLkg guarded against empty SnapshotId

## Session E: Snapshot-Per-Boot Refactor

### The Problem

Current model creates a new snapshot for every validation result, producing 4-6 identical-timing entries per boot. The Manage tab and Compare dropdown are noisy.

### The New Model

**One canonical snapshot per boot. Validations are children of it.**

```
Boot (2026-04-14T15:56Z)
├── Snapshot: CL16-20-20-42 @ DDR4-3600  (immutable per boot)
├── Validation: Karhu 600% PASS [30m]
├── Validation: Karhu 260% PASS [20m]
└── Validation: Karhu 39% PASS [6m]
```

### Boot ID: Derived from Wall Clock + Uptime

Current: sequential counter (`boot_0001`). New: ISO minute from boot time.

```csharp
var bootTime = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
var bootId = bootTime.ToString("yyyy-MM-ddTHH:mmZ");
```

If a second boot lands in the same wall minute (uptime dropped), disambiguate with `.2` suffix. Display truncates to the minute. Journal keys on the full string.

### Implementation Plan

#### 1. Boot ID Generation (Service)
- Replace `CsvLogger.GenerateBootId()` with boot-time derivation
- Add same-minute collision detection (compare uptime: if shorter than previous boot with same minute, append `.2`)
- Update all boot ID consumers

#### 2. SnapshotJournal: One Per Boot
- `Save()` checks if a snapshot with the same boot ID already exists → update instead of append
- Remove the per-validation snapshot creation in `HandleLogValidationAsync`
- The auto-snapshot on first complete hardware read IS the boot snapshot
- `GetAll()` returns one entry per boot

#### 3. ValidationTestLogger: Link to Boot Snapshot
- `LogValidation()` stores `ActiveSnapshotId` pointing to the boot's snapshot
- Remove the `WithIdAndLabel` + `_snapshotJournal.Save()` block in the validation handler
- Validations reference the boot snapshot, don't create their own

#### 4. Manage Tab: Collapsible Cards
- `SnapshotManageRow` becomes a card with expandable validation list
- Collapsed: boot date + timing summary + label + validation count
- Expanded: list of validation results with individual delete
- Rename edits the snapshot label
- Delete on card removes snapshot + all linked validations

#### 5. Compare Dropdown: Clean
- One entry per boot-snapshot (instead of 4-6)
- Display: `2026-04-14 15:56 — DDR4-3600 CL16-20-20-42`
- Validation info shown as tooltip or subtitle, not as separate entries

#### 6. Timeline: Show Boot Context
- Validation entries show which boot-snapshot they belong to
- Config changes reference boot snapshots on both sides

#### 7. Migration
- Existing snapshots with sequential boot IDs (`boot_0001`) are legacy
- Read them, display them, but new boots use the ISO format
- Legacy snapshots that share a boot ID are already one-per-boot (the auto-save)
- Legacy validation-linked snapshots become orphans — they'll show as regular snapshots until manually deleted

### Files to Modify

| File | Changes |
|------|---------|
| `RamWatchService.cs` | Boot ID derivation, remove validation snapshot creation |
| `CsvLogger.cs` | Remove `GenerateBootId()` or update to new format |
| `SnapshotJournal.cs` | Upsert by boot ID instead of always-append |
| `ValidationTestLogger.cs` | Link to boot snapshot instead of creating new one |
| `SnapshotsViewModel.cs` | Simplify — one entry per boot, expand for validations |
| `TimelineViewModel.cs` | Minor — boot context on validation entries |
| `Views/SnapshotsTab.xaml` | Manage tab becomes collapsible cards |
| `SnapshotDisplayName.cs` | Simplify — no more validation-in-label logic |
| Tests | Update snapshot journal tests, add boot ID tests |

### Also Do (Tier 1 Ship Blockers)

1. **Version numbers** — Add `<Version>0.1.0</Version>` to all .csproj files
2. **README rewrite** — Screenshots, install steps, PawnIO requirement, system requirements
3. **Release zip script** — Package dist/ into downloadable archive
4. **GitHub repo** — `gh repo create`, push, description, topics
5. **v0.1.0 release** — GitHub Release with zip attached

## Test Environment

AMD Ryzen 7 5800X3D, MSI B550 TOMAHAWK MAX WIFI, DDR4-3600 CL16-20-20-42, PawnIO installed and running, Windows 11 Pro, .NET 10.0.201 SDK at `$HOME/.dotnet/dotnet.exe`.

## Key Files

| File | What |
|------|------|
| `src/RAMWatch.Service/RamWatchService.cs` | Main service — boot ID, auto-snapshot, all IPC handlers |
| `src/RAMWatch.Service/Services/SnapshotJournal.cs` | Snapshot storage — needs upsert-by-boot-ID |
| `src/RAMWatch.Service/Services/ValidationTestLogger.cs` | Validation storage |
| `src/RAMWatch.Service/Hardware/SmuPowerTableReader.cs` | FCLK/UCLK reads + SnapClockMhz |
| `src/RAMWatch/ViewModels/SnapshotsViewModel.cs` | Snapshot compare + manage |
| `src/RAMWatch/ViewModels/MainViewModel.cs` | GUI state, error sources with baseline |
| `src/RAMWatch/Views/MonitorTab.xaml` | Baseline-colored error counts + Normal column |
| `src/RAMWatch/TrayIconManager.cs` | Tray icon with Lucide circuit-board |
| `src/RAMWatch/MainWindow.xaml.cs` | Window sizing, start-minimized, tray behavior |

## Rules

- `timeout: 30000` on every Bash call. Use `"$USERPROFILE/.dotnet/dotnet.exe"` not `dotnet`
- Test after every change: `"$USERPROFILE/.dotnet/dotnet.exe" test src/RAMWatch.Tests`
- Commit every meaningful step. Never add features on top of uncommitted work.
- Delete `%LocalAppData%\RAMWatch\window.json` to reset window position after sizing changes
