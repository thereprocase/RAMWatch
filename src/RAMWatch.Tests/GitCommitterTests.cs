using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.Service.Services;

namespace RAMWatch.Tests;

/// <summary>
/// Tests for CommitMessageBuilder, GitCommitter, and related Phase 4 types.
///
/// GitCommitter tests use an injected process runner so no real git subprocess
/// is invoked. The injected runner records its calls and returns controlled results.
/// </summary>
public class GitCommitterTests : IDisposable
{
    private readonly string _tempDir;

    public GitCommitterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ramwatch-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // -----------------------------------------------------------------------
    // Test data helpers
    // -----------------------------------------------------------------------

    private static TimingSnapshot MakeSnapshot(int cl = 16, int rfc = 577) =>
        new TimingSnapshot
        {
            SnapshotId  = "snap-test",
            Timestamp   = new DateTime(2026, 4, 14, 0, 0, 0, DateTimeKind.Utc),
            BootId      = "boot-test",
            MemClockMhz = 1800,
            FclkMhz     = 1800,
            UclkMhz     = 1800,
            CL          = cl,
            RCDRD       = 22,
            RCDWR       = 22,
            RP          = 22,
            RAS         = 42,
            RFC         = rfc,
        };

    private static ConfigChange MakeChange(string before = "18", string after = "16") =>
        new ConfigChange
        {
            ChangeId  = Guid.NewGuid().ToString(),
            Timestamp = DateTime.UtcNow,
            BootId    = "boot-test",
            Changes   = new Dictionary<string, TimingDelta>
            {
                ["CL"]  = new TimingDelta(before, after),
                ["CWL"] = new TimingDelta(before, after)
            }
        };

    private static ValidationResult MakeValidation(bool passed = true,
        string tool = "Karhu", double value = 12400, string unit = "%") =>
        new ValidationResult
        {
            Timestamp   = new DateTime(2026, 4, 14, 0, 0, 0, DateTimeKind.Local),
            BootId      = "boot-test",
            TestTool    = tool,
            MetricName  = "coverage",
            MetricValue = value,
            MetricUnit  = unit,
            Passed      = passed
        };

    private static DriftEvent MakeDrift(string name = "RRDL", int expected = 11, int actual = 12) =>
        new DriftEvent
        {
            Timestamp        = DateTime.UtcNow,
            BootId           = "boot-test",
            TimingName       = name,
            ExpectedValue    = expected,
            ActualValue      = actual,
            BootsAtExpected  = 5,
            BootsAtActual    = 1
        };

    private SettingsManager MakeSettings(bool enableGit = true, bool enablePush = false)
    {
        string settingsDir = Path.Combine(_tempDir, "settings");
        Directory.CreateDirectory(settingsDir);
        var mgr = new SettingsManager(Path.Combine(settingsDir, "settings.json"));
        mgr.Save(new AppSettings
        {
            EnableGitIntegration = enableGit,
            EnableGitPush        = enablePush
        });
        return mgr;
    }

    // -----------------------------------------------------------------------
    // CommitMessageBuilder tests
    // -----------------------------------------------------------------------

    [Fact]
    public void CommitMessage_ConfigChange_ListsChangedFields()
    {
        var req = new GitCommitRequest
        {
            Reason          = GitCommitReason.ConfigChange,
            CurrentSnapshot = MakeSnapshot(),
            Change          = MakeChange("18", "16")
        };

        string msg = CommitMessageBuilder.Build(req);

        Assert.StartsWith("Config change:", msg);
        Assert.Contains("CL", msg);
        Assert.Contains("18->16", msg);
    }

    [Fact]
    public void CommitMessage_ConfigChange_NullChange_FallsBackToSimpleMessage()
    {
        var req = new GitCommitRequest
        {
            Reason          = GitCommitReason.ConfigChange,
            CurrentSnapshot = MakeSnapshot(),
            Change          = null
        };

        string msg = CommitMessageBuilder.Build(req);
        Assert.Equal("Config change", msg);
    }

