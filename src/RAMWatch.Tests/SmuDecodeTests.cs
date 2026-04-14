using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.Service.Hardware;

namespace RAMWatch.Tests;

// FakeHardwareAccess is defined in UmcDecodeTests.cs (same assembly).

/// <summary>
/// Tests for SmuDecode voltage decode logic and SmuPowerTableReader
/// layout table. The SMU power table path requires a live PawnIO driver
/// and is therefore not exercised here. What can be tested without hardware:
///
/// - SVI2 VID-to-voltage formula
/// - SOC plane address mapping per CPU family
/// - PM table layout lookup (version → byte offsets)
/// - PopulateClockVoltage behaviour with mocked SMN reads
/// </summary>
public class SmuDecodeTests
{
    // ── VID-to-voltage formula ───────────────────────────────────────────

    [Theory]
    [InlineData(0u, 1.55)]       // VID 0 → maximum voltage
    [InlineData(1u, 1.54375)]    // 1.55 - 1 * 0.00625
    [InlineData(72u, 1.1)]       // 1.55 - 72 * 0.00625 = 1.1
    [InlineData(144u, 0.65)]     // 1.55 - 144 * 0.00625 = 0.65
    public void VidToVoltage_Formula_MatchesSpec(uint vid, double expected)
    {
        double result = SmuDecode.VidToVoltage(vid);
        Assert.Equal(expected, result, precision: 6);
    }

    // ── SOC plane address mapping ────────────────────────────────────────

    [Theory]
    [InlineData(CpuDetect.CpuFamily.Zen, 0x5A010u)]        // Plane 1
    [InlineData(CpuDetect.CpuFamily.ZenPlus, 0x5A010u)]    // Plane 1
    [InlineData(CpuDetect.CpuFamily.Zen2, 0x5A00Cu)]       // Plane 0 (swapped)
    [InlineData(CpuDetect.CpuFamily.Zen3, 0x5A00Cu)]       // Plane 0
    [InlineData(CpuDetect.CpuFamily.Zen4, 0x5A00Cu)]       // Plane 0
    [InlineData(CpuDetect.CpuFamily.Unknown, 0u)]           // Unsupported
    [InlineData(CpuDetect.CpuFamily.Zen5, 0u)]              // Not yet mapped
    public void GetSocPlaneAddress_ReturnsExpected(CpuDetect.CpuFamily family, uint expected)
    {
        uint addr = SmuDecode.GetSocPlaneAddress(family);
        Assert.Equal(expected, addr);
    }

    // ── PopulateClockVoltage — voltage path ──────────────────────────────

    [Fact]
    public void PopulateClockVoltage_ReadsVSoc_WhenBitsClean()
    {
        // VID = 72 → 1.55 - 72 * 0.00625 = 1.1 V
        // bits [15:8] = 0 (status clean)
        // bits [7:0] = 0 (current — not used)
        uint vid = 72u;
        uint raw = vid << 16;  // bits [23:16] = VID, [15:8] = 0

        uint socAddr = SmuDecode.GetSocPlaneAddress(CpuDetect.CpuFamily.Zen3);
        var regs = new Dictionary<uint, uint> { [socAddr] = raw };

        using var hw = new FakeHardwareAccess(regs);
        using var smu = new SmuDecode(hw, CpuDetect.CpuFamily.Zen3);

        var snap = MakeSnapshot();
        smu.PopulateClockVoltage(snap);

        Assert.Equal(1.1, snap.VSoc, precision: 4);
    }

    [Fact]
    public void PopulateClockVoltage_DiscardsVSoc_WhenStatusBitsSet()
    {
        // bits [15:8] != 0 → SMU is mid-update, discard
        uint vid = 72u;
        uint raw = (vid << 16) | 0x0100u;  // bit 8 set

        uint socAddr = SmuDecode.GetSocPlaneAddress(CpuDetect.CpuFamily.Zen3);
        var regs = new Dictionary<uint, uint> { [socAddr] = raw };

        using var hw = new FakeHardwareAccess(regs);
        using var smu = new SmuDecode(hw, CpuDetect.CpuFamily.Zen3);

        var snap = MakeSnapshot();
        smu.PopulateClockVoltage(snap);

        Assert.Equal(0.0, snap.VSoc);
    }

