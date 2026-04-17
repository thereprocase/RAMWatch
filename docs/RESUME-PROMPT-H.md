# Resume Prompt — after 2026-04-17 evening session (provenance + tuning eras)

## Session context

This session picked up from `docs/RESUME-PROMPT-G.md`. Focus: surface the
"how trustworthy is this number, and which config was I testing when the
number was recorded?" story that was buried in the data but invisible in
the GUI. Two UX arcs landed:

1. **Sensor provenance glyphs** — 10×10 shape+color dot next to every live
   number telling the user at a glance whether it's a direct measurement,
   a reported setpoint, or static BIOS config.
2. **Tuning eras as first-class timeline anchors** — user-named campaigns
   that every snapshot / test / change / drift event files under, with an
   inline "Start new config" banner and a "new BIOS detected — name it?"
   nudge.

11 commits, all local then pushed. 625 → 684 tests passing.

## Where things stand

BRANCH: main
HEAD: 164eb67 (refactor(gui): LOTC Three Seers consensus — action scoping + discoverability)
BASE_WHEN_SESSION_STARTED: 2a62c66
COMMITS_THIS_SESSION: 11
TESTS: 684 passing, 0 regressions, +59 added
BUILD: clean Debug
PUSHED: yes — all commits on origin/main

## Headline changes (chronological)

- **tRFC readback-bug banner** on Timings tab — the deferred Timings UI wire
  from the previous session. Amber warning appears only when the UMC
  decoder hit the ComboAM4v2PI 1.2.0.x magic value on reg260.
- **Reflection audit** guards `AppSettings ↔ SettingsViewModel.ToSettings`
  from silent field-drift recurrence of the W1 wholesale-replace bug.
- **Dead converter removed**, **autostart race fixed** (named EventWaitHandle
  replaces BringExistingToFront for reliable hidden-window reach),
  **BiosWmi timeout helper extracted** + 3 new tests.
- **Sensor provenance system ported from RAMBurn**:
  - `Services/SensorProvenance.cs` — enums, record, 22-sensor registry
    keyed on TimingsViewModel property names, ForVoltage sentinel for
    ASRock-absent BIOS WMI reads.
  - `Services/ProvenanceObserver.cs` — per-sensor min/max/count ledger;
    Adjust demotes flat Measured/Reported or drifted Static to Unknown.
    Never promotes. Process-wide singleton via SensorProvenanceResolver.
  - `Controls/ProvenanceGlyph.cs` — 10×10 FrameworkElement, OnRender
    with frozen brushes + geometry, 3 dependency properties, subscribes
    to observer on Loaded. Tooltip rebuild on input change.
  - Wired onto: Timings tab (22 glyphs — voltages, clocks, signal
    integrity, thermal), MainWindow header (system-info line with Static
    glyph), Monitor tab section headers (ERROR MONITOR green,
    INTEGRITY amber), Timeline rows (green circle for user-logged,
    amber diamond for derived).
- **SystemInfoText on MainWindow header** — Board / CPU / BIOS / AGESA
  surfaced from TimingSnapshot fields that previously only appeared in
  the clipboard digest.
- **Tuning eras GUI**:
  - Service-side CreateEra/CloseEra IPC and TuningEra model were already
    complete but unwired in the GUI. Wired now.
  - Timeline tab gains a hero banner at the top with three states:
    active era (green), unnamed-config nudge (amber), neutral prompt.
    Inline name entry (TextBox + Enter/Esc bindings) — no modal dialog.
  - User-labeled snapshots appear as TimelineEntry rows with green
    circle glyph. Auto before/after captures stay hidden.
  - Every timeline row now carries an italic "under &lt;era name&gt;" footer
    so PASS / FAIL / CHANGE / SNAPSHOT all point back to their campaign.
  - "Change" filter renamed "Retrain" and defaults off — the rows were
    mostly tick-level auto-retraining noise that drowned intentional
    events; users who want the history toggle it on.
- **LOTC Three-Seer consensus pass** on Frodo's whole-UI audit:
  - Log Test Result moved off global action bar → Timeline section
    header (contextual instead of stranded).
  - Monitor rows get cursor=hand + "Double-click for details" hint.
  - Ctrl+S guarded when Timeline.IsNamingEra is true (prevents snapshot
    dialog from popping over the era-naming TextBox mid-word).
  - Era banner bumped to 14pt bold hero element.

## What's NOT done (conscious deferrals)

DEFERRED: Provenance glyph **legend strip** in MainWindow header.
  Gandalf + Frodo both flagged that the 10×10 dots are near-invisible at
  12pt text without a persistent legend. The vocabulary is invented;
  users can't learn it from tooltip-alone.
DEFERRED: **PawnIO install link / hint** on the no-driver empty state at
  `TimingsTab.xaml:10`. Frodo's round-walk catch: a first-time user
  launches and sees "Timing display requires PawnIO driver" with no next
  step, quits.
DEFERRED: **ActionDescriptor architectural pattern** (Gandalf). Would
  unify global-vs-tab-local action placement as a class of bug instead
  of one-off fixes. Worth doing when we add a 4th action that wants
  placement decided; three is still within one-off territory.
DEFERRED: **Auto-coalesce retraining ConfigChanges** — currently ShowChange
  defaults off, but if turned on the rows are per-boot. Grouping into
  "Boot X: 3 minor retrains (tRC, tRFC, PHYRDL_A)" was Frodo's original
  recommendation; deferred in favour of simpler "hide by default."
DEFERRED: Service-side **"big enough change" classifier** for the
  unnamed-config prompt. Current heuristic is "ConfigChange in the last
  2 hours + no active era" — works, but could miss subtle flashes.