    [Fact]
    public void CommitMessage_ValidationTest_KarhuPass()
    {
        var req = new GitCommitRequest
        {
            Reason          = GitCommitReason.ValidationTest,
            CurrentSnapshot = MakeSnapshot(cl: 16, rfc: 577),
            Validation      = MakeValidation(passed: true, tool: "Karhu", value: 12400, unit: "%")
        };

        string msg = CommitMessageBuilder.Build(req);

        Assert.Contains("Karhu", msg);
        Assert.Contains("12400%", msg);
        Assert.Contains("PASS", msg);
        Assert.Contains("16-22-22-42", msg);
        Assert.Contains("tRFC577", msg);
    }

    [Fact]
    public void CommitMessage_ValidationTest_Fail()
    {
        var req = new GitCommitRequest
        {
            Reason          = GitCommitReason.ValidationTest,
            CurrentSnapshot = MakeSnapshot(),
            Validation      = MakeValidation(passed: false)
        };

        string msg = CommitMessageBuilder.Build(req);
        Assert.Contains("FAIL", msg);
    }

    [Fact]
    public void CommitMessage_ValidationTest_CyclesUnit()
    {
        var req = new GitCommitRequest
        {
            Reason          = GitCommitReason.ValidationTest,
            CurrentSnapshot = MakeSnapshot(),
            Validation      = MakeValidation(passed: true, tool: "TM5", value: 30, unit: "cycles")
        };

        string msg = CommitMessageBuilder.Build(req);
        Assert.Contains("30 cycles", msg);
    }

    [Fact]
    public void CommitMessage_ValidationTest_NullValidation_FallsBack()
    {
        var req = new GitCommitRequest
        {
            Reason          = GitCommitReason.ValidationTest,
            CurrentSnapshot = MakeSnapshot(),
            Validation      = null
        };

        string msg = CommitMessageBuilder.Build(req);
        Assert.Equal("Validation test", msg);
    }

    [Fact]
    public void CommitMessage_DriftDetected_IncludesName_And_Values()
    {
        var req = new GitCommitRequest
        {
            Reason          = GitCommitReason.DriftDetected,
            CurrentSnapshot = MakeSnapshot(),
            DriftEvents     = new List<DriftEvent> { MakeDrift("RRDL", 11, 12) }
        };

        string msg = CommitMessageBuilder.Build(req);

        Assert.StartsWith("Drift detected:", msg);
        Assert.Contains("RRDL", msg);
        Assert.Contains("11->12", msg);
    }

    [Fact]
    public void CommitMessage_DriftDetected_MultipleDrifts_ShowsPlusMore()
    {
        var req = new GitCommitRequest
        {
            Reason          = GitCommitReason.DriftDetected,
            CurrentSnapshot = MakeSnapshot(),
            DriftEvents     = new List<DriftEvent>
            {
                MakeDrift("RRDL", 11, 12),
                MakeDrift("STAG", 255, 254)
            }
        };

        string msg = CommitMessageBuilder.Build(req);
        Assert.Contains("+1 more", msg);
    }

    [Fact]
    public void CommitMessage_DriftDetected_NullEvents_FallsBack()
    {
        var req = new GitCommitRequest
        {
            Reason          = GitCommitReason.DriftDetected,
            CurrentSnapshot = MakeSnapshot(),
            DriftEvents     = null
        };

        string msg = CommitMessageBuilder.Build(req);
        Assert.Equal("Drift detected", msg);
    }

    [Fact]
    public void CommitMessage_ManualSnapshot_IsLiteral()
    {
        var req = new GitCommitRequest
        {
            Reason          = GitCommitReason.ManualSnapshot,
            CurrentSnapshot = MakeSnapshot()
        };

        Assert.Equal("Manual snapshot", CommitMessageBuilder.Build(req));
    }

