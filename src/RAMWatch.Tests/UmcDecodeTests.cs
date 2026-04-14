using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.Service.Hardware;

namespace RAMWatch.Tests;

/// <summary>
/// FakeHardwareAccess records register values keyed by SMN address.
/// UmcDecode.ReadSmn writes the target address to PCI offset 0x60 then reads
/// the result from PCI offset 0x64 — both on bus 0, device 0, function 0.
/// The fake captures the last write to offset 0x60 and uses it to look up
/// the canned value to return on the next read of offset 0x64.
/// </summary>
internal sealed class FakeHardwareAccess : IHardwareAccess
{
    private readonly Dictionary<uint, uint> _smnValues;
    private uint _pendingAddress;

    public bool IsAvailable => true;
    public string StatusDescription => "Fake hardware (test)";

    public FakeHardwareAccess(Dictionary<uint, uint> smnValues)
    {
        _smnValues = smnValues;
    }

    public uint ReadPciConfigDword(uint bus, uint device, uint function, uint offset)
    {
        // Offset 0x64 is the SMN data register: return the value for the pending address.
        if (offset == 0x64)
            return _smnValues.TryGetValue(_pendingAddress, out uint value) ? value : 0;

        return 0;
    }

    public void WritePciConfigDword(uint bus, uint device, uint function, uint offset, uint value)
    {
        // Offset 0x60 is the SMN address register: record which SMN address is being requested.
        if (offset == 0x60)
            _pendingAddress = value;
    }

    public ulong ReadMsr(uint index) => 0;

    public void Dispose() { }
}

public class UmcDecodeTests
{
    // UMC0 base address (channel 0)
    private const uint Base = 0x50000;

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Place a value at a register offset relative to the UMC0 base.
    /// </summary>
    private static Dictionary<uint, uint> Regs(params (uint offset, uint value)[] entries)
    {
        var d = new Dictionary<uint, uint>();
        foreach (var (offset, value) in entries)
            d[Base + offset] = value;
        return d;
    }

    private static TimingSnapshot? Decode(Dictionary<uint, uint> regs)
    {
        using var hw = new FakeHardwareAccess(regs);
        var decoder = new UmcDecode(hw);
        return decoder.ReadTimings("boot_test");
    }

    // ── Primary timing decode ────────────────────────────────────────────────

    [Fact]
    public void ReadTimings_DecodesKnownPrimaries()
    {
        // Register 0x204 layout: RCDWR[29:24], RCDRD[21:16], RAS[14:8], CL[5:0]
        // CL=16, RAS=42, RCDRD=22, RCDWR=24
        // CL:   0b010000  = 16   → bits [5:0]
        // RAS:  0b101010  = 42   → bits [14:8]
        // RCDRD:0b010110  = 22   → bits [21:16]
        // RCDWR:0b011000  = 24   → bits [29:24]
        uint reg204 = (24u << 24) | (22u << 16) | (42u << 8) | 16u;

        // Register 0x208 layout: RP[21:16], RC[7:0]
        // RP=22, RC=64
        uint reg208 = (22u << 16) | 64u;

        // Register 0x200 layout: Cmd2T[10], GDM[11]
        // Both false for this test
        uint reg200 = 0u;

        var regs = Regs((0x200, reg200), (0x204, reg204), (0x208, reg208));
        var snap = Decode(regs);

        Assert.NotNull(snap);
        Assert.Equal(16, snap!.CL);
        Assert.Equal(42, snap.RAS);
        Assert.Equal(22, snap.RCDRD);
        Assert.Equal(24, snap.RCDWR);
        Assert.Equal(22, snap.RP);
        Assert.Equal(64, snap.RC);
    }

    [Fact]
    public void ReadTimings_DecodesGdmAndCmd2T_WhenBothSet()
    {
        // Register 0x200: Cmd2T is bit 10, GDM is bit 11
        uint reg200 = (1u << 10) | (1u << 11);
        var regs = Regs((0x200, reg200));
        var snap = Decode(regs);

        Assert.NotNull(snap);
        Assert.True(snap!.GDM);
        Assert.True(snap.Cmd2T);
    }

    [Fact]
    public void ReadTimings_DecodesGdmAndCmd2T_WhenNeitherSet()
    {
        var regs = Regs((0x200, 0u));
        var snap = Decode(regs);

        Assert.NotNull(snap);
        Assert.False(snap!.GDM);
        Assert.False(snap.Cmd2T);
    }

    [Fact]
    public void ReadTimings_DecodesGdm_OnlyGdmSet()
    {
        uint reg200 = 1u << 11; // GDM only
        var regs = Regs((0x200, reg200));
        var snap = Decode(regs);

        Assert.NotNull(snap);
        Assert.True(snap!.GDM);
        Assert.False(snap.Cmd2T);
    }

