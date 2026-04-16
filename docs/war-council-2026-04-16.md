# War Council Report — 2026-04-16

**Branch:** main
**Reviewed:** commits `bbdec3f` (error detail view) + `dbc6da9` (DDR5 label) + `1d9eb7c` (TimingSnapshot refactor)
**Reviewers:** 9 (Sauron, Gandalf, Frodo, Aragorn, Legolas, Uruk-Hai, Treebeard/Ents, Gimli, Gollum)
**Findings:** 5 critical, ~12 actionable warnings, ~25 notes
**Verdict per reviewer:**

| Reviewer | Tier | Verdict |
|---|---|---|
| Sauron | Opus | 2 critical, 4 warning, 3 note |
| Gandalf | Opus | 0 critical, 3 warning, 4 note |
| Frodo | Opus | 2 critical, 7 warning, 4 note |
| Aragorn | Sonnet | 0 critical, 2 warning, 4 note |
| Legolas | Sonnet | 0 critical, 3 warning, 4 note |
| Treebeard | Sonnet | 1 critical, 7 warning, 10 note |
| Gimli | Sonnet | 0 critical, 1 warning, 7 note |
| Uruk-Hai | Haiku | CLEAN |
| Gollum | Haiku | CLEAN |

## Fixed (commit `76b9379`)

All 5 criticals from the testable surface, plus 7 warnings on the same files.

| Severity | Reviewer | File:Line | Issue | Fix |
|---|---|---|---|---|
| critical | Sauron | `TimingsViewModel.cs:345` | GDM/Cmd2T/PowerDown rendered as "0"/"1" not "On"/"Off" | Short-circuit booleans before `GetIntField` |
| critical | Sauron | `EventDecoder.cs:560-573` | App Hang 1002 positional indices wrong | Re-derived against MS schema; fixed test fixture |
| critical | Frodo | `EventDecoder.cs:497-583` | Broken sentences when EventData missing | Gate on essential fields, fall back to plain summary |
| warning | Sauron | `CurrentMdBuilder.cs:179` | AllTimingPairs missing PowerDown | Added PowerDown alongside GDM/Cmd2T |
| warning | Sauron | `DriftDetector.cs:235` | ExtractAllIntTimings hand-rolled | Now iterates `TimingSnapshotFields` |
| warning | Sauron | `MinimumComputer.cs:29` | TimingFields hand-rolled | Now derived from `TimingSnapshotFields.Timings` |
| warning | Sauron | `TimingCsvLogger.cs:20` | CSV header vs row count not cross-checked | Added lock-in test |
| warning | Frodo | `EventDecoder.cs:182` | "four parameters above" misleading when params absent | Conditional text |
| warning | Frodo | `EventDecoder.cs:283` | Unexpected Shutdown didn't classify bugcheck | Reuses `ClassifyBugcheck` |
| warning | Frodo | `EventDecoder.cs:293` | SleepInProgress only checked "0" form | New `IsTruthy` helper accepts "1"/"true" |
| note | Gandalf | `CurrentMdBuilder.cs:169` | Dead `AllTimingPairsPublic` wrapper | Removed |

## Deferred — pick up next session

Sequenced by impact. Each item lists reviewer, file:line, and recommended approach.

### Critical (UX regression)

**1. Per-source seed bias** — Frodo crit #1
- Where: `StateAggregator.cs:308` (`GetRecentEventsForState`) returns last 50 across ALL sources.
- Bug: A noisy source (Disk Error, NTFS) easily fills the 50-slot ring, leaving zero events for the WHEA row the user actually wants to inspect. The dialog opens and shows "No events recorded for this source yet" despite the count column saying 3.
- Fix: change the seed to per-source — e.g., last 10 events per `WatchedSource`. Touches `EventLogMonitor.GetRecentEvents` (add a per-source variant) or do the bucketing in `StateAggregator`.
- Also: when `events.Count == 0` but `ErrorSource.Count > 0`, `EventDetailDialog.RenderEmpty` should say "N events occurred before RAMWatch started; details aren't available. Live events will appear here, or open Event Viewer for historical records."
- Test: extend `StateAggregatorTests` to cover `RecentEvents` per-source bias.

### Test coverage gap (real bug risk)

**2. MainViewModel event buffer untested** — Treebeard crit #16, Gandalf warning #4
- Where: `MainViewModel.cs:472-506` (`StoreEvent`, `SeedEvents`, `GetEventsForSource`).
- Risk: cap enforcement (`RemoveRange(0, list.Count - EventsPerSourceCap)`) and seed-once semantics are pure data-structure code with zero unit tests. An off-by-one or a flag flip would silently lose events.
- Recommended approach: extract to a `RecentEventsCache` sibling class as Gandalf suggested. MainViewModel reduces to one field. Tests then live against the cache directly without WPF dependencies.
- Bonus: extracting cleanly enables fix #3 below (latch reset on reconnect) since the cache owns the seeded flag.

### Warning — service-restart bug

**3. `_eventsSeeded` (and `_settingsLoaded`, `_dimmsLoaded`) latches forever** — Sauron warning, Gandalf warning #5
- Where: `MainViewModel.cs:39, 43, 106` and `MainViewModel.cs:516`.
- Bug: on pipe reconnect after service restart, the flags stay true. Fresh `RecentEvents` from the new service incarnation are dropped; the GUI keeps stale events from the dead service.
- Fix: reset all three flags in the disconnect branch (`MainViewModel.cs:417-418`). Cleaner long-term: dedupe-merge by `(Source, Timestamp, EventId)` instead of `Clear()`-then-seed.
- Same broken pattern affects settings reload and DIMM re-detection.

### Warnings — security defense-in-depth

