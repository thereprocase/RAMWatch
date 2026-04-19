using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.Service.Services;

namespace RAMWatch.Tests;

public class DriftDetectorTests : IDisposable
{
    private readonly string _tempDir;

    public DriftDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ramwatch-drift-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static TimingSnapshot MakeSnapshot(string bootId, int cl = 18, int rtp = 10) =>
        new TimingSnapshot
        {
            SnapshotId = Guid.NewGuid().ToString("N"),
            Timestamp  = DateTime.UtcNow,
            BootId     = bootId,
            // Clocks are non-zero so CheckForDrift's incomplete-read guard passes.
            MemClockMhz = 1900, FclkMhz = 1900, UclkMhz = 1900,
            CL  = cl,
            RTP = rtp,
            // Set all other fields to consistent defaults so they don't produce
            // spurious drift events in tests that track multiple Auto designations.
            RCDRD = 18, RCDWR = 18, RP = 18, RAS = 36, RC = 54, CWL = 14,
            RFC = 312, RFC2 = 200, RFC4 = 100,
            RRDS = 4, RRDL = 6, FAW = 16, WTRS = 4, WTRL = 12, WR = 18,
            RDRDSCL = 2, WRWRSCL = 2,
            RDRDSC = 2, RDRDSD = 6, RDRDDD = 8,
            WRWRSC = 2, WRWRSD = 6, WRWRDD = 8,
            RDWR = 14, WRRD = 2,
            REFI = 65535, CKE = 6, STAG = 2, MOD = 6, MRD = 6,
            PHYRDL_A = 40, PHYRDL_B = 42,
            GDM = false, Cmd2T = false, PowerDown = false,
            VSoc = 1.05, VDimm = 1.35
        };

    /// <summary>
    /// Designation map with only CL marked Auto unless overridden.
    /// </summary>
    private static DesignationMap AutoCL() =>
        new DesignationMap
        {
            Designations = new Dictionary<string, TimingDesignation>
            {
                ["CL"] = TimingDesignation.Auto
            }
        };

    private static DesignationMap AutoCLandRTP() =>
        new DesignationMap
        {
            Designations = new Dictionary<string, TimingDesignation>
            {
                ["CL"]  = TimingDesignation.Auto,
                ["RTP"] = TimingDesignation.Auto
            }
        };

    /// <summary>
    /// Seed the detector with N boots, all with the given CL value.
    /// Boot IDs are globally unique (Guid-based) so repeated SeedBoots calls
    /// produce distinct boots even when called with the same parameters.
    /// </summary>
    private static void SeedBoots(DriftDetector detector, int count, int cl,
        DesignationMap? desig = null)
    {
        desig ??= AutoCL();
        for (int i = 0; i < count; i++)
        {
            detector.CheckForDrift(MakeSnapshot($"seed_{Guid.NewGuid():N}", cl: cl), desig);
        }
    }

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [Fact]
    public void FullWindowAtValue11_CurrentIs12_DriftDetected()
    {
        using var detector = new DriftDetector(_tempDir);
        SeedBoots(detector, 20, cl: 11);

        var current  = MakeSnapshot("boot_current", cl: 12);
        var events   = detector.CheckForDrift(current, AutoCL());

        Assert.Single(events);
        var evt = events[0];
        Assert.Equal("CL",    evt.TimingName);
        Assert.Equal(11,      evt.ExpectedValue);
        Assert.Equal(12,      evt.ActualValue);
        Assert.Equal("boot_current", evt.BootId);
    }

    [Fact]
    public void FullWindowAtValue11_CurrentIs11_NoDrift()
    {
        using var detector = new DriftDetector(_tempDir);
        SeedBoots(detector, 20, cl: 11);

        var events = detector.CheckForDrift(MakeSnapshot("boot_current", cl: 11), AutoCL());

        Assert.Empty(events);
    }

