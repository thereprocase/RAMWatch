# Resume Prompt — after 2026-04-17 session

## Session context (read first)

See `docs/SESSION-REPORT-2026-04-17.md` for the full grepable record:
35 findings triaged, 26 commits, 636 tests passing, three waves of
parallel Sauron + Uruk-Hai audits.

Also see `docs/HARDWARE-DATA-SOURCES.md` — the 10 data pipes RAMWatch
reads machine state from, categorized 8 ways.

## Where things stand

BRANCH: main
HEAD: 8fc7fbe (fix(gui): RequestId-keyed digest waiters for concurrent CopyDigest)
BASE_WHEN_SESSION_STARTED: af7750d
COMMITS_THIS_SESSION: 26
TESTS: 636 passing, 0 regressions, +11 added
BUILD: clean Debug + Release
PUSHED: no — all commits local

## What changed (headline)

- **Drift detection was broken** — rolling 20-boot window was filling with 20 copies of the current boot within minutes. Fixed.
- **Hardware decode had multiple silent-zero paths** — now aborts on any failure instead of committing partial snapshots to the journal.
- **SVI2 VID=0 reported as 1.55V** — fixed; VID=0 now treated as sentinel.
- **BIOS WMI could deadlock the service forever** — 10s timeout + Kill.
- **Service shutdown raced its own dispose** — shutdown barrier + drain window.
- **Settings wholesale-replace wiped fields** — JSON-presence merge on the service side, `_lastLoadedSettings` round-trip preservation on the GUI side.
- **Rolling windows, unbounded channels, and a regressed PM-table buffer** — all capped / corrected.
- **GUI async void crash path** — wrapped; tray won't silently disappear on transient errors anymore.

## What's NOT done (conscious deferrals)

DEFERRED: UI wiring for TrfcReadbackBugDetected — data now reaches every state push; Timings tab needs a banner/label. Data layer complete.
DEFERRED: AGESA version parser for decode dispatch — tRFC bug is magic-compare specific; no other known AGESA-gated decode; full parser is larger scope.
DEFERRED: Pipe DACL narrowing to console-session SID — requires install-time SID capture or WTS runtime APIs; single-user enthusiast target box unaffected.
DEFERRED: SystemInfoReader staleness on mid-session BIOS flash — requires reboot in practice.
DEFERRED: BootBaselineJournal crash-window between dedup check and atomic write — too narrow.
DEFERRED: git gc scheduling — auto-gc fires ~6700 loose objects; not catastrophic for years.
DEFERRED: dead converters in App.xaml (GroupsToLeftColumn / GroupsToRightColumn) — cosmetic removal, not urgent.
DEFERRED: MinimizeToTray Hide() vs BringExistingToFront ShowWindow race on autostart — only affects first-instance restore from tray; second-instance exits silently today.

## What to do next (in priority order)

1. **Deploy the fixes.** All 26 commits are local. `scripts/Update.ps1` (admin) rebuilds the service and hot-swaps — the running service (boot_000039) is still pre-session code. Confirm with user before running Update.ps1; it touches the live service.
2. **Reset polluted state on deploy.** `C:\ProgramData\RAMWatch\drift_window.json` holds 20 copies of boot_000039. After Update.ps1, either delete it (drift resumes after 3 future boots) or let it self-heal over 20 future reboots.
3. **Wire the tRFC bug indicator into the Timings tab.** Field exists on TimingSnapshot, flows via state pushes. Want a small warning banner/icon near the tRFC rows.
4. **Run CI / the test suite once on a fresh clone** — the session landed a lot of changes; a clean-slate verification pass is cheap insurance.
5. **Consider adding tests for the newly-fixed paths that don't have them**:
   - BiosWmi timeout behavior (hard to test without subprocess fixture; skip unless a fake IProcess is worth adding)
   - Shutdown barrier / ObjectDisposedException swallow (would need to exercise StopAsync mid-callback)
   - GitCommitter bounded channel DropOldest behavior (doable if a test drains synchronously)
6. **Audit the GUI's ToSettings against AppSettings every time AppSettings changes.** The W1 critical is a shape-of-bug that'll recur the moment someone adds a new AppSettings field without updating the GUI. Consider a reflection-based or source-generated checker, or a unit test that compares property counts.

## Don't-break invariants

- `_driverLock` in HardwareReader serializes every PawnIO IOCTL. Hot tier, warm tier, and event-callback thread all acquire it. Don't add a new reader path that bypasses it.
- `_shuttingDown` flag in RamWatchService must be checked at the top of any thread-callback path before touching disposable fields.
- `ConfigChangeDetector.DetectChanges` has a 5 MHz clock tolerance — don't remove it without re-auditing how BuildDeltas handles jitter on FCLK/UCLK/MCLK.
- `SettingsManager.Save` now assigns `_current` ONLY after the atomic File.Move. Don't re-order back to "assign first"; it breaks the disk/memory invariant.
- `ApplyPatch` uses JsonElement field-presence to detect what was sent. If you add a typed patch path, be sure it has an equivalent way to distinguish absent-from-default; System.Text.Json typed deserialize cannot.
- `_seenRecordIds` in EventLogMonitor clears on 10k overflow. That's a documented tradeoff, not a TODO.
- `_rawBuf` / `_floatBuf` in SmuPowerTableReader must be accessed via `ExecuteInto` (not the Range-indexer form). Range-indexer allocates; that was the regression.
- `MirrorLogger` drops on full semaphore. That's correct — don't switch to an unbounded queue "to catch up"; catching up is the slow mirror's problem.

## Running service state

Service boot_000039, started 2026-04-16 22:21:16Z, ~12h uptime at start of session. 
VCore varied live (40 distinct values observed), VSoc stepped once from 1.1062 → 1.1125 (proof SVI2 reads are not cached).
Static rails VDimm/VDDG_IOD/VDDG_CCD/VDDP bit-identical the entire session — expected behavior for the data source (commanded/set-point, not sensed).
Zero WHEA all session. Today's user "known-good" profile is holding.

## Background agents

Three Wave 3 Sauron agents completed successfully. No pending background tasks.

## Files likely to touch next session

- `src/RAMWatch/Views/TimingsTab.xaml` and/or `src/RAMWatch/ViewModels/TimingsViewModel.cs` — for the tRFC banner.
- `scripts/Update.ps1` — for the deploy step.
- `src/RAMWatch.Tests/SettingsTests.cs` — if adding a "every AppSettings field is covered by ToSettings" check.
