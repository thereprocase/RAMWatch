# RAMWatch - End-to-End Code Review & Next Sprint Plan

## Code Review Summary

RAMWatch is a Windows-only DRAM tuning monitor consisting of a Native AOT Windows Service (`RAMWatch.Service`) and a self-contained WPF GUI (`RAMWatch`).

**Architecture & Security:**
* The two-process architecture communicates over a secure named pipe (`\\.\pipe\RAMWatch`). The code appropriately restricts the DACL to LocalSystem and interactive user SID, fixing the local privilege escalation vector (B4).
* A single `System.Text.Json` source generator context (`RamWatchJsonContext`) is properly utilized for Native AOT serialization.
* Settings and configuration are secured, removing `inpOutDllPath` from `settings.json` (fixing B5).

**Current Status (Phases 1-3 Completed):**
* Core services (EventLogMonitor, IntegrityChecker parsing, CsvLogger, SettingsManager) are implemented and stable.
* `HardwareReader` successfully decodes Zen 3 UMC registers, SVI2 voltages, and interfaces with the SMU Mailbox.
* The `StateAggregator` pushes accurate JSON models to the client over the `PipeServer`.
* WPF MVVM structure and XAML components adhere to AOT limitations.
* Testing coverage is substantial.

**Phase 4 (Git + Community) Progress:**
* `GitCommitter` is implemented, generating `CURRENT.md`, `LKG.md`, `CHANGELOG.md` and invoking `git` / `gh` CLI cleanly in a dedicated background task.
* **Missing:** `PublicContributor.cs` and the anonymous community contribution push mechanism defined in the design docs.

**Phase 5 (Polish) Progress:**
* The `IntegrityChecker` parses `CBS.log`, but `SFC`/`DISM` subprocess executions are currently stubbed.
* `ToastNotification` implementations using PowerShell are present but could be refined.
* **Missing:** FCLK WHEA Classifier to distinguish correctable FCLK errors from other WHEA events.
* **Missing:** Native Windows Runtime Toast notification implementation (currently falls back to spawning PowerShell).

---

## Next Sprint Plan (Completing Phase 4 & 5)

1. **Implement PublicContributor (Phase 4)**
   - Create `src/RAMWatch.Service/Services/PublicContributor.cs`.
   - Implement the anonymous contribution mechanism using `gh repo create` / `gh api` or `git` to the designated `publicRepoName`.
   - Map properties from `TimingSnapshot` to the `PublicContribution` model, strictly omitting serials, event history, and exact timestamps.

2. **Implement full SFC/DISM Subprocess Execution (Phase 5)**
   - Update `IntegrityChecker.cs` in `RAMWatch.Service` to actually execute `sfc /verifyonly`, `sfc /scannow`, and `DISM /Online /Cleanup-Image /ScanHealth` using `Process.Start`.
   - Forward progress output securely over the IPC `integrityProgress` message to update the GUI state.

3. **Implement FCLK WHEA Classifier (Phase 5)**
   - Update `McaBankClassifier.cs` (or create a new classifier) to parse MCA bank register values.
   - Distinguish correctable FCLK errors (e.g., Bank 27 / Bank 18 specific signatures) from memory or CPU core WHEA errors.

4. **Refine Toast Notifications (Phase 5)**
   - Refine the Toast Notification implementation to be more robust if AOT limitations permit, or solidify the existing implementation against shell-injection.
