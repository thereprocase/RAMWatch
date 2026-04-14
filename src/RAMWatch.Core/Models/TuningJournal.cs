namespace RAMWatch.Core.Models;

// ---------------------------------------------------------------------------
// Phase 3 data models — defined now so the on-disk schema is stable before
// Phase 3 implementation begins. Service writes these; GUI reads them via IPC.
// All types are registered in RamWatchJsonContext for source-generated JSON.
// ---------------------------------------------------------------------------

/// <summary>
/// A point-in-time snapshot of the active timing configuration.
/// Field names use the community-standard short names (CL, RCDRD, etc.)
/// that match what users see in BIOS and ZenTimings.
/// </summary>
public sealed class TimingSnapshot
{
    public required string SnapshotId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string BootId { get; init; }
    public int SchemaVersion { get; init; } = 1;

    // --- Clocks ---
    public int MemClockMhz { get; set; }    // MCLK (half DDR rate)
    public int FclkMhz { get; set; }        // FCLK (infinity fabric)
    public int UclkMhz { get; set; }        // UCLK (memory controller)

    // --- Primaries ---
    public int CL { get; set; }
    public int RCDRD { get; set; }
    public int RCDWR { get; set; }
    public int RP { get; set; }
    public int RAS { get; set; }
    public int RC { get; set; }
    public int CWL { get; set; }

    // --- tRFC group ---
    public int RFC { get; set; }            // tRFC1 in clocks
    public int RFC2 { get; set; }
    public int RFC4 { get; set; }

    // --- Secondaries ---
    public int RRDS { get; set; }
    public int RRDL { get; set; }
    public int FAW { get; set; }
    public int WTRS { get; set; }
    public int WTRL { get; set; }
    public int WR { get; set; }
    public int RTP { get; set; }
    public int RDRDSCL { get; set; }
    public int WRWRSCL { get; set; }

    // --- Turn-around ---
    public int RDRDSC { get; set; }
    public int RDRDSD { get; set; }
    public int RDRDDD { get; set; }
    public int WRWRSC { get; set; }
    public int WRWRSD { get; set; }
    public int WRWRDD { get; set; }
    public int RDWR { get; set; }
    public int WRRD { get; set; }

    // --- Misc ---
    public int REFI { get; set; }           // tREFI
    public int CKE { get; set; }
    public int STAG { get; set; }
    public int MOD { get; set; }
    public int MRD { get; set; }

    // --- PHY (per-channel, training artifacts) ---
    public int PHYRDL_A { get; set; }       // channel A
    public int PHYRDL_B { get; set; }       // channel B — mismatch is normal

    // --- Controller config ---
    public bool GDM { get; set; }
    public bool Cmd2T { get; set; }         // true = 2T, false = 1T
    public bool PowerDown { get; set; }

    // --- Voltages ---
    public double VSoc { get; set; }        // SVI2 telemetry
    public double VDimm { get; set; }       // BIOS WMI on some boards

    // --- System info ---
    public string CpuCodename { get; set; } = "";
    public string AgesaVersion { get; set; } = "";
    public string BiosVersion { get; set; } = "";

    // --- User metadata (Phase 3) ---
    public string Label { get; set; } = "";
    public string Notes { get; set; } = "";

