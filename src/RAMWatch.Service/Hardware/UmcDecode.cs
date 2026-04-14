using RAMWatch.Core.Models;

namespace RAMWatch.Service.Hardware;

/// <summary>
/// Decode DDR4 DRAM timings from AMD Unified Memory Controller (UMC) registers.
///
/// Register offsets are hardware facts from AMD Processor Programming References.
/// The decode implementation is original GPL-3.0 work.
///
/// UMC registers live in the SMN address space. PawnIO's ioctl_read_smn
/// handles the indirect access atomically in the kernel.
///
/// Channel bases: UMC0 = 0x50000, UMC1 = 0x150000 (offset by 0x100000).
/// All register addresses below are absolute for channel 0.
/// Channel offset is applied via bitwise OR (lower 20 bits are zero).
/// </summary>
public sealed class UmcDecode
{
    private readonly IHardwareAccess _hw;

    // UMC channel bases — channel offset is i << 20, OR'd with register address
    private static readonly uint[] ChannelBases = [0x000000, 0x100000];

    // tRFC readback bug: this magic value in register 0x50260 indicates
    // the ComboAM4v2PI 1.2.0.x bug. Use 0x50264 instead.
    private const uint TrfcBugValue = 0x21060138;

    public UmcDecode(IHardwareAccess hw)
    {
        _hw = hw;
    }

    /// <summary>
    /// Read all timings from the UMC. Returns null if driver unavailable.
    /// </summary>
    public TimingSnapshot? ReadTimings(string bootId)
    {
        if (!_hw.IsAvailable) return null;

        try
        {
            var snapshot = new TimingSnapshot
            {
                SnapshotId = Guid.NewGuid().ToString("N"),
                Timestamp = DateTime.UtcNow,
                BootId = bootId,
            };

            // Read from channel 0
            uint ch0 = ChannelBases[0];

            ReadClockRatio(ch0, snapshot);
            ReadPrimaries(ch0, snapshot);
            ReadSecondaries(ch0, snapshot);
            ReadTurnaround(ch0, snapshot);
            ReadControllerConfig(ch0, snapshot);
            ReadRfc(ch0, snapshot);
            ReadMisc(ch0, snapshot);

            // PHY timings from both channels (mismatch is normal)
            ReadPhy(ch0, snapshot, channel: 0);
            if (ChannelBases.Length > 1)
                ReadPhy(ChannelBases[1], snapshot, channel: 1);

            return snapshot;
        }
        catch
        {
            return null;
        }
    }

    private uint ReadSmn(uint address)
    {
        if (!_hw.TryReadSmn(address, out uint value))
            return 0;
        return value;
    }

    private static int Bits(uint value, int hi, int lo)
    {
        uint mask = (1u << (hi - lo + 1)) - 1;
        return (int)((value >> lo) & mask);
    }

    // ── Register decode groups ───────────────────────────────
    // Addresses are absolute SMN addresses for channel 0 (0x50xxx).
    // Channel offset is OR'd by the caller.

    private void ReadClockRatio(uint chOffset, TimingSnapshot s)
    {
        // 0x50200 [6:0] = clock ratio divider. MCLK ≈ ratio / 3.0 * BCLK.
        // Bits [6:0] per ZenStates-Core Ddr4Timings.cs — bit 7 is reserved.
        // Full frequency computation requires SMU power table (Phase 2 stretch).
        // For now, store the raw ratio — the GUI can display it or compute from BCLK=100.
        uint reg200 = ReadSmn(chOffset | 0x50200);
        int ratio = Bits(reg200, 6, 0);
        if (ratio > 0)
        {
            // Approximate: MCLK = ratio / 3 * 100 (BCLK assumed 100 MHz)
            s.MemClockMhz = (int)Math.Round(ratio / 3.0 * 100);
        }
    }

    private void ReadPrimaries(uint chOffset, TimingSnapshot s)
    {
        // 0x50200: Cmd2T [10], GDM [11]
        uint reg200 = ReadSmn(chOffset | 0x50200);
        s.Cmd2T = Bits(reg200, 10, 10) == 1;
        s.GDM = Bits(reg200, 11, 11) == 1;

        // 0x50204: RCDWR [29:24], RCDRD [21:16], RAS [14:8], CL [5:0]
        uint reg204 = ReadSmn(chOffset | 0x50204);
        s.CL = Bits(reg204, 5, 0);
        s.RAS = Bits(reg204, 14, 8);
        s.RCDRD = Bits(reg204, 21, 16);
        s.RCDWR = Bits(reg204, 29, 24);

        // 0x50208: RC [7:0], RP [21:16]
        uint reg208 = ReadSmn(chOffset | 0x50208);
        s.RC = Bits(reg208, 7, 0);
        s.RP = Bits(reg208, 21, 16);
    }

