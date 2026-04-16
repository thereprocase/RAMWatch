using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.Service.Services;

namespace RAMWatch.Tests;

public class DigestBuilderTests
{
    // -----------------------------------------------------------------------
    // Test data helpers
    // -----------------------------------------------------------------------

    private static ServiceState MakeState(string driverStatus = "ready", List<ErrorSource>? errors = null) =>
        new ServiceState
        {
            Timestamp     = DateTime.UtcNow,
            BootTime      = DateTime.UtcNow.AddHours(-3),
            Ready         = true,
            DriverStatus  = driverStatus,
            ServiceUptime = TimeSpan.FromHours(3),
            Errors        = errors ?? new List<ErrorSource>(),
            Integrity     = new IntegrityState(0, IntegrityCheckStatus.NotRun, IntegrityCheckStatus.NotRun)
        };

    private static TimingSnapshot MakeSnapshot(
        string snapshotId = "snap01",
        string bootId     = "boot01",
        int    cl         = 16,
        int    rcdrd      = 22,
        int    rfc        = 577,
        string notes      = "") =>
        new TimingSnapshot
        {
            SnapshotId   = snapshotId,
            Timestamp    = new DateTime(2026, 4, 13, 22, 30, 0, DateTimeKind.Local),
            BootId       = bootId,
            MemClockMhz  = 1800,
            FclkMhz      = 1800,
            UclkMhz      = 1800,
            CL           = cl,
            RCDRD        = rcdrd,
            RCDWR        = 22,
            RP           = 22,
            RAS          = 42,
            RC           = 64,
            CWL          = 16,
            RFC          = rfc,
            RFC2         = 375,
            RFC4         = 260,
            RRDS         = 7,
            RRDL         = 11,
            FAW          = 38,
            WTRS         = 5,
            WTRL         = 14,
            WR           = 26,
            RTP          = 14,
            RDRDSCL      = 5,
            WRWRSCL      = 5,
            RDRDSC       = 1,
            RDRDSD       = 5,
            RDRDDD       = 4,
            WRWRSC       = 1,
            WRWRSD       = 7,
            WRWRDD       = 6,
            RDWR         = 9,
            WRRD         = 2,
            REFI         = 14029,
            CKE          = 6,
            STAG         = 255,
            MOD          = 27,
            MRD          = 8,
            PHYRDL_A     = 28,
            PHYRDL_B     = 26,
            GDM          = true,
            Cmd2T        = false,
            VSoc         = 1.088,
            VDimm        = 1.400,
            CpuCodename  = "5800X3D",
            BiosVersion  = "E7C91AMS.2A0",
            AgesaVersion = "1.2.0.E",
            Notes        = notes
        };

    private static DesignationMap MakeDesignations() =>
        new DesignationMap
        {
            Designations = new Dictionary<string, TimingDesignation>
            {
                ["CL"]      = TimingDesignation.Manual,
                ["RCDRD"]   = TimingDesignation.Manual,
                ["RCDWR"]   = TimingDesignation.Manual,
                ["RP"]      = TimingDesignation.Manual,
                ["RAS"]     = TimingDesignation.Manual,
                ["RC"]      = TimingDesignation.Manual,
                ["CWL"]     = TimingDesignation.Manual,
                ["RFC"]     = TimingDesignation.Manual,
                ["RFC2"]    = TimingDesignation.Manual,
                ["RFC4"]    = TimingDesignation.Manual,
                ["RRDS"]    = TimingDesignation.Auto,
                ["RRDL"]    = TimingDesignation.Auto,
                ["FAW"]     = TimingDesignation.Auto,
                ["WTRS"]    = TimingDesignation.Auto,
                ["WTRL"]    = TimingDesignation.Auto,
                ["WR"]      = TimingDesignation.Auto,
                ["RTP"]     = TimingDesignation.Auto,
            }
        };

    private static ValidationResult MakeValidation(bool passed, string snapshotId = "snap01") =>
        new ValidationResult
        {
            Timestamp        = new DateTime(2026, 4, 13, 22, 30, 0, DateTimeKind.Local),
            BootId           = "boot01",
            TestTool         = "Karhu",
            MetricName       = "coverage",
            MetricValue      = 12400,
            MetricUnit       = "%",
            Passed           = passed,
            DurationMinutes  = 30,
            ActiveSnapshotId = snapshotId
        };

