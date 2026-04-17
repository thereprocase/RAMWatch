# Session Report — 2026-04-17

META: date=2026-04-17
META: branch=main
META: base_sha=af7750d
META: head_sha=8fc7fbe
META: commits=26
META: tests_before=625
META: tests_after=636
META: tests_added=11
META: regressions=0
META: waves=3

## Summary

Triggered by a passing observation ("lazy logging is a bummer") that turned into a
War-Council-scale audit: first a Uruk-Hai sweep for drift-detector-shaped bugs, then
three adversarial Sauron-Opus agents across concurrency, hardware decode, and
IPC/persistence. 19 tier-triaged findings, 18 commits, 636 tests passing. A fresh
Sauron trio (regression / WPF / resource exhaustion) is running in the background.

## How the Session Unfolded

ORIGIN: User asked where the current boot log was. Empty `events_YYYY-MM-DD.csv`
before the first error meant "clean boot" and "dead service" looked identical.
Added lifecycle events. That seeded the question "are we building correctly?",
which opened the drift-detector bug. One finding led to the systematic hunt.

DISCOVERY: Drift window on disk (`C:\ProgramData\RAMWatch\drift_window.json`)
held 20 copies of `boot_000039`. Rolling "last 20 boots" baseline completely
self-overwritten within minutes.

TRIAGE: 3 Uruk-Hai in parallel on adjacent patterns → 1 real bug (EventLog
RecordId dedup). 3 Sauron-Opus in parallel across broader surface → 17 more
findings triaged into Tier 1/2/3.

EXECUTION: All 19 items walked sequentially under TaskCreate tracking. Each fix:
read context, edit, build, test, commit. 17 of 19 produced code changes; 2
(T2f HardwareReader lock, T3f DACL scope) verified already-correct or deferred
with documented rationale.

## Commits (reverse chronological)

COMMIT: 0e38493 docs(ipc) clarify pipe DACL scope for multi-session hosts
COMMIT: d2e06fd fix(git)  Guid-suffix WriteAtomic + cap CHANGELOG.md read size
COMMIT: f88842f fix(persistence) archive corrupt JSON before recovery to defaults
COMMIT: 43ecb8a fix(settings) resolve reparse points in IsValidDataPath
COMMIT: f9c0019 fix(hardware) guard Bits() width-32 case and PM-table stale-read fallthrough
COMMIT: 5aa342d fix(ipc) validate BootFailKind against Enum.IsDefined before persisting
COMMIT: 1b105dc fix(ipc) rate-limit RequestTimingRefresh to 1 Hz
COMMIT: 851b01a fix(settings) clamp numerics on load and on patch
COMMIT: 5d0dd6e fix(validation) sanitize MetricName and MetricUnit at the trust boundary
COMMIT: 258cab0 fix(hardware) surface tRFC readback-bug fallback to the snapshot
COMMIT: 048f4dd fix(settings) merge by JSON presence instead of wholesale replace
COMMIT: 917d050 fix(service) shutdown barrier on EventLog callback path
COMMIT: 76cce9d fix(hardware) treat SVI2 VID=0 as sentinel, not 1.55V
COMMIT: af53d02 fix(hardware) abort UMC decode on any per-register SMN read failure
COMMIT: 56408b3 fix(hardware) bound BiosWmi PowerShell invocation with wall-clock timeout
COMMIT: 03a9b51 fix(eventlog) dedup by (LogName, RecordId) to guard against re-delivery
COMMIT: b5ecd1a fix(drift) dedup window by BootId and guard against incomplete reads
COMMIT: e97422e feat(service) emit lifecycle events on startup and shutdown

## Findings — Tier 1 (correctness with user-visible impact)

FINDING: DRIFT-WINDOW-DUPES
TIER: 1
STATUS: fixed
SHA: b5ecd1a
FILE: src/RAMWatch.Service/Services/DriftDetector.cs
SYMPTOM: 20-slot rolling "last 20 boots" window filled with 20 copies of current boot within minutes
ROOT_CAUSE: warm-tier tick called AppendToWindow every 30-60s without dedup by BootId
IMPACT: drift detection silenced; historical baselines evicted
FIX: upsert by BootId + early-return on FCLK/UCLK=0
TEST_ADDED: RepeatedSameBootId_WindowDoesNotInflate, IncompleteRead_FclkZero_NoWindowPollution

