# Hardware Data Sources

Reference for every place RAMWatch pulls machine/hardware state from.
Grepable; keys are stable.

## Count

TOTAL_SOURCES: 10

## Source Pipes

---

SOURCE: UMC-REGISTERS
TIER: cold (on boot + on RequestTimingRefresh)
TRANSPORT: PawnIO IOCTL → SMN reads
READER: src/RAMWatch.Service/Hardware/UmcDecode.cs
DATA: primary/secondary/turn-around/tRFC/misc timings, address map, BgsEnabled, PHYRDL
CHANNEL: per-channel (UMC0 base 0x000000, UMC1 base 0x100000)
NOTES: register offsets from AMD PPR; clean-room decode; tRFC1 has ComboAM4v2PI 1.2.0.x magic-sentinel fallback

---

SOURCE: SMU-PM-TABLE
TIER: hot (3s) — thermals + power; warm (30-60s) — rail voltages via same shared table
TRANSPORT: PawnIO IOCTL (resolve_pm_table → read_pm_table via DRAM-mapped pointer)
READER: src/RAMWatch.Service/Hardware/SmuPowerTableReader.cs + src/RAMWatch.Service/Hardware/SmuDecode.cs
DATA: FCLK, UCLK, VDDP, VDDG_IOD, VDDG_CCD, Tctl, CCD temps, socket power, EDC/TDC/PPT, boost clocks
NOTES: commanded/set-point values, not live sensor reads — SMU updates in-place; no checksum; update-failure now returns null (not stale)

---

SOURCE: SVI2-REGISTERS
TIER: hot (3s)
TRANSPORT: PawnIO IOCTL → SMN reads (SVI2 telemetry planes 0x5A00C / 0x5A010 on Zen 2/3)
READER: src/RAMWatch.Service/Hardware/SmuDecode.cs (ReadSvi2)
DATA: VCore, VSoC as VID bytes decoded via `1.55 - vid * 0.00625`
NOTES: VID=0 rejected as sentinel (would decode to 1.55V); status bits [15:8] reject mid-update torn reads

---

SOURCE: BIOS-AMD-ACPI-WMI
TIER: cold (once per service lifetime, cached)
TRANSPORT: PowerShell subprocess → `Get-WmiObject -Namespace root\wmi -Class AMD_ACPI` (MSI-style)
READER: src/RAMWatch.Service/Hardware/BiosWmiReader.cs
DATA: VDimm, Vtt, Vpp, ProcODT, RttNom, RttWr, RttPark, ClkDrvStren, AddrCmdDrvStren, CsOdtCmdDrvStren, CkeDrvStren, AddrCmdSetup, CsOdtSetup, CkeSetup
NOTES: static BIOS values (not live); 10s wall-clock timeout + Kill on hang (commit 56408b3); read under HardwareReader _driverLock

---

SOURCE: BIOS-VENDOR-WMI
TIER: cold (inside AMD_ACPI read)
TRANSPORT: PowerShell subprocess → vendor-specific WMI (ASUS BoardItem, some MSI)
READER: src/RAMWatch.Service/Hardware/BiosWmiReader.cs (fallback paths)
DATA: VDIMM on boards that don't expose it via AMD_ACPI
NOTES: same subprocess pipe as AMD_ACPI; returns 0 when vendor WMI absent (ASRock and many others)

---

SOURCE: DIMM-SPD-WMI
TIER: cold (on boot)
TRANSPORT: PowerShell subprocess → `Get-CimInstance Win32_PhysicalMemory`
READER: src/RAMWatch.Service/Hardware/DimmReader.cs
DATA: per-slot capacity, manufacturer, part number, serial (optional), speed, rank, DDR generation
NOTES: aggregated into StateAggregator._dimms; private data (serial numbers) stays local-only

---