    [Fact]
    public void BimodalWindow_TenElevenTenTwelve_CurrentIsTwelve_DriftDetected()
    {
        // 10 boots at value 11 (older), then 10 boots at value 12 (newer).
        // The mode is tied at 10 each. Tie-break: 11 was seen first → 11 is baseline.
        // Current = 12 → drift.
        using var detector = new DriftDetector(_tempDir);
        SeedBoots(detector, 10, cl: 11);
        SeedBoots(detector, 10, cl: 12);

        var events = detector.CheckForDrift(MakeSnapshot("boot_current", cl: 12), AutoCL());

        Assert.Single(events);
        var evt = events[0];
        Assert.Equal(11, evt.ExpectedValue); // older value wins the tie
        Assert.Equal(12, evt.ActualValue);
    }

    [Fact]
    public void BimodalWindow_TenElevenTenTwelve_CurrentIsEleven_NoDrift()
    {
        // Same bimodal window; current = 11 (matches the older baseline) → no drift.
        using var detector = new DriftDetector(_tempDir);
        SeedBoots(detector, 10, cl: 11);
        SeedBoots(detector, 10, cl: 12);

        var events = detector.CheckForDrift(MakeSnapshot("boot_current", cl: 11), AutoCL());

        Assert.Empty(events);
    }

    [Fact]
    public void FewerThanThreeBootsInWindow_NoDriftEvents()
    {
        using var detector = new DriftDetector(_tempDir);
        // Only seed 2 boots — below the minimum of 3.
        SeedBoots(detector, 2, cl: 11);

        var events = detector.CheckForDrift(MakeSnapshot("boot_current", cl: 99), AutoCL());

        Assert.Empty(events);
    }

    [Fact]
    public void EmptyWindow_NoDriftEvents()
    {
        using var detector = new DriftDetector(_tempDir);

        var events = detector.CheckForDrift(MakeSnapshot("boot_current", cl: 99), AutoCL());

        Assert.Empty(events);
    }

    [Fact]
    public void ManualTiming_NeverEmitsDrift()
    {
        using var detector = new DriftDetector(_tempDir);
        SeedBoots(detector, 20, cl: 11);

        var desig = new DesignationMap
        {
            Designations = new Dictionary<string, TimingDesignation>
            {
                ["CL"] = TimingDesignation.Manual   // Manual, not Auto
            }
        };

        var events = detector.CheckForDrift(MakeSnapshot("boot_current", cl: 99), desig);

        Assert.Empty(events);
    }

    [Fact]
    public void UnknownTiming_NeverEmitsDrift()
    {
        using var detector = new DriftDetector(_tempDir);
        SeedBoots(detector, 20, cl: 11);

        var desig = new DesignationMap
        {
            Designations = new Dictionary<string, TimingDesignation>
            {
                ["CL"] = TimingDesignation.Unknown  // Unknown, not Auto
            }
        };

        var events = detector.CheckForDrift(MakeSnapshot("boot_current", cl: 99), desig);

        Assert.Empty(events);
    }

    [Fact]
    public void MultipleTimingsDrift_MultipleEventsReturned()
    {
        using var detector = new DriftDetector(_tempDir);
        // Seed 20 boots with CL=11, RTP=10.
        for (int i = 0; i < 20; i++)
        {
            detector.CheckForDrift(MakeSnapshot($"seed_{i:D3}", cl: 11, rtp: 10), AutoCLandRTP());
        }

        // Current boot has both drifted.
        var current = MakeSnapshot("boot_current", cl: 13, rtp: 8);
        var events  = detector.CheckForDrift(current, AutoCLandRTP());

        Assert.Equal(2, events.Count);

        var clEvt  = events.FirstOrDefault(e => e.TimingName == "CL");
        var rtpEvt = events.FirstOrDefault(e => e.TimingName == "RTP");

        Assert.NotNull(clEvt);
        Assert.NotNull(rtpEvt);
        Assert.Equal(11, clEvt!.ExpectedValue);
        Assert.Equal(13, clEvt.ActualValue);
        Assert.Equal(10, rtpEvt!.ExpectedValue);
        Assert.Equal(8,  rtpEvt.ActualValue);
    }