FINDING: EVENTLOG-RECORDID-DUPES
TIER: 1
STATUS: fixed
SHA: 03a9b51
FILE: src/RAMWatch.Service/Services/EventLogMonitor.cs
SYMPTOM: Windows event re-delivery double-counted ErrorSource.Count and duplicated _recentEvents
ROOT_CAUSE: no dedup key on RecordEvent; historical/live handoff and watcher reconnect could re-deliver
IMPACT: inflated error counters on Monitor tab; duplicate rows in recent events
FIX: HashSet<(LogName, RecordId)> with 10_000 cap, clear-on-overflow
TEST_ADDED: DuplicateRecordId_IsDedupedWithinSameLog, SameRecordId_DifferentLogs_BothCounted

FINDING: BIOSWMI-DEADLOCK
TIER: 1
STATUS: fixed
SHA: 56408b3
FILE: src/RAMWatch.Service/Hardware/BiosWmiReader.cs
SYMPTOM: blocking ReadToEnd() before WaitForExit(5000); if WMI hung, ReadToEnd blocked forever
ROOT_CAUSE: WaitForExit unreachable after ReadToEnd blocked on unclosed stdout
IMPACT: HardwareReader deadlocked under _driverLock → hot-tier thermal loop frozen → service effectively dead
FIX: read via Task.Run + 10s wall-clock timeout; Process.Kill on timeout (closes stdout, unblocks reader)

FINDING: UMC-SILENT-ZERO
TIER: 1
STATUS: fixed
SHA: af53d02
FILE: src/RAMWatch.Service/Hardware/UmcDecode.cs
SYMPTOM: ReadSmn silently returned 0 on TryReadSmn failure; partial-register failures leaked zeros into snapshot
ROOT_CAUSE: no failure propagation from per-register reads; only CL/RAS zero check upstream
IMPACT: polluted snapshot journal + drift window with mix of real and zero timings
FIX: per-call _readFailed flag set by ReadSmn; ReadTimings and ReadAddressMap return null on any failure
TEST_ADDED: ReadTimings_ReturnsNull_WhenAnySmnReadFails, ReadAddressMap_ReturnsNull_WhenAnySmnReadFails

FINDING: BGS-ENABLED-FALSE-POSITIVE
TIER: 1
STATUS: fixed
SHA: af53d02
FILE: src/RAMWatch.Service/Hardware/UmcDecode.cs
SYMPTOM: BgsEnabled=true reported when SMN reads failed (0 != 0x87654321 sentinel)
ROOT_CAUSE: comparison against disabled-sentinel treated failure as "not disabled"
IMPACT: user tuning address map saw BGS=on when driver couldn't read the register
FIX: co-fixed by UMC-SILENT-ZERO — whole address map returns null when any read fails

FINDING: SVI2-VID-ZERO-SENTINEL
TIER: 1
STATUS: fixed
SHA: 76cce9d
FILE: src/RAMWatch.Service/Hardware/SmuDecode.cs
SYMPTOM: SVI2 VID=0 decoded to 1.55V via VidToVoltage formula, passed plausibility window
ROOT_CAUSE: all-zero read not distinguishable from a live VID=0 (which is non-physical at idle)
IMPACT: transient all-zero read reported as alarm-level 1.55V VSoc/VCore
FIX: reject vid == 0 before VidToVoltage conversion

FINDING: SHUTDOWN-RACE
TIER: 1
STATUS: fixed
SHA: 917d050
FILE: src/RAMWatch.Service/RamWatchService.cs
SYMPTOM: EventLogWatcher callbacks could fire on disposed _csvLogger/_hardwareReader/_pipeServer
ROOT_CAUSE: StopAsync disposed downstream services while callback-thread events were in flight
IMPACT: ObjectDisposedException in callback thread would escape and could crash the service
FIX: volatile _shuttingDown flag, unsubscribe EventDetected before dispose, 50ms drain window, try/catch in OnEventDetected, ContinueWith on fire-and-forget broadcast

