using Xunit;
using RAMWatch.Core.Ipc;
using RAMWatch.Core.Models;
using RAMWatch.Service.Services;

namespace RAMWatch.Tests;

/// <summary>
/// Tests for StateAggregator.BuildState() — RecentChanges population and uptime semantics.
/// Dependencies (EventLogMonitor, SettingsManager, PipeServer) are constructed without
/// starting their background machinery; only the in-memory query paths are exercised.
/// </summary>
public class StateAggregatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly EventLogMonitor _eventLog;
    private readonly SettingsManager _settings;
    private readonly PipeServer _pipe;

    public StateAggregatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ramwatch-sa-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _eventLog = new EventLogMonitor();
        _settings = new SettingsManager(Path.Combine(_tempDir, "settings.json"));
        _pipe     = new PipeServer();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private StateAggregator MakeAggregator() =>
        new StateAggregator(_eventLog, _settings, _pipe);

    private static TimingSnapshot MakeSnapshot(string bootId, int cl = 18) =>
        new TimingSnapshot
        {
            SnapshotId  = Guid.NewGuid().ToString("N"),
            Timestamp   = DateTime.UtcNow,
            BootId      = bootId,
            CL    = cl,    RCDRD = 18, RCDWR = 18,
            RP    = 18,    RAS   = 36, RC    = 54,    CWL  = 14,
            RFC   = 312,   RFC2  = 200, RFC4  = 100,
            RRDS  = 4,     RRDL  = 6,  FAW   = 16,
            WTRS  = 4,     WTRL  = 12, WR    = 18,    RTP  = 10,
            RDRDSCL = 2,   WRWRSCL = 2,
            RDRDSC = 2,    RDRDSD = 6, RDRDDD = 8,
            WRWRSC = 2,    WRWRSD = 6, WRWRDD = 8,
            RDWR  = 14,    WRRD  = 2,
            REFI  = 65535, CKE   = 6,  STAG  = 2,     MOD  = 6, MRD = 6,
            PHYRDL_A = 40, PHYRDL_B = 42,
        };

    // -----------------------------------------------------------------------
    // RecentChanges
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildState_WithoutPhase3Services_RecentChangesIsNull()
    {
        var aggregator = MakeAggregator();

        var state = aggregator.BuildState();

        // Phase 3 services never wired in — field must be null so JSON omits it.
        Assert.Null(state.RecentChanges);
    }

    [Fact]
    public void BuildState_WithDetectorHavingNoChanges_RecentChangesIsNull()
    {
        var aggregator  = MakeAggregator();
        var detector    = new ConfigChangeDetector(_tempDir);
        var drift       = new DriftDetector(_tempDir);
        var valLogger   = new ValidationTestLogger(_tempDir);
        var lkg         = new LkgTracker(_tempDir);
        var snapshots   = new SnapshotJournal(_tempDir);

        aggregator.SetPhase3Services(detector, drift, valLogger, lkg, snapshots);

        // Detector has no changes — first DetectChanges is baseline only.
        detector.DetectChanges(MakeSnapshot("boot_a", cl: 18));

        var state = aggregator.BuildState();

        // No changes detected yet — list omitted from push.
        Assert.Null(state.RecentChanges);
    }

    [Fact]
    public void BuildState_WithDetectedChange_RecentChangesPopulated()
    {
        var aggregator  = MakeAggregator();
        var detector    = new ConfigChangeDetector(_tempDir);
        var drift       = new DriftDetector(_tempDir);
        var valLogger   = new ValidationTestLogger(_tempDir);
        var lkg         = new LkgTracker(_tempDir);
        var snapshots   = new SnapshotJournal(_tempDir);

        aggregator.SetPhase3Services(detector, drift, valLogger, lkg, snapshots);

        // Baseline then a change.
        detector.DetectChanges(MakeSnapshot("boot_a", cl: 18));
        detector.DetectChanges(MakeSnapshot("boot_b", cl: 16));

        var state = aggregator.BuildState();

        Assert.NotNull(state.RecentChanges);
        Assert.Single(state.RecentChanges);
        Assert.True(state.RecentChanges[0].Changes.ContainsKey("CL"));
    }

    [Fact]
    public void BuildState_WithMoreThanFiveChanges_RecentChangesCapAtFive()
    {
        var aggregator  = MakeAggregator();
        var detector    = new ConfigChangeDetector(_tempDir);
        var drift       = new DriftDetector(_tempDir);
        var valLogger   = new ValidationTestLogger(_tempDir);
        var lkg         = new LkgTracker(_tempDir);
        var snapshots   = new SnapshotJournal(_tempDir);

        aggregator.SetPhase3Services(detector, drift, valLogger, lkg, snapshots);

        // 7 snapshots → 6 changes (each pair differs in CL).
        for (int i = 0; i <= 6; i++)
            detector.DetectChanges(MakeSnapshot($"boot_{i}", cl: 18 + i));

        var state = aggregator.BuildState();

        Assert.NotNull(state.RecentChanges);
        Assert.Equal(5, state.RecentChanges.Count);
    }

    // -----------------------------------------------------------------------
    // Uptime
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildState_ServiceUptime_IsPositive()
    {
        var aggregator = MakeAggregator();

        var state = aggregator.BuildState();

        // System uptime is time since last boot — always positive on a running machine.
        Assert.True(state.ServiceUptime > TimeSpan.Zero);
    }

    [Fact]
    public void BuildState_ServiceUptime_EqualsTimestampMinusBootTime()
    {
        var aggregator = MakeAggregator();

        var state = aggregator.BuildState();

        // The field carries system uptime: Timestamp - BootTime.
        // Allow a 2-second window for execution time.
        var expected = state.Timestamp - state.BootTime;
        var delta    = (state.ServiceUptime - expected).Duration();
        Assert.True(delta < TimeSpan.FromSeconds(2),
            $"ServiceUptime={state.ServiceUptime} expected ~{expected} (delta={delta})");
    }

    [Fact]
    public void BuildState_BootTime_IsInThePast()
    {
        var aggregator = MakeAggregator();

        var before = DateTime.UtcNow;
        var state  = aggregator.BuildState();

        // Boot time must be before now (the machine was booted before this test ran).
        Assert.True(state.BootTime < before,
            $"BootTime={state.BootTime} should be before {before}");
    }
}