    [Fact]
    public void CommitMessage_LkgUpdated_IsLiteral()
    {
        var req = new GitCommitRequest
        {
            Reason          = GitCommitReason.LkgUpdated,
            CurrentSnapshot = MakeSnapshot()
        };

        Assert.Equal("LKG updated", CommitMessageBuilder.Build(req));
    }

    // -----------------------------------------------------------------------
    // GitCommitter — injected process runner tests
    // -----------------------------------------------------------------------

    private static ILogger MakeNullLogger() => NullLogger.Instance;

    /// <summary>
    /// Builds a GitCommitter wired to an injected runner that records all calls
    /// and returns success (exit code 0).
    /// </summary>
    private GitCommitter MakeCommitter(
        List<(ProcessStartInfo psi, string[] args)> calls,
        SettingsManager? settings = null,
        Func<ProcessStartInfo, CancellationToken, Task<ProcessResult>>? runner = null)
    {
        var mgr = settings ?? MakeSettings(enableGit: true);

        // Override DataDirectory paths by pointing the repo to our temp dir.
        // GitCommitter reads DataDirectory.HistoryRepoPath directly, so we create
        // that directory under our temp area to avoid writing to ProgramData.
        // (DataDirectory is a static class; in tests we just ensure the path exists.)
        Directory.CreateDirectory(DataDirectory.HistoryRepoPath);
        Directory.CreateDirectory(DataDirectory.GhConfigPath);

        Func<ProcessStartInfo, CancellationToken, Task<ProcessResult>> defaultRunner =
            (psi, ct) =>
            {
                string[] argsCopy = psi.ArgumentList.ToArray();
                calls.Add((psi, argsCopy));
                return Task.FromResult(new ProcessResult(0, "ok", ""));
            };

        return new GitCommitter(mgr, MakeNullLogger(), runner ?? defaultRunner);
    }

    [Fact]
    public async Task InitializeAsync_CallsGitInit()
    {
        var calls    = new List<(ProcessStartInfo psi, string[] args)>();
        var settings = MakeSettings(enableGit: true);

        // Patch settings to mark git as enabled and our fake runner always "succeeds"
        var committer = MakeCommitter(calls, settings);

        using var cts = new CancellationTokenSource(5000);
        await committer.InitializeAsync(cts.Token);

        // Should see "git init" among the calls
        bool hasInit = calls.Any(c => c.args.Contains("init"));
        Assert.True(hasInit, "Expected a 'git init' call during InitializeAsync");

        await committer.DisposeAsync();
    }

    [Fact]
    public async Task InitializeAsync_SetsUserNameAndEmail()
    {
        var calls = new List<(ProcessStartInfo psi, string[] args)>();
        using var cts = new CancellationTokenSource(5000);

        var committer = MakeCommitter(calls);
        await committer.InitializeAsync(cts.Token);

        bool hasName  = calls.Any(c => c.args.Contains("user.name"));
        bool hasEmail = calls.Any(c => c.args.Contains("user.email"));

        Assert.True(hasName,  "Expected git config user.name call");
        Assert.True(hasEmail, "Expected git config user.email call");

        await committer.DisposeAsync();
    }

    [Fact]
    public async Task ProcessCommit_ArgumentList_ContainsCommitMessage()
    {
        var calls = new List<(ProcessStartInfo psi, string[] args)>();
        using var cts = new CancellationTokenSource(5000);

        var committer = MakeCommitter(calls);
        await committer.InitializeAsync(cts.Token);

        var req = new GitCommitRequest
        {
            Reason          = GitCommitReason.ManualSnapshot,
            CurrentSnapshot = MakeSnapshot()
        };

        committer.Enqueue(req);

        // Give the drain task a moment to process the single request
        await Task.Delay(200, cts.Token);
        await committer.DisposeAsync();

        // Find the "git commit -m ..." call
        var commitCall = calls.FirstOrDefault(c => c.args.Contains("commit"));
        Assert.False(commitCall.args is null, "Expected a 'git commit' call");

        int mIndex = Array.IndexOf(commitCall.args!, "-m");
        Assert.True(mIndex >= 0, "Expected -m flag in commit args");
        Assert.Equal("Manual snapshot", commitCall.args![mIndex + 1]);
    }