FINDING: SETTINGS-WHOLESALE-REPLACE
TIER: 1
STATUS: fixed
SHA: 048f4dd
FILE: src/RAMWatch.Service/Services/SettingsManager.cs
SYMPTOM: partial updateSettings payload wiped every absent field to JSON defaults
ROOT_CAUSE: System.Text.Json typed deserialize can't distinguish field-absent from field-default
IMPACT: client forgetting one field silently reset GitRemoteRepo, MirrorDirectory, retention, notifications
FIX: ApplyPatch(JsonElement) merges only present fields; handler re-parses raw line as JsonDocument; unknown keys ignored (forward compat); bad value for known key skipped not thrown
TEST_ADDED: ApplyPatch_PartialPayload_PreservesUnsetFields, ApplyPatch_UnknownKey_Ignored, ApplyPatch_BadValueForKnownField_KeepsCurrent

FINDING: SERVICE-LIFECYCLE-SILENT
TIER: 1
STATUS: fixed
SHA: e97422e
FILE: src/RAMWatch.Service/RamWatchService.cs
SYMPTOM: empty events_YYYY-MM-DD.csv was ambiguous — clean boot vs dead service looked identical
ROOT_CAUSE: events CSV created lazily on first write; no marker from service itself
IMPACT: diagnostic opacity after service restart or on clean boot
FIX: EmitLifecycleEvent helper routes through normal event path (CSV + mirror + broadcast); Info event on startup (pipe/eventlog/hardware/board/BIOS/AGESA) and shutdown (uptime)

## Findings — Tier 2 (real, lower urgency)

FINDING: TRFC-READBACK-INVISIBLE
TIER: 2
STATUS: fixed
SHA: 258cab0
FILE: src/RAMWatch.Service/Hardware/UmcDecode.cs + src/RAMWatch.Core/Models/TuningJournal.cs
SYMPTOM: fallback from 0x50260 to 0x50264 for ComboAM4v2PI 1.2.0.x tRFC bug not surfaced to UI
IMPACT: user on buggy AGESA saw workaround-register values without knowing
FIX: TrfcReadbackBugDetected bool on TimingSnapshot, set when fallback triggered
FOLLOWUP: UI wiring (banner/warning label) — data reaches every state push, UI consumption deferred

FINDING: METRIC-NEWLINE-INJECTION
TIER: 2
STATUS: fixed
SHA: 5d0dd6e
FILE: src/RAMWatch.Service/RamWatchService.cs + src/RAMWatch.Service/Services/CommitMessageBuilder.cs
SYMPTOM: LogValidationMessage MetricName/MetricUnit flowed unsanitized into snapshot label + git commit + CHANGELOG.md
ROOT_CAUSE: sanitize helper was private to CommitMessageBuilder; handler bypassed
IMPACT: client could inject fake CHANGELOG H2 sections; TrimChangelog miscounted and truncated real history; unbounded MetricUnit grew CHANGELOG arbitrarily
FIX: promote Sanitize to internal; apply in HandleLogValidationAsync (TestTool/MetricName/MetricUnit/label) before any downstream use

FINDING: SETTINGS-NUMERIC-UNCLAMPED
TIER: 2
STATUS: fixed
SHA: 851b01a
FILE: src/RAMWatch.Core/Models/AppSettings.cs + src/RAMWatch.Service/Services/SettingsManager.cs
SYMPTOM: only RefreshIntervalSeconds clamped; LogRetentionDays/MaxLogSizeMb/NotifyCooldownSeconds accepted any int
IMPACT: LogRetentionDays=0 deleted every CSV on next startup; MaxLogSizeMb=0 evicted everything; negative values overflowed AddDays
FIX: AppSettings.ClampNumerics() applied in Load and ApplyPatch. Ranges: Refresh 5-3600s, Retention 1-3650d, Size 1-10000MB, Cooldown 0-86400s
TEST_ADDED: ApplyPatch_ClampsNumerics_ToSaneRanges, Load_CorruptNumerics_AreClamped

FINDING: REFRESH-DOS
TIER: 2
STATUS: fixed
SHA: 1b105dc
FILE: src/RAMWatch.Service/RamWatchService.cs
SYMPTOM: RequestTimingRefreshMessage had no rate limit
IMPACT: wire-speed client loop pinned hardware reader lock; starved warm/hot loops; flooded all clients with state broadcasts
FIX: Interlocked long tracks last-accepted tick; reject with rate_limited if < 1s elapsed

