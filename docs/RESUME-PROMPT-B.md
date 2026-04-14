# Resume Prompt for RAMWatch (Session B)

Copy everything below this line into a new Claude Code session to resume work.

---

## Context

You're continuing work on RAMWatch, a Windows DRAM tuning monitor at F:\Claude\projects\RAMwatch. Read these files first:

1. `CLAUDE.md` — project rules, architecture, build commands, constraints
2. `docs/SESSION-REPORT-2026-04-14-B.md` — what was built in Session B, what remains

## Current State

- **55 commits on main**, pushed to https://github.com/thereprocase/RAMWatch
- **426 tests**, all passing: `"$HOME/.dotnet/dotnet.exe" test src/RAMWatch.Tests`
- **Build**: `"$HOME/.dotnet/dotnet.exe" build RAMWatch.sln` — zero errors
- **Phases 1-4 complete**, verified on live 5800X3D
- **29/29 timing values match ZenTimings v1.36**, tRFC ns verified correct
- **FCLK/UCLK/VSoC/VDIMM all reading correctly** (VDIMM via vendor WMI)
- **Install workflow**: `scripts/Publish-Release.ps1` → `scripts/Install-RAMWatch.ps1`
- **Dev iteration**: `scripts/Update-RAMWatch.ps1` (one command, admin PowerShell)

## Test Environment

AMD Ryzen 7 5800X3D, MSI B550 TOMAHAWK MAX WIFI, DDR4-3600 CL16-20-20-42, PawnIO installed and running, Windows 11 Pro, .NET 10.0.201 SDK at `$HOME/.dotnet/dotnet.exe`.

## What Needs Doing (priority order)

### User-Requested Features
1. **Default window size** — should be taller and narrower so timings fit without scrolling. See `docs/screenshots/timings-ideal-size.png` for the target size. Current 40%/60% of work area is too wide and not tall enough.

2. **Monitor tab empty space** — the bottom half of the Monitor tab is empty (see `docs/screenshots/monitor-empty-space.png`). Fill it with useful OC info: quick timing summary, last test result, uptime-since-last-change, WHEA count history, buttons for common actions. Things overclockers want to see every boot.

3. **Timeline enrichment** — each entry should show RAM freq + primary timings (e.g., "DDR4-3600 CL16-20-20-42") for tuning progression visibility. Add a detail toggle that expands to show secondaries, voltages, duration, notes. The validation result links to a snapshot via `ActiveSnapshotId` — use that to pull timing context.

2. **Designation dots in Timings tab** — `TimingDisplayRow` already has `DesignationIndicator` ("●" for Manual). Just needs a `TextBlock` bound to it in the row template in `TimingsTab.xaml` (both left and right column ItemsControl templates). Use `InteractiveBlue` color.

### Technical Debt
3. **MessageSerializer AOT trim warnings** — refactor `Deserialize()` from reflection-based `JsonSerializer.Deserialize(string, Type)` to type-specific overloads using the source-generated context. This is the last AOT-unfriendly code in the service.

4. **Git line ending renormalization** — `.gitattributes` is in place but existing files haven't been renormalized. Run `git add --renormalize .` and commit.

5. **DigestBuilder double dict lookup** (Legolas L2) — `GroupLabel` calls `desig.Designations[t]` after `TryGetValue`. Minor.

### Phase 5 (Future)
6. SFC/DISM execution (currently stubbed)
7. High contrast mode
8. GitHub OAuth flow for git push
9. Zen 2/4 register map validation
10. Additional voltages (VDDG CCD/IOD, CLDO VDDP, VTT)
11. Full export/import

### Known Quirks
- Window position persists to `%LocalAppData%\RAMWatch\window.json`. Delete it to reset: `Remove-Item "$env:LOCALAPPDATA\RAMWatch\window.json"`
- Service data at `%ProgramData%\RAMWatch\` — snapshots.json, tests.json, changes.json, designations.json, settings.json
- Native AOT publish requires vswhere.exe on PATH (scripts auto-detect VS Installer dir)
- VDIMM reads via PowerShell subprocess (AMD_ACPI WMI) — adds ~200ms per read cycle

## Key Files

| File | What |
|------|------|
| `src/RAMWatch.Service/RamWatchService.cs` | Main service — all IPC handlers |
| `src/RAMWatch.Service/Hardware/HardwareReader.cs` | Hardware orchestrator |
| `src/RAMWatch.Service/Hardware/UmcDecode.cs` | Timing register decode (verified) |
| `src/RAMWatch.Service/Hardware/SmuDecode.cs` | FCLK/UCLK/VSoC decode |
| `src/RAMWatch.Service/Hardware/VdimmReader.cs` | VDIMM via vendor WMI |
| `src/RAMWatch.Service/Services/GitCommitter.cs` | Git auto-commit (Channel drain) |
| `src/RAMWatch.Service/Services/SnapshotJournal.cs` | Snapshot persistence (1000 cap) |
| `src/RAMWatch.Core/Models/TuningJournal.cs` | All data models |
| `src/RAMWatch.Core/Models/BiosLayout.cs` | Vendor BIOS timing layouts |
| `src/RAMWatch.Core/Ipc/MessageSerializer.cs` | JSON-over-pipe (AOT warnings here) |
| `src/RAMWatch/ViewModels/MainViewModel.cs` | GUI state management |
| `src/RAMWatch/ViewModels/TimingsViewModel.cs` | Timing display + masonry columns |
| `src/RAMWatch/ViewModels/TimelineViewModel.cs` | Timeline entries + delete |
| `src/RAMWatch/ViewModels/SnapshotsViewModel.cs` | Compare + Manage sub-tabs |
| `src/RAMWatch/Views/TimingsTab.xaml` | Masonry two-column layout |
| `src/RAMWatch/Themes/Dark.xaml` | Charcoal theme tokens |

## Naming Vocabulary

Watchdog (service), Dashboard (GUI), Logbook (tuning journal), Drift Watch (drift detection), the Bench (community sharing), the Brief (AI digest), Scopes (privacy tiers).

## Rules

- `timeout: 30000` on every Bash call. Use `"$USERPROFILE/.dotnet/dotnet.exe"` not `dotnet`
- Test after every change: `"$USERPROFILE/.dotnet/dotnet.exe" test src/RAMWatch.Tests`
- Commit every meaningful step. Never add features on top of uncommitted work.
- PawnIO .bin modules are embedded resources — never load from user-writable paths (B8)
- XAML changes from agents need manual verification — agents can't run the GUI
