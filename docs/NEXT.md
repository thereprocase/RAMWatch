# NEXT

Crash Capture sprint — 2 of 4 commits landed 2026-04-19 night. Service restart needed: `scripts/Update.ps1` as admin.

## HEAD
- `69f0305 (2026-04-19)` — auto-detect prior-boot crash
- `ce38c59 (2026-04-19)` — manual "Log failed boot" dialog on Timeline tab

Tests: 803/803. Build clean.

## Pick up here

- **Commit #3** — pipe protocol v3 bump. Add `CrashClass` enum + `WheaCorrectedCount/FatalCount/LastEventAtUtc` to state push. Wire spec: `ram3` msg #491 (SQLite-cached). RAMBurn-Opus task #5 consumer waits on this.
- **Commit #4** — reorder `RamWatchService.ExecuteAsync` to do cold-tier UMC read first. Self-contained, RAMWatch-only.

Either order fine. #3 is cross-repo; #4 is ~1 hr.

## Relevant files

- `src/RAMWatch.Service/Services/BootFailDetector.cs` — classifier from commit #2
- `src/RAMWatch.Service/RamWatchService.cs` — startup wire, look for `BootFailDetector.CreateDefault()`
- `src/RAMWatch/Views/LogBootFailDialog.xaml{,.cs}` — manual entry dialog
- `src/RAMWatch.Core/Models/TuningJournal.cs` — `BootFailEntry`, `BootFailKind`
- `src/RAMWatch.Core/Models/IpcMessages.cs` — `LogBootFailMessage` (for v3 bump look here)
- `docs/RESUME-PROMPT-I.md` — prior session handoff for broader context

## Deferred (non-blocking)

- `TryParseChanges` unit test (parser lives in LogBootFailDialog; extract to internal-static to test).
- Auto-detect BaseSnapshotId is always null; could look up latest snapshot.
- `BootFailKind` enum extensions (FailedBeforeLogin / FailedDuringLogin) if user wants finer granularity.

## Memory pointers

- `project_failure_signatures.md` — bugcheck-no-WHEA vs WHEA-heavy fingerprints (today's new memory).
- `feedback_token_estimates.md` — post token estimate at top of each new task.
- `project_ramburn_integration.md` — pipe semantics, RAMBurn's consumer side.

## Trio

Channel `ram3`. Peer `RAMBurn-Opus` was mid-F-0 native-build-spike (WIP stashed their side). Reconnect if continuing; safe to let the channel linger.