    [Fact]
    public void WindowBootCount_ReflectsPreCheckWindow()
    {
        // After seeding N boots the window has N entries; the current boot is
        // checked against those N before being added itself.
        using var detector = new DriftDetector(_tempDir);
        SeedBoots(detector, 5, cl: 11);

        var events = detector.CheckForDrift(MakeSnapshot("boot_current", cl: 99), AutoCL());

        Assert.Single(events);
        Assert.Equal(5, events[0].WindowBootCount);
    }

    [Fact]
    public void WindowStabilityRatio_FullAgreement_IsOne()
    {
        using var detector = new DriftDetector(_tempDir);
        SeedBoots(detector, 10, cl: 11);

        var events = detector.CheckForDrift(MakeSnapshot("boot_current", cl: 12), AutoCL());

        Assert.Single(events);
        Assert.Equal(1.0, events[0].WindowStabilityRatio, precision: 4);
    }

    [Fact]
    public void WindowStabilityRatio_HalfAgreement()
    {
        // 5 boots at 11, 5 boots at 12; mode = 11 (first seen).
        // 5 / 10 = 0.5.
        using var detector = new DriftDetector(_tempDir);
        SeedBoots(detector, 5, cl: 11);
        SeedBoots(detector, 5, cl: 12);

        var events = detector.CheckForDrift(MakeSnapshot("boot_current", cl: 12), AutoCL());

        Assert.Single(events);
        Assert.Equal(0.5, events[0].WindowStabilityRatio, precision: 4);
    }

    [Fact]
    public void WindowCappedAt20_OldestEntryDropped()
    {
        using var detector = new DriftDetector(_tempDir);
        // Seed 25 boots — the window should cap at 20.
        SeedBoots(detector, 25, cl: 11);

        Assert.Equal(20, detector.GetWindow().Count);
    }

    [Fact]
    public void CurrentBootAddedAfterCheck_NotIncludedInDriftCheck()
    {
        // If the current value were included in the window before the check,
        // it would inflate its own count and could flip the mode. Verify it doesn't.
        // Window: 3 boots at 11. Current = 12.
        // After the check, window has 4 entries: 3×11, 1×12. Mode still = 11.
        using var detector = new DriftDetector(_tempDir);
        SeedBoots(detector, 3, cl: 11);

        // First check: current = 12 → should see drift (window = 3 boots at 11).
        var events1 = detector.CheckForDrift(MakeSnapshot("boot_current_1", cl: 12), AutoCL());
        Assert.Single(events1);

        // Window now has 4 entries: 3×11, 1×12. Mode = 11.
        // Second check: current = 12 → still drift.
        var events2 = detector.CheckForDrift(MakeSnapshot("boot_current_2", cl: 12), AutoCL());
        Assert.Single(events2);
        Assert.Equal(11, events2[0].ExpectedValue);
    }

    [Fact]
    public void Persistence_RoundTrip_WindowSurvivedRestart()
    {
        // Seed 10 boots and let the detector persist the window.
        using (var detector = new DriftDetector(_tempDir))
        {
            SeedBoots(detector, 10, cl: 11);
        }

        // New instance loads the persisted window.
        using var detector2 = new DriftDetector(_tempDir);
        detector2.LoadWindow();

        Assert.Equal(10, detector2.GetWindow().Count);

        // Drift should be detected against the loaded history.
        var events = detector2.CheckForDrift(MakeSnapshot("boot_after_restart", cl: 99), AutoCL());
        Assert.Single(events);
        Assert.Equal(11, events[0].ExpectedValue);
    }

    [Fact]
    public void Persistence_CorruptFile_TreatedAsEmptyWindow()
    {
        File.WriteAllText(Path.Combine(_tempDir, "drift_window.json"), "{ not valid json !!!");

        using var detector = new DriftDetector(_tempDir);
        detector.LoadWindow();

        // Window is empty → fewer than 3 boots → no drift events regardless of value.
        var events = detector.CheckForDrift(MakeSnapshot("boot_after_corrupt", cl: 99), AutoCL());
        Assert.Empty(events);
    }

