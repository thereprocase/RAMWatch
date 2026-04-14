# RAMWatch

Lightweight DRAM tuning monitor for Windows. Tracks system health, timing configurations, stability test results, and tuning history across boots. Read-only — never modifies hardware.

**Status:** Phase 1 implementation in progress. Service + IPC + event monitoring + GUI shell functional.

## What It Does

- **System health monitor** — persistent, event-driven tracking of WHEA errors, MCEs, filesystem corruption, and application crashes. Runs as a Windows service, starts at boot, never misses an event.
- **Tuning journal** — timestamped log of every timing configuration, every stability test result, and every config change, with automatic drift detection for auto-trained values.
- **Shareable history** — git-backed version control of your tuning journey with phone-readable BIOS checklists and optional anonymous community data pooling.

## Building

Requires .NET 10 SDK (`net10.0-windows`).

```bash
dotnet build RAMWatch.sln
dotnet test src/RAMWatch.Tests
dotnet publish src/RAMWatch.Service -c Release -r win-x64   # Native AOT service (~15MB)
dotnet publish src/RAMWatch -c Release -r win-x64           # WPF GUI (~80-120MB)
```

## License

GPL-3.0. See [LICENSE](LICENSE).

## Notice

UMC register offsets used for DRAM timing decode are hardware facts derived from AMD Processor Programming References (PPRs) and are not copyrightable. The decode implementation in this project is original work licensed under GPL-3.0.

ZenTimings and ZenStates-Core (by Ivan Rusanov, GPL-3.0) are used as reference material for register map validation. PawnIO (by namazso, GPL-2+) is the kernel driver interface. Neither is bundled — both are runtime dependencies provided by the user.
