# RAMWatch Session Report — 2026-04-14

## What Was Accomplished

One session. Architecture docs → working hardware reads on a live AMD 5800X3D.

### Phase 1: Service + IPC + GUI (Complete)
- Windows service with 12 event log sources (WHEA, MCE, BSOD, disk, NTFS, app crashes)
- Secured named pipe (DACL: SYSTEM + interactive user only, B4)
- Atomic settings persistence (write-temp-rename, B7)
- Data directory with restricted ACLs (users read-only, B5)
- CSV event logging with daily rotation, retention, and mirror support
- WPF GUI: dark theme (JetBrains Mono Nerd Font bundled), 5-tab layout
- Monitor tab with live error counts, severity-colored source names
- Settings tab with collapsible sections, save-via-pipe
- System tray (green/red/gray, context menu, minimize-to-tray)
- Keyboard shortcuts (Ctrl+1-5 tabs, Ctrl+C copy, F5 refresh)
- Window position persistence, dark title bar on Win11
- Frodo UX review: 20 of 22 findings fixed

### Phase 2: Hardware Reads (Complete + Verified)
- PawnIO integration via PawnIOLib.dll (official C API, documented header)
- AMDFamily17.bin module extracted from ZenStates-Core, SHA-256 verified before kernel load (B8)
- IHardwareAccess interface: TryReadSmn/TryReadMsr (not PCI-level)
- UMC register decode: 4 wrong addresses fixed per Sauron audit, tRFC readback bug workaround
- CPU detection: Zen through Zen5 via registry (no WMI)
- **29/29 timing values match ZenTimings v1.36 on live hardware**
- Driver abstraction is swappable (Gandalf requirement)

### Phase 3: The Logbook (Complete — Service-Side)
- ConfigChangeDetector: field-by-field comparison, persisted previous snapshot
- DriftDetector: 20-boot rolling window, mode calculation, bimodal tie-break
- LkgTracker: configurable thresholds per test tool
- ValidationTestLogger: append-only with atomic persistence
- Timeline tab: chronological interleaving of changes/drift/validations
- Snapshots tab: two-dropdown comparison with direction-aware coloring
- DigestBuilder (the Brief): AI-readable export under 2000 tokens
- Full IPC wiring: LogValidation, GetSnapshots, GetDigest messages

### Infrastructure
- Public GitHub repo: https://github.com/thereprocase/RAMWatch (GPL-3.0)
- 25 commits on main, all pushed
- 171 automated tests, 0 failures
- .NET 10 LTS, `net10.0-windows`
- Service: Native AOT target (PublishAot=true)
- GUI: Self-contained single-file (WPF doesn't support AOT)

## Test Environment

- **CPU:** AMD Ryzen 7 5800X3D (Family 25 Model 33, Zen 3 Vermeer)
- **Board:** MSI MAG B550 TOMAHAWK MAX WIFI (MS-7C91)
- **BIOS:** 2.A0, AGESA ComboAM4v2PI 1.2.0.F
- **RAM:** CMH64GX4M2Z4000C18, 2x32GB, DDR4-3600
- **Timings:** CL=16 RCDRD=20 RCDWR=20 RP=20 RAS=42 RC=64
- **PawnIO:** Installed, driver RUNNING, AMDFamily17 module loads successfully
- **ZenTimings:** v1.36 installed at C:\Tools\ZenTimings\ (cross-reference validated)
- **OS:** Windows 11 Pro 10.0.26200
- **.NET SDK:** 10.0.201

## What Remains

### Immediate (wire what's built)
1. **Wire HardwareReader into RamWatchService** — connect to the periodic refresh loop so the Timings tab populates live. The reader works, the tab exists, the state message has a Timings field. Just needs the plumbing. ~30 min.
2. **Wire DigestBuilder into the GetDigest IPC handler** — currently returns null. DigestBuilder exists and is tested. Connect the two.
3. **TimingCsvLogger integration** — TimingCsvLogger exists but isn't called from the service loop yet.

### Phase 4: Git + The Bench
4. **GitCommitter** — auto-commit on config change/validation. Dedicated background thread (Legolas: never block the service loop). Shell out to git with ArgumentList (Aragorn: no command injection).
5. **CURRENT.md / LKG.md generation** — phone-readable BIOS checklists
6. **GH_CONFIG_DIR isolation** — repo-scoped git/gh credentials
7. **Public contribution records** — anonymized, deny-by-default privacy filter

### Polish
8. **Voltages via RyzenSMU.bin** — module is extracted, needs wiring. SVI2 for VSOC, SMU power table for VDDG/VDDP.
9. **Boot ID collision fix** — switch from boot_MMDD_HHMM to sequential counter (Uruk-Hai finding)
10. **Toast notifications** — needs NuGet package (Frodo finding #11)
11. **High contrast mode** — detect SystemParameters.HighContrast, swap theme (Frodo finding #12 partial)

### Known Issues
- PawnIoDriver uses [DllImport] with hardcoded path — works but not AOT-friendly. Consider NativeLibrary.Load() for the publish path.
- The all-ones test in UmcDecodeTests needs updating for the corrected PHYRDL register address (0x50258 vs old 0x502A4).
- MessageSerializer.Deserialize uses reflection-based JsonSerializer.Deserialize(string, Type, options) — produces AOT trim warnings on service publish. Should be refactored to use type-specific overloads.

## Key Architecture Decisions Made This Session

| Decision | Rationale |
|----------|-----------|
| GPL-3.0 | ZenTimings/ZenStates-Core are GPL-3.0. PawnIO is GPL-2+. |
| .NET 10 LTS | .NET 8 EOL Nov 2026 — insufficient runway |
| PawnIO over InpOutx64 | Signed driver, actively maintained, ZenTimings migrated |
| PawnIOLib.dll (not raw IOCTL) | Official C API with documented header, simpler than DeviceIoControl |
| TryReadSmn (not ReadPciConfigDword) | PawnIO's ioctl_read_smn is atomic in-kernel; PCI-level abstraction was wrong |
| Embedded .bin modules | B8: never load kernel code from user-writable paths |
| Service owns all file writes | B5: GUI sends changes via pipe, never writes directly |
| JetBrains Mono Nerd Font | Bundled as WPF resource, SIL OFL license, fallback chain to system fonts |

## Naming Vocabulary (Agreed)

| Component | Name |
|-----------|------|
| RAMWatch.Service | the Watchdog |
| RAMWatch (GUI) | the Dashboard |
| Tuning journal | the Logbook |
| Drift detection | Drift Watch |
| Community sharing | the Bench |
| AI digest export | the Brief |
| Privacy tiers | Scopes |
