using Xunit;
using RAMWatch.Core;
using RAMWatch.Core.Models;
using RAMWatch.Service.Services;

namespace RAMWatch.Tests;

/// <summary>
/// Lock-in tests for TimingSnapshotFields — single source of truth for
/// enumerating TimingSnapshot by category.
///
/// These tests are intentionally golden-number assertions. They fail loudly when
/// a field is added to TimingSnapshot without a corresponding update to the
/// helper. Update the expected counts intentionally and with a comment.
/// </summary>
public class TimingSnapshotFieldsTests
{
    // ─── 1. Field-count regression ───────────────────────────────────────────

    [Fact]
    public void FieldCounts_MatchGoldenNumbers()
    {
        // Update these numbers intentionally. Adding a category field without
        // updating this assertion means the lock is working correctly.
        Assert.Equal(3,  TimingSnapshotFields.Clocks.Length);
        Assert.Equal(32, TimingSnapshotFields.Timings.Length);
        Assert.Equal(2,  TimingSnapshotFields.Phy.Length);
        Assert.Equal(3,  TimingSnapshotFields.Booleans.Length);
        Assert.Equal(8,  TimingSnapshotFields.Voltages.Length);
        Assert.Equal(5,  TimingSnapshotFields.SignalIntegrityNumeric.Length);
        Assert.Equal(6,  TimingSnapshotFields.SignalIntegrityStrings.Length);
    }

    // ─── 2. GetIntField round-trip ────────────────────────────────────────────

    [Fact]
    public void GetIntField_AllNamedFields_MatchTupleSelector()
    {
        // Each field has a unique sentinel value so a name→wrong-field mapping
        // is caught by the equality check.
        var probe = MakeProbeSnapshot();

        var allIntCategories = TimingSnapshotFields.Clocks
            .Concat(TimingSnapshotFields.Timings)
            .Concat(TimingSnapshotFields.Phy)
            .Select(t => (t.Name, Expected: t.Get(probe)));

        foreach (var (name, expected) in allIntCategories)
        {
            int? actual = TimingSnapshotFields.GetIntField(probe, name);
            Assert.True(actual.HasValue,
                $"GetIntField returned null for '{name}' — name may be missing from the switch");
            Assert.Equal(expected, actual.Value);
        }
    }

    [Fact]
    public void GetIntField_BooleanFields_ProjectedToZeroOrOne()
    {
        // Booleans are 0/1 in GetIntField, not the raw Func<...,bool> result.
        var probeTrue = MakeProbeSnapshot(gdm: true, cmd2T: true, powerDown: true);
        var probeFalse = MakeProbeSnapshot(gdm: false, cmd2T: false, powerDown: false);

        Assert.Equal(1, TimingSnapshotFields.GetIntField(probeTrue,  "GDM"));
        Assert.Equal(0, TimingSnapshotFields.GetIntField(probeFalse, "GDM"));
        Assert.Equal(1, TimingSnapshotFields.GetIntField(probeTrue,  "Cmd2T"));
        Assert.Equal(0, TimingSnapshotFields.GetIntField(probeFalse, "Cmd2T"));
        Assert.Equal(1, TimingSnapshotFields.GetIntField(probeTrue,  "PowerDown"));
        Assert.Equal(0, TimingSnapshotFields.GetIntField(probeFalse, "PowerDown"));
    }

    [Fact]
    public void GetIntField_UnknownFieldName_ReturnsNull()
    {
        var probe = MakeProbeSnapshot();

        Assert.Null(TimingSnapshotFields.GetIntField(probe, "DoesNotExist"));
        Assert.Null(TimingSnapshotFields.GetIntField(probe, "VSoc"));      // voltage — not an int field
        Assert.Null(TimingSnapshotFields.GetIntField(probe, "RttNom"));    // string SI
        Assert.Null(TimingSnapshotFields.GetIntField(probe, ""));
    }

    // ─── 3. CSV column-count lock ────────────────────────────────────────────

    [Fact]
    public void FormatRow_ColumnCount_MatchesHelperFieldCount()
    {
        // FormatRow is frozen (load-bearing CSV column order). This assertion
        // ties it to the helper world: if the helper gains a new field that the
        // CSV does not cover, this test fails and the author must decide whether
        // the CSV header needs updating or the discrepancy is intentional.
        //
        // The CSV covers SignalIntegrityNumeric (all 5) and 3 of the 6
        // SignalIntegrityStrings (RttNom, RttWr, RttPark — not AddrCmdSetup,
        // CsOdtSetup, CkeSetup). That known discrepancy is captured by the
        // constant below. If it changes, update the constant and the comment.
        const int siStringsInCsv = 3; // RttNom, RttWr, RttPark only

        int expectedColumns =
            2   // structural: timestamp, boot_id
            + TimingSnapshotFields.Clocks.Length
            + TimingSnapshotFields.Timings.Length
            + TimingSnapshotFields.Phy.Length
            + TimingSnapshotFields.Booleans.Length
            + TimingSnapshotFields.Voltages.Length
            + TimingSnapshotFields.SignalIntegrityNumeric.Length
            + siStringsInCsv;

        var row = TimingCsvLogger.FormatRow(MakeProbeSnapshot());
        int actualColumns = row.Count(c => c == ',') + 1;

        Assert.Equal(expectedColumns, actualColumns);
    }

    // ─── 3b. CSV header lock — header column count must match row column count ──

