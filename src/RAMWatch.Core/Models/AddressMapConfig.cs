namespace RAMWatch.Core.Models;

/// <summary>
/// UMC DRAM address mapping configuration per channel.
/// Read from UMC SMN registers at service startup — these are set by BIOS
/// and don't change at runtime. Exposed over IPC so external tools (e.g.,
/// RAMBurn rowhammer) can do exact physical-to-DRAM address translation
/// instead of heuristic bit position guessing.
/// </summary>
public sealed class AddressMapConfig
{
    /// <summary>UMC channel index (0 or 1 on desktop Zen).</summary>
    public int Channel { get; set; }

    // --- Raw register values (for consumers that want to decode themselves) ---

    /// <summary>
    /// UMC_CH_AddrHashBank (SMN 0x50040). Controls bank XOR hashing.
    /// Bits [0] = enable, bits [31:1] = XOR mask applied to bank address bits.
    /// </summary>
    public uint AddrHashBank { get; set; }

    /// <summary>
    /// UMC_CH_AddrHashPC (SMN 0x50044). Controls rank/chip-select interleave.
    /// </summary>
    public uint AddrHashPC { get; set; }

    /// <summary>
    /// UMC_CH_AddrCfg (SMN 0x500C8). Column, row, and bank bit positions.
    /// </summary>
    public uint AddrCfg { get; set; }

    /// <summary>BankGroupSwap register 0 (SMN 0x50050).</summary>
    public uint BankGroupSwap0 { get; set; }

    /// <summary>BankGroupSwap register 1 (SMN 0x50058).</summary>
    public uint BankGroupSwap1 { get; set; }

    /// <summary>BankGroupSwapAlt register 0 (SMN 0x500D0).</summary>
    public uint BankGroupSwapAlt0 { get; set; }

    /// <summary>BankGroupSwapAlt register 1 (SMN 0x500D4).</summary>
    public uint BankGroupSwapAlt1 { get; set; }

    // --- Computed convenience fields ---

    /// <summary>True when bank address XOR hashing is enabled (bit 0 of AddrHashBank).</summary>
    public bool BankHashEnabled { get; set; }

    /// <summary>True when BankGroupSwap is active (both registers != 0x87654321).</summary>
    public bool BgsEnabled { get; set; }

    /// <summary>True when BankGroupSwapAlt is active.</summary>
    public bool BgsAltEnabled { get; set; }
}
