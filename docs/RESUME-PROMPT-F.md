# Resume Prompt for RAMWatch (Session G)

Copy everything below this line into a new Claude Code session to resume work.

---

## Context

You're continuing work on RAMWatch, a Windows DRAM tuning monitor at F:\Claude\projects\RAMwatch. Read these files first:

1. `CLAUDE.md` — project rules, architecture, build commands, constraints
2. `docs/RESUME-PROMPT-F.md` — this file

## Current State

- **100 commits on main**
- **472 tests**, all passing: `"$HOME/.dotnet/dotnet.exe" test src/RAMWatch.Tests`
- **Build**: `"$HOME/.dotnet/dotnet.exe" build RAMWatch.sln` — zero errors
- **Version**: 0.1.0
- **Scripts** (cleaned up this session):
  - `scripts/Dev.ps1` — build + run GUI from source (no admin)
  - `scripts/Install.ps1` — full publish + install (admin)
  - `scripts/Update.ps1` — hot-swap installed binaries (admin)
  - `scripts/Uninstall.ps1` — clean removal (admin)

## What Was Built in Session F

### Scripts Overhaul (3 commits)
- Consolidated 8 scripts into 4 (Dev, Install, Update, Uninstall)
- Install.ps1 merges publish + install into one command
- Dev.ps1 is zero-admin `dotnet run` for GUI iteration
- Fixed PS 5.1 string parsing bug in Install.ps1 summary output
- Tested full install flow — service running, shortcuts created, autostart wired

### LOTC Back-Check (2 commits, 10 fixes)
Deployed Ent triage + Sauron/Uruk-Hai/Frodo review formation on sessions E-F code.

**Critical fixes:**
- SnapshotJournal.GetById returned live mutable reference — added `SetEraById` for atomic mutation under lock
- StateAggregator.ComputeMinimums passed only 5 most recent validations — `BestValidated` silently regressed after 6+ tests. Now passes all.

**Warning fixes:**
- EraJournal.GetActive/GetAll now return defensive copies (was same live-ref bug class)
- StateAggregator captures era/bootfail journals inside lock block (was reading field refs outside lock)
- SettingsViewModel guard typo `"SaveDesignationsStatus"` fixed to `"DesignationSaveStatus"` — was causing spurious auto-saves
- SetAllManual/SetAllAuto now suppress per-item saves, fire once at end (was 37 concurrent IPC calls)
- G6 scientific notation fixed in TimelineViewModel and SnapshotDisplayName.BuildLkg (same bug fixed in Build() earlier, missed in these two)
- BootFailJournal.GetRecent guards against negative count (was ArgumentOutOfRangeException)
- Removed dead `"HasDesignations"` from guard (property doesn't exist)
- CancellationTokenSource disposed on debounce reset (was leaking kernel handles)

## Known Open Issues from Back-Check

### W7 — BelowFloor display (design decision needed)
`MinimumsViewModel.cs:159` — When user beats their personal best (current tighter than best posted), Room shows "0" / "AtFloor" instead of indicating they've surpassed the floor. For REFI (higher-is-better), same issue in reverse. Need a "BelowFloor" severity or negative delta display.

### Feature Gaps (backend done, no GUI)
1. **Boot Fail Dialog** — `LogBootFailMessage` handler, `BootFailJournal` all wired. Needs: `LogBootFailDialog.xaml`, "Log Boot Fail" button in action bar, timeline integration.
2. **Era Management** — `CreateEraMessage`/`CloseEraMessage`/`MoveToEraMessage` handlers wired, `EraJournal` persists. Needs: era UI in Settings, era filtering on timeline/snapshots/minimums.
3. **UntestedWarning** — Computed per row in `MinimumsViewModel.Rebuild` but not bound in `MinimumsTab.xaml`. Warning is silently discarded.
4. **Boot Fail Timeline** — No `BootFail` variant in `TimelineEntryType`, `LoadFromState` doesn't process `state.BootFails`, no filter checkbox.

### Test Coverage Gaps (~37 tests needed)
- `EraJournalTests.cs` — 0 tests (need ~12: create, close, idempotency, corrupt file, empty name)
- `BootFailJournalTests.cs` — 0 tests (need ~8: save, delete, cap eviction, GetRecent edge cases)
- `IpcRoundtripTests` — missing era/bootfail message types (~8 new cases)
- `StateAggregatorTests` — missing era/minimums fields (~5 new cases)
- `MinimumsViewModel` — 0 tests, pure logic testable without WPF (~7 cases)

## Session G TODO (priority order)

### Tier 1: Stability (fix + test backfill)
1. **W7 BelowFloor** — Decide display for "user beat personal best" and implement
2. **EraJournalTests.cs** — ~12 tests covering create, close, GetActive, corrupt/missing file, empty name
3. **BootFailJournalTests.cs** — ~8 tests covering save, delete, cap eviction, negative GetRecent
4. **IPC roundtrip tests** — CreateEra, CloseEra, MoveToEra, LogBootFail, DeleteBootFail, state with eras/bootfails
5. **StateAggregator tests** — era/bootfail/minimums fields populated correctly

### Tier 2: Features (backend exists, wire the GUI)
6. **Boot Fail Timeline integration** — Add `BootFail` to `TimelineEntryType`, process `state.BootFails` in `LoadFromState`, add filter checkbox, red styling
7. **UntestedWarning XAML binding** — DataTrigger on `UntestedWarning=True` in MinimumsTab (amber highlight or warning icon)
8. **Log Boot Fail Dialog** — 420x520 modal per design in `docs/RESUME-PROMPT-D.md` "Log Boot Fail Feature": When dropdown, What Happened radios, base snapshot, sparse timing editor, notes. "Log Boot Fail" button in action bar.
9. **Era Management UI** — Settings panel: "New Era" name field + button, "Close Era" button for active era. Active era shown in status header. Era filter toggle on Timeline/Snapshots/Minimums.

### Tier 3: Polish
10. **Snapshot-per-boot cleanup** — Stop creating new snapshots in `HandleLogValidationAsync`, link to existing boot snapshot
11. **MinimumsViewModel tests** — ~7 cases for frequency label, room computation, negative delta

## Boot Fail Dialog Design Reference

Full design is in `docs/RESUME-PROMPT-D.md` under "Log Boot Fail Feature". Key points:
- 420x520 modal, dark theme
- "When" preset dropdown ("~5 min ago", "~15 min ago" default, etc.)
- "What happened" radio buttons (No POST / Boot Loop / Unstable / Other)
- Base snapshot dropdown pre-fills timing values
- Sparse timing editor: user adds rows with [timing name dropdown] [new value] [was: old value]
- Notes free text
- Timeline: new `BootFail` entry type, red border, kind labels