    private static DriftEvent MakeDrift(string timingName, int expected, int actual) =>
        new DriftEvent
        {
            Timestamp          = new DateTime(2026, 4, 15, 8, 30, 0, DateTimeKind.Local),
            BootId             = "boot02",
            TimingName         = timingName,
            ExpectedValue      = expected,
            ActualValue        = actual,
            BootsAtExpected    = 5,
            BootsAtActual      = 1,
            WindowBootCount    = 6,
            WindowStabilityRatio = 0.83
        };

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [Fact]
    public void FullDigest_AllDataPresent_ContainsExpectedSections()
    {
        var state       = MakeState();
        var current     = MakeSnapshot();
        var lkg         = MakeSnapshot(); // identical → "Same as current"
        var validations = new List<ValidationResult> { MakeValidation(passed: true) };
        var drifts      = new List<DriftEvent>();
        var desig       = MakeDesignations();

        string digest = DigestBuilder.BuildDigest(state, current, lkg, validations, drifts, desig, historyCount: 10);

        Assert.Contains("RAMWatch Digest", digest);
        Assert.Contains("Hardware:", digest);
        Assert.Contains("5800X3D", digest);
        Assert.Contains("AGESA 1.2.0.E", digest);
        Assert.Contains("Current: DDR4-3600", digest);
        Assert.Contains("FCLK 1800", digest);
        Assert.Contains("1:1:1", digest);
        Assert.Contains("Primaries (manual)", digest);
        Assert.Contains("CL 16", digest);
        Assert.Contains("RCDRD 22", digest);
        Assert.Contains("CWL (manual)", digest);
        Assert.Contains("GDM: On", digest);
        Assert.Contains("Cmd: 1T", digest);
        Assert.Contains("tRFC (manual)", digest);
        Assert.Contains("577/375/260", digest);
        Assert.Contains("Secondaries (auto)", digest);
        Assert.Contains("RRDS 7", digest);
        Assert.Contains("LKG: Same as current", digest);
        Assert.Contains("Validation History", digest);
        Assert.Contains("Karhu", digest);
        Assert.Contains("PASS", digest);
        Assert.Contains("Errors (this boot):", digest);
        Assert.Contains("Errors (all time):", digest);
    }

    [Fact]
    public void Digest_NoTimingData_DriverNotFound_ShowsPlaceholder()
    {
        var state = MakeState(driverStatus: "not_found");

        string digest = DigestBuilder.BuildDigest(
            state,
            current:     null,
            lkg:         null,
            recentValidations: new List<ValidationResult>(),
            drifts:      new List<DriftEvent>(),
            designations: null,
            historyCount: 0);

        Assert.Contains("Hardware: [PawnIO driver required]", digest);
        Assert.Contains("Timings: [Not available — driver not loaded]", digest);
        // Should not throw or crash — no timing rows should appear.
        Assert.DoesNotContain("DDR4-", digest);
        Assert.DoesNotContain("Primaries", digest);
    }

    [Fact]
    public void Digest_NoValidations_ShowsNoResultsMessage()
    {
        var state   = MakeState();
        var current = MakeSnapshot();

        string digest = DigestBuilder.BuildDigest(
            state,
            current:      current,
            lkg:          null,
            recentValidations: new List<ValidationResult>(),
            drifts:       new List<DriftEvent>(),
            designations: null,
            historyCount: 0);

        Assert.Contains("No validation results", digest);
    }

    [Fact]
    public void Digest_DriftEvents_WarningsAppearInline()
    {
        var state   = MakeState();
        var current = MakeSnapshot(rcdrd: 22);
        var drifts  = new List<DriftEvent>
        {
            MakeDrift("RRDL", expected: 11, actual: 12)
        };

        string digest = DigestBuilder.BuildDigest(
            state,
            current:      current,
            lkg:          null,
            recentValidations: new List<ValidationResult>(),
            drifts:       drifts,
            designations: null,
            historyCount: 0);

        // Warning should appear and contain the from→to values.
        Assert.Contains("RRDL drifted", digest);
        Assert.Contains("11", digest);
        Assert.Contains("12", digest);
        // The ⚠ character should be present.
        Assert.Contains("\u26a0", digest);
    }