    [Fact]
    public void PopulateClockVoltage_DiscardsVSoc_WhenOutOfRange()
    {
        // VID = 0 → 1.55 V (still plausible), but VID very large → voltage < 0.5
        // Use VID = 168: 1.55 - 168 * 0.00625 = 0.5 (edge, should be accepted)
        // Use VID = 169: 1.55 - 169 * 0.00625 = 0.49375 (below range, discard)
        uint vid = 169u;
        uint raw = vid << 16;

        uint socAddr = SmuDecode.GetSocPlaneAddress(CpuDetect.CpuFamily.Zen3);
        var regs = new Dictionary<uint, uint> { [socAddr] = raw };

        using var hw = new FakeHardwareAccess(regs);
        using var smu = new SmuDecode(hw, CpuDetect.CpuFamily.Zen3);

        var snap = MakeSnapshot();
        smu.PopulateClockVoltage(snap);

        Assert.Equal(0.0, snap.VSoc);
    }

    [Fact]
    public void PopulateClockVoltage_NoOp_WhenDriverUnavailable()
    {
        using var hw = new NullHardwareAccess();
        using var smu = new SmuDecode(hw, CpuDetect.CpuFamily.Zen3);

        var snap = MakeSnapshot();
        smu.PopulateClockVoltage(snap);  // must not throw

        Assert.Equal(0.0, snap.VSoc);
        Assert.Equal(0, snap.FclkMhz);
        Assert.Equal(0, snap.UclkMhz);
    }

    [Fact]
    public void PopulateClockVoltage_NoOp_ForUnknownFamily()
    {
        // Unknown family → soc address = 0 → voltage skipped
        uint raw = 72u << 16;
        var regs = new Dictionary<uint, uint> { [0x5A00Cu] = raw, [0x5A010u] = raw };

        using var hw = new FakeHardwareAccess(regs);
        using var smu = new SmuDecode(hw, CpuDetect.CpuFamily.Unknown);

        var snap = MakeSnapshot();
        smu.PopulateClockVoltage(snap);

        Assert.Equal(0.0, snap.VSoc);
    }

    // ── PM table layout lookup ───────────────────────────────────────────

    [Theory]
    // Zen2 generic v1
    [InlineData(0x000200u, true, 0xB0u, 0xB8u)]
    // Zen2 v2 revision
    [InlineData(0x240802u, true, 0xBCu, 0xC4u)]
    // Zen2 v3 revision (common Matisse)
    [InlineData(0x240903u, true, 0xC0u, 0xC8u)]
    [InlineData(0x000203u, true, 0xC0u, 0xC8u)]
    // Zen3 generic
    [InlineData(0x000300u, true, 0xC0u, 0xC8u)]
    // Zen3 Vermeer revision
    [InlineData(0x380905u, true, 0xC0u, 0xC8u)]
    [InlineData(0x380805u, true, 0xC0u, 0xC8u)]
    // Zen4 Raphael
    [InlineData(0x540004u, true, 0x118u, 0x128u)]
    [InlineData(0x540208u, true, 0x11Cu, 0x12Cu)]
    // Unknown version
    [InlineData(0xDEADBEEFu, false, 0u, 0u)]
    public void GetLayout_ReturnsExpected(uint version, bool valid, uint fclkOffset, uint uclkOffset)
    {
        var layout = SmuPowerTableReader.GetLayout(version);
        Assert.Equal(valid, layout.IsValid);
        if (valid)
        {
            Assert.Equal(fclkOffset, layout.FclkByteOffset);
            Assert.Equal(uclkOffset, layout.UclkByteOffset);
        }
    }

