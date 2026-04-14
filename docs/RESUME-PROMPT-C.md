# Resume Prompt for RAMWatch (Session C)

Copy everything below this line into a new Claude Code session to resume work.

---

## Context

You're continuing work on RAMWatch, a Windows DRAM tuning monitor at F:\Claude\projects\RAMwatch. Read these files first:

1. `CLAUDE.md` — project rules, architecture, build commands, constraints
2. `docs/SESSION-REPORT-2026-04-14-B.md` — what was built in Session B

## Current State

- **59 commits on main**
- **437 tests**, all passing: `"$HOME/.dotnet/dotnet.exe" test src/RAMWatch.Tests`
- **Build**: `"$HOME/.dotnet/dotnet.exe" build RAMWatch.sln` — zero errors
- **Phases 1-4 complete**, verified on live 5800X3D
- **Install workflow**: `scripts/Publish-Release.ps1` → `scripts/Install-RAMWatch.ps1`
- **Dev iteration**: `scripts/Update-RAMWatch.ps1` (one command, admin PowerShell)

## Session C Changes (4 commits)

1. **Timeline delete fix** — Config change deletes now persist via `DeleteChangeMessage` IPC. Previously local-only (reappeared on state push).
2. **Snapshot default selection** — Right=Current, Left=last-different snapshot. **BROKEN — see bugs below.**
3. **Boot baseline journal** — `BootBaselineJournal` tracks per-source event counts across 50 boots with IQR-filtered means. Monitor tab colors: grey/bold=normal, amber=elevated, red=very elevated/hardware. Needs 3+ boots to calibrate.
4. **Window width** — Reduced from 40% to 28% of work area (max 560px, was 700).

## Bugs To Fix (priority order)

### 1. Snapshot Compare default selection picks wrong left snapshot
**Problem:** `FindLastDifferentSnapshot()` in `SnapshotsViewModel.cs` is supposed to pick the most recent saved snapshot whose timings differ from Current. Instead it picks the most recent snapshot period (an auto-snap with identical timings), which produces a useless comparison.

**Investigate:**
- `FindLastDifferentSnapshot()` walks `AvailableSnapshots` backwards — but the filter (`ApplyFilter()`) may have already excluded the different snapshots (they're old enough to be filtered out)
- The `TimingsEqual()` comparison might not cover all fields, or auto-snapshots might have trivially different metadata that makes them pass
- The fallback path (lines 407-413) always returns the most recent saved, masking the failure
- Test by adding logging or checking what `AvailableSnapshots` contains after `ApplyFilter()`

**Files:** `src/RAMWatch/ViewModels/SnapshotsViewModel.cs` lines 396-427

### 2. Spurious config changes on boot from incomplete hardware reads
**Problem:** Three bogus CHANGE entries appear on every boot (see screenshot):
- 15:31:30 — Detects "change" from previous boot snapshot, but FCLK/UCLK read as 0 (SMU not ready yet). Shows `FclkMhz: 1800 -> 0, UclkMhz: 1800 -> 0` plus all the real timing differences from a profile switch.
- 15:31:31 — FCLK/UCLK now available: `FclkMhz: 0 -> 1900, UclkMhz: 0 -> 1900` — second spurious change.
- 15:32:32 — FCLK/UCLK jitter: `1900 -> 1902, 1900 -> 1902` — 2 MHz noise from SMU readback.

**Root cause:** `ConfigChangeDetector.DetectChanges()` fires on every hardware read cycle. Early reads have incomplete data (clocks at 0). Subsequent reads have minor jitter. The detector treats each as a real config change.

**Fix options (pick one or combine):**
- **Stabilization window:** Don't compare until N consecutive reads produce the same values (e.g., 3 stable reads in a row). This handles both the 0-value startup and jitter.
- **Jitter tolerance:** For FCLK/UCLK/MemClockMhz, ignore differences <= 5 MHz.
- **Skip zero values:** Don't treat a snapshot with FCLK=0 or UCLK=0 as valid for change detection.
- **First-read flag:** Skip the first comparison after boot (the previous-boot-to-first-read transition is always noisy).

**Files:** `src/RAMWatch.Service/Services/ConfigChangeDetector.cs`, `src/RAMWatch.Service/RamWatchService.cs` (the `OnTimingSnapshotAsync` method that calls DetectChanges)

### 3. Snapshot dropdown entries need dates + primaries + freq
**Problem:** The dropdown entries are bare labels like "26-04-14 Mid Evo Stab" — no timing info to distinguish entries. User needs to see e.g. "26-04-14 Mid Evo Stab — DDR4-3600 CL16-20-20-42" or similar.

**Files:** `SnapshotDisplayName.Build()` is where display names are constructed. Search for `SnapshotDisplayName` in the codebase.

### User-Requested Features (from Session B, still open)

3. **Monitor tab empty space** — bottom half is empty. Fill with OC quick-glance: timing summary, last test result, uptime-since-change, WHEA count history, action buttons.

4. **Timeline enrichment** — each entry should show RAM freq + primary timings. Detail toggle for secondaries, voltages, duration, notes.

5. **Designation dots in Timings tab** — `TimingDisplayRow.DesignationIndicator` exists. Bind in `TimingsTab.xaml` templates, `InteractiveBlue` color.

### Technical Debt

6. **MessageSerializer AOT trim warnings** — refactor to type-specific overloads
7. **Git line ending renormalization** — `git add --renormalize .`
8. **DigestBuilder double dict lookup**

## Test Environment

AMD Ryzen 7 5800X3D, MSI B550 TOMAHAWK MAX WIFI, DDR4-3600 CL16-20-20-42, PawnIO installed and running, Windows 11 Pro, .NET 10.0.201 SDK at `$HOME/.dotnet/dotnet.exe`.

## Key Files

| File | What |
|------|------|
| `src/RAMWatch/ViewModels/SnapshotsViewModel.cs` | Snapshot compare + default selection (BUG #1) |
| `src/RAMWatch/ViewModels/MainViewModel.cs` | GUI state, error source VM with baseline |
| `src/RAMWatch/Views/MonitorTab.xaml` | Baseline-colored error counts |
| `src/RAMWatch.Service/Services/BootBaselineJournal.cs` | 50-boot rolling baseline |
| `src/RAMWatch.Service/Services/ConfigChangeDetector.cs` | Change detection + delete |
| `src/RAMWatch.Service/RamWatchService.cs` | Main service — all IPC handlers |
| `src/RAMWatch/MainWindow.xaml.cs` | Window sizing (28%/60%) |

## Rules

- `timeout: 30000` on every Bash call. Use `"$USERPROFILE/.dotnet/dotnet.exe"` not `dotnet`
- Test after every change: `"$USERPROFILE/.dotnet/dotnet.exe" test src/RAMWatch.Tests`
- Commit every meaningful step. Never add features on top of uncommitted work.
- Delete `%LocalAppData%\RAMWatch\window.json` to reset window position after sizing changes
- Baseline needs 3+ boots to calibrate — first few boots will show amber for system events