    [Fact]
    public void Digest_LkgSameAsCurrent_SayssSameAsCurrent()
    {
        var state   = MakeState();
        var current = MakeSnapshot();
        var lkg     = MakeSnapshot(); // identical fields

        var validations = new List<ValidationResult>
        {
            MakeValidation(passed: true)
        };

        string digest = DigestBuilder.BuildDigest(
            state,
            current:      current,
            lkg:          lkg,
            recentValidations: validations,
            drifts:       new List<DriftEvent>(),
            designations: null,
            historyCount: 1);

        Assert.Contains("LKG: Same as current", digest);
    }

    [Fact]
    public void Digest_LkgDifferentFromCurrent_ShowsDiff()
    {
        var state   = MakeState();
        var current = MakeSnapshot(cl: 16, rfc: 577);
        var lkg     = MakeSnapshot(cl: 18, rfc: 600);

        string digest = DigestBuilder.BuildDigest(
            state,
            current:      current,
            lkg:          lkg,
            recentValidations: new List<ValidationResult>(),
            drifts:       new List<DriftEvent>(),
            designations: null,
            historyCount: 2);

        Assert.Contains("LKG diff:", digest);
        // CL changed from 18 (lkg) to 16 (current)
        Assert.Contains("CL:", digest);
        Assert.Contains("18", digest);
        Assert.Contains("16", digest);
        // RFC changed from 600 to 577
        Assert.Contains("RFC:", digest);
        Assert.Contains("600", digest);
        Assert.Contains("577", digest);
    }

    [Fact]
    public void Digest_OutputLength_IsReasonableForTypicalConfig()
    {
        var state       = MakeState();
        var current     = MakeSnapshot();
        var lkg         = MakeSnapshot();
        var validations = new List<ValidationResult>
        {
            MakeValidation(passed: true,  snapshotId: "snap01"),
            MakeValidation(passed: true,  snapshotId: "snap01"),
            MakeValidation(passed: false, snapshotId: "snap01"),
            MakeValidation(passed: true,  snapshotId: "snap01"),
            MakeValidation(passed: true,  snapshotId: "snap01"),
        };
        var drifts = new List<DriftEvent>
        {
            MakeDrift("RRDL", 11, 12),
            MakeDrift("STAG", 255, 254)
        };
        var desig = MakeDesignations();

        string digest = DigestBuilder.BuildDigest(
            state,
            current:      current,
            lkg:          lkg,
            recentValidations: validations,
            drifts:       drifts,
            designations: desig,
            historyCount: 20);

        // Under 3000 characters for a typical full config.
        Assert.True(digest.Length < 3000,
            $"Digest length {digest.Length} chars exceeds 3000 — too verbose for AI context window");

        // Should still contain key content — not trivially short.
        Assert.True(digest.Length > 200,
            $"Digest length {digest.Length} chars is suspiciously short");
    }

    [Fact]
    public void Digest_ErrorsPresent_CountsAggregatedCorrectly()
    {
        var errors = new List<ErrorSource>
        {
            new ErrorSource("WHEA Hardware Error", EventCategory.Hardware, 3, DateTime.UtcNow),
            new ErrorSource("MCE Machine Check",   EventCategory.Hardware, 1, DateTime.UtcNow),
            new ErrorSource("BugCheck BSOD",       EventCategory.Hardware, 2, DateTime.UtcNow),
        };
        var state = MakeState(errors: errors);

        string digest = DigestBuilder.BuildDigest(
            state,
            current:      null,
            lkg:          null,
            recentValidations: new List<ValidationResult>(),
            drifts:       new List<DriftEvent>(),
            designations: null,
            historyCount: 0);

        Assert.Contains("3 WHEA", digest);
        Assert.Contains("1 MCE", digest);
        Assert.Contains("2 BSOD", digest);
    }

    [Fact]
    public void Digest_NextPlannedNote_AppearsWhenPresent()
    {
        var state   = MakeState();
        var current = MakeSnapshot(notes: "tRCDRD 22\u219221");

        string digest = DigestBuilder.BuildDigest(
            state,
            current:      current,
            lkg:          null,
            recentValidations: new List<ValidationResult>(),
            drifts:       new List<DriftEvent>(),
            designations: null,
            historyCount: 0);

        Assert.Contains("Next planned:", digest);
        Assert.Contains("tRCDRD", digest);
    }

    [Fact]
    public void Digest_1To1To1Ratio_DisplayedCorrectly()
    {
        var state   = MakeState();
        var current = MakeSnapshot();
        // MakeSnapshot already sets all clocks to 1800 → 1:1:1

        string digest = DigestBuilder.BuildDigest(
            state,
            current:      current,
            lkg:          null,
            recentValidations: new List<ValidationResult>(),
            drifts:       new List<DriftEvent>(),
            designations: null,
            historyCount: 0);

        Assert.Contains("1:1:1", digest);
    }

