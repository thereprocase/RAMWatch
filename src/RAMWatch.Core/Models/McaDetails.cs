namespace RAMWatch.Core.Models;

/// <summary>
/// Tuning-relevant classification of an MCA error source.
/// Used to group errors by what a RAM tuner would care about.
/// </summary>
public enum McaClassification
{
    /// <summary>Unknown or unrecognized MCA bank.</summary>
    Unknown,

    /// <summary>Data Fabric / Infinity Fabric — FCLK stability indicator.</summary>
    DataFabric,

    /// <summary>Unified Memory Controller — memory training or signal integrity.</summary>
    Umc,

    /// <summary>L3 cache — may indicate VCORE or core clock issues.</summary>
    L3Cache,

    /// <summary>Per-core execution units (LS, IF, DE, EX, FP).</summary>
    Core,

    /// <summary>PCIe / NBIO — bus link errors, possibly FCLK-adjacent.</summary>
    Pcie,

    /// <summary>IOMMU / IO Hub Controller — DMA remapping errors.</summary>
    IoHub
}

/// <summary>
/// Decoded MCA (Machine Check Architecture) details extracted from WHEA event XML.
/// Populated only for WHEA-Logger events that contain MCA bank data.
/// All hex values stored as strings for display; parsed flags as bools.
/// </summary>
public sealed class McaDetails
{
    /// <summary>MCA bank number from the WHEA event record.</summary>
    public required int BankNumber { get; init; }

    /// <summary>MCI_STATUS register value (hex string, e.g., "0x982000000002080b").</summary>
    public required string MciStatus { get; init; }

    /// <summary>MCI_ADDR register value, or null if ADDRV bit is clear.</summary>
    public string? MciAddr { get; init; }

    /// <summary>MCI_MISC register value, or null if MISCV bit is clear.</summary>
    public string? MciMisc { get; init; }

    /// <summary>APIC ID of the reporting processor.</summary>
    public int ApicId { get; init; }

    /// <summary>
    /// Human-readable component name (e.g., "Data Fabric (PIE)", "UMC Channel 0",
    /// "L3 Cache", "Load-Store Unit"). Best-effort based on bank number and CPU family.
    /// </summary>
    public required string Component { get; init; }

    /// <summary>Tuning-relevant classification for grouping and display.</summary>
    public required McaClassification Classification { get; init; }

    /// <summary>True if the error was uncorrectable (UC bit set in MCI_STATUS).</summary>
    public bool IsUncorrectable { get; init; }

    /// <summary>True if additional errors occurred while this one was being logged.</summary>
    public bool IsOverflow { get; init; }

    /// <summary>True if processor context may be corrupted (PCC bit set).</summary>
    public bool IsContextCorrupted { get; init; }

    /// <summary>
    /// Windows WHEA ErrorType value from the event data.
    /// Maps to WHEA_ERROR_TYPE enum (e.g., 10 = Bus/Interconnect).
    /// </summary>
    public int WheaErrorType { get; init; }
}