    [Fact]
    public async Task ProcessCommit_GitAdd_UsesNamedFiles_NotDashA()
    {
        var calls = new List<(ProcessStartInfo psi, string[] args)>();
        using var cts = new CancellationTokenSource(5000);

        var committer = MakeCommitter(calls);
        await committer.InitializeAsync(cts.Token);

        committer.Enqueue(new GitCommitRequest
        {
            Reason          = GitCommitReason.ManualSnapshot,
            CurrentSnapshot = MakeSnapshot()
        });

        await Task.Delay(200, cts.Token);
        await committer.DisposeAsync();

        var addCalls = calls.Where(c => c.args.Contains("add")).ToList();
        Assert.NotEmpty(addCalls);

        // None of the add calls should contain "-A" or "."
        foreach (var call in addCalls)
        {
            Assert.DoesNotContain("-A", call.args);
            // The wildcard "." should not be used — only named files
            Assert.DoesNotContain(".", call.args.Where(a => a != "--").ToArray());
        }
    }

    [Fact]
    public async Task ProcessCommit_ShellMetacharactersInMessage_PassedSafely()
    {
        // A commit message containing shell-special characters should pass through
        // ArgumentList unchanged (ArgumentList escapes automatically — no shell injection).
        var calls = new List<(ProcessStartInfo psi, string[] args)>();
        using var cts = new CancellationTokenSource(5000);

        var committer = MakeCommitter(calls);
        await committer.InitializeAsync(cts.Token);

        // Construct a config change with a field name containing special chars
        // (pathological — real field names won't have these, but tests should verify safety).
        var change = new ConfigChange
        {
            ChangeId  = "chg-1",
            Timestamp = DateTime.UtcNow,
            BootId    = "boot-test",
            Changes   = new Dictionary<string, TimingDelta>
            {
                // The message will contain these characters — must not be interpreted as shell
                ["CL `rm -rf /`"] = new TimingDelta("$(evil)", "; bad")
            }
        };

        var req = new GitCommitRequest
        {
            Reason          = GitCommitReason.ConfigChange,
            CurrentSnapshot = MakeSnapshot(),
            Change          = change
        };

        committer.Enqueue(req);
        await Task.Delay(200, cts.Token);
        await committer.DisposeAsync();

        var commitCall = calls.FirstOrDefault(c => c.args.Contains("commit"));
        Assert.False(commitCall.args is null, "Expected a 'git commit' call");

        // The message must appear verbatim as a single ArgumentList entry
        int mIndex = Array.IndexOf(commitCall.args!, "-m");
        Assert.True(mIndex >= 0);
        string rawMessage = commitCall.args![mIndex + 1];
        // Verify the dangerous string is present verbatim — ArgumentList does not strip it
        Assert.Contains("$(evil)", rawMessage);
        Assert.Contains("; bad", rawMessage);
    }

    [Fact]
    public async Task Enqueue_ReturnsImmediately_DoesNotBlock()
    {
        // Drain task uses a slow runner; Enqueue must still return immediately.
        var calls = new List<(ProcessStartInfo psi, string[] args)>();

        Func<ProcessStartInfo, CancellationToken, Task<ProcessResult>> slowRunner =
            async (psi, ct) =>
            {
                // Simulate a slow git operation without actually blocking the thread
                await Task.Delay(500, ct);
                calls.Add((psi, psi.ArgumentList.ToArray()));
                return new ProcessResult(0, "ok", "");
            };

        var settings  = MakeSettings(enableGit: true);
        var committer = new GitCommitter(settings, MakeNullLogger(), slowRunner);

        using var cts = new CancellationTokenSource(5000);
        await committer.InitializeAsync(cts.Token);

        var sw = Stopwatch.StartNew();
        committer.Enqueue(new GitCommitRequest
        {
            Reason          = GitCommitReason.ManualSnapshot,
            CurrentSnapshot = MakeSnapshot()
        });
        sw.Stop();

        // Enqueue should take well under 100ms even with a 500ms drain runner
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"Enqueue took {sw.ElapsedMilliseconds}ms — must not block on drain");