    /// <summary>
    /// Returns a shallow copy with the supplied SnapshotId and Label.
    /// All timing values are shared (they are value types or immutable strings).
    /// </summary>
    public TimingSnapshot WithIdAndLabel(string snapshotId, string label) =>
        new()
        {
            SnapshotId    = snapshotId,
            Timestamp     = Timestamp,
            BootId        = BootId,
            SchemaVersion = SchemaVersion,
            MemClockMhz   = MemClockMhz,
            FclkMhz       = FclkMhz,
            UclkMhz       = UclkMhz,
            CL            = CL,
            RCDRD         = RCDRD,
            RCDWR         = RCDWR,
            RP            = RP,
            RAS           = RAS,
            RC            = RC,
            CWL           = CWL,
            RFC           = RFC,
            RFC2          = RFC2,
            RFC4          = RFC4,
            RRDS          = RRDS,
            RRDL          = RRDL,
            FAW           = FAW,
            WTRS          = WTRS,
            WTRL          = WTRL,
            WR            = WR,
            RTP           = RTP,
            RDRDSCL       = RDRDSCL,
            WRWRSCL       = WRWRSCL,
            RDRDSC        = RDRDSC,
            RDRDSD        = RDRDSD,
            RDRDDD        = RDRDDD,
            WRWRSC        = WRWRSC,
            WRWRSD        = WRWRSD,
            WRWRDD        = WRWRDD,
            RDWR          = RDWR,
            WRRD          = WRRD,
            REFI          = REFI,
            CKE           = CKE,
            STAG          = STAG,
            MOD           = MOD,
            MRD           = MRD,
            PHYRDL_A      = PHYRDL_A,
            PHYRDL_B      = PHYRDL_B,
            GDM           = GDM,
            Cmd2T         = Cmd2T,
            PowerDown     = PowerDown,
            VSoc          = VSoc,
            VDimm         = VDimm,
            CpuCodename   = CpuCodename,
            AgesaVersion  = AgesaVersion,
            BiosVersion   = BiosVersion,
            Label         = label,
            Notes         = Notes,
        };
}

/// <summary>
/// A detected change to the timing configuration between boots.
/// </summary>
public sealed record ConfigChange
{
    public required string ChangeId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string BootId { get; init; }
    public required Dictionary<string, TimingDelta> Changes { get; init; }
    public string? SnapshotBeforeId { get; init; }
    public string? SnapshotAfterId { get; init; }
    public string? UserNotes { get; init; }
}

public sealed record TimingDelta(string Before, string After);

/// <summary>
/// Drift in an auto-trained timing across boots.
/// </summary>
public sealed record DriftEvent
{
    public required DateTime Timestamp { get; init; }
    public required string BootId { get; init; }
    public required string TimingName { get; init; }
    public required int ExpectedValue { get; init; }
    public required int ActualValue { get; init; }
    public required int BootsAtExpected { get; init; }
    public required int BootsAtActual { get; init; }
    public int WindowBootCount { get; init; }
    public double WindowStabilityRatio { get; init; }
}

/// <summary>
/// A stability test result linked to the snapshot that was active.
/// Uses ActiveSnapshotId (foreign key) not an embedded copy —
/// keeps git diffs clean when the snapshot list grows.
/// </summary>
public sealed record ValidationResult
{
    /// <summary>
    /// Stable identifier for this result. Auto-generated on construction.
    /// Used by DeleteValidationMessage to target a specific entry.
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required DateTime Timestamp { get; init; }
    public required string BootId { get; init; }
    public required string TestTool { get; init; }
    public required string MetricName { get; init; }
    public required double MetricValue { get; init; }
    public required string MetricUnit { get; init; }
    public required bool Passed { get; init; }
    public int ErrorCount { get; init; }
    public int DurationMinutes { get; init; }
    public string? ActiveSnapshotId { get; init; }
    public string? Notes { get; init; }
}

public enum TimingDesignation
{
    Unknown,
    Manual,
    Auto
}

/// <summary>
/// Persisted map of timing name to manual/auto/unknown designation.
/// </summary>
public sealed class DesignationMap
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime LastUpdated { get; set; }
    public Dictionary<string, TimingDesignation> Designations { get; set; } = new();
}

// ---------------------------------------------------------------------------
// DriftDetector persistence types
// ---------------------------------------------------------------------------

/// <summary>
/// Persisted rolling window of timing values across the last N boots.
/// Written by DriftDetector; read back on service startup.
/// </summary>
public sealed class DriftWindow
{
    public int SchemaVersion { get; set; } = 1;
    public List<BootEntry> Boots { get; set; } = new();
}

/// <summary>
/// One entry in the drift window: the timing values observed during a single boot.
/// Boolean fields (GDM, Cmd2T, PowerDown) are stored as 0/1.
/// </summary>
public sealed class BootEntry
{
    public required string BootId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required Dictionary<string, int> Values { get; init; }
}
