namespace RAMWatch.Services;

/// <summary>
/// The five provenance tiers, in ascending order of skepticism:
/// Measured → Reported → Static → Derived → Unknown. The glyph on a sensor
/// tile conveys which tier the underlying reading is in; the tooltip
/// expands the story. Source classifications come from
/// docs/HARDWARE-DATA-SOURCES.md — that file is the contract.
/// </summary>
public enum Provenance
{
    /// <summary>Direct live telemetry from an ADC or measurement register.</summary>
    Measured,

    /// <summary>Live-polled, but what's polled is a setpoint/echo, not a measurement.</summary>
    Reported,

    /// <summary>Read once and cached; does not change within a service lifetime.</summary>
    Static,

    /// <summary>Computed from other sensors; color tracks weakest input.</summary>
    Derived,

    /// <summary>Unverified, unavailable, or explicitly flagged via a sentinel.</summary>
    Unknown,
}

/// <summary>
/// Glyph geometry. Circle for direct observation (regardless of tier),
/// diamond for derived, square for unknown. Colour conveys tier within
/// observations; shape conveys the kind of observation.
/// </summary>
public enum ProvenanceShape
{
    Circle,
    Diamond,
    Square,
}

/// <summary>
/// Everything the glyph and tooltip need for a single sensor lookup.
/// Source is the human-readable provenance pipe (e.g. "SMU PM table"),
/// Detail is the one-paragraph "what this is and why it matters."
/// </summary>
public readonly record struct SensorProvenanceInfo(
    Provenance Provenance,
    ProvenanceShape Shape,
    string Source,
    string Detail)
{
    public static SensorProvenanceInfo Measured(string source, string detail)
        => new(Services.Provenance.Measured, ProvenanceShape.Circle, source, detail);

    public static SensorProvenanceInfo Reported(string source, string detail)
        => new(Services.Provenance.Reported, ProvenanceShape.Circle, source, detail);

    public static SensorProvenanceInfo Static(string source, string detail)
        => new(Services.Provenance.Static, ProvenanceShape.Circle, source, detail);

    public static SensorProvenanceInfo Derived(Provenance weakestInput, string source, string detail)
        => new(weakestInput, ProvenanceShape.Diamond, source, detail);

    public static readonly SensorProvenanceInfo Unknown = new(
        Services.Provenance.Unknown,
        ProvenanceShape.Square,
        "unknown",
        "Sensor not recognised, not verified, or returning an unread sentinel.");
}

/// <summary>
/// Static registry mapping TimingsViewModel property names to provenance
/// info. Keys match the XAML SensorKey attribute exactly (e.g. SensorKey
/// ="Vsoc" on the glyph matches the registry key "Vsoc"). A miss returns
/// <see cref="SensorProvenanceInfo.Unknown"/>, which is intentional — we
/// would rather surface an unlabelled glyph than invent a classification.
/// </summary>
public static class SensorProvenanceRegistry
{
    // Source-pipe constants — match docs/HARDWARE-DATA-SOURCES.md labels.
    private const string SrcSmuPmTable  = "SMU PM table";
    private const string SrcSvi2        = "SVI2 registers";
    private const string SrcUmc         = "UMC registers";
    private const string SrcBiosWmi     = "BIOS AMD_ACPI WMI";
    private const string SrcEventLog    = "Windows Event Log";
    private const string SrcCbsLog      = "CBS.log tail";
    private const string SrcRegistry    = "Windows registry + WMI";
    private const string SrcDerivedState = "ConfigChangeDetector";
    private const string SrcDerivedDrift = "DriftDetector + 20-boot window";
    private const string SrcUserLog     = "User test log";

    // Canned detail strings keep the tooltip voice consistent across tiles.
    private const string DetMeasTemp =
        "SMU PM table thermal sensor — on-die thermal diode readout. Updated every 3s.";
    private const string DetMeasPwr =
        "SMU PM table power telemetry — integrated over the SMU's short window. Updated every 3s.";
    private const string DetMeasSvi2 =
        "SVI2 telemetry plane — live VID reported by the VRM. Changes every sample when the rail is active.";
    private const string DetRepLdo =
        "SMU commanded setpoint — what the on-package LDO was asked to produce, not a measurement. Holds steady while the SMU doesn't reprogram it.";
    private const string DetRepClk =
        "SMU-reported clock target — the configured clock domain, re-read each warm tick to detect mid-session changes.";
    private const string DetRepTiming =
        "UMC register readback — commanded timing value the memory controller was programmed with. Not a measured latency.";
    private const string DetStatBios =
        "BIOS WMI configuration — captured once at service startup and cached. Reflects the BIOS setting, not what the VRM is currently delivering.";
    private const string DetStatSignal =
        "BIOS WMI termination / drive-strength configuration — static for the duration of a boot.";
    private const string DetDimmV =
        "BIOS WMI DRAM voltage — a 0 here means the board doesn't expose the AMD_ACPI APCB block (common on ASRock), not zero volts.";