    [Fact]
    public void Digest_AsymmetricRatio_DisplayedAsRatio()
    {
        var state   = MakeState();
        var current = MakeSnapshot();
        current.MemClockMhz = 1800;
        current.FclkMhz     = 1800;
        current.UclkMhz     = 900; // UCLK decoupled at half-rate

        string digest = DigestBuilder.BuildDigest(
            state,
            current:      current,
            lkg:          null,
            recentValidations: new List<ValidationResult>(),
            drifts:       new List<DriftEvent>(),
            designations: null,
            historyCount: 0);

        // Should NOT say 1:1:1 for asymmetric clocks.
        Assert.DoesNotContain("1:1:1", digest);
        // Should contain a ratio expression.
        Assert.Contains("FCLK 1800", digest);
    }

    // ── PHY-aware equality (deliberate behavior change, step 4 of refactor) ────

    [Fact]
    public void Digest_LkgPhyDiffers_ShowsDiff()
    {
        // Before the refactor, SnapshotsEqual excluded PHY so a PHY-only change
        // was reported as "Same as current". Now PHY participates in TuningEqual,
        // so a PHY delta triggers the diff path.
        var state   = MakeState();
        var current = MakeSnapshot();
        var lkg     = MakeSnapshot();
        // Mutate PHY on lkg to simulate training drift between boots.
        lkg.PHYRDL_A = current.PHYRDL_A + 4;

        string digest = DigestBuilder.BuildDigest(
            state,
            current:      current,
            lkg:          lkg,
            recentValidations: new List<ValidationResult>(),
            drifts:       new List<DriftEvent>(),
            designations: null,
            historyCount: 0);

        // Must NOT say "Same as current" — PHY differs.
        Assert.DoesNotContain("Same as current", digest);
        Assert.Contains("LKG", digest);
    }

    [Fact]
    public void Digest_LkgPhyIdentical_TimingsDiffer_ShowsDiff()
    {
        // Sanity: when timing fields differ the diff path fires regardless of PHY.
        var state   = MakeState();
        var current = MakeSnapshot(cl: 16);
        var lkg     = MakeSnapshot(cl: 18);
        // PHY is the same in both (MakeSnapshot default).

        string digest = DigestBuilder.BuildDigest(
            state,
            current:      current,
            lkg:          lkg,
            recentValidations: new List<ValidationResult>(),
            drifts:       new List<DriftEvent>(),
            designations: null,
            historyCount: 0);

        Assert.Contains("LKG diff:", digest);
        Assert.Contains("CL:", digest);
    }

    [Fact]
    public void Digest_LkgVoltagesOnly_SayssSameAsCurrent()
    {
        // Voltages are excluded from TuningEqual. A voltage-only change between
        // current and LKG is still "Same as current" tuning-wise.
        var state   = MakeState();
        var current = MakeSnapshot();
        var lkg     = MakeSnapshot();
        lkg.VSoc  = current.VSoc  + 0.050;
        lkg.VCore = current.VCore + 0.050;

        string digest = DigestBuilder.BuildDigest(
            state,
            current:      current,
            lkg:          lkg,
            recentValidations: new List<ValidationResult>(),
            drifts:       new List<DriftEvent>(),
            designations: null,
            historyCount: 0);

        Assert.Contains("LKG: Same as current", digest);
    }

    [Fact]
    public void Digest_LkgPhyDiffers_PhyFieldAppearsInDiffOutput()
    {
        // Behavior change (step 5): PHY now appears in AppendSnapshotDiff output.
        // The "(training)" annotation distinguishes it from hand-tuned fields.
        var state   = MakeState();
        var current = MakeSnapshot();
        var lkg     = MakeSnapshot();
        lkg.PHYRDL_A = current.PHYRDL_A + 4;

        string digest = DigestBuilder.BuildDigest(
            state,
            current:      current,
            lkg:          lkg,
            recentValidations: new List<ValidationResult>(),
            drifts:       new List<DriftEvent>(),
            designations: null,
            historyCount: 0);

        Assert.Contains("LKG diff:", digest);
        Assert.Contains("PHYRDL_A", digest);
        Assert.Contains("training", digest);
    }
}