SOURCE: WINDOWS-EVENT-LOG
TIER: push (kernel callback — zero CPU between events)
TRANSPORT: System.Diagnostics.Eventing.Reader.EventLogWatcher + EventLogReader
READER: src/RAMWatch.Service/Services/EventLogMonitor.cs
PROVIDERS:
  - Microsoft-Windows-WHEA-Logger (event ids 17 18 19 20 46 47 48 1)
  - Microsoft-Windows-Kernel-WHEA (event ids 1 17 18 19 20 46 47 48)
  - Microsoft-Windows-Kernel-PCI (PCIe AER: 1 3 5 7 9 11 13 15 17 19 21 23)
  - Microsoft-Windows-WER-SystemErrorReporting (bugcheck 1001)
  - Microsoft-Windows-Kernel-Power (unexpected shutdown 41)
  - disk (7 11 15 51 52)
  - Ntfs (55 98 137 140)
  - volsnap (14 25 35 36)
  - Microsoft-Windows-CodeIntegrity (3001 3002 3003 3004 3033)
  - Microsoft-Windows-FilterManager (3 6)
  - Application Error (1000)
  - Application Hang (1002)
  - Microsoft-Windows-MemoryDiagnostics-Results (1001 1002)
NOTES: dedup by (LogName, RecordId) after commit 03a9b51; rate-limited per source at 1000ms with coalescing

---

SOURCE: WHEA-MCA-XML
TIER: push (piggybacks on WHEA event delivery)
TRANSPORT: EventRecord.ToXml() parsed via XmlReader
READER: src/RAMWatch.Service/Services/McaBankClassifier.cs
DATA: MCA bank number, bank name (Load-Store/Data-Fabric/IF-controller), status/address/misc/synd registers, correctable flag, error type classification
NOTES: DTD prohibited + XmlResolver=null; CPU family drives bank-name map

---

SOURCE: WINDOWS-REGISTRY
TIER: cold (once at service startup, cached for service lifetime)
TRANSPORT: Microsoft.Win32.Registry
READER: src/RAMWatch.Service/Hardware/SystemInfoReader.cs
DATA: CPU name (HARDWARE\DESCRIPTION\System\CentralProcessor\0 → ProcessorNameString), board vendor/model/BIOS version/AGESA version (HARDWARE\DESCRIPTION\System\BIOS)
NOTES: stale if user flashes BIOS without rebooting (deferred; rare workflow)

---

SOURCE: CBS-LOG-TAIL
TIER: warm (30-60s — runs alongside state broadcast)
TRANSPORT: filesystem read + regex scan of C:\Windows\Logs\CBS\CBS.log
READER: src/RAMWatch.Service/Services/IntegrityChecker.cs
DATA: component-based servicing corruption markers (SFC/DISM-adjacent); count + last seen timestamp
NOTES: scans last 64KB on each call; broadcasts state snapshot, does not emit per-event CSV rows

---

SOURCE: LIVE-KERNEL-REPORTS
TIER: cold (once at service startup)
TRANSPORT: filesystem scan of C:\Windows\LiveKernelReports
READER: src/RAMWatch.Service/Services/LiveKernelReportScanner.cs
DATA: kernel live-dump file enumeration (WHEA.cab, USBHUB3.cab, etc.); filename + timestamp
NOTES: presence indicates a historical hardware/driver event too transient for a full bugcheck

---

## Categorization

### By Liveness

CATEGORY_LIVE_SENSOR: SVI2-REGISTERS (only truly live telemetry — VID changes every sample)
CATEGORY_COMMANDED_SETPOINT: SMU-PM-TABLE, UMC-REGISTERS (configured by SMU/BIOS, re-read to detect changes)
CATEGORY_STATIC_CONFIG: BIOS-AMD-ACPI-WMI, BIOS-VENDOR-WMI, DIMM-SPD-WMI, WINDOWS-REGISTRY (one-shot capture, cached until service restart)
CATEGORY_EVENT_STREAM: WINDOWS-EVENT-LOG, WHEA-MCA-XML (fire on hardware incidents)
CATEGORY_FORENSIC: CBS-LOG-TAIL, LIVE-KERNEL-REPORTS (post-hoc corruption/crash artifacts)

