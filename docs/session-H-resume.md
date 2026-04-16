# Session H Resume Prompt

**Date:** 2026-04-15
**Branch:** main @ `6413e6d`
**Tests:** 576 passing, zero build errors
**Remote:** pushed, up to date

## What happened this session

Two parallel work streams (two Claude sessions), then a War Council review and fixes.

### Stream 1 (this session): WHEA monitoring + GUI fixes + clipboard + system info
1. **WHEA monitoring expansion** (`51a7df0`) — MCA bank decode (McaBankClassifier), Event IDs 46/48, Kernel-WHEA + Kernel-PCI providers, LiveKernelReports scanner. 13 new tests.
2. **Tray icon + launch-at-logon + DIMM display** (`9af3831`) — ForceCreate(false) for H.NotifyIcon, Environment.ProcessPath for registry write, collapsible DIMM section on Timings tab.
3. **Clipboard export rewrite + SystemInfoReader** (`cbcb31b`) — Full timing/voltage/SI/DIMM export. New SystemInfoReader populates BiosVersion/AgesaVersion/CpuCodename from registry (fields existed but were empty).

### Stream 2 (other session): Hardware measurements
1. **Voltages, resistance, DIMMs** (`27e5477`) — VCore via SVI2, VDDP/VDDG_IOD/VDDG_CCD via SMU PM table, Vtt/Vpp/ProcODT/Rtt/drive strengths via BIOS WMI (new BiosWmiReader replacing VdimmReader), DIMMs via WMI.
2. **GUI wiring** (`4860a36`) — All new fields wired into Timings tab, Snapshots comparison, clipboard, digest, MD exports.

### War Council review + fixes (7 commits: `9d547f2`..`6413e6d`)
Full 7-reviewer War Council on all today's work. Found 3 critical, 14 warning. All addressed:
- **BiosWmiReader deadlocks** — stdout/WaitForExit ordering, stderr redirect removed, result cached (no more PowerShell every 30s)
- **SnapshotsEqual truncated field set** — was hiding SCL/turn-around changes in digest
- **PowerDown missing from GetTimingPair** — rendered as "?" in CURRENT.md/LKG.md
- **Boot time drift** — cached at construction instead of recomputing per state push
- **UX fixes** — SI grid empty rows removed, VSoc "—"→"N/A", autostart registry write feedback, tray statusItem conflict
- **Build** — NuGet versions pinned, IsCacheErrorCode simplified, stale comment, unused import
- **Performance** — LoadDimms/TimingDisplayGroups rebuild guards (skip when unchanged)
- **Security** — XmlDocument DTD prohibition explicit, WMI InstanceName escaping
- **Tests** — SystemInfoReader (AGESA/BIOS parsing), MCA bank boundaries, DimmReader \r\n. 576 total.

## Ready to implement next

### Wire thermal telemetry into reactive events + GUI (plan approved)

**UPDATE:** The other session (`1e866eb`) built the entire read infrastructure:
- `ThermalPowerSnapshot` model (Tctl, CCD temps, SoC temp, peak temp, power, PPT/TDC/EDC)
- `SmuDecode.PopulateThermalPower()` — direct SMN + PM table reads
- `HardwareReader.ReadThermalPower()` + StateAggregator + ServiceState wiring
- Already called on the 30s poll

**What remains for us:**
1. Reactive WHEA capture — call `ReadThermalPower()` in `OnEventDetected`, stamp onto MonitoredEvent (new `Vitals` field)
2. GUI display — consume `state.ThermalPower` in MainViewModel, show on Timings tab
3. Clipboard/digest — add thermal section
4. Event CSV — add vitals columns for hardware events

Full plan: `C:\Users\repro\.claude\plans\curious-finding-fountain.md`
Memory: `project_vitals_snapshot.md`

### Still on the shelf

- **Error detail view** (double-click → what/where/why) — filed in `project_whea_detail_view.md`
- **TimingSnapshot record refactor** — 75 fields, 6 hand-enumerated sites (Gandalf's top concern)
- **DDR4 hardcoded in 3 places** — needs parameterization for Zen 4/5
- **CSV logger missing fields** — pre-existing gap, several timing/SI fields not in CSV

## User hasn't run `scripts/Update.ps1` yet

All the tray icon, launch-at-logon, DIMM display, clipboard, and War Council fixes are committed and pushed but not installed on the other machine. First thing tomorrow: run Update.ps1 and verify the tray icon appears.