**4. Cap RawXml size** — Aragorn warning #3
- Where: `EventLogMonitor.cs:202-203` calls `record.ToXml()` with no size cap.
- Threat: a low-priv user can `ReportEvent` a crafted Application Error 1000 with an 8 MB Data field. The full XML rides through the pipe, into the GUI's `_eventsBySource`, then into the Facts grid as a `TextBlock` with `TextWrapping.Wrap` → UI freeze during layout.
- Fix: `private const int MaxRawXmlBytes = 65536;` in `EventLogMonitor`; truncate after `record.ToXml()`.
- Belt-and-suspenders: cap individual fact-value strings at 512 chars in `EventDetailDialog.RenderDecoded` regardless of upstream behaviour.

**5. Enforce `MaxMessageSize` on the read path** — Aragorn warning #4
- Where: `PipeClient.cs:97`. The constant `MaxMessageSize = 1024 * 1024` exists in `PipeConstants.cs:7` but is never checked.
- Fix: after `ReadLineAsync`, drop and log oversized lines instead of deserialising. This is defence-in-depth given the DACL boundary; the constant's existence implies it was meant to be enforced.

**6. Cap `_eventsBySource` source-count** — Aragorn warning #5
- Where: `MainViewModel.cs:476-483`.
- Defense in depth: a compromised service could send events with arbitrary source names, growing the dictionary unbounded. Per-source cap is enforced; per-dictionary cap is not.
- Fix: drop new keys when `_eventsBySource.Count > MaxSourceCount` (~20).

### Warnings — architecture & maintenance

**7. EventDecoder string-based dispatch** — Gandalf warning #1
- Where: `EventDecoder.cs:19-36`. Dispatches on `evt.Source switch { "WHEA Hardware Errors" => ... }`.
- Risk: `WatchedSource.Name` is the canonical list in `EventLogMonitor`; the decoder re-types each name freehand. A rename in one place silently routes to `DecodeGeneric`.
- Direction: lift identity to `enum SourceKind` carried on `WatchedSource` and `MonitoredEvent`. Compile-time lock. Cheaper alternative: a single test that iterates `WatchedSources` and asserts the decoder doesn't fall through to generic.

**8. `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]` on `EventDecoder.Decode`** — Gimli warning #1
- Where: `src/RAMWatch.Core/Decode/EventDecoder.cs:18`.
- Why: `XmlDocument`/XPath are AOT-unsafe. Service is AOT and currently doesn't call the decoder. Annotating now turns "future maintainer accidentally wires it into the service" from a runtime bug into a compile error.

### Warnings — UX polish

**9. `CopyConfirm` never clears** — Frodo warning #5. Use the same `Task.Delay(...).ContinueWith(...)` pattern `ValidationConfirmation` uses.

**10. Copy button loses source name + system identity** — Frodo warning #4. Forum-pasting a decoded WHEA gives reviewers no idea what platform fired it. Reuse `BuildClipboardExport`'s header logic.

**11. No "Open in Event Viewer" button** — Frodo warning #6. The decoder routinely says "Check Event Viewer for vendor-specific detail"; a button that runs `Process.Start("eventvwr.msc", "/c:System")` would close the loop.

**12. PHY "(training)" annotation insufficient context** — Frodo warning #3. Forum readers will misread it as a tuning change.

### Warnings — performance

**13. `EventDecoder.Decode` re-parses XML on Copy click** — Legolas warning. Cache the `DecodedEvent` on `EventListItem` so it's computed once on selection, not again on copy.

**14. `EventLogMonitor._recentEvents.RemoveRange(0, ...)` is O(N)** — Legolas warning. Front-removal antipattern. Switch to a true ring buffer (or a `LinkedList`/`Queue` with capacity tracking) — fires during WHEA storms.

### Notes — test coverage gaps (Treebeard)

- 11 of 14 bugcheck stop codes untested
- `DecodePcie`, `DecodeVolSnap`, `DecodeFilterManager` have no tests
- 4 MCA classifications (L3Cache, Core, Pcie, IoHub) have no tests
- Malformed-XML graceful-degradation path untested
- `TimingSnapshotFields` category names not pinned (only counts)
- `TuningEqual` only mutates GDM (not Cmd2T/PowerDown)
- One real-world captured event payload per source as fixture would be valuable

## Notes deferred without action

- Gandalf #2 (`DecodedEvent` prose-soup vs structured inlines): defer until first "make this clickable" feature lands.
- Gandalf #6 (`RecentEvents` sent on every state push, GUI consumes once): tied to fix #3 — let connect-time gate or dedupe-merge handle this together.
- Gandalf #7 (helper file should reference design memo): trivial one-line comment add on next touch.
- Gimli notes on AOT publish failing on `vswhere.exe`: pre-existing CI/env issue, not a code defect.
- Sauron note #7 (`DdrLabel` MCLK threshold edge cases on Intel): not relevant to AM4-only target today.
- Frodo notes on shouty header casing, threshold edge cases, etc.: subjective polish.

## Suggested next-session order

1. Fix #1 (per-source seed) — closes Frodo's critical.
2. Fix #2 (extract `RecentEventsCache` + tests) — closes Treebeard's critical AND enables fix #3 cleanly.
3. Fix #3 (latch reset on reconnect) — closes Sauron + Gandalf warning.
4. Fix #4 + #5 (security caps) — small, high-value.
5. Fix #8 (AOT attribute) — one-line forward defence.
6. Fix #13 (cache decoded event) — small Legolas win.
7. Everything else as time permits.

Test coverage gaps (Treebeard notes) can be backfilled in a separate "test backfill" commit pass since they don't depend on the other fixes.
