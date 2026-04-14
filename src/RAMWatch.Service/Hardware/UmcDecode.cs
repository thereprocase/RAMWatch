using RAMWatch.Core.Models;

namespace RAMWatch.Service.Hardware;

/// <summary>
/// Decode DDR4 DRAM timings from AMD Unified Memory Controller (UMC) registers.
///
/// Register offsets are hardware facts from AMD Processor Programming References
/// (PPRs/BKDGs). These are not copyrightable — they describe the physical layout
/// of silicon registers. The decode implementation is original GPL-3.0 work.
///
/// The UMC registers live in the SMN (System Management Network) address space.
/// Access pattern: write SMN address to PCI reg 0x60, read result from PCI reg 0x64,
/// both on bus 0, device 0, function 0.
///
/// Each UMC instance (one per DRAM channel) has its own SMN base address.
/// UMC0: base 0x50000, UMC1: base 0x150000 (offset by 0x100000).
/// </summary>
public sealed class UmcDecode
{
    private readonly IHardwareAccess _hw;

    // SMN access port: PCI bus 0, device 0, function 0
    private const uint SmnBus = 0;
    private const uint SmnDevice = 0;
    private const uint SmnFunction = 0;
    private const uint SmnAddrReg = 0x60;
    private const uint SmnDataReg = 0x64;

    // UMC base addresses per channel
    private static readonly uint[] ChannelBases = [0x50000, 0x150000];

    public UmcDecode(IHardwareAccess hw)
    {
        _hw = hw;
    }