DEFERRED: Filter chip font size (10pt is below Windows a11y minimum),
  connection-banner retry, settings-designations filter, tray
  "Connecting..." timeout fallback — all batch-sized polish.
DEFERRED: `drift_window.json` reset — admin ACL blocks user-level
  delete, self-heals over 20 future unique boots per the new upsert
  logic. The 19 stale boot_000039 entries decay one-per-unique-boot.
DEFERRED: `scripts/Update.ps1` deploy — the GUI has visible changes that
  don't affect the running service, but the service hasn't been restarted
  since boot_000040 (the post-Update.ps1 run from last session). If the
  user wants to see the new era banner / glyph UI live, publish GUI
  (`dotnet publish src/RAMWatch -c Release -r win-x64`) and launch the
  produced single-file exe. The service doesn't need a redeploy —
  nothing in this session touched service code except BiosWmi refactor.

## What to do next (in priority order)

1. **Publish the GUI** so the user can see this session's work:
   `dotnet publish src/RAMWatch -c Release -r win-x64` then launch the
   produced exe (or run `scripts/Dev.ps1` which builds + runs from
   source). Confirm with user before running Update.ps1 — the service
   was touched (BiosWmi timeout refactor) but the deploy-live-swap is
   still a live-system action.
2. **Ship the two queued consensus items** — provenance legend strip
   in the MainWindow header and a "What's PawnIO?" link on the
   TimingsTab no-driver empty state. Small, high-value.
3. **Test the full tuner round live** — the user's canonical workflow:
   boot → Timeline tab → "Start new config" → name → run a stress test
   → click "Log test result" (now on Timeline section header) → confirm
   the row appears with "under &lt;era name&gt;" footer. Worth doing once
   to catch any binding typos a build-pass alone wouldn't flag.
4. **Consider the ActionDescriptor pattern** if/when adding a 4th
   action-bar verb — the Gandalf architectural call that would eliminate
   action-bar bifurcation. Not urgent; current three-action split works.
5. **Timeline UX polish** from the queue: coalesce same-boot retraining
   ConfigChanges into one row ("Boot X: 3 minor retrains"), add a
   "severity" classifier (Frodo's #2 biggest unshipped recommendation).
6. **More surfaces for provenance glyphs** — the Minimums tab was not
   glyphed; Settings tab has tier data that would benefit from visible
   source classification. Deferred.

## Don't-break invariants

- `SensorProvenanceRegistry` keys match `TimingsViewModel` property names
  **exactly** (`Vsoc` not `VSoc`, `CsOdtCmdDrvStren` not `CsOdtDrvStren`).
  A test rows `Registry_For_ReturnsExpectedTier` covers each; adding a
  new glyph-wired property means adding a row there and a registry entry.
- `SensorProvenanceRegistry.ForVoltage` treats `Vdimm` / `Vtt` / `Vpp` == 0
  as **Unknown** sentinel (ASRock boards without AMD_ACPI WMI). Do not
  add other keys to this branch without re-reading the ASRock notes.
- `ProvenanceObserver` **never promotes**. Only the registry (or future
  pipe-side source tags) can confer Measured status. If you add a
  "promote on high variance" path it will lie about cached reads.
- `SettingsViewModel._lastLoadedSettings` preserves 7 fields the GUI
  doesn't surface (SchemaVersion, LogDirectory, DebugLogging,
  EnableGitIntegration, EnableGitPush, GitRemoteRepo, GitUserDisplayName).
  The reflection-audit test `ToSettings_PreservesEveryAppSettingsProperty_ThroughLoadRoundTrip`
  catches regressions. Any new AppSettings field MUST be added to either
  LoadFromSettings + ToSettings (if user-editable) or preserved via
  `_lastLoadedSettings` (if internal).
- `TimelineViewModel.IsNamingEra` gates `Ctrl+S` in MainWindow.xaml.cs.
  Do not add Window-level hotkeys that might pop modals on top of the
  era-naming TextBox without a similar guard.
- `Log Test Result` button now lives on the Timeline tab section header,
  **not** the global action bar. Do not add it back to MainWindow.xaml —
  it had no context on Settings / Monitor and the consensus was clear.
- `Timeline.ShowChange` defaults **false** — "Retrain" rows are noise.
  If you find yourself wanting to default it on, either add severity
  classification or coalesce per-boot first.
- `ProvenanceGlyph.MeasureOverride` returns 10×10 regardless of
  availableSize. Don't try to flex it — the subscription is at fixed
  pixel positions and the legend (when built) assumes this size.
- `Services/NotificationHelper.cs` is the **only** pre-existing file in
  `src/RAMWatch/Services/`. Don't delete it assuming it's session debris.

## Running service state

Service boot_000040, started 2026-04-17 12:04:17Z. ~4.5h uptime at end
of session. Zero WHEA. VCore varied live across all voltage reads (SVI2
confirmed working post-deploy). User's "known-good" profile holding.
Live system info snapshot: AMD Ryzen 7 5800X3D, MSI MAG B550 TOMAHAWK
MAX WIFI, BIOS 2.A0, AGESA string empty (SystemInfoReader doesn't
populate it — registry field is blank on this board; accepted limitation).

## Background agents

Three LOTC seers (Sauron, Gandalf, Frodo) completed successfully.
One earlier Frodo solo review completed. No pending background tasks.

## Files likely to touch next session

- `src/RAMWatch/MainWindow.xaml` — adding provenance legend strip.
- `src/RAMWatch/Views/TimingsTab.xaml` — adding PawnIO install link on
  no-driver empty state.
- `scripts/Update.ps1` — when user wants the service-side BiosWmi
  refactor deployed to the running service.
- `src/RAMWatch/Services/SensorProvenance.cs` — adding Minimums /
  Settings glyphs if that work is prioritized next.