    [Fact]
    public void ReadTimings_DecodesCmd2T_OnlyCmd2TSet()
    {
        uint reg200 = 1u << 10; // Cmd2T only
        var regs = Regs((0x200, reg200));
        var snap = Decode(regs);

        Assert.NotNull(snap);
        Assert.False(snap!.GDM);
        Assert.True(snap.Cmd2T);
    }

    // ── RFC decode ───────────────────────────────────────────────────────────

    [Fact]
    public void ReadTimings_DecodesRfc()
    {
        // Register 0x260: RFC[10:0] (tRFC1 in clocks)
        // Register 0x264: RFC2[10:0], RFC4[26:16]
        uint rfc1 = 630;   // bits [10:0] in reg 0x260
        uint rfc2 = 315;   // bits [10:0] in reg 0x264
        uint rfc4 = 157;   // bits [26:16] in reg 0x264

        uint reg260 = rfc1;
        uint reg264 = (rfc4 << 16) | rfc2;

        var regs = Regs((0x260, reg260), (0x264, reg264));
        var snap = Decode(regs);

        Assert.NotNull(snap);
        Assert.Equal((int)rfc1, snap!.RFC);
        Assert.Equal((int)rfc2, snap.RFC2);
        Assert.Equal((int)rfc4, snap.RFC4);
    }

    [Fact]
    public void ReadTimings_RfcFromReg260And264()
    {
        // RFC4 sits at bits [26:16] of reg 0x264, not [10:0].
        // Verify that RFC4=8 does not bleed into RFC2.
        uint reg260 = 500u;
        uint reg264 = (8u << 16) | 250u; // RFC4=8, RFC2=250
        var regs = Regs((0x260, reg260), (0x264, reg264));
        var snap = Decode(regs);

        Assert.NotNull(snap);
        Assert.Equal(500, snap!.RFC);
        Assert.Equal(250, snap.RFC2);
        Assert.Equal(8, snap.RFC4);
    }

    // ── All-zeros register ───────────────────────────────────────────────────

    [Fact]
    public void ReadTimings_AllZeroRegisters_ReturnsAllZeroTimings()
    {
        // Empty dictionary — all SMN reads return 0
        var snap = Decode(new Dictionary<uint, uint>());

        Assert.NotNull(snap);
        Assert.Equal(0, snap!.CL);
        Assert.Equal(0, snap.RAS);
        Assert.Equal(0, snap.RCDRD);
        Assert.Equal(0, snap.RCDWR);
        Assert.Equal(0, snap.RP);
        Assert.Equal(0, snap.RC);
        Assert.Equal(0, snap.CWL);
        Assert.Equal(0, snap.RFC);
        Assert.Equal(0, snap.RFC2);
        Assert.Equal(0, snap.RFC4);
        Assert.Equal(0, snap.RRDS);
        Assert.Equal(0, snap.RRDL);
        Assert.Equal(0, snap.FAW);
        Assert.Equal(0, snap.WTRS);
        Assert.Equal(0, snap.WTRL);
        Assert.Equal(0, snap.WR);
        Assert.Equal(0, snap.RDRDSCL);
        Assert.Equal(0, snap.WRWRSCL);
        Assert.False(snap.GDM);
        Assert.False(snap.Cmd2T);
    }

    // ── All-ones (0xFFFFFFFF) register ───────────────────────────────────────

    [Fact]
    public void ReadTimings_AllOnesRegisters_ReturnsMaxBitfieldValues()
    {
        // 0xFFFFFFFF in every register — each field should be all ones within its mask.
        var allOnes = new Dictionary<uint, uint>();
        foreach (uint offset in new uint[]
        {
            0x200, 0x204, 0x208, 0x20C, 0x210, 0x214, 0x218,
            0x220, 0x224, 0x228, 0x230, 0x244, 0x248,
            0x12C, 0x260, 0x264, 0x2A4
        })
        {
            allOnes[Base + offset] = 0xFFFFFFFF;
            // Also register the UMC1 base for PHYRDL_B
            allOnes[0x150000 + offset] = 0xFFFFFFFF;
        }

        var snap = Decode(allOnes);

        Assert.NotNull(snap);

        // CL[5:0]: max = 63
        Assert.Equal(63, snap!.CL);
        // RAS[14:8]: max = 127
        Assert.Equal(127, snap.RAS);
        // RCDRD[21:16]: max = 63
        Assert.Equal(63, snap.RCDRD);
        // RCDWR[29:24]: max = 63
        Assert.Equal(63, snap.RCDWR);
        // RP[21:16]: max = 63
        Assert.Equal(63, snap.RP);
        // RC[7:0]: max = 255
        Assert.Equal(255, snap.RC);
        // CWL[5:0]: max = 63
        Assert.Equal(63, snap.CWL);
        // RFC[10:0]: max = 2047
        Assert.Equal(2047, snap.RFC);
        // RFC2[10:0]: max = 2047
        Assert.Equal(2047, snap.RFC2);
        // RFC4[26:16]: max = 2047
        Assert.Equal(2047, snap.RFC4);
        // RDRDSCL[29:24]: max = 63
        Assert.Equal(63, snap.RDRDSCL);
        // WRWRSCL[29:24]: max = 63
        Assert.Equal(63, snap.WRWRSCL);
        // GDM[11], Cmd2T[10]: both true
        Assert.True(snap.GDM);
        Assert.True(snap.Cmd2T);
        // PHYRDL[4:0]: max = 31
        Assert.Equal(31, snap.PHYRDL_A);
        Assert.Equal(31, snap.PHYRDL_B);
    }