FINDING: BOOTFAIL-ENUM-UNVALIDATED
TIER: 2
STATUS: fixed
SHA: 5aa342d
FILE: src/RAMWatch.Service/RamWatchService.cs
SYMPTOM: LogBootFailMessage.Kind deserialized as int without Enum.IsDefined check
IMPACT: client could persist Kind=99; any future branch on value hit undefined enum
FIX: Enum.IsDefined guard in HandleLogBootFailAsync

FINDING: IOCTL-SERIALIZATION
TIER: 2
STATUS: clean
FILE: src/RAMWatch.Service/Hardware/HardwareReader.cs
VERIFIED: all four public read methods (ReadTimings, ReadHotTier, ReadThermalPower, ReadAddressMap) already acquire _driverLock. No change needed.

## Findings — Tier 3 (hygiene / latent)

FINDING: BITS-WIDTH-32
TIER: 3
STATUS: fixed
SHA: f9c0019
FILE: src/RAMWatch.Service/Hardware/UmcDecode.cs
SYMPTOM: Bits(value, 31, 0) computed 1u << 32; C# masks shift count to 5 bits → mask=0 → always zero
IMPACT: latent; no current caller uses full-width; silent decode bug for first adopter
FIX: early-return 0xFFFFFFFFu when width >= 32

FINDING: PM-TABLE-STALE-FALLTHROUGH
TIER: 3
STATUS: fixed
SHA: f9c0019
FILE: src/RAMWatch.Service/Hardware/SmuPowerTableReader.cs
SYMPTOM: on ioctl_update_pm_table failure, code fell through and read stale DRAM-mapped table
IMPACT: pre-change thermals passed off as current during tuning decisions
FIX: return null on update failure; skip the stale read

FINDING: REPARSE-POINT-BYPASS
TIER: 3
STATUS: fixed
SHA: 43ecb8a
FILE: src/RAMWatch.Core/Models/AppSettings.cs
SYMPTOM: junction/symlink targeting UNC share or system dir bypassed prefix checks
ROOT_CAUSE: Path.GetFullPath does not resolve reparse points
IMPACT: theoretical escalation once attacker has any filesystem foothold (pre-staged junction)
FIX: DirectoryInfo.ResolveLinkTarget(returnFinalTarget: true); refuse if resolution fails

FINDING: CORRUPT-JSON-SILENT-RESET
TIER: 3
STATUS: fixed
SHA: f88842f
FILES: 10 Load paths across SettingsManager/SnapshotJournal/ValidationTestLogger/LkgTracker/BootBaselineJournal/BootFailJournal/EraJournal/ConfigChangeDetector(x2)/DriftDetector/LoadDesignations
SYMPTOM: corrupt JSON silently replaced with defaults; user lost entire journal with no trace
IMPACT: hand-edit mistake or power-loss truncation destroyed history
FIX: DataDirectory.ArchiveCorruptFile(path) renames to <name>.corrupt.<yyyyMMddHHmmss><ext>; best effort

FINDING: GIT-TMP-COLLISION
TIER: 3
STATUS: fixed
SHA: d2e06fd
FILE: src/RAMWatch.Service/Services/GitCommitter.cs
SYMPTOM: WriteAtomic used fixed ".tmp" suffix; concurrent callers for same path race on File.Move
IMPACT: latent; single-reader drain loop today but future parallel caller would lose one write
FIX: Guid suffix

FINDING: CHANGELOG-UNBOUNDED-READ
TIER: 3
STATUS: fixed
SHA: d2e06fd
FILE: src/RAMWatch.Service/Services/GitCommitter.cs
SYMPTOM: UpdateChangelog called File.ReadAllText on CHANGELOG.md with no size cap
IMPACT: AOT service on constrained box could OOM on imported/tampered CHANGELOG
FIX: FileInfo.Length check; cap at 16 MiB; truncate tail (TrimChangelog enforces entry cap anyway)

