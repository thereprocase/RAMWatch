# TimingSnapshot Field-Enumeration Refactor

**Status:** design accepted, helper landed, callers untouched.
**Author:** Gandalf (architecture review).
**Date:** 2026-04-16.
**Scope:** collapse nine hand-enumerated field lists down onto one Core-side helper.

## Problem

`TimingSnapshot` (defined in `src/RAMWatch.Core/Models/TuningJournal.cs:87`)
carries roughly seventy-five fields across clocks, primaries, secondaries,
turn-around timings, PHY, booleans, voltages, and signal integrity. Nine sites
walk subsets of those fields by hand. Every time the record grows, the
walks drift apart — either silently dropping the new field, or inconsistently
including it. Treebeard's triage:

| # | Site | Function | Field set today |
|---|---|---|---|
| 1 | `src/RAMWatch.Service/Services/CurrentMdBuilder.cs:170` | `AllTimingPairs` | timings + booleans, no PHY, no voltages, no SI |
| 2 | `src/RAMWatch.Service/Services/CurrentMdBuilder.cs:215` | `GetTimingPair` | timings + PHY + booleans |
| 3 | `src/RAMWatch.Service/Services/DigestBuilder.cs:307` | `AppendSnapshotDiff` | timings + booleans (no PHY, no voltages, no SI) |
| 4 | `src/RAMWatch.Service/Services/DigestBuilder.cs:490` | `SnapshotsEqual` | timings + booleans + clocks (no PHY, no voltages, no SI) |
| 5 | `src/RAMWatch.Service/Services/DriftDetector.cs:295` | `GetTimingValue` | clocks + timings + PHY + booleans-as-0/1 |
| 6 | `src/RAMWatch.Service/Services/MinimumComputer.cs:132` | `GetTimingValue` | timings only |
| 7 | `src/RAMWatch/ViewModels/MinimumsViewModel.cs:192` | `GetTimingValue` | duplicate of #6 |
| 8 | `src/RAMWatch/ViewModels/TimingsViewModel.cs:333` | `GetFieldValue` | timings + PHY + booleans (display formatting) |
| 9 | `src/RAMWatch.Service/Services/TimingCsvLogger.cs:95` | `FormatRow` | structural CSV row, frozen header |

