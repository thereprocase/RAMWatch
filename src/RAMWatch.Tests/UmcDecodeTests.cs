using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.Service.Hardware;

namespace RAMWatch.Tests;

/// <summary>
/// FakeHardwareAccess for the new TryReadSmn/TryReadMsr interface.
/// Keyed by absolute SMN address — returns canned values directly.
/// </summary>
internal sealed class FakeHardwareAccess : IHardwareAccess
{
    private readonly Dictionary<uint, uint> _smnValues;

    public bool IsAvailable => true;
    public string StatusDescription => "Fake hardware (test)";
    public string DriverName => "Fake";

    public FakeHardwareAccess(Dictionary<uint, uint> smnValues)
    {
        _smnValues = smnValues;
    }

    public bool TryReadSmn(uint address, out uint value)
    {
        value = _smnValues.TryGetValue(address, out uint v) ? v : 0;
        return true; // Always succeeds in test — returns 0 for unregistered addresses
    }

    public bool TryReadMsr(uint index, out ulong value) { value = 0; return false; }
    public void Dispose() { }
}

public class UmcDecodeTests
{
    // Helpers — register addresses are absolute SMN addresses (channel 0 = 0x50xxx)
    private static Dictionary<uint, uint> Regs(params (uint addr, uint value)[] entries)
    {
        var d = new Dictionary<uint, uint>();
        foreach (var (addr, value) in entries)
            d[addr] = value;
        return d;
    }

    private static TimingSnapshot? Decode(Dictionary<uint, uint> regs)
    {
        using var hw = new FakeHardwareAccess(regs);
        var decoder = new UmcDecode(hw);
        return decoder.ReadTimings("boot_test");
    }

    // ── Primary timing decode ────────────────────────────────────────────