FINDING: DACL-INTERACTIVE-SCOPE
TIER: 3
STATUS: deferred_documented
SHA: 0e38493
FILE: src/RAMWatch.Core/Ipc/PipeServer.cs
SYMPTOM: InteractiveSid grants all interactive sessions (console + RDP + FUS), not just console user
IMPACT: multi-session host exposure; single-user enthusiast box (target audience) effectively unaffected
DEFERRED_BECAUSE: narrowing requires install-time SID capture or runtime WTS APIs; not justified for target audience; documented inline

## Deferred / Not Fixed (with rationale)

DEFERRED: ConfigChangeDetector SaveSnapshot on every tick (disk churn not correctness; ClockToleranceMhz=5 absorbs jitter)
DEFERRED: SystemInfoReader cache staleness on BIOS flash (requires reboot in practice)
DEFERRED: BootBaselineJournal crash-window between dedup check and atomic write (too narrow)
DEFERRED: UI wiring for TrfcReadbackBugDetected flag (data now available; UI is follow-up)
DEFERRED: AGESA version parser for register-map dispatch (tRFC bug is magic-compare specific; no other known AGESA-gated decode; full parser is larger scope)
DEFERRED: retention re-run on long-running sessions (noted; runs only at startup)

## Test Delta

TEST_COUNT: before=625 after=636 added=11
TEST_FILE: src/RAMWatch.Tests/DriftDetectorTests.cs +3
TEST_FILE: src/RAMWatch.Tests/EventLogMonitorRateLimiterTests.cs +2
TEST_FILE: src/RAMWatch.Tests/UmcDecodeTests.cs +2
TEST_FILE: src/RAMWatch.Tests/SettingsTests.cs +5 (3 ApplyPatch + 2 clamping)
REGRESSIONS: 0

## Agent Activity

AGENT_WAVE_1: Uruk-Hai x3 (Haiku) — event-stream / per-boot / persistence
AGENT_WAVE_1_FINDINGS: 1 real bug (EventLogMonitor RecordId dedup); 2 reports reframed as not-a-bug on my review

AGENT_WAVE_2: Sauron x3 (Opus) — concurrency / hardware-decode / IPC-persistence
AGENT_WAVE_2_FINDINGS: 7 critical + 17 high/warning + 7 note; 17 actioned as Tier 1/2/3

AGENT_WAVE_3: Sauron x3 (Opus) — regression / WPF / resource-exhaustion
AGENT_WAVE_3_FINDINGS: 1 GUI critical + 2 exhaustion critical + 3 regression + 6 WPF warnings + various notes; 16 actioned across 8 commits

## Wave 3 Commits

COMMIT: 4aa9661 fix(regression) address Sauron regression audit findings (R1-R4)
COMMIT: b347427 fix(gui) preserve unsurfaced AppSettings fields through ToSettings round-trip (W1 — invalidated T1f end-to-end; critical)
COMMIT: 8595f1e fix(config-change) cap _changes list and skip per-tick save on no-delta (E1 + E6)
COMMIT: fe91e2b fix(hardware) ExecuteInto overload restores PM table pre-allocation (E2 — ~120MB/day Gen0 saved)
COMMIT: 6097c98 fix(exhaustion) bound long-running growth vectors (E3 + E4 + E5)
COMMIT: 1367d85 fix(gui) designation staleness, async void crash, CTS race, O(n) event trim (W2+W3+W4+W6)
COMMIT: f3f6191 docs enumerate and categorize all hardware data sources
COMMIT: 8fc7fbe fix(gui) RequestId-keyed digest waiters for concurrent CopyDigest (W5)

## Wave 3 Findings

FINDING: R1-TRFC-DOUBLE-MAGIC
TIER: 2-regression
STATUS: fixed
SHA: 4aa9661
FILE: src/RAMWatch.Service/Hardware/UmcDecode.cs
SYMPTOM: when BOTH reg260 and reg264 return TrfcBugValue, fallback decoded the magic as tRFC (312/48/8 garbage) and flag stayed false
IMPACT: T2a claim of "fallback surfaced" not true on double-magic edge
FIX: always set flag on reg260==sentinel; when reg264 also matches, zero RFC fields and return

FINDING: R2-BIOSWMI-READTASK-UNOBSERVED
TIER: 1-regression
STATUS: fixed
SHA: 4aa9661
FILE: src/RAMWatch.Service/Hardware/BiosWmiReader.cs
SYMPTOM: Kill-then-Dispose left readTask with unobserved ObjectDisposedException
FIX: catch inside worker lambda; Wait(2s) after Kill before finally-dispose