### By Delivery Model

CATEGORY_POLL_HOT: SMU-PM-TABLE, SVI2-REGISTERS (3s)
CATEGORY_POLL_WARM: UMC-REGISTERS, SMU-PM-TABLE (voltage channels), CBS-LOG-TAIL (30-60s)
CATEGORY_POLL_COLD: BIOS-AMD-ACPI-WMI, DIMM-SPD-WMI, WINDOWS-REGISTRY, LIVE-KERNEL-REPORTS, UMC-REGISTERS (initial + trigger)
CATEGORY_PUSH: WINDOWS-EVENT-LOG, WHEA-MCA-XML (kernel callback)

### By Privilege Required

CATEGORY_NEEDS_SIGNED_DRIVER: UMC-REGISTERS, SMU-PM-TABLE, SVI2-REGISTERS (PawnIO kernel driver)
CATEGORY_NEEDS_LOCAL_SYSTEM: WINDOWS-EVENT-LOG (some providers), CBS-LOG-TAIL (C:\Windows\Logs\CBS)
CATEGORY_NEEDS_ADMIN: BIOS-AMD-ACPI-WMI (WMI namespace access)
CATEGORY_USER_VISIBLE: DIMM-SPD-WMI, WINDOWS-REGISTRY, LIVE-KERNEL-REPORTS

### By Failure Mode

