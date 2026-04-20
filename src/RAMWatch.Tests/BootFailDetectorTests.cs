using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.Service.Services;

namespace RAMWatch.Tests;

/// <summary>
/// Tests for the BootFailDetector classifier logic. EventLog queries are
/// mocked via delegate injection so tests don't touch real Windows logs.
/// </summary>
public class BootFailDetectorTests
{
    private static readonly DateTime T0 =
        new(2026, 4, 19, 23, 57, 0, DateTimeKind.Utc);

    // ── No signals → null ────────────────────────────────────────────────

    [Fact]
    public void DetectPriorCrash_NoSignals_ReturnsNull()
    {
        var detector = new BootFailDetector(
            querySignals: _ => Array.Empty<BootFailDetector.CrashSignal>(),
            queryWhea:    _ => false);

        var result = detector.DetectPriorCrash(
            sinceUtc:        T0,
            baseSnapshotId:  null,
            activeEraId:     null,
            existingEntries: Array.Empty<BootFailEntry>());

        Assert.Null(result);
    }

    // ── Signals present → entry returned with Unstable kind ──────────────

    [Fact]
    public void DetectPriorCrash_SignalsPresent_ReturnsUnstableEntry()
    {
        var detector = new BootFailDetector(
            querySignals: _ => new[]
            {
                new BootFailDetector.CrashSignal(T0.AddMinutes(1),
                    "Microsoft-Windows-Kernel-Power", 41),
                new BootFailDetector.CrashSignal(T0.AddMinutes(1).AddSeconds(6),
                    "Microsoft-Windows-WER-SystemErrorReporting", 1001),
            },
            queryWhea: _ => false);

        var result = detector.DetectPriorCrash(
            sinceUtc:        T0,
            baseSnapshotId:  null,
            activeEraId:     null,
            existingEntries: Array.Empty<BootFailEntry>());

        Assert.NotNull(result);
        Assert.Equal(BootFailKind.Unstable, result!.Kind);
    }

    // ── Timestamp = earliest signal ──────────────────────────────────────

    [Fact]
    public void DetectPriorCrash_Timestamp_UsesEarliestSignal()
    {
        var earliest = T0.AddSeconds(30);
        var later    = T0.AddMinutes(2);

        var detector = new BootFailDetector(
            querySignals: _ => new[]
            {
                new BootFailDetector.CrashSignal(later,    "WER",          1001),
                new BootFailDetector.CrashSignal(earliest, "Kernel-Power", 41),
            },
            queryWhea: _ => false);

        var result = detector.DetectPriorCrash(T0, null, null,
            Array.Empty<BootFailEntry>());

        Assert.NotNull(result);
        Assert.Equal(earliest, result!.Timestamp);
    }

    // ── Dedup: existing entry within 5 min → null ────────────────────────

    [Fact]
    public void DetectPriorCrash_ExistingEntryWithinDedupeWindow_ReturnsNull()
    {
        var signalTime = T0.AddMinutes(1);

        var detector = new BootFailDetector(
            querySignals: _ => new[]
            {
                new BootFailDetector.CrashSignal(signalTime, "Kernel-Power", 41),
            },
            queryWhea: _ => false);

        var existing = new[]
        {
            new BootFailEntry
            {
                BootFailId = "existing",
                Timestamp  = signalTime.AddMinutes(2), // within ±5 min
                Kind       = BootFailKind.Unstable,
            }
        };

        var result = detector.DetectPriorCrash(T0, null, null, existing);

        Assert.Null(result);
    }

    [Fact]
    public void DetectPriorCrash_ExistingEntryOutsideDedupeWindow_ReturnsNewEntry()
    {
        var signalTime = T0.AddMinutes(1);

        var detector = new BootFailDetector(
            querySignals: _ => new[]
            {
                new BootFailDetector.CrashSignal(signalTime, "Kernel-Power", 41),
            },
            queryWhea: _ => false);

        var existing = new[]
        {
            new BootFailEntry
            {
                BootFailId = "old",
                Timestamp  = signalTime.AddHours(-2), // well outside window
                Kind       = BootFailKind.Unstable,
            }
        };

        var result = detector.DetectPriorCrash(T0, null, null, existing);

        Assert.NotNull(result);
    }

    // ── Notes annotate with WHEA / no-WHEA class hint ────────────────────

    [Fact]
    public void DetectPriorCrash_NoWhea_NotesFlagOsBugcheckClass()
    {
        var detector = new BootFailDetector(
            querySignals: _ => new[]
            {
                new BootFailDetector.CrashSignal(T0.AddSeconds(30), "Kernel-Power", 41),
            },
            queryWhea: _ => false);

        var result = detector.DetectPriorCrash(T0, null, null,
            Array.Empty<BootFailEntry>());

        Assert.NotNull(result);
        Assert.Contains("OS-bugcheck class", result!.Notes);
    }

    [Fact]
    public void DetectPriorCrash_WithWhea_NotesFlagMemoryMceClass()
    {
        var detector = new BootFailDetector(
            querySignals: _ => new[]
            {
                new BootFailDetector.CrashSignal(T0.AddSeconds(30), "Kernel-Power", 41),
            },
            queryWhea: _ => true);

        var result = detector.DetectPriorCrash(T0, null, null,
            Array.Empty<BootFailEntry>());

        Assert.NotNull(result);
        Assert.Contains("memory/MCE class", result!.Notes);
    }

    // ── EraId + BaseSnapshotId pass-through ──────────────────────────────

    [Fact]
    public void DetectPriorCrash_PropagatesEraIdAndBaseSnapshot()
    {
        var detector = new BootFailDetector(
            querySignals: _ => new[]
            {
                new BootFailDetector.CrashSignal(T0.AddSeconds(30), "Kernel-Power", 41),
            },
            queryWhea: _ => false);

        var result = detector.DetectPriorCrash(
            sinceUtc:        T0,
            baseSnapshotId:  "snap-abc",
            activeEraId:     "era-xyz",
            existingEntries: Array.Empty<BootFailEntry>());

        Assert.NotNull(result);
        Assert.Equal("snap-abc", result!.BaseSnapshotId);
        Assert.Equal("era-xyz",  result.EraId);
    }

    // ── AttemptedChanges null by default (user can fill in later) ────────

    [Fact]
    public void DetectPriorCrash_AttemptedChangesIsNull()
    {
        var detector = new BootFailDetector(
            querySignals: _ => new[]
            {
                new BootFailDetector.CrashSignal(T0.AddSeconds(30), "Kernel-Power", 41),
            },
            queryWhea: _ => false);

        var result = detector.DetectPriorCrash(T0, null, null,
            Array.Empty<BootFailEntry>());

        Assert.NotNull(result);
        Assert.Null(result!.AttemptedChanges);
    }

    // ── Notes list all signals ──────────────────────────────────────────

    [Fact]
    public void DetectPriorCrash_NotesListAllSignalProviders()
    {
        var detector = new BootFailDetector(
            querySignals: _ => new[]
            {
                new BootFailDetector.CrashSignal(T0.AddSeconds(10), "Kernel-Power", 41),
                new BootFailDetector.CrashSignal(T0.AddSeconds(20), "WER",          1001),
                new BootFailDetector.CrashSignal(T0.AddSeconds(30), "EventLog",     6008),
            },
            queryWhea: _ => false);

        var result = detector.DetectPriorCrash(T0, null, null,
            Array.Empty<BootFailEntry>());

        Assert.NotNull(result);
        Assert.Contains("Kernel-Power", result!.Notes);
        Assert.Contains("WER",          result.Notes);
        Assert.Contains("EventLog",     result.Notes);
    }
}
