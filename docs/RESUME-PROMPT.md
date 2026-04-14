# Resume Prompt for RAMWatch

Copy everything below this line into a new Claude Code session to resume work.

---

## Context

You're continuing work on RAMWatch, a Windows DRAM tuning monitor at F:\Claude\projects\RAMwatch. Read these files first:

1. `CLAUDE.md` — project rules, architecture, build commands, constraints
2. `SPRINT-PLAN.md` — blockers (B1-B8), architecture corrections, phase plan, test plan
3. `docs/SESSION-REPORT-2026-04-14.md` — what was built, what remains, key decisions, test environment

## Current State

- **25 commits on main**, all pushed to https://github.com/thereprocase/RAMWatch
- **171 tests**, all passing: `"$HOME/.dotnet/dotnet.exe" test src/RAMWatch.Tests`
- **Build**: `"$HOME/.dotnet/dotnet.exe" build RAMWatch.sln` — zero errors
- **Phases 1-3 complete**, Phase 2 hardware-verified on live 5800X3D
- **PawnIO integration working** — 29/29 timing values match ZenTimings v1.36

## Test Environment

AMD Ryzen 7 5800X3D, MSI B550 TOMAHAWK MAX WIFI, DDR4-3600 CL16-20-20-42, PawnIO installed and running, Windows 11 Pro, .NET 10.0.201 SDK at `$HOME/.dotnet/dotnet.exe`.

## What Needs Doing Next (priority order)

1. **Wire HardwareReader into RamWatchService.cs** — the reader works (verified on hardware), the Timings tab exists, the StateAggregator has a Timings field. Connect the periodic refresh loop to call `HardwareReader.ReadTimings()` and populate `ServiceState.Timings`. Also trigger ConfigChangeDetector and DriftDetector when a snapshot arrives.

2. **Wire DigestBuilder into GetDigest IPC handler** — `RamWatchService.OnClientMessage` has a stub that returns null for GetDigest. DigestBuilder exists and is tested. Connect them.

3. **Wire TimingCsvLogger** — exists but not called from the service loop.

4. **Phase 4: Git integration** — GitCommitter (background thread, ArgumentList for shell-out, GH_CONFIG_DIR isolation), CURRENT.md/LKG.md generation, public contribution records.

## Key Files

| File | What |
|------|------|
| `src/RAMWatch.Service/RamWatchService.cs` | Main service — wire hardware reads here |
| `src/RAMWatch.Service/Hardware/HardwareReader.cs` | Orchestrator — already works |
| `src/RAMWatch.Service/Hardware/PawnIo/PawnIoAccess.cs` | PawnIO driver — working |
| `src/RAMWatch.Service/Hardware/UmcDecode.cs` | Register decode — verified correct |
| `src/RAMWatch.Service/Services/StateAggregator.cs` | Builds ServiceState — needs Timings field populated |
| `src/RAMWatch.Service/Services/DigestBuilder.cs` | The Brief — tested, needs IPC wiring |
| `src/RAMWatch.Core/Models/TuningJournal.cs` | All Phase 3 data models |
| `src/RAMWatch.Core/Models/ServiceState.cs` | State pushed over pipe — has Timings field |
| `reference/zenstates-core/` | GPL-3.0 reference (gitignored) |

## Naming Vocabulary

Use these names in UI text and conversation: the Watchdog (service), the Dashboard (GUI), the Logbook (tuning journal), Drift Watch (drift detection), the Bench (community sharing), the Brief (AI digest), Scopes (privacy tiers).

## Rules

- Read `~/.claude/CLAUDE.md` for voice, code discipline, and tool reliability rules
- Commit every meaningful step. Never add features on top of uncommitted work.
- C#: PascalCase classes, snake_case for file names only where existing convention
- `timeout: 30000` on every Bash call. Use `"$USERPROFILE/.dotnet/dotnet.exe"` not `dotnet` (SDK not on PATH in bash)
- Test after every change: `"$USERPROFILE/.dotnet/dotnet.exe" test src/RAMWatch.Tests`
- The service runs as admin — test with `.\scripts\TestTimingRead.ps1` from admin PowerShell
- PawnIO .bin modules are embedded resources — never load from user-writable paths (B8)