    [Fact]
    public void FormatRow_HeaderColumnCount_MatchesRowColumnCount()
    {
        // Header is a frozen const string; FormatRow writes one value per column.
        // If a future patch grows FormatRow but forgets the header (or vice versa),
        // the on-disk CSV will start writing rows with a different column count
        // than the header advertises and external parsers will misalign silently.
        int headerColumns = TimingCsvLogger.Header.Count(c => c == ',') + 1;
        int rowColumns    = TimingCsvLogger.FormatRow(MakeProbeSnapshot()).Count(c => c == ',') + 1;
        Assert.Equal(headerColumns, rowColumns);
    }

    // ─── 4. TuningEqual ──────────────────────────────────────────────────────

    [Fact]
    public void TuningEqual_IdenticalSnapshots_ReturnsTrue()
    {
        var a = MakeProbeSnapshot();
        var b = MakeProbeSnapshot();
        Assert.True(TimingSnapshotFields.TuningEqual(a, b));
    }

    [Fact]
    public void TuningEqual_DifferentClock_ReturnsFalse()
    {
        var a = MakeProbeSnapshot();
        var b = MakeProbeSnapshot();
        b.MemClockMhz = a.MemClockMhz + 1;
        Assert.False(TimingSnapshotFields.TuningEqual(a, b));
    }

    [Fact]
    public void TuningEqual_DifferentPhy_ReturnsFalse()
    {
        // PHY now participates in tuning equality (deliberate behavior change
        // from the refactor — see timing-snapshot-refactor.md §4).
        var a = MakeProbeSnapshot();
        var b = MakeProbeSnapshot();
        b.PHYRDL_A = a.PHYRDL_A + 1;
        Assert.False(TimingSnapshotFields.TuningEqual(a, b));
    }

    [Fact]
    public void TuningEqual_DifferentBoolean_ReturnsFalse()
    {
        var a = MakeProbeSnapshot(gdm: true);
        var b = MakeProbeSnapshot(gdm: false);
        Assert.False(TimingSnapshotFields.TuningEqual(a, b));
    }

    [Fact]
    public void TuningEqual_DifferentVoltageOnly_ReturnsTrue()
    {
        // Voltages are deliberately excluded from tuning equality — SVI2 telemetry
        // is sub-millivolt noisy and does not represent a tuning change.
        var a = MakeProbeSnapshot();
        var b = MakeProbeSnapshot();
        b.VSoc = a.VSoc + 0.050;
        b.VCore = a.VCore + 0.050;
        Assert.True(TimingSnapshotFields.TuningEqual(a, b));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a snapshot where every integer field has a distinct value so
    /// a name→wrong-selector mapping is detected by the round-trip test.
    /// The values are chosen so no two fields share the same int.
    /// </summary>
    private static TimingSnapshot MakeProbeSnapshot(
        bool gdm = false, bool cmd2T = false, bool powerDown = false)
    {
        // Assign sequential sentinel values starting at 100 to avoid zero
        // (zero means "unset" in several callers) and to keep them all distinct.
        return new TimingSnapshot
        {
            SnapshotId   = "probe",
            Timestamp    = DateTime.UtcNow,
            BootId       = "probe-boot",
            // Clocks — sentinels 100, 101, 102
            MemClockMhz  = 100,
            FclkMhz      = 101,
            UclkMhz      = 102,
            // Primaries — 103–109
            CL           = 103,
            RCDRD        = 104,
            RCDWR        = 105,
            RP           = 106,
            RAS          = 107,
            RC           = 108,
            CWL          = 109,
            // tRFC — 110–112
            RFC          = 110,
            RFC2         = 111,
            RFC4         = 112,
            // Secondaries — 113–121
            RRDS         = 113,
            RRDL         = 114,
            FAW          = 115,
            WTRS         = 116,
            WTRL         = 117,
            WR           = 118,
            RTP          = 119,
            RDRDSCL      = 120,
            WRWRSCL      = 121,
            // Turn-around — 122–129
            RDRDSC       = 122,
            RDRDSD       = 123,
            RDRDDD       = 124,
            WRWRSC       = 125,
            WRWRSD       = 126,
            WRWRDD       = 127,
            RDWR         = 128,
            WRRD         = 129,
            // Misc — 130–134
            REFI         = 130,
            CKE          = 131,
            STAG         = 132,
            MOD          = 133,
            MRD          = 134,
            // PHY — 135–136
            PHYRDL_A     = 135,
            PHYRDL_B     = 136,
            // Booleans — parameterised
            GDM          = gdm,
            Cmd2T        = cmd2T,
            PowerDown    = powerDown,
            // Voltages — distinct non-zero doubles
            VSoc         = 1.001,
            VCore        = 1.002,
            VDimm        = 1.003,
            VDDP         = 1.004,
            VDDG_IOD     = 1.005,
            VDDG_CCD     = 1.006,
            Vtt          = 1.007,
            Vpp          = 1.008,
            // Signal integrity
            ProcODT          = 48.0,
            ClkDrvStren      = 40.0,
            AddrCmdDrvStren  = 40.0,
            CsOdtCmdDrvStren = 40.0,
            CkeDrvStren      = 34.0,
            RttNom       = "RZQ/4",
            RttWr        = "RZQ/2",
            RttPark      = "RZQ/6",
            AddrCmdSetup = "1/4",
            CsOdtSetup   = "1/4",
            CkeSetup     = "1/4",
        };
    }
}