CATEGORY_FAIL_SILENT_ZERO: none (fixed — UMC silent-zero abort in commit af53d02, VID=0 sentinel in 76cce9d)
CATEGORY_FAIL_HANG_PRONE: BIOS-AMD-ACPI-WMI (10s timeout + Kill after 56408b3)
CATEGORY_FAIL_STALE: WINDOWS-REGISTRY (BIOS flash without reboot — accepted risk)
CATEGORY_FAIL_TORN_READ: SVI2-REGISTERS (status-bit guard rejects mid-update; after 76cce9d, VID=0 also rejected)
CATEGORY_FAIL_PARTIAL: none (SMU PM table now returns null on update failure — commit f9c0019)
CATEGORY_FAIL_MISSING: BIOS-VENDOR-WMI (ASRock etc. don't expose it — emit zero, caller treats as unread)

### By Authoritative Decode Source

CATEGORY_AMD_PPR_DERIVED: UMC-REGISTERS, SMU-PM-TABLE, SVI2-REGISTERS
CATEGORY_VENDOR_SPECIFIC: BIOS-AMD-ACPI-WMI (MSI APCB buffer layout), BIOS-VENDOR-WMI (ASUS)
CATEGORY_WINDOWS_STANDARD: WINDOWS-EVENT-LOG, WHEA-MCA-XML, DIMM-SPD-WMI, WINDOWS-REGISTRY, CBS-LOG-TAIL, LIVE-KERNEL-REPORTS

### By Caching Behavior

CATEGORY_CACHE_SERVICE_LIFETIME: BIOS-AMD-ACPI-WMI (BiosConfig), WINDOWS-REGISTRY (SystemInfo), CPU-family from CpuDetect
CATEGORY_CACHE_PER_CALL: UMC-REGISTERS, SMU-PM-TABLE, SVI2-REGISTERS, DIMM-SPD-WMI (re-read every cold/warm tick)
CATEGORY_CACHE_APPEND_ONLY: WINDOWS-EVENT-LOG (rolling 500-event buffer + dedup), CBS-LOG-TAIL (last-64KB re-scan)

### By Populated TimingSnapshot Fields

FIELDS_UMC: MemClockMhz, CL, RCDRD, RCDWR, RP, RAS, RC, CWL, RFC/RFC2/RFC4, RRDS/RRDL, FAW, WTRS/WTRL, WR, RTP, RDRDSCL/WRWRSCL, turn-around group, REFI, CKE, STAG, MOD, MRD, PHYRDL_A/B, GDM, Cmd2T, PowerDown, TrfcReadbackBugDetected
FIELDS_SMU_PMTABLE: FclkMhz, UclkMhz, VDDP, VDDG_IOD, VDDG_CCD (+ thermal/power into ThermalPowerSnapshot, not TimingSnapshot)
FIELDS_SVI2: VSoc, VCore
FIELDS_BIOS_WMI: VDimm, Vtt, Vpp, ProcODT, RttNom, RttWr, RttPark, ClkDrvStren, AddrCmdDrvStren, CsOdtCmdDrvStren, CkeDrvStren, AddrCmdSetup, CsOdtSetup, CkeSetup
FIELDS_REGISTRY: CpuCodename, BiosVersion, AgesaVersion (+ BoardVendor/BoardModel on the snapshot's stamped context)

### By IPC Broadcast Type

BROADCAST_thermalUpdate: SMU-PM-TABLE (thermal), SVI2-REGISTERS (VCore/VSoC)
BROADCAST_state: all other sources (full warm-tier push)
BROADCAST_event: WINDOWS-EVENT-LOG items + optional vitals capture from SVI2 + SMU-PM-TABLE at moment of event

## Summary by Transport Layer

TRANSPORT_PAWNIO: UMC-REGISTERS, SMU-PM-TABLE, SVI2-REGISTERS (and CPUID in CpuDetect)
TRANSPORT_POWERSHELL_WMI: BIOS-AMD-ACPI-WMI, BIOS-VENDOR-WMI, DIMM-SPD-WMI
TRANSPORT_KERNEL_CALLBACK: WINDOWS-EVENT-LOG
TRANSPORT_XML_PARSE: WHEA-MCA-XML (on top of EVENT-LOG)
TRANSPORT_REGISTRY: WINDOWS-REGISTRY
TRANSPORT_FILESYSTEM: CBS-LOG-TAIL, LIVE-KERNEL-REPORTS

## Three-Tier Polling Integration

HOT_TIER_3S: SMU-PM-TABLE (thermals + power), SVI2-REGISTERS (VCore/VSoC)
WARM_TIER_30_60S: UMC-REGISTERS (re-read), SMU-PM-TABLE (clocks/voltages), CBS-LOG-TAIL, WHEA-MCA-XML decode of recent events
COLD_TIER_BOOT: UMC-REGISTERS, BIOS-AMD-ACPI-WMI, BIOS-VENDOR-WMI, DIMM-SPD-WMI, WINDOWS-REGISTRY, LIVE-KERNEL-REPORTS, historical WINDOWS-EVENT-LOG scan
PUSH_TIER: WINDOWS-EVENT-LOG live watchers (zero CPU between events)

## Gotchas Worth Remembering

NOTE: BIOS WMI values are static — NOT live telemetry. VDimm at 1.400V reflects the BIOS setting, not what the VRM is currently delivering.
NOTE: SMU PM table values are commanded/set-point, NOT sensor reads. VDDG_IOD holds steady at 1.0241V because the SMU doesn't reprogram it at idle.
NOTE: SVI2 IS live sensor telemetry. VCore varies every sample; VSoC wobbles occasionally (6.25mV VID steps).
NOTE: VDimm=0 / Vtt=0 / Vpp=0 is common on ASRock and boards without AMD_ACPI WMI — means "unread", not "zero volts".
NOTE: PHYRDL A vs B channel mismatch is normal (PHY training artifact), never flag as anomaly.
NOTE: tRFC1 readback bug on ComboAM4v2PI 1.2.0.x — register 0x50260 returns magic 0x21060138; service now falls back to 0x50264 and sets TrfcReadbackBugDetected on the snapshot.
