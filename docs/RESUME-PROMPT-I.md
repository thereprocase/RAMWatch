# Resume Prompt — after 2026-04-19 evening session (polish + integration)

## Session context

This session picked up from `docs/RESUME-PROMPT-H.md` (2026-04-17 evening — provenance glyphs + tuning eras). Status-dot three-axis design had been ratified 2026-04-18 and landed in-tree earlier on 2026-04-19 across commits `71212aa` (glyph), `6cb023e` (service cold-boot gate), and `6aebe49` (DotLegend + cold-boot banner + VCore/VSoC status bindings).

This session closed most of the RESUME-PROMPT-H deferred list, landed the AI-digest integration of the severity classifier, added a long-missing stuck-connect watchdog, and — unusually — peer-delegated a RAMBurn-side diagnostic over the trio channel that produced a 1.9× Prime phase speedup on the other codebase.

8 RAMWatch commits, 793/793 tests passing (+74 added). GUI Release single-file at `src/RAMWatch/bin/Release/net10.0-windows/win-x64/publish/RAMWatch.exe` (138 MB) includes everything.

## Where things stand

BRANCH: main
HEAD: `8351628 (2026-04-19 fix(gui): Timeline filter chips 10pt→11pt for Windows a11y)`
COMMITS_THIS_SESSION: 8 local, all pushed
TESTS: 793 passing, +74 added (31 ChangeSeverity + 33 Voltage/Thermal + 5 Digest + 5 skipped-miscount). No regressions.
BUILD: clean Debug + Release; publish OK
SERVICE_RESTART_NEEDED: **yes** — service touched (cold-boot gate earlier, BiosWmi refactor in prior H session). Run `Update.ps1` as admin.

## Commits (newest → oldest)

- `8351628 (2026-04-19 fix(gui): Timeline filter chips 10pt→11pt for Windows a11y)` — trivial 10→11pt bump on Snap/Pass/Fail/Change/Retrain/Drift chips.
- `a1fa5f8 (2026-04-19 fix(gui): surface "service not running" after stuck 12s connect)` — `MainViewModel.ConnectStuckThreshold = 12s`; `Task.WhenAny` in `ConnectAndListenAsync` flips `ConnectionStatus` when the connect is still pending past the threshold. Tray tooltip mirrors it via a new `ConnectionStatus` case in `MainWindow.xaml.cs:OnViewModelPropertyChanged`.
- `b2f2b41 (2026-04-19 feat(digest): Recent changes section — major listed, minor coalesced)` — `DigestBuilder.BuildDigest` gains optional `List<ConfigChange>? recentChanges`. Major change rows list individually (`CL 16→15`), Minor rows coalesce per `BootId`. Existing callers unaffected (parameter is optional). Service call site at `RamWatchService.cs:842` passes `state.RecentChanges`.
- `dc7a7df (2026-04-19 feat(minimums): surface UntestedWarning on Best Posted column)` — the `UntestedWarning` bool on `MinimumRow` was dead VM state. Now turns the Best Posted text amber with a tooltip; matches the existing Room-column amber pattern.
- `3b72095 (2026-04-19 refactor(timeline): only major changes trigger the unnamed-config nudge)` — `HasUnnamedConfig` now requires a `ChangeSeverity.Major` change within the last 2 hours, not just any change. Fixed a latent list-ordering assumption I'd introduced earlier in the session (RecentChanges is oldest-first out of `ConfigChangeDetector.GetRecentChanges`).
- `1b7314d (2026-04-19 feat(gui): Status bindings for DRAM/infinity-fabric rails + Tctl)` — new `Services/VoltageThresholds.cs` and `ThermalThresholds` centralise banding for VCore/VSoC/VDimm/VDDP/VDDG I+C and Tctl/CCD. Zero reading → `StatusLevel.None` invariant. Thresholds cite `project_voltage_tuning_state.md` + community sources. `TimingsViewModel` adds 5 new `StatusLevel` observables; `TimingsTab.xaml` binds them on the relevant glyphs. +33 tests (per-rail banding + zero→None + negative guard + IOD ≡ CCD identity).
- `32684bc (2026-04-19 feat(timeline): ConfigChange severity + per-boot retrain coalescing)` — new `RAMWatch.Core.ChangeSeverityClassifier`. Major iff any delta key ∈ {CL, RCDRD, RCDWR, RP, RAS, RC, CWL, RFC/RFC2/RFC4, MemClockMhz, FclkMhz, UclkMhz, V* rails, GDM, Cmd2T, PowerDown}. TimelineViewModel splits Major (individual rows, default visible) vs Minor (coalesced per BootId into a single "N retrains, K field(s): …" row, default hidden). XAML filter chips split into Change (blue) + Retrain (amber). +31 tests.
- `c90500f (2026-04-19 feat(gui): PawnIO install hint + legend strip in header)` — hyperlink to pawnio.eu on the no-driver empty state; `controls:DotLegend` moved out of MonitorTab and into the persistent MainWindow header under the system-info row.

## Cross-repo deliverables (in F:/Claude/ramburn/, user pre-authorised)

- `bfb9a53 (2026-04-19 docs: refresh NEXT-SESSION.md for Chunks D + E)` — RAMBurn bundled my `docs/NATIVE-BUILD-SPIKE-DESIGN.md` (287 lines) into their own handoff commit (peer-authorship credited in commit body). Doc scopes the native-build plumbing for Chunk F (HPL/OpenBLAS) and Chunk C-Full (PocketFFT BigInteger multiplication) so a future session can execute without re-deriving the approach.
- `docs/PRIME-MERSENNE-REDUCE-ANALYSIS.local.md` — gitignored analysis doc on why RAMBurn's `MersenneReduce` failed at p ≥ 2203. Identified two compounding bugs: (1) `ToByteArray().Length * 8` guard over-counts when the positive-sign pad isn't emitted, (2) final single subtract can't handle `x = 2·mp` cases. Diagnosis produced RAMBurn commit `3587090 (2026-04-19 perf(engine): restore Mersenne fast-reduction in PrimePhase (~1.9x))` — measured 1.9× throughput improvement on p=9941 target_cache=L1.