    [Fact]
    public void AtomicWrite_NoTempFilesAfterCheckForDrift()
    {
        using var detector = new DriftDetector(_tempDir);
        SeedBoots(detector, 3, cl: 11);
        detector.CheckForDrift(MakeSnapshot("boot_current", cl: 11), AutoCL());

        var tmpFiles = Directory.GetFiles(_tempDir, "*.tmp");
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public void RepeatedSameBootId_WindowDoesNotInflate()
    {
        // The warm-tier polling loop calls CheckForDrift many times per boot.
        // Without dedup, the rolling window fills with copies of the current boot.
        using var detector = new DriftDetector(_tempDir);

        for (int i = 0; i < 50; i++)
        {
            detector.CheckForDrift(MakeSnapshot("boot_same", cl: 11), AutoCL());
        }

        Assert.Equal(1, detector.GetWindow().Count);
    }

    [Fact]
    public void ColdBootIncomplete_DriftCheckSkipped_WindowUnchanged()
    {
        // During the startup window, cold-tier readers stamp in sequence.
        // A drift check fired before all three stamps would misread the
        // transient state as drift. Gate must suppress both the event list
        // and the window append.
        using var detector = new DriftDetector(_tempDir);
        SeedBoots(detector, 20, cl: 11);

        int windowBefore = detector.GetWindow().Count;

        var partial = new ColdBootStatus
        {
            TimingsStampedUtc    = DateTime.UtcNow,
            DimmsStampedUtc      = null,            // still pending
            AddressMapStampedUtc = DateTime.UtcNow,
        };

        var events = detector.CheckForDrift(
            MakeSnapshot("boot_current", cl: 99), AutoCL(), partial);

        Assert.Empty(events);
        Assert.Equal(windowBefore, detector.GetWindow().Count);
    }

    [Fact]
    public void ColdBootComplete_DriftCheckProceeds()
    {
        // All stamps present → detector operates normally.
        using var detector = new DriftDetector(_tempDir);
        SeedBoots(detector, 20, cl: 11);

        var complete = new ColdBootStatus
        {
            TimingsStampedUtc    = DateTime.UtcNow,
            DimmsStampedUtc      = DateTime.UtcNow,
            AddressMapStampedUtc = DateTime.UtcNow,
        };

        var events = detector.CheckForDrift(
            MakeSnapshot("boot_current", cl: 99), AutoCL(), complete);

        Assert.Single(events);
        Assert.Equal(11, events[0].ExpectedValue);
        Assert.Equal(99, events[0].ActualValue);
    }

    [Fact]
    public void ColdBootNullParam_PreservesLegacyBehavior()
    {
        // Callers that don't track cold-boot (tests, older code paths)
        // pass null and get the original, non-gated behaviour.
        using var detector = new DriftDetector(_tempDir);
        SeedBoots(detector, 20, cl: 11);

        var events = detector.CheckForDrift(
            MakeSnapshot("boot_current", cl: 99), AutoCL(), coldBoot: null);

        Assert.Single(events);
    }

    [Fact]
    public void IncompleteRead_FclkZero_NoWindowPollution()
    {
        // An SMU PM table read during cold-tier warm-up can produce a snapshot
        // with FCLK/UCLK = 0. It must not enter the drift window or the whole
        // baseline would be shifted toward zero clocks.
        using var detector = new DriftDetector(_tempDir);
        var incomplete = MakeSnapshot("boot_cold", cl: 11);
        incomplete.FclkMhz = 0;
        incomplete.UclkMhz = 0;

        var events = detector.CheckForDrift(incomplete, AutoCL());

        Assert.Empty(events);
        Assert.Equal(0, detector.GetWindow().Count);
    }

    [Fact]
    public void ExactlyThreeBoots_DriftCheckOccurs()
    {
        // MinBootsForDrift = 3. After seeding exactly 3, drift should be detected.
        using var detector = new DriftDetector(_tempDir);
        SeedBoots(detector, 3, cl: 11);

        var events = detector.CheckForDrift(MakeSnapshot("boot_current", cl: 99), AutoCL());

        Assert.Single(events);
    }
}