Nine sites, four overlapping shapes. Two of them (#6 and #7) are literal
duplicates living in different assemblies.

## Field-set audit — deliberate vs accidental

Read every site before judging. Findings:

- **#3 `AppendSnapshotDiff` skipping PHY: accidental.** The function's intent
  is "what user-visible tuning fields changed between current and LKG"; PHY
  values legitimately drift from one boot to the next (training artifact)
  and a diff that hides them denies the user useful signal when something
  unusual is happening. Including them with a tiny "(training)" caveat in
  the digest output is the right behaviour. Refactor task: include PHY in
  the iteration; let the digest formatter decide whether to annotate.

- **#4 `SnapshotsEqual` skipping voltages and SI: deliberate but underspecified.**
  This function answers "did the user change tuning between snapshot a and
  snapshot b". SVI2-telemetry voltages (VCore, VSoc) wobble below the
  millivolt across reads and would make every comparison false if included
  raw. The right field set for "tuning equality" is **clocks + integer
  timings + PHY + booleans**. Today the implementation excludes PHY too;
  add it for the same reason as #3 — PHY shifting between snapshots is a
  real signal worth catching as "not the same configuration". The new
  `TimingSnapshotFields.TuningEqual` helper encodes this set.

  Voltage equality, if ever needed, deserves its own helper with explicit
  tolerance — do not bolt it onto `SnapshotsEqual`.

- **#6/#7 `MinimumComputer.GetTimingValue` skipping booleans: deliberate
  and correct.** Booleans don't have a "minimum"; PHY excluded for the
  same reason (mismatch between channels is normal, an A/B comparison of
  PHY readings across boots is meaningless). The `MinimumComputer` keeps
  its own `TimingFields` allowlist; the dispatch helper `GetTimingValue`
  just becomes `TimingSnapshotFields.GetIntField` and silently returns
  null if the caller asks for a field outside the allowlist.

- **#5 `DriftDetector.GetTimingValue` including booleans-as-0/1: deliberate.**
  Drift wants to know "did this field move across boots", which is a
  yes/no for booleans. The 0/1 projection is exactly what `GetIntField`
  does for `GDM`/`Cmd2T`/`PowerDown`, so this site collapses cleanly.

- **#9 `TimingCsvLogger.FormatRow`: keep as-is.** Frozen header order, hot
  path, allocation-sensitive (uses a thread-static `StringBuilder`).
  Refactoring this site against the helper would either break header
  stability (existing CSVs depend on column order being eternal) or
  reintroduce per-call allocations through tuple iteration. Add a code
  comment pointing to the helper as the canonical field list, and add a
  test (see "lock-in test" below) that fails if the helper grows a field
  the CSV row doesn't.

## Chosen API and why

Four shapes were on the table:

1. **Single boxed enumerator** — one `IEnumerable<(string, object)>`, every
   caller filters. Simple, but boxes every int/double/bool on every walk
   and forces consumers to type-test. Rejected: the digest builder runs
   on every state push; the minimums computer iterates per-snapshot
   per-field. Boxing on a hot path for the sake of one-line callers is a
   bad trade.
2. **Reflection-based dispatch** — read fields via `PropertyInfo`. Killed
   on sight: service publishes with `PublishAot=true`, reflection over
   record properties trips trim warnings at minimum and crashes at runtime
   at worst. Forbidden by the architecture (CLAUDE.md, "AOT-safe" rule).
3. **Source-generator with `[FieldGroup]` attributes** — emit the helpers
   at build time. Cleanest caller code in theory, but adds a roslyn
   project to the repo for nine call sites. The build complexity buys
   nothing the typed-array form doesn't already provide. Rejected as
   premature.
4. **Typed category arrays of `(string, Func<TimingSnapshot, T>)` tuples**
   — chosen. Each category is a `static readonly` array built once at
   type init. Selectors are `static` lambdas (no closure capture, JIT
   inlines them on full runtime; AOT compiles them as direct calls).
   Iteration is `foreach` over the array — no allocation, no boxing.
   Adding a field is one line in one array. Callers pick the categories
   they care about and compose.

The combined name→int dispatch (`GetIntField`) lives next to the arrays
because it's what `MinimumComputer`, `DriftDetector`, and the duplicated
ViewModel helper all want, and a `Dictionary<string, Func<...>>` lookup
would defeat the "no allocation per call" property when the call site
wants raw int return.

## Where it lives

`src/RAMWatch.Core/TimingSnapshotFields.cs` — sibling static class to
`SnapshotDisplayName.cs` (which sets the precedent: lightweight Core-side
helpers that both Service and GUI consume sit at the Core root, not
inside `Models/`). The record itself stays clean — no new methods on
`TimingSnapshot`, so the on-disk JSON shape is untouched.

Cross-assembly: both `RAMWatch.Service` and `RAMWatch` already reference
`RAMWatch.Core`, so no new project edges. The two duplicated
`GetTimingValue` helpers (#6 and #7) collapse onto the single Core-side
`GetIntField`.

## Migration order

Land in this order. Each step is independently committable and reversible.

1. **Lock-in test first.** Before any caller changes — add a test in
   `src/RAMWatch.Tests` that asserts the size and contents of every
   `TimingSnapshotFields.*` array against an expected list. If a future
   commit drops a field from a category, the test fails loudly. Same
   test should assert that `TimingSnapshotFields.GetIntField` returns
   non-null for every name appearing in `Clocks ∪ Timings ∪ Phy ∪
   Booleans` and null for everything else. This is the regression net
   for the refactor itself.

2. **MinimumComputer + MinimumsViewModel** (#6, #7) — the two duplicates
   collapse onto `GetIntField`. Lowest risk: pure dispatch replacement,
   no semantic change. Confirms the helper works in both assemblies.

3. **DriftDetector `GetTimingValue`** (#5) — same pattern as step 2,
   one-for-one substitution. The 0/1 projection for booleans is built
   into `GetIntField` so behaviour is identical. The neighbouring
   dictionary builder at `DriftDetector.cs:240` is a separate concern;
   keep it as the test vector for "does the int dispatch return what
   the dictionary builder says it should".

4. **DigestBuilder `SnapshotsEqual`** (#4) — replace with
   `TimingSnapshotFields.TuningEqual`. **Behaviour change:** PHY now
   participates in equality. This is intentional (see audit above) and
   needs a corresponding test update. Land separately from step 3 so
   the commit message can call out the semantic shift.

5. **DigestBuilder `AppendSnapshotDiff`** (#3) — iterate `Clocks`,
   `Timings`, `Phy`, `Booleans` arrays, formatting each tuple with the
   existing `Check`/`CheckBool` helpers. **Behaviour change:** PHY
   diffs now appear in the digest. Same separate-commit discipline.

6. **CurrentMdBuilder `AllTimingPairs` and `GetTimingPair`** (#1, #2)
   — replace the yield-return cascade and the switch with composed
   iteration over `Timings` and `Booleans`. Display formatting (the
   "On"/"Off", "2T"/"1T" strings) stays at the call site since the
   helper deliberately returns raw values.

7. **TimingsViewModel `GetFieldValue`** (#8) — same as step 6, plus
   the RFC nanosecond conversion stays GUI-side (it's display logic,
   not field enumeration).

8. **TimingCsvLogger** (#9) — *do not* refactor. Add a code comment at
   the top of `FormatRow` pointing to `TimingSnapshotFields` as the
   field-set source of truth, and add the lock-in test (step 1) that
   asserts the CSV header column count equals the sum of the helper
   arrays plus the structural prefix (timestamp, bootId).

## Lock-in test recommendation

Single test class, `TimingSnapshotFieldsTests`, in `src/RAMWatch.Tests`.
Three assertions, all of which fail loudly if someone adds a field to
the record without touching the helper:

```csharp
// 1. Field-count regression — golden numbers, update intentionally.
Assert.Equal(3,  TimingSnapshotFields.Clocks.Length);
Assert.Equal(32, TimingSnapshotFields.Timings.Length);
Assert.Equal(2,  TimingSnapshotFields.Phy.Length);
Assert.Equal(3,  TimingSnapshotFields.Booleans.Length);
Assert.Equal(8,  TimingSnapshotFields.Voltages.Length);

// 2. Round-trip — every named int field returned by GetIntField
//    matches the tuple selector value. Catches name typos.
var probe = MakeProbeSnapshot(); // distinct sentinel value per field
foreach (var (name, get) in TimingSnapshotFields.Clocks
                              .Concat(TimingSnapshotFields.Timings)
                              .Concat(TimingSnapshotFields.Phy))
    Assert.Equal(get(probe), TimingSnapshotFields.GetIntField(probe, name));

// 3. CSV row column count — structural test against TimingCsvLogger.
//    Asserts FormatRow's column count equals the sum of helper arrays
//    plus 2 (timestamp, bootId). Fires the moment someone adds a field
//    to TimingSnapshot and updates the helper but forgets the CSV.
var row = TimingCsvLogger.FormatRow(probe);
int columns = row.Count(c => c == ',') + 1;
int expected = 2 // timestamp, bootId
             + TimingSnapshotFields.Clocks.Length
             + TimingSnapshotFields.Timings.Length
             + TimingSnapshotFields.Phy.Length
             + TimingSnapshotFields.Booleans.Length
             + TimingSnapshotFields.Voltages.Length
             + TimingSnapshotFields.SignalIntegrityNumeric.Length
             + TimingSnapshotFields.SignalIntegrityStrings.Length;
Assert.Equal(expected, columns);
```

The third assertion is the most valuable: it ties the un-refactored CSV
to the helper-driven world, so the trap door under "add a field, forget
the CSV header" snaps shut.

## What we are not doing

- **No reflection.** Repeated for emphasis: AOT publishes the service.
- **No new methods on `TimingSnapshot` itself.** Keeps the record a
  pure data carrier and keeps the JSON source generator output stable.
- **No source generator.** Nine sites is too few to justify the build
  complexity. Revisit if the count grows past twenty or if the field
  list begins to vary by CPU generation (Zen 4 / Zen 5 may add new
  timings — at that point a generator with a `[ZenGen(>=4)]` attribute
  on each field starts to earn its keep).
- **No touching `TimingCsvLogger.FormatRow`.** The CSV header order is
  load-bearing for users who already have logs. Pin the field list with
  the lock-in test, leave the row-formatter alone.

## Files changed by this design phase

- New: `src/RAMWatch.Core/TimingSnapshotFields.cs` (this design's helper).
- New: `docs/timing-snapshot-refactor.md` (this memo).
- Untouched: all nine caller sites. Ents will execute the migration in
  the order above, one commit per step.