    [Fact]
    public void GetLayout_AllKnownVersions_HavePositiveTableSize()
    {
        uint[] knownVersions =
        [
            0x000200, 0x240003, 0x240802, 0x240902, 0x000202,
            0x240503, 0x240603, 0x240703, 0x240803, 0x240903, 0x000203,
            0x2D0008, 0x2D0803, 0x2D0903,
            0x380005, 0x380505, 0x380605, 0x380705, 0x380804, 0x380805, 0x380904, 0x380905, 0x000300,
            0x540100, 0x540101, 0x540102, 0x540103, 0x540104, 0x540105, 0x540108,
            0x540000, 0x540001, 0x540002, 0x540003, 0x540004, 0x540005, 0x540208,
        ];

        foreach (uint v in knownVersions)
        {
            var layout = SmuPowerTableReader.GetLayout(v);
            Assert.True(layout.TableSizeBytes > 0, $"Version 0x{v:X8} has zero table size");
            Assert.True(layout.FclkByteOffset > 0, $"Version 0x{v:X8} has zero FCLK offset");
            Assert.True(layout.UclkByteOffset > 0, $"Version 0x{v:X8} has zero UCLK offset");
        }
    }

    [Fact]
    public void GetLayout_AllKnownVersions_FclkOffsetWithinTableSize()
    {
        uint[] knownVersions =
        [
            0x000200, 0x240003, 0x240802, 0x240902, 0x000202,
            0x240503, 0x240603, 0x240703, 0x240803, 0x240903, 0x000203,
            0x2D0008, 0x2D0803, 0x2D0903,
            0x380005, 0x380505, 0x380605, 0x380705, 0x380804, 0x380805, 0x380904, 0x380905, 0x000300,
            0x540100, 0x540101, 0x540102, 0x540103, 0x540104, 0x540105, 0x540108,
            0x540000, 0x540001, 0x540002, 0x540003, 0x540004, 0x540005, 0x540208,
        ];

        foreach (uint v in knownVersions)
        {
            var layout = SmuPowerTableReader.GetLayout(v);
            // Each offset points to a float (4 bytes) — must be within the table
            Assert.True(layout.FclkByteOffset + 4 <= layout.TableSizeBytes,
                $"Version 0x{v:X8}: FCLK offset 0x{layout.FclkByteOffset:X} past end of table 0x{layout.TableSizeBytes:X}");
            Assert.True(layout.UclkByteOffset + 4 <= layout.TableSizeBytes,
                $"Version 0x{v:X8}: UCLK offset 0x{layout.UclkByteOffset:X} past end of table 0x{layout.TableSizeBytes:X}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static TimingSnapshot MakeSnapshot() =>
        new TimingSnapshot
        {
            SnapshotId = Guid.NewGuid().ToString("N"),
            Timestamp = DateTime.UtcNow,
            BootId = "test_boot",
        };

    // ── SnapClockMhz tests ──────────────────────────────────────────────

    [Theory]
    [InlineData(1900.0f, 1900)]   // exact
    [InlineData(1901.7f, 1900)]   // +1.7 jitter → snap to 1900
    [InlineData(1898.3f, 1900)]   // -1.7 jitter → snap to 1900
    [InlineData(1902.0f, 1900)]   // +2 → snap (within 3)
    [InlineData(1903.0f, 1900)]   // +3 → snap (boundary)
    [InlineData(1904.0f, 1904)]   // +4 → too far, keep raw
    [InlineData(1800.0f, 1800)]   // DDR4-3600 exact
    [InlineData(1801.5f, 1800)]   // DDR4-3600 with jitter
    [InlineData(2000.0f, 2000)]   // DDR4-4000 exact
    [InlineData(1933.3f, 1933)]   // DDR4-3866 = 1933.33, snaps to 1933
    [InlineData(1966.7f, 1967)]   // DDR4-3933 = 1966.67, snaps to 1967
    public void SnapClockMhz_SnapsToNearestIncrement(float raw, int expected)
    {
        Assert.Equal(expected, SmuPowerTableReader.SnapClockMhz(raw));
    }
}
