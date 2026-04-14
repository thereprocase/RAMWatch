# RAMWatch Session Report — 2026-04-14 (Session B)

## Summary

Continued from Session A (25 commits). This session: 28 commits, from service wiring through full GUI polish. The app is now installable, usable as a daily driver, and verified against ZenTimings v1.36 with 29/29 timing matches + FCLK/UCLK/VSoC/VDIMM.

## What Was Accomplished

### Service Wiring (Phases 1-3 → Live)
- Wired HardwareReader into service refresh loop (timings populate live)
- Wired DigestBuilder into GetDigest IPC (the Brief works)
- Wired TimingCsvLogger into service loop

### Phase 4: Git Integration
- GitCommitter with Channel<T> drain loop, circuit breaker, subprocess safety
- CURRENT.md / LKG.md generation with vendor BIOS ordering
- CommitMessageBuilder for 5 commit message formats
- Aragorn security review: orphan subprocess kill, git config injection fix, input sanitization
- Path validation for LogDirectory/MirrorDirectory (Aragorn C1)

### Install Workflow
- Publish-Release.ps1 (service AOT + GUI single-file)
- Install-RAMWatch.ps1 (Program Files, ACLs, service, shortcuts, autostart)
- Update-RAMWatch.ps1 (one-command dev iteration)
- Uninstall-RAMWatch.ps1 (clean removal)
- Auto-detection of vswhere.exe for Native AOT (7.6 MB service exe)
- De-elevated GUI launch via scheduled task

### Hardware Reads
- FCLK/UCLK via SMU power table (RyzenSMU.bin, version-aware offsets)
- VSoC via SVI2 telemetry SMN registers
- VDIMM via vendor WMI (AMD_ACPI for MSI, ASUSHW for ASUS)
- tRFC ns formula fixed (was dividing by 2*MCLK, should be MCLK)

### GUI Overhaul
- Error severity tiers (CLEAN when 0 stability errors, regardless of boot noise)
- Dark slate/charcoal theme (replaced navy/blue)
- BIOS vendor layout auto-detection (MSI, ASUS, Gigabyte, ASRock)
- Masonry two-column timings layout (bin-packed, no cross-column alignment)
- Snapshot sub-tabs: Compare (default) + Manage (browse/rename/delete)
- Validation-labeled snapshot dropdowns ("Karhu 8000% PASS")
- Auto-snapshot on validation logging
- Smart snapshot filtering (only show timing changes)
- Log Test Result dialog
- Snapshot naming dialog (Ctrl+S shortcut)
- Timeline with config changes and validation entries
- Timeline two-click delete wired to IPC
- Toast notifications via PowerShell subprocess
- Designation map GUI in Settings
- DPI-aware window sizing (PerMonitorV2 manifest)
- Single-instance mutex with foreground-on-relaunch
- ComboBox dark theme templates
- Always-on-top wired to Window.Topmost

### Service Hardening
- BuildState lock cleanup (no nested lock acquisition)
- Parallel broadcast with per-client 5s timeout
- Event storm rate limiter (1s cooldown per source)
- Settings field clamping (RefreshInterval, string lengths)
- Journal caps (1000 snapshots, 500 validations)
- Protocol version check on IPC messages
- Boot ID sequential counter (no more timestamp collisions)

### War Council Findings Addressed
- Sauron: tRFC ns formula, PowerDown in BIOS layouts
- Aragorn: path validation (C1), RepoPath null (C2), string truncation (W3/W5)
- Legolas: nested locks (C1), sequential broadcast (C2), LINQ optimizations (H1/H5)

## Test Results

- **426 tests**, all passing
- **29/29 timing values** match ZenTimings v1.36
- **FCLK 1800, UCLK 1800** — correct
- **VSoC 1.0875** — within 1 SVI2 step of ZenTimings (1.0813)
- **VDIMM 1.4000** — matches ZenTimings exactly (via WMI)
- **tRFC 577 (321ns)** — matches ZenTimings (was showing 160ns before fix)

## What Remains

### Known Issues
- Line endings need renormalization (`.gitattributes` added but existing files not renormalized)
- MessageSerializer AOT trim warnings (reflection-based Deserialize needs type-specific overloads)

### Phase 5 (Future Sessions)
- SFC/DISM execution (currently stubbed)
- High contrast mode
- GitHub OAuth flow for git push
- Full export/import
- Zen 2/4 register map validation
- Additional voltage reads (VDDG CCD/IOD, CLDO VDDP, VTT)

## Test Environment

- **CPU:** AMD Ryzen 7 5800X3D (Zen 3 Vermeer)
- **Board:** MSI MAG B550 TOMAHAWK MAX WIFI
- **RAM:** CMH64GX4M2Z4000C18, 2x32GB, DDR4-3600 CL16-20-20-42
- **PawnIO:** Installed, RUNNING
- **ZenTimings:** v1.36 (cross-reference validated)
- **OS:** Windows 11 Pro 10.0.26200
- **.NET SDK:** 10.0.201