FINDING: R3-SAVE-ORDER
TIER: 1-regression
STATUS: fixed
SHA: 4aa9661
FILE: src/RAMWatch.Service/Services/SettingsManager.cs
SYMPTOM: _current = settings before disk write; memory/disk diverged on write failure
FIX: assign _current only after File.Move succeeds

FINDING: R4-ENUM-ISDEFINED-AOT
TIER: 3-regression
STATUS: fixed
SHA: 4aa9661
FILE: src/RAMWatch.Service/RamWatchService.cs
SYMPTOM: reflection-based Enum.IsDefined(Type, object) works but triggers ILLink warnings under AOT
FIX: switch to generic Enum.IsDefined<BootFailKind>(msg.Kind)

FINDING: W1-GUI-SETTINGS-OMISSIONS
TIER: 1-critical
STATUS: fixed
SHA: b347427
FILE: src/RAMWatch/ViewModels/SettingsViewModel.cs
SYMPTOM: ToSettings omits SchemaVersion/LogDirectory/DebugLogging/EnableGitIntegration/EnableGitPush/GitRemoteRepo/GitUserDisplayName. System.Text.Json emits as defaults. Service ApplyPatch merges them as explicit sets.
IMPACT: every GUI auto-save silently wiped git integration + MirrorDirectory + LogDirectory. T1f fix existed but was nullified end-to-end by this GUI gap.
FIX: _lastLoadedSettings preserves full payload; ToSettings clones it and overrides only GUI-managed fields

FINDING: E1-CONFIG-CHANGES-UNBOUNDED
TIER: 1-critical
STATUS: fixed
SHA: 8595f1e
FILE: src/RAMWatch.Service/Services/ConfigChangeDetector.cs
SYMPTOM: _changes list had no cap; every detected change appended and persisted forever
IMPACT: heavily-tuned system could accumulate multi-MB changes.json; whole file rewritten on every atomic save
FIX: MaxChanges=500, trim on append AND on load

FINDING: E2-PMTABLE-BUFFER-REGRESSION
TIER: 1-critical
STATUS: fixed
SHA: fe91e2b
FILE: src/RAMWatch.Service/Hardware/SmuPowerTableReader.cs + src/RAMWatch.Service/Hardware/PawnIo/PawnIoDriver.cs
SYMPTOM: _rawBuf[..(int)qwordCount] Range-indexer allocated new input array per call; Execute allocated new output array per call. Defeated pre-allocation.
IMPACT: ~120MB/day Gen0 allocation churn at 3s hot-tier cadence on a feature written to eliminate it
FIX: PawnIoDriver.ExecuteInto overload writes into caller-supplied output buffer; zero allocations per hot-tier tick

FINDING: E3-RETENTION-STARTUP-ONLY
TIER: 2
STATUS: fixed
SHA: 6097c98
FILE: src/RAMWatch.Service/Services/CsvLogger.cs
SYMPTOM: RunRetention ran once at service startup; long-running sessions (30+ days) accumulated past MaxLogSizeMb
FIX: RotateFile invokes RunRetention on every date change

FINDING: E4-MIRROR-NO-BACKPRESSURE
TIER: 2
STATUS: fixed
SHA: 6097c98
FILE: src/RAMWatch.Service/Services/MirrorLogger.cs
SYMPTOM: EnqueueCopy spawned unbounded Tasks with FileStreams; slow mirror + event storm = hundreds of concurrent handles
FIX: SemaphoreSlim(4) slot cap; drop copy when full (CSV is locally safe, mirror catches up)

FINDING: E5-GIT-CHANNEL-UNBOUNDED
TIER: 2
STATUS: fixed
SHA: 6097c98
FILE: src/RAMWatch.Service/Services/GitCommitter.cs
SYMPTOM: Channel.CreateUnbounded held every queued GitCommitRequest + full TimingSnapshot + designation map + validation list
FIX: BoundedChannel(1000) with DropOldest — latest state wins, bounded memory