    private void ReadSecondaries(uint chOffset, TimingSnapshot s)
    {
        // 0x5020C: RTP [28:24], RRDL [12:8], RRDS [4:0]
        uint reg20c = ReadSmn(chOffset | 0x5020C);
        s.RRDS = Bits(reg20c, 4, 0);
        s.RRDL = Bits(reg20c, 12, 8);
        s.RTP = Bits(reg20c, 28, 24);

        // 0x50210: FAW [7:0]
        uint reg210 = ReadSmn(chOffset | 0x50210);
        s.FAW = Bits(reg210, 7, 0);

        // 0x50214: WTRL [22:16], WTRS [12:8], CWL [5:0]
        uint reg214 = ReadSmn(chOffset | 0x50214);
        s.CWL = Bits(reg214, 5, 0);
        s.WTRS = Bits(reg214, 12, 8);
        s.WTRL = Bits(reg214, 22, 16);

        // 0x50218: WR [7:0]
        uint reg218 = ReadSmn(chOffset | 0x50218);
        s.WR = Bits(reg218, 7, 0);
    }

    private void ReadTurnaround(uint chOffset, TimingSnapshot s)
    {
        // 0x50220: RDRDSCL [29:24], RDRDSC [19:16], RDRDSD [11:8], RDRDDD [3:0]
        uint reg220 = ReadSmn(chOffset | 0x50220);
        s.RDRDDD = Bits(reg220, 3, 0);
        s.RDRDSD = Bits(reg220, 11, 8);
        s.RDRDSC = Bits(reg220, 19, 16);
        s.RDRDSCL = Bits(reg220, 29, 24);

        // 0x50224: WRWRSCL [29:24], WRWRSC [19:16], WRWRSD [11:8], WRWRDD [3:0]
        uint reg224 = ReadSmn(chOffset | 0x50224);
        s.WRWRDD = Bits(reg224, 3, 0);
        s.WRWRSD = Bits(reg224, 11, 8);
        s.WRWRSC = Bits(reg224, 19, 16);
        s.WRWRSCL = Bits(reg224, 29, 24);

        // 0x50228: RDWR [13:8], WRRD [3:0]
        uint reg228 = ReadSmn(chOffset | 0x50228);
        s.WRRD = Bits(reg228, 3, 0);
        s.RDWR = Bits(reg228, 13, 8);
    }

    private void ReadControllerConfig(uint chOffset, TimingSnapshot s)
    {
        // 0x5012C: PowerDown [28]
        uint reg12c = ReadSmn(chOffset | 0x5012C);
        s.PowerDown = Bits(reg12c, 28, 28) == 1;
    }

    private void ReadRfc(uint chOffset, TimingSnapshot s)
    {
        // Both 0x50260 and 0x50264 contain the same packed layout:
        // RFC1 [10:0], RFC2 [21:11], RFC4 [31:22]
        //
        // tRFC1 readback bug (ComboAM4v2PI 1.2.0.x): register 0x50260
        // returns magic value 0x21060138. If so, use 0x50264 instead.
        uint reg260 = ReadSmn(chOffset | 0x50260);
        uint reg264 = ReadSmn(chOffset | 0x50264);

        uint rfcReg;
        if (reg260 != reg264)
            rfcReg = (reg260 != TrfcBugValue) ? reg260 : reg264;
        else
            rfcReg = reg260;

        s.RFC = Bits(rfcReg, 10, 0);
        s.RFC2 = Bits(rfcReg, 21, 11);
        s.RFC4 = Bits(rfcReg, 31, 22);
    }

    private void ReadMisc(uint chOffset, TimingSnapshot s)
    {
        // 0x50230: REFI [15:0]
        uint reg230 = ReadSmn(chOffset | 0x50230);
        s.REFI = Bits(reg230, 15, 0);

        // 0x50234: MODPDA [29:24], MRDPDA [21:16], MOD [13:8], MRD [5:0]
        uint reg234 = ReadSmn(chOffset | 0x50234);
        s.MRD = Bits(reg234, 5, 0);
        s.MOD = Bits(reg234, 13, 8);

        // 0x50250: STAG [26:16]
        uint reg250 = ReadSmn(chOffset | 0x50250);
        s.STAG = Bits(reg250, 26, 16);

        // 0x50254: CKE [28:24]
        uint reg254 = ReadSmn(chOffset | 0x50254);
        s.CKE = Bits(reg254, 28, 24);
    }

    private void ReadPhy(uint chOffset, TimingSnapshot s, int channel)
    {
        // 0x50258: PHYRDL [23:16] (per-channel, mismatch is normal — PHY training artifact)
        uint reg258 = ReadSmn(chOffset | 0x50258);
        int phyRdl = Bits(reg258, 23, 16);

        if (channel == 0)
            s.PHYRDL_A = phyRdl;
        else
            s.PHYRDL_B = phyRdl;
    }
}