    private static readonly Dictionary<string, SensorProvenanceInfo> Map = new()
    {
        // ── SVI2 (live, measured) ────────────────────────────────
        ["Vsoc"]  = SensorProvenanceInfo.Measured(SrcSvi2, DetMeasSvi2),
        ["Vcore"] = SensorProvenanceInfo.Measured(SrcSvi2, DetMeasSvi2),

        // ── SMU PM table, LDO voltage setpoints (reported) ───────
        ["Vddp"]    = SensorProvenanceInfo.Reported(SrcSmuPmTable, DetRepLdo),
        ["VddgIod"] = SensorProvenanceInfo.Reported(SrcSmuPmTable, DetRepLdo),
        ["VddgCcd"] = SensorProvenanceInfo.Reported(SrcSmuPmTable, DetRepLdo),

        // ── SMU PM table, clocks (reported setpoint) ─────────────
        ["Mclk"] = SensorProvenanceInfo.Reported(SrcUmc, DetRepTiming),
        ["Fclk"] = SensorProvenanceInfo.Reported(SrcSmuPmTable, DetRepClk),
        ["Uclk"] = SensorProvenanceInfo.Reported(SrcSmuPmTable, DetRepClk),

        // ── SMU PM table, thermal (measured) ─────────────────────
        ["CpuTemp"] = SensorProvenanceInfo.Measured(SrcSmuPmTable, DetMeasTemp),

        // ── SMU PM table, power (measured + reported limit) ──────
        // Ppt tile combines an actual sensor reading with a setpoint
        // limit; classify as Measured since the user is primarily
        // watching the live actuals track against the (static) limit.
        ["SocketPower"] = SensorProvenanceInfo.Measured(SrcSmuPmTable, DetMeasPwr),
        ["Ppt"]         = SensorProvenanceInfo.Measured(SrcSmuPmTable, DetMeasPwr),

        // ── BIOS WMI, DRAM voltages (static) ─────────────────────
        // Vdimm uses ForVoltage() so a 0 reading gets flagged as Unknown
        // (ASRock + non-MSI boards return 0 when AMD_ACPI APCB is absent).
        ["Vdimm"] = SensorProvenanceInfo.Static(SrcBiosWmi, DetDimmV),
        ["Vtt"]   = SensorProvenanceInfo.Static(SrcBiosWmi, DetStatBios),
        ["Vpp"]   = SensorProvenanceInfo.Static(SrcBiosWmi, DetStatBios),

        // ── BIOS WMI, signal integrity (static) ──────────────────
        ["ProcOdt"]         = SensorProvenanceInfo.Static(SrcBiosWmi, DetStatSignal),
        ["RttNom"]          = SensorProvenanceInfo.Static(SrcBiosWmi, DetStatSignal),
        ["RttWr"]           = SensorProvenanceInfo.Static(SrcBiosWmi, DetStatSignal),
        ["RttPark"]         = SensorProvenanceInfo.Static(SrcBiosWmi, DetStatSignal),
        ["ClkDrvStren"]     = SensorProvenanceInfo.Static(SrcBiosWmi, DetStatSignal),
        ["AddrCmdDrvStren"] = SensorProvenanceInfo.Static(SrcBiosWmi, DetStatSignal),
        ["CsOdtCmdDrvStren"] = SensorProvenanceInfo.Static(SrcBiosWmi, DetStatSignal),
        ["CkeDrvStren"]     = SensorProvenanceInfo.Static(SrcBiosWmi, DetStatSignal),

        // ── Non-sensor surfaces (section headers, info lines) ────
        // Event Monitor table is a live push feed; the row counts update
        // in real time as Windows delivers EventLogWatcher callbacks.
        ["EventMonitor"] = SensorProvenanceInfo.Measured(
            SrcEventLog,
            "Every row below is a live push from Windows Event Log. Counts update in real time via EventLogWatcher kernel callbacks — zero CPU between events."),

        // Integrity panel blends CBS.log tail (warm-polled) with SFC/DISM
        // on-demand outputs; classify as Reported since the dominant tier
        // is periodic polling of a filesystem log.
        ["Integrity"] = SensorProvenanceInfo.Reported(
            SrcCbsLog,
            "Component-based servicing (CBS) log is scanned every warm tick (30–60s). SFC/DISM results appear here only when you run them manually."),

        // Board / CPU / BIOS line — read once from the registry + WMI at
        // service startup; goes stale only if the user flashes BIOS
        // without rebooting (rare, deferred).
        ["SystemInfo"] = SensorProvenanceInfo.Static(
            SrcRegistry,
            "CPU codename, BIOS version, and AGESA version captured once at service startup from HKLM\\HARDWARE\\DESCRIPTION\\System\\BIOS. Stale only if you flash BIOS without rebooting."),

        // ── Timeline entries (Derived + user-logged) ─────────────
        // Config changes are computed by ConfigChangeDetector comparing
        // consecutive boot snapshots; diamond + amber (weakest input is
        // the UMC Reported register readback).
        ["TimelineConfigChange"] = SensorProvenanceInfo.Derived(
            Provenance.Reported,
            SrcDerivedState,
            "Computed: ConfigChangeDetector diffs the current TimingSnapshot against the last-persisted one and emits a delta when any field (after tolerance filtering) changes. Primary inputs are UMC register readbacks — commanded setpoints, not direct measurements."),

        // Drift = DriftDetector's finding that a timing landed somewhere
        // other than the rolling 20-boot mode. Derived from historical
        // comparison of the same Reported UMC fields.
        ["TimelineDrift"] = SensorProvenanceInfo.Derived(
            Provenance.Reported,
            SrcDerivedDrift,
            "Computed: DriftDetector compares this boot's trained timings against the mode of the last 20 boots. Inputs are UMC register readbacks."),

        // Validation pass/fail entries are user-authored — a human ran a
        // stress test and wrote down the outcome. Primary observation, so
        // Measured (circle, green), but the source is a user log not an
        // ADC — the tooltip makes that distinction explicit.
        ["TimelineValidation"] = SensorProvenanceInfo.Measured(
            SrcUserLog,
            "User-entered stress-test result. Primary observation: a human ran MemTest / TM5 / y-cruncher / etc. and logged whether the system passed and for how long. Not a hardware sensor — these rows are authoritative because you wrote them."),

        // User-saved snapshots (Save Snapshot / Ctrl+S) are deliberate
        // markers in the tuning journal — the canonical "I'm starting a
        // new thing" row. Measured (primary observation) + green circle.
        ["TimelineSnapshot"] = SensorProvenanceInfo.Measured(
            SrcUserLog,
            "User-saved snapshot. You named and filed this one yourself — it's the canonical bookmark for 'I'm testing this config from here on.' Validations and changes that follow are tagged to the snapshot's era."),
    };

