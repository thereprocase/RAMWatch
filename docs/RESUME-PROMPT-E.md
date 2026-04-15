# Resume Prompt for RAMWatch (Session F)

Copy everything below this line into a new Claude Code session to resume work.

---

## Context

You're continuing work on RAMWatch, a Windows DRAM tuning monitor at F:\Claude\projects\RAMwatch. Read these files first:

1. `CLAUDE.md` — project rules, architecture, build commands, constraints
2. `docs/RESUME-PROMPT-E.md` — this file

## Current State

- **~90 commits on main**
- **472 tests**, all passing: `"$HOME/.dotnet/dotnet.exe" test src/RAMWatch.Tests`
- **Build**: `"$HOME/.dotnet/dotnet.exe" build RAMWatch.sln` — zero errors
- **Version**: 0.1.0 (in all .csproj files and title bar)
- **Dev iteration**: `scripts/Update-RAMWatch.ps1` (one command, admin PowerShell)

## What Was Built in Sessions D-E

### Session D (18 commits)
- Spurious config change suppression (incomplete read + clock jitter tolerance)
- Auto-snapshot deferred until clocks populated
- Snapshot dropdown sorted newest-first, default selection fixed
- Timing summaries on dropdown labels and timeline entries
- Monitor tab: baseline "Normal" column, dimmed zero rows, collapsed SFC/DISM
- Lucide circuit-board icon (app + tray states)
- --minimized CLI arg and StartMinimized setting wired
- FCLK/UCLK SnapClockMhz (rounds to nearest BCLK/3 multiple)
- LOTC safety review: clock ratio bit fix, partial-failure rejection, path validation, designations cap

### Session E (13 commits)
- UI polish: denser timings, tab underline, WCAG contrast fix, Segoe UI font, smaller window
- Settings persistence: load from service on connect (was write-only)
- LaunchAtLogon wired to HKCU Run registry
- Settings auto-save with 500ms debounce (no Save button)
- Numeric input validation, designations bulk "All Manual" / "All Auto"
- Timeline type filters (Pass/Fail/Change/Drift checkboxes)
- Performance: ComputeBaselines caching, CSV AutoFlush
- **Data model**: TuningEra, BootFailEntry, FrequencyMinimums models
- **Service journals**: EraJournal, BootFailJournal, MinimumComputer (8 tests)
- **Service wiring**: all IPC handlers, era stamping, minimums in state push
- **Minimums tab**: frequency dropdown, grouped table with Current/BestPosted/BestTested/Room
- Metric display fix (G6 → F1, no more scientific notation)

## What's Built But Not Yet Visible in GUI

The backend for these features is fully wired (IPC handlers, journals, persistence) but no GUI exists yet:

1. **Boot Fail logging** — `LogBootFailMessage` handler works, `BootFailJournal` persists to `boot-fails.json`. Needs: `LogBootFailDialog.xaml` (sparse timing editor), "Log Boot Fail" button in action bar, timeline integration (new entry type + filter checkbox).

2. **Era management** — `CreateEraMessage` / `CloseEraMessage` / `MoveToEraMessage` handlers work, `EraJournal` persists to `eras.json`. Needs: era UI (create/close/switch in Settings or a panel), era filter on timeline/snapshots/minimums, "Move to Era" on Manage tab.

## Session F TODO (priority order)

### 1. Log Boot Fail Dialog
Full design in `docs/RESUME-PROMPT-D.md` under "Log Boot Fail Feature". Key points:
- 420x520 modal, same dark theme
- When: preset dropdown ("~5 min ago", "~15 min ago" default, etc.)
- What happened: radio buttons (No POST / Boot Loop / Unstable)
- Base snapshot: dropdown pre-fills timing values
- What changed: sparse editor — user adds rows with [timing name dropdown] [new value] [was: old value]
- Notes: free text
- "Log Boot Fail" button in action bar next to "Log Test Result"
- Timeline: new `BootFail` entry type, red border, "NO POST" / "BOOT LOOP" / "UNSTABLE" labels
- Filter checkbox in timeline header

### 2. Era Management UI
- Settings tab or a small panel: "New Era" button, name field, close button for active era
- Active era name shown in status header or tab somewhere
- Filter toggle on Timeline/Snapshots/Minimums to scope to active era
- "Move to Era" action on Manage tab snapshot cards

### 3. Snapshot-Per-Boot Cleanup
Original plan was a full refactor, but the auto-save already creates one per boot. The remaining noise is validation-linked snapshots. Consider:
- Stop creating new snapshots in `HandleLogValidationAsync` — link to existing boot snapshot instead
- Or: keep creating them but mark them with a flag so the dropdown can collapse them

### 4. Minimums Tab Polish
- Verify it populates correctly after reboot with real data
- Consider adding tRFC nanosecond display (cycles × 1000 / MemClockMhz)
- BestPosted green when Current matches the floor
- Untested warning indicator when BestPosted < BestValidated

### 5. Ship Prep (when code is locked)
- README rewrite with screenshots
- Release zip script
- `gh repo create` + push
- v0.1.0 GitHub Release

## Test Environment

AMD Ryzen 7 5800X3D, MSI B550 TOMAHAWK MAX WIFI, DDR4-3800 CL16-20-20-38, PawnIO installed and running, Windows 11 Pro, .NET 10.0.201 SDK at `$HOME/.dotnet/dotnet.exe`.

## Key Files

| File | What |
|------|------|
| `src/RAMWatch.Service/RamWatchService.cs` | Main service — all IPC handlers including era/boot-fail |
| `src/RAMWatch.Service/Services/EraJournal.cs` | Era CRUD + persistence |
| `src/RAMWatch.Service/Services/BootFailJournal.cs` | Boot fail persistence |
| `src/RAMWatch.Service/Services/MinimumComputer.cs` | Per-frequency minimum computation |
| `src/RAMWatch.Service/Services/StateAggregator.cs` | State push with eras/minimums/boot-fails |
| `src/RAMWatch/ViewModels/MinimumsViewModel.cs` | Minimums tab ViewModel |
| `src/RAMWatch/Views/MinimumsTab.xaml` | Minimums tab XAML |
| `src/RAMWatch/ViewModels/MainViewModel.cs` | GUI state, settings load, minimums wiring |
| `src/RAMWatch/ViewModels/SettingsViewModel.cs` | Auto-save, LaunchAtLogon, designations |
| `src/RAMWatch.Core/Models/TuningJournal.cs` | All models: TimingSnapshot, TuningEra, BootFailEntry, etc. |
| `src/RAMWatch.Core/Models/IpcMessages.cs` | All IPC message types |
| `docs/RESUME-PROMPT-D.md` | Contains full Log Boot Fail design and Minimums/Eras designs |

## Rules

- `timeout: 30000` on every Bash call. Use `"$USERPROFILE/.dotnet/dotnet.exe"` not `dotnet`
- Test after every change: `"$USERPROFILE/.dotnet/dotnet.exe" test src/RAMWatch.Tests`
- Commit every meaningful step. Never add features on top of uncommitted work.
- Delete `%LocalAppData%\RAMWatch\window.json` to reset window position after sizing changes