    // ── Secondary timings ────────────────────────────────────────────────────

    [Fact]
    public void ReadTimings_DecodesSecondaryTimings()
    {
        // 0x20C: RTP[28:24], RRDL[12:8], RRDS[4:0]
        uint reg20c = (12u << 24) | (8u << 8) | 4u; // RTP=12, RRDL=8, RRDS=4

        // 0x210: FAW[7:0]
        uint reg210 = 20u;

        // 0x214: WTRL[22:16], WTRS[12:8], CWL[5:0]
        uint reg214 = (12u << 16) | (6u << 8) | 14u; // WTRL=12, WTRS=6, CWL=14

        // 0x218: WR[7:0]
        uint reg218 = 18u;

        var regs = Regs(
            (0x20C, reg20c),
            (0x210, reg210),
            (0x214, reg214),
            (0x218, reg218)
        );
        var snap = Decode(regs);

        Assert.NotNull(snap);
        Assert.Equal(4, snap!.RRDS);
        Assert.Equal(8, snap.RRDL);
        Assert.Equal(12, snap.RTP);
        Assert.Equal(20, snap.FAW);
        Assert.Equal(14, snap.CWL);
        Assert.Equal(6, snap.WTRS);
        Assert.Equal(12, snap.WTRL);
        Assert.Equal(18, snap.WR);
    }

    // ── Turnaround timings ───────────────────────────────────────────────────

    [Fact]
    public void ReadTimings_DecodesTurnaroundTimings()
    {
        // 0x220: RDRDSCL[29:24], RDRDSC[19:16], RDRDSD[11:8], RDRDDD[3:0]
        uint reg220 = (32u << 24) | (2u << 16) | (3u << 8) | 4u;

        // 0x224: WRWRSCL[29:24], WRWRSC[19:16], WRWRSD[11:8], WRWRDD[3:0]
        uint reg224 = (33u << 24) | (5u << 16) | (6u << 8) | 7u;

        // 0x228: RDWR[13:8], WRRD[3:0]
        uint reg228 = (15u << 8) | 3u;

        var regs = Regs((0x220, reg220), (0x224, reg224), (0x228, reg228));
        var snap = Decode(regs);

        Assert.NotNull(snap);
        Assert.Equal(4, snap!.RDRDDD);
        Assert.Equal(3, snap.RDRDSD);
        Assert.Equal(2, snap.RDRDSC);
        Assert.Equal(32, snap.RDRDSCL);
        Assert.Equal(7, snap.WRWRDD);
        Assert.Equal(6, snap.WRWRSD);
        Assert.Equal(5, snap.WRWRSC);
        Assert.Equal(33, snap.WRWRSCL);
        Assert.Equal(3, snap.WRRD);
        Assert.Equal(15, snap.RDWR);
    }

    // ── PHY read latency ─────────────────────────────────────────────────────

    [Fact]
    public void ReadTimings_DecodesPhy_BothChannels()
    {
        // 0x2A4: PHYRDL[4:0] — distinct values on each channel
        var regs = new Dictionary<uint, uint>
        {
            [0x50000 + 0x2A4] = 7u,   // channel 0 PHYRDL = 7
            [0x150000 + 0x2A4] = 9u,  // channel 1 PHYRDL = 9
        };

        using var hw = new FakeHardwareAccess(regs);
        var decoder = new UmcDecode(hw);
        var snap = decoder.ReadTimings("boot_test");

        Assert.NotNull(snap);
        Assert.Equal(7, snap!.PHYRDL_A);
        Assert.Equal(9, snap.PHYRDL_B);
    }

    // ── Unavailable hardware ─────────────────────────────────────────────────

    [Fact]
    public void ReadTimings_ReturnsNull_WhenHardwareUnavailable()
    {
        using var hw = new NullHardwareAccess();
        var decoder = new UmcDecode(hw);
        var snap = decoder.ReadTimings("boot_test");

        Assert.Null(snap);
    }

    // ── Boot ID is propagated ────────────────────────────────────────────────

    [Fact]
    public void ReadTimings_SetsBootId()
    {
        var snap = Decode(new Dictionary<uint, uint>());

        Assert.NotNull(snap);
        Assert.Equal("boot_test", snap!.BootId);
    }
}
