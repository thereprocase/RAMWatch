# Resume Prompt for RAMWatch (Session B)

Copy everything below this line into a new Claude Code session to resume work.

---

## Context

You're continuing work on RAMWatch, a Windows DRAM tuning monitor at F:\Claude\projects\RAMwatch. Read these files first:

1. `CLAUDE.md` — project rules, architecture, build commands, constraints
2. `docs/SESSION-REPORT-2026-04-14-B.md` — what was built in Session B, what remains

## Current State

- **53 commits on main**, pushed to https://github.com/thereprocase/RAMWatch
- **426 tests**, all passing: `"$HOME/.dotnet/dotnet.exe" test src/RAMWatch.Tests`
- **Build**: `"$HOME/.dotnet/dotnet.exe" build RAMWatch.sln` — zero errors
- **Phases 1-4 complete**, verified on live 5800X3D
- **29/29 timing values match ZenTimings v1.36**
- **FCLK/UCLK/VSoC/VDIMM all reading correctly**
- **Install workflow**: `scripts/Publish-Release.ps1` → `scripts/Install-RAMWatch.ps1`
- **Dev iteration**: `scripts/Update-RAMWatch.ps1` (one command)

## Test Environment

AMD Ryzen 7 5800X3D, MSI B550 TOMAHAWK MAX WIFI, DDR4-3600 CL16-20-20-42, PawnIO installed and running, Windows 11 Pro, .NET 10.0.201 SDK at `$HOME/.dotnet/dotnet.exe`.

## What Needs Doing (priority order)

1. **MessageSerializer AOT trim warnings** — refactor `Deserialize()` from reflection-based `JsonSerializer.Deserialize(string, Type)` to type-specific overloads using the source-generated context. This is the last AOT-unfriendly code in the service.

2. **Git line ending renormalization** — `.gitattributes` is in place but existing files haven't been renormalized. Run `git add --renormalize .` and commit.

3. **Phase 5 polish** — SFC/DISM execution, high contrast mode, GitHub OAuth flow, Zen 2/4 register validation, additional voltages.

## Key Files

| File | What |
|------|------|
| `src/RAMWatch.Service/RamWatchService.cs` | Main service entry point |
| `src/RAMWatch.Service/Hardware/HardwareReader.cs` | Hardware orchestrator |
| `src/RAMWatch.Service/Hardware/UmcDecode.cs` | Timing register decode |
| `src/RAMWatch.Service/Hardware/SmuDecode.cs` | FCLK/UCLK/VSoC decode |
| `src/RAMWatch.Service/Hardware/VdimmReader.cs` | VDIMM via WMI |
| `src/RAMWatch.Service/Services/GitCommitter.cs` | Git auto-commit |
| `src/RAMWatch.Service/Services/SnapshotJournal.cs` | Snapshot persistence |
| `src/RAMWatch.Core/Models/TuningJournal.cs` | All data models |
| `src/RAMWatch.Core/Ipc/MessageSerializer.cs` | JSON-over-pipe framing (AOT warnings here) |
| `src/RAMWatch/ViewModels/MainViewModel.cs` | GUI state management |
| `src/RAMWatch/Views/TimingsTab.xaml` | Masonry timing layout |

## Naming Vocabulary

Watchdog (service), Dashboard (GUI), Logbook (tuning journal), Drift Watch (drift detection), the Bench (community sharing), the Brief (AI digest), Scopes (privacy tiers).

## Rules

- `timeout: 30000` on every Bash call. Use `"$USERPROFILE/.dotnet/dotnet.exe"` not `dotnet`
- Test after every change: `"$USERPROFILE/.dotnet/dotnet.exe" test src/RAMWatch.Tests`
- Commit every meaningful step. Never add features on top of uncommitted work.
- PawnIO .bin modules are embedded resources — never load from user-writable paths (B8)