    [Fact]
    public void ReadTimings_DecodesKnownPrimaries()
    {
        // 0x50204: RCDWR[29:24]=24, RCDRD[21:16]=22, RAS[14:8]=42, CL[5:0]=16
        uint reg204 = (24u << 24) | (22u << 16) | (42u << 8) | 16u;
        // 0x50208: RP[21:16]=22, RC[7:0]=64
        uint reg208 = (22u << 16) | 64u;

        var regs = Regs((0x50204, reg204), (0x50208, reg208));
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
    public void ReadTimings_DecodesGdmAndCmd2T_BothSet()
    {
        uint reg200 = (1u << 10) | (1u << 11);
        var snap = Decode(Regs((0x50200, reg200)));
        Assert.True(snap!.GDM);
        Assert.True(snap.Cmd2T);
    }

    [Fact]
    public void ReadTimings_DecodesGdmAndCmd2T_NeitherSet()
    {
        var snap = Decode(Regs((0x50200, 0u)));
        Assert.False(snap!.GDM);
        Assert.False(snap.Cmd2T);
    }

    // ── tRFC decode (corrected: same layout in both registers) ──────────

    [Fact]
    public void ReadTimings_DecodesRfc_NormalCase()
    {
        // Both 0x50260 and 0x50264 have: RFC1[10:0], RFC2[21:11], RFC4[31:22]
        uint packed = (157u << 22) | (315u << 11) | 630u;
        var snap = Decode(Regs((0x50260, packed), (0x50264, packed)));

        Assert.Equal(630, snap!.RFC);
        Assert.Equal(315, snap.RFC2);
        Assert.Equal(157, snap.RFC4);
    }

    [Fact]
    public void ReadTimings_Rfc_ReadbackBugWorkaround()
    {
        // tRFC1 readback bug: 0x50260 returns magic value 0x21060138
        // Decoder should use 0x50264 instead
        uint goodPacked = (100u << 22) | (200u << 11) | 400u;
        var snap = Decode(Regs((0x50260, 0x21060138), (0x50264, goodPacked)));

        Assert.Equal(400, snap!.RFC);
        Assert.Equal(200, snap.RFC2);
        Assert.Equal(100, snap.RFC4);
    }

    // ── Corrected register addresses (Sauron audit fixes) ───────────────

    [Fact]
    public void ReadTimings_DecodesModMrd_CorrectRegister()
    {
        // 0x50234: MOD[13:8]=27, MRD[5:0]=8
        uint reg234 = (27u << 8) | 8u;
        var snap = Decode(Regs((0x50234, reg234)));

        Assert.Equal(27, snap!.MOD);
        Assert.Equal(8, snap.MRD);
    }

    [Fact]
    public void ReadTimings_DecodesStag_CorrectRegister()
    {
        // 0x50250: STAG[26:16]=255
        uint reg250 = 255u << 16;
        var snap = Decode(Regs((0x50250, reg250)));

        Assert.Equal(255, snap!.STAG);
    }

    [Fact]
    public void ReadTimings_DecodesCke_CorrectRegister()
    {
        // 0x50254: CKE[28:24]=9
        uint reg254 = 9u << 24;
        var snap = Decode(Regs((0x50254, reg254)));

        Assert.Equal(9, snap!.CKE);
    }

    [Fact]
    public void ReadTimings_DecodesPhyRdl_CorrectRegister()
    {
        // 0x50258: PHYRDL[23:16]=28 (channel 0)
        // 0x150258: PHYRDL[23:16]=26 (channel 1)
        var regs = new Dictionary<uint, uint>
        {
            [0x50258] = 28u << 16,
            [0x150258] = 26u << 16,
        };

        using var hw = new FakeHardwareAccess(regs);
        var decoder = new UmcDecode(hw);
        var snap = decoder.ReadTimings("boot_test");

        Assert.Equal(28, snap!.PHYRDL_A);
        Assert.Equal(26, snap.PHYRDL_B);
    }

    // ── Secondary + turnaround timings ──────────────────────────────────

    [Fact]
    public void ReadTimings_DecodesSecondaries()
    {
        uint reg20c = (12u << 24) | (8u << 8) | 4u;
        uint reg210 = 20u;
        uint reg214 = (12u << 16) | (6u << 8) | 14u;
        uint reg218 = 18u;

        var snap = Decode(Regs((0x5020C, reg20c), (0x50210, reg210), (0x50214, reg214), (0x50218, reg218)));

        Assert.Equal(4, snap!.RRDS);
        Assert.Equal(8, snap.RRDL);
        Assert.Equal(12, snap.RTP);
        Assert.Equal(20, snap.FAW);
        Assert.Equal(14, snap.CWL);
        Assert.Equal(6, snap.WTRS);
        Assert.Equal(12, snap.WTRL);
        Assert.Equal(18, snap.WR);
    }

    [Fact]
    public void ReadTimings_DecodesTurnaround()
    {
        uint reg220 = (32u << 24) | (2u << 16) | (3u << 8) | 4u;
        uint reg224 = (33u << 24) | (5u << 16) | (6u << 8) | 7u;
        uint reg228 = (15u << 8) | 3u;

        var snap = Decode(Regs((0x50220, reg220), (0x50224, reg224), (0x50228, reg228)));

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

    // ── Edge cases ──────────────────────────────────────────────────────

    [Fact]
    public void ReadTimings_AllZeros_ReturnsAllZeroTimings()
    {
        var snap = Decode(new Dictionary<uint, uint>());

        Assert.NotNull(snap);
        Assert.Equal(0, snap!.CL);
        Assert.Equal(0, snap.RAS);
        Assert.Equal(0, snap.RFC);
        Assert.False(snap.GDM);
        Assert.False(snap.Cmd2T);
    }

    [Fact]
    public void ReadTimings_ReturnsNull_WhenHardwareUnavailable()
    {
        using var hw = new NullHardwareAccess();
        var decoder = new UmcDecode(hw);
        Assert.Null(decoder.ReadTimings("boot_test"));
    }

    [Fact]
    public void ReadTimings_SetsBootId()
    {
        var snap = Decode(new Dictionary<uint, uint>());
        Assert.Equal("boot_test", snap!.BootId);
    }

    [Fact]
    public void ReadTimings_ClockRatio()
    {
        // 0x50200[6:0] = ratio (7 bits). MCLK ≈ ratio/3 * 100
        // ratio=54 → 54/3*100 = 1800 MHz
        uint reg200 = 54u;
        var snap = Decode(Regs((0x50200, reg200)));

        Assert.Equal(1800, snap!.MemClockMhz);
    }

    [Fact]
    public void ReadTimings_ClockRatio_Bit7Reserved()
    {
        // Bit 7 of 0x50200 is reserved — must not be included in the ratio.
        // ratio=54 with bit 7 set (54 | 0x80 = 182) should still decode as 1800 MHz.
        uint reg200 = 54u | 0x80;
        var snap = Decode(Regs((0x50200, reg200)));

        Assert.Equal(1800, snap!.MemClockMhz);
    }
}