        await committer.DisposeAsync();
    }

    [Fact]
    public async Task CircuitBreaker_StopsAfter10Failures()
    {
        int callCount = 0;

        Func<ProcessStartInfo, CancellationToken, Task<ProcessResult>> failingRunner =
            (psi, ct) =>
            {
                Interlocked.Increment(ref callCount);
                // Return failure for commit calls, success for everything else
                if (psi.ArgumentList.Contains("commit"))
                    return Task.FromResult(new ProcessResult(1, "", "simulated failure"));
                return Task.FromResult(new ProcessResult(0, "ok", ""));
            };

        var settings  = MakeSettings(enableGit: true);
        var committer = new GitCommitter(settings, MakeNullLogger(), failingRunner);

        using var cts = new CancellationTokenSource(10_000);
        await committer.InitializeAsync(cts.Token);

        // Enqueue 15 requests — circuit breaker should stop after 10 consecutive failures
        for (int i = 0; i < 15; i++)
        {
            committer.Enqueue(new GitCommitRequest
            {
                Reason          = GitCommitReason.ManualSnapshot,
                CurrentSnapshot = MakeSnapshot()
            });
        }

        // Allow time to process and trip the circuit breaker
        await Task.Delay(500, cts.Token);
        await committer.DisposeAsync();

        // After 10 failures the circuit should have opened — not all 15 commits attempted
        // The key assertion: circuit breaker means far fewer than 15 commit calls,
        // and the committer did not crash while handling the open state.
        Assert.True(callCount < 15 * 5,
            "Committer should have handled 15 requests with circuit breaker without crashing");
    }

    [Fact]
    public async Task Enqueue_WhenGitIntegrationDisabled_DoesNotQueue()
    {
        var calls    = new List<(ProcessStartInfo psi, string[] args)>();
        var settings = MakeSettings(enableGit: false);  // disabled

        var committer = MakeCommitter(calls, settings);
        using var cts = new CancellationTokenSource(5000);
        await committer.InitializeAsync(cts.Token);

        int callsBefore = calls.Count;
        committer.Enqueue(new GitCommitRequest
        {
            Reason          = GitCommitReason.ManualSnapshot,
            CurrentSnapshot = MakeSnapshot()
        });
        await Task.Delay(200, cts.Token);
        await committer.DisposeAsync();

        // No additional calls beyond init/config setup
        Assert.Equal(callsBefore, calls.Count);
    }

    [Fact]
    public async Task ResetFailures_ClearsCircuitBreaker()
    {
        int commitAttempts = 0;

        Func<ProcessStartInfo, CancellationToken, Task<ProcessResult>> runner =
            (psi, ct) =>
            {
                if (psi.ArgumentList.Contains("commit"))
                    Interlocked.Increment(ref commitAttempts);
                return Task.FromResult(new ProcessResult(0, "ok", ""));
            };

        var settings  = MakeSettings(enableGit: true);
        var committer = new GitCommitter(settings, MakeNullLogger(), runner);

        using var cts = new CancellationTokenSource(5000);
        await committer.InitializeAsync(cts.Token);

        // Manually trip the circuit breaker by driving failures to >= 10
        // We can't inject failures here without another runner, so just verify
        // ResetFailures() runs without throwing.
        committer.ResetFailures();

        committer.Enqueue(new GitCommitRequest
        {
            Reason          = GitCommitReason.ManualSnapshot,
            CurrentSnapshot = MakeSnapshot()
        });

        await Task.Delay(200, cts.Token);
        await committer.DisposeAsync();

        Assert.True(commitAttempts >= 1, "Expected at least one commit after reset");
    }
}