## What's NOT done (conscious deferrals)

DEFERRED: **ActionDescriptor pattern (Gandalf).** Would unify global-vs-tab-local action placement as a class of bug instead of one-off fixes. Still one-off territory until a 4th action appears.

DEFERRED: **Bench-verification of cold-boot banner + three-axis glyphs** on a fresh reboot. User-only action — can't test this from a Claude session; needs the user to boot into a fresh cold-boot state and watch the banner behaviour.

DEFERRED: **drift_window.json admin-ACL reset.** The 19 stale boot_000039 entries are decaying naturally via the upsert path (1 per unique boot). Admin-ACL blocks a user-level wipe; if accelerated cleanup is wanted, the service needs a self-clean endpoint.

DEFERRED: **Per-CCD thermal status exposure.** `ThermalThresholds.CcdTemp` exists and is tested; not yet wired into the UI (per-CCD data lives on `ThermalPowerSnapshot.CcdTempsC[]`). Would be one row expansion in the TimingsTab thermal section.

DEFERRED: **ConnectionBanner retry UI.** User-visible "retry now" button during the stuck-connect state. The watchdog flips the text; there's no explicit user-retry affordance beyond waiting for the retry cycle.

DEFERRED: **Timeline minor-retrain row shows field names only, not values.** Could render "PHYRDL_A 17→19, RTL_A 3→4" instead of just "PHYRDL_A, RTL_A". Moved to the end of the deferred list because retrain rows are hidden by default anyway.

## What to do next (in priority order)

1. **Live test-drive of the published binary.** User is rebooting into cold to test cold. On boot: launch `Update.ps1` (admin), open the GUI, confirm:
   - Status header banner (should flip to "Cold boot in progress…" until ColdBootComplete).
   - Three-axis glyphs render with ring colours on all 7 classified rails.
   - DotLegend strip visible under the MainWindow header.
   - Timeline filter chips show "Change" (default on) and "Retrain" (default off).
   - Stop `RAMWatch` service → tray tooltip should flip from "Connecting..." to "Service not responding..." within 12 s.
   - Tray → Copy Digest should produce text that includes a "Recent changes:" section.

2. **If the cold-boot banner doesn't render correctly**, the regression is most likely in the `ColdBootComplete` IPC wire. Check `ServiceState.ColdBootComplete` serialisation round-trip (IpcRoundtripTests covers it) and the MainViewModel binding.

3. **Consider the ActionDescriptor pattern** only if/when the fourth action-bar verb appears. Gandalf's call; not urgent with 3 current actions.

4. **Per-CCD thermal row** on the TimingsTab would be a natural next glyph if the user asks for more thermal visibility after the cold-boot test.

## Don't-break invariants (new this session)

- **`ChangeSeverityClassifier.MajorFields` is case-sensitive** (`StringComparer.Ordinal`). If you rename a TimingSnapshot field the test `Classifier_is_case_sensitive` will still pass with the OLD name in the set. Keep the set aligned with `TimingSnapshotFields.Timings` / `Clocks` / `Voltages` / `Booleans` names exactly.
- **`VoltageThresholds.*` and `ThermalThresholds.*` return `StatusLevel.None` on zero or negative.** Don't refactor to "pass-by-default" — a zero-reading rail is an unread sensor, not a safe one.
- **`DigestBuilder.BuildDigest`'s `recentChanges` parameter is last + optional** so existing call sites stay positional. Don't reorder.
- **`MainViewModel.ConnectStuckThreshold`** is `internal static readonly` for future fake-clock testing. Don't change to `private`.
- **Timeline filter `ShowChange` semantics flipped**: it now controls **Minor** (retrain) rows, not all ConfigChange rows. Major is controlled by `ShowMajorChange` (new). The XAML label stays "Retrain" for `ShowChange` to match user vocabulary.
- **`state.RecentChanges` is oldest-first** (ConfigChangeDetector.GetRecentChanges returns a GetRange from the end of its internal list — chronological). Any scan must walk the whole list; don't break on the first out-of-window entry.

## Files likely to touch next session

- `src/RAMWatch/Views/TimingsTab.xaml` — per-CCD thermal row if prioritised.
- `src/RAMWatch/Services/VoltageThresholds.cs` — add Vtt/Vpp classifiers when thresholds are agreed.
- `src/RAMWatch/ViewModels/TimelineViewModel.cs` — richer retrain summary (deltas in the coalesced row).
- `src/RAMWatch.Service/Services/DigestBuilder.cs` — if the AI digest wants more sections (era history, snapshot drift).

## Running service state

Service not yet updated with this session's changes — running older binary from boot_000040 (2026-04-17). Full Update.ps1 required to pick up BiosWmi + the tray watchdog-adjacent wire on the service side.

## Background agents

None running. No pending subagent threads.

## Trio coordination

Channel `ram-new` active at session-end. Member IDs: RAMWatch=`pt4gqb` (me, renamed from ram-main mid-session), RAMBurn=`gnr265`. Session tokens expire at session end.

RAMBurn this session: 5 phase-backlog items shipped (B loadstep, C-MVP numerics, C-MVP idle fix, D prime, E rebar_detect) plus my Mersenne fast-reduce fix → 596/596 tests green on their side at session end. Their `NEXT-SESSION.md` at `F:/Claude/ramburn/NEXT-SESSION.md` was refreshed in `bfb9a53`.