    /// <summary>
    /// Dictionary lookup; misses return <see cref="SensorProvenanceInfo.Unknown"/>.
    /// </summary>
    public static SensorProvenanceInfo For(string sensorKey)
        => Map.TryGetValue(sensorKey, out var info) ? info : SensorProvenanceInfo.Unknown;

    /// <summary>
    /// Voltage-aware lookup. The only key this treats specially is "Vdimm":
    /// BIOS WMI returns 0 on boards without AMD_ACPI APCB support (common
    /// on ASRock), and the UI should show that as Unknown rather than
    /// displaying a fake "0.0000 V" Static reading.
    /// </summary>
    public static SensorProvenanceInfo ForVoltage(string sensorKey, double value)
    {
        if (sensorKey == "Vdimm" && value == 0.0)
        {
            return SensorProvenanceInfo.Unknown with
            {
                Source = SrcBiosWmi,
                Detail = "BIOS WMI returned 0 for DRAM voltage — this board doesn't expose the AMD_ACPI APCB block. The DRAM rail is powered; we just can't read the setpoint.",
            };
        }
        if ((sensorKey == "Vtt" || sensorKey == "Vpp") && value == 0.0)
        {
            return SensorProvenanceInfo.Unknown with
            {
                Source = SrcBiosWmi,
                Detail = "BIOS WMI returned 0 for this rail — board doesn't expose the AMD_ACPI APCB block. Not zero volts; unread.",
            };
        }
        return For(sensorKey);
    }

    /// <summary>Registry keys, for diagnostic iteration.</summary>
    public static IReadOnlyCollection<string> AllKeys => Map.Keys;
}