FINDING: E6-CONFIG-PER-TICK-SAVE
TIER: 2
STATUS: fixed
SHA: 8595f1e
FILE: src/RAMWatch.Service/Services/ConfigChangeDetector.cs
SYMPTOM: SaveSnapshot(current) ran every warm-tier tick (~2880/day) even on no-delta
IMPACT: pure NTFS/SSD churn, no state change
FIX: move SaveSnapshot inside real-change branch; first-boot anchor still persisted

FINDING: W2-DESIGNATION-STALE
TIER: 2
STATUS: fixed
SHA: 1367d85
FILE: src/RAMWatch/ViewModels/TimingsViewModel.cs
SYMPTOM: _lastTimingKey cache skipped rebuild on unchanged timings; Manual/Auto toggle didn't invalidate
IMPACT: ● indicator stayed stale after designation change until unrelated rebuild
FIX: fold order-independent designation map fingerprint into cache key

FINDING: W3-ASYNC-VOID-CRASH
TIER: 2
STATUS: fixed
SHA: 1367d85
FILE: src/RAMWatch/MainWindow.xaml.cs
SYMPTOM: OnLoaded async void; any exception escaping inner catches terminated tray-resident process silently
FIX: wrap entire OnLoaded body in try/catch Exception; log; never rethrow

FINDING: W4-CTS-DISPOSE-RACE
TIER: 2
STATUS: fixed
SHA: 1367d85
FILE: src/RAMWatch/ViewModels/SettingsViewModel.cs
SYMPTOM: ScheduleAutoSave disposed previous CTS while its captured token was mid-Task.Delay; ObjectDisposedException instead of OperationCanceledException
FIX: swap CTS reference first; cancel; defer Dispose to worker's finally; catch ODE alongside OCE

FINDING: W5-DIGEST-SINGLE-SLOT-RACE
TIER: 2
STATUS: fixed
SHA: 8fc7fbe
FILE: src/RAMWatch/ViewModels/MainViewModel.cs
SYMPTOM: _pendingDigestRequestId single-slot + shared _lastDigestText; two rapid Copy Digest invocations collided
FIX: Dictionary<RequestId, TaskCompletionSource<string>> per-request waiters; await with WaitAsync cancellation

FINDING: W6-EVENT-LIST-ONE
TIER: 2
STATUS: fixed
SHA: 1367d85
FILE: src/RAMWatch/ViewModels/MainViewModel.cs
SYMPTOM: _eventsBySource used List with RemoveRange(0, ...); every append at cap shifted 49 entries under lock on UI-bound hot path during WHEA storm
FIX: Queue<MonitoredEvent> with Dequeue on overflow — O(1) append + trim

## File Churn

TOP_MODIFIED: src/RAMWatch.Service/RamWatchService.cs (6 commits touched)
TOP_MODIFIED: src/RAMWatch.Service/Services/SettingsManager.cs (3 commits)
TOP_MODIFIED: src/RAMWatch.Service/Hardware/UmcDecode.cs (3 commits)
TOP_MODIFIED: src/RAMWatch.Service/Services/GitCommitter.cs (1 commit, 2 fixes)
NEW_PUBLIC_SURFACE: AppSettings.ClampNumerics, SettingsManager.ApplyPatch, DataDirectory.ArchiveCorruptFile, TimingSnapshot.TrfcReadbackBugDetected, CommitMessageBuilder.Sanitize (visibility bump)

## Build and Run State

BUILD: clean (RAMWatch.sln, Debug/Release both compile)
TEST_FULL: 636 passing
SERVICE_RUNNING: yes (boot_000039 — pre-session service, fixes NOT yet deployed to running instance)
DEPLOY_STATE: all 18 commits local on main; not pushed; not hot-swapped via Update.ps1

## Next Session Prep

NEXT: await Agent Wave 3 completion (regression / WPF / exhaustion audits)
NEXT: decide deploy cadence — service restart via scripts/Update.ps1 (admin) picks up all fixes
NEXT: consider reset of polluted C:\ProgramData\RAMWatch\drift_window.json on deploy (currently 20 copies of boot_000039; self-heals over 20 future boots otherwise)
NEXT: UI wiring for TrfcReadbackBugDetected
NEXT: triage Wave 3 findings into the same Tier 1/2/3 model