    /// <summary>
    /// Read all timings from the UMC. Returns a populated TimingSnapshot
    /// or null if the hardware is unavailable.
    /// </summary>
    public TimingSnapshot? ReadTimings(string bootId)
    {
        if (!_hw.IsAvailable) return null;

        try
        {
            // Read from channel 0 (UMC0). Channel 1 should be identical
            // except for PHY training values (PHYRDL etc.).
            uint baseAddr = ChannelBases[0];

            var snapshot = new TimingSnapshot
            {
                SnapshotId = Guid.NewGuid().ToString("N"),
                Timestamp = DateTime.UtcNow,
                BootId = bootId,
            };

            // Clock detection
            ReadClocks(baseAddr, snapshot);

            // Timing registers
            ReadPrimaries(baseAddr, snapshot);
            ReadSecondaries(baseAddr, snapshot);
            ReadTurnaround(baseAddr, snapshot);
            ReadControllerConfig(baseAddr, snapshot);
            ReadRfc(baseAddr, snapshot);
            ReadMisc(baseAddr, snapshot);

            // PHY timings from both channels
            ReadPhy(ChannelBases[0], snapshot, channel: 0);
            if (ChannelBases.Length > 1)
                ReadPhy(ChannelBases[1], snapshot, channel: 1);

            return snapshot;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Read a 32-bit value from the SMN address space via PCI indirect access.
    /// </summary>
    private uint ReadSmn(uint address)
    {
        _hw.WritePciConfigDword(SmnBus, SmnDevice, SmnFunction, SmnAddrReg, address);
        return _hw.ReadPciConfigDword(SmnBus, SmnDevice, SmnFunction, SmnDataReg);
    }

    /// <summary>
    /// Extract a bitfield from a 32-bit register value.
    /// </summary>
    private static int ExtractBits(uint value, int highBit, int lowBit)
    {
        uint mask = (1u << (highBit - lowBit + 1)) - 1;
        return (int)((value >> lowBit) & mask);
    }

    // ── Register decode groups ───────────────────────────────
    // Offsets relative to UMC base (0x50000 for channel 0).
    // Source: AMD PPR for Family 17h/19h processors.

    private void ReadClocks(uint @base, TimingSnapshot s)
    {
        // MCLK frequency is derived from the UMC clock divider
        // Register 0x50000 offset varies — for now, populate from
        // BIOS-reported values via a future WMI/registry path.
        // Phase 2 stretch: compute from UMC_CLK_DIV register.
    }

    private void ReadPrimaries(uint @base, TimingSnapshot s)
    {
        // 0x200: Cmd2T [10], GDM [11]
        uint reg200 = ReadSmn(@base + 0x200);
        s.Cmd2T = ExtractBits(reg200, 10, 10) == 1;
        s.GDM = ExtractBits(reg200, 11, 11) == 1;

        // 0x204: RCDWR [29:24], RCDRD [21:16], RAS [14:8], CL [5:0]
        uint reg204 = ReadSmn(@base + 0x204);
        s.CL = ExtractBits(reg204, 5, 0);
        s.RAS = ExtractBits(reg204, 14, 8);
        s.RCDRD = ExtractBits(reg204, 21, 16);
        s.RCDWR = ExtractBits(reg204, 29, 24);

        // 0x208: RC [7:0], RP [21:16]
        uint reg208 = ReadSmn(@base + 0x208);
        s.RC = ExtractBits(reg208, 7, 0);
        s.RP = ExtractBits(reg208, 21, 16);
    }

    private void ReadSecondaries(uint @base, TimingSnapshot s)
    {
        // 0x20C: RTP [28:24], RRDL [12:8], RRDS [4:0]
        uint reg20c = ReadSmn(@base + 0x20C);
        s.RRDS = ExtractBits(reg20c, 4, 0);
        s.RRDL = ExtractBits(reg20c, 12, 8);
        s.RTP = ExtractBits(reg20c, 28, 24);

        // 0x210: FAW [7:0]
        uint reg210 = ReadSmn(@base + 0x210);
        s.FAW = ExtractBits(reg210, 7, 0);

        // 0x214: WTRL [22:16], WTRS [12:8], CWL [5:0]
        uint reg214 = ReadSmn(@base + 0x214);
        s.CWL = ExtractBits(reg214, 5, 0);
        s.WTRS = ExtractBits(reg214, 12, 8);
        s.WTRL = ExtractBits(reg214, 22, 16);

        // 0x218: WR [7:0]
        uint reg218 = ReadSmn(@base + 0x218);
        s.WR = ExtractBits(reg218, 7, 0);
    }

    private void ReadTurnaround(uint @base, TimingSnapshot s)
    {
        // 0x220: RDRDSCL [29:24], RDRDSC [19:16], RDRDSD [11:8], RDRDDD [3:0]
        uint reg220 = ReadSmn(@base + 0x220);
        s.RDRDDD = ExtractBits(reg220, 3, 0);
        s.RDRDSD = ExtractBits(reg220, 11, 8);
        s.RDRDSC = ExtractBits(reg220, 19, 16);
        s.RDRDSCL = ExtractBits(reg220, 29, 24);

        // 0x224: WRWRSCL [29:24], WRWRSC [19:16], WRWRSD [11:8], WRWRDD [3:0]
        uint reg224 = ReadSmn(@base + 0x224);
        s.WRWRDD = ExtractBits(reg224, 3, 0);
        s.WRWRSD = ExtractBits(reg224, 11, 8);
        s.WRWRSC = ExtractBits(reg224, 19, 16);
        s.WRWRSCL = ExtractBits(reg224, 29, 24);

        // 0x228: RDWR [13:8], WRRD [3:0]
        uint reg228 = ReadSmn(@base + 0x228);
        s.WRRD = ExtractBits(reg228, 3, 0);
        s.RDWR = ExtractBits(reg228, 13, 8);
    }

    private void ReadControllerConfig(uint @base, TimingSnapshot s)
    {
        // 0x12C: PowerDown [28]
        uint reg12c = ReadSmn(@base + 0x12C);
        s.PowerDown = ExtractBits(reg12c, 28, 28) == 1;
    }

    private void ReadRfc(uint @base, TimingSnapshot s)
    {
        // 0x260: RFC [10:0] (tRFC1 in clocks)
        uint reg260 = ReadSmn(@base + 0x260);
        s.RFC = ExtractBits(reg260, 10, 0);

        // 0x264: RFC2 [10:0], RFC4 [26:16]
        uint reg264 = ReadSmn(@base + 0x264);
        s.RFC2 = ExtractBits(reg264, 10, 0);
        s.RFC4 = ExtractBits(reg264, 26, 16);
    }

    private void ReadMisc(uint @base, TimingSnapshot s)
    {
        // 0x230: REFI [15:0]
        uint reg230 = ReadSmn(@base + 0x230);
        s.REFI = ExtractBits(reg230, 15, 0);

        // 0x244: MOD [5:0], MRD [12:8]
        uint reg244 = ReadSmn(@base + 0x244);
        s.MOD = ExtractBits(reg244, 5, 0);
        s.MRD = ExtractBits(reg244, 12, 8);

        // 0x248: CKE [4:0], STAG [31:24]
        uint reg248 = ReadSmn(@base + 0x248);
        s.CKE = ExtractBits(reg248, 4, 0);
        s.STAG = ExtractBits(reg248, 31, 24);
    }

    private void ReadPhy(uint @base, TimingSnapshot s, int channel)
    {
        // PHY read delays — these are training results, not user-settable.
        // Mismatch between channels is normal (PHY training artifact, not an error).
        // 0x2A4: PHYRDL [4:0]
        uint regPhyRd = ReadSmn(@base + 0x2A4);
        int phyRdl = ExtractBits(regPhyRd, 4, 0);

        if (channel == 0)
            s.PHYRDL_A = phyRdl;
        else
            s.PHYRDL_B = phyRdl;
    }
}
