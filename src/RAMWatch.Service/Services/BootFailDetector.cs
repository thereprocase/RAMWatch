using System.Diagnostics.Eventing.Reader;
using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Detects whether the prior boot failed by scanning the Windows event log
/// on service startup for crash signals written during the next-boot recovery
/// window. When signals are present, persists a partial <see cref="BootFailEntry"/>
/// so the user doesn't have to remember to log the crash themselves.
///
/// Signals considered:
///   Kernel-Power 41 — "system has rebooted without cleanly shutting down first"
///   WER 1001        — "The bucket was created for the crash dump"
///   EventLog 6008   — "The previous system shutdown... was unexpected"
///
/// These events are written on the *next* boot (not at crash time), so their
/// TimeCreated is in the early window of the current session. Querying from
/// service start back to a caller-supplied floor (typically the current boot
/// time) catches them without pulling in older unrelated crashes.
///
/// Also scans for WHEA-Logger id 1 (Machine Check Exception) in the same
/// window. Presence annotates the entry with a memory/MCE-class hint; absence
/// annotates it as OS-bugcheck class (the primary-timing setup/hold fingerprint
/// the user observed on 2026-04-19).
///
/// Dedups against the existing <see cref="BootFailJournal"/> contents so
/// repeated service restarts in the same boot don't log duplicate entries.
/// </summary>
public sealed class BootFailDetector
{
    /// <summary>
    /// Maximum timestamp distance (minutes) between a detected signal and an
    /// existing BootFailEntry to treat them as the same crash.
    /// </summary>
    private const double DedupeWindowMinutes = 5.0;

    public readonly record struct CrashSignal(DateTime Time, string Provider, int EventId);

    public delegate IReadOnlyList<CrashSignal> SignalQueryFn(DateTime since);
    public delegate bool WheaFatalInWindowFn(DateTime since);

    private readonly SignalQueryFn _querySignals;
    private readonly WheaFatalInWindowFn _queryWhea;

    public BootFailDetector(SignalQueryFn querySignals, WheaFatalInWindowFn queryWhea)
    {
        _querySignals = querySignals;
        _queryWhea = queryWhea;
    }

    /// <summary>
    /// Production factory — uses real EventLogReader queries.
    /// </summary>
    public static BootFailDetector CreateDefault() =>
        new(DefaultQuerySignals, DefaultQueryWheaFatal);

    /// <summary>
    /// Scan for crash signals since <paramref name="sinceUtc"/> and return a
    /// populated <see cref="BootFailEntry"/> when one is detected. Returns null
    /// when no signals were found or the detected crash matches an existing
    /// journal entry within <see cref="DedupeWindowMinutes"/>.
    /// </summary>
    public BootFailEntry? DetectPriorCrash(
        DateTime sinceUtc,
        string? baseSnapshotId,
        string? activeEraId,
        IReadOnlyList<BootFailEntry> existingEntries)
    {
        var signals = _querySignals(sinceUtc);
        if (signals.Count == 0)
            return null;

        // Earliest signal is the closest approximation of when the crash
        // actually happened (Kernel-Power 41 typically fires first).
        var earliest = signals.Min(s => s.Time);

        foreach (var existing in existingEntries)
        {
            if (Math.Abs((existing.Timestamp - earliest).TotalMinutes) <= DedupeWindowMinutes)
                return null;
        }

        bool hadWhea = _queryWhea(sinceUtc);

        string signalList = string.Join(", ",
            signals.Select(s => $"{s.Provider} #{s.EventId} @ {s.Time:HH:mm:ss}"));
        string wheaNote = hadWhea
            ? "WHEA-fatal events also present in window (memory/MCE class)."
            : "No WHEA in window (OS-bugcheck class — often primary-timing setup/hold failure).";

        return new BootFailEntry
        {
            BootFailId = Guid.NewGuid().ToString("N"),
            Timestamp = earliest,
            Kind = BootFailKind.Unstable,
            BaseSnapshotId = baseSnapshotId,
            AttemptedChanges = null,
            Notes = $"Auto-detected from EventLog. {signalList}. {wheaNote}",
            EraId = activeEraId,
            Class = hadWhea ? CrashClass.WheaFatal : CrashClass.OsBugcheck
        };
    }

    // ── Production EventLog query implementations ────────────────────────

    private static IReadOnlyList<CrashSignal> DefaultQuerySignals(DateTime since)
    {
        var result = new List<CrashSignal>();
        var sources = new (string Provider, string LogName, int[] EventIds)[]
        {
            ("Microsoft-Windows-Kernel-Power",             "System", new[] { 41 }),
            ("Microsoft-Windows-WER-SystemErrorReporting", "System", new[] { 1001 }),
            ("EventLog",                                    "System", new[] { 6008 }),
        };

        var sinceUtc = since.ToUniversalTime();
        foreach (var src in sources)
        {
            try
            {
                string ids = string.Join(" or ", src.EventIds.Select(id => $"EventID={id}"));
                string query = $"*[System[Provider[@Name='{src.Provider}'] and ({ids}) and TimeCreated[@SystemTime>='{sinceUtc:o}']]]";
                var logQuery = new EventLogQuery(src.LogName, PathType.LogName, query);

                using var reader = new EventLogReader(logQuery);
                EventRecord? record;
                while ((record = reader.ReadEvent()) is not null)
                {
                    using (record)
                    {
                        var t = record.TimeCreated ?? DateTime.UtcNow;
                        result.Add(new CrashSignal(t, src.Provider, record.Id));
                    }
                }
            }
            catch (EventLogNotFoundException) { }
            catch (UnauthorizedAccessException) { }
            catch
            {
                // Best-effort scan — skip sources that fail to query. The
                // service should never crash because EventLog is unhappy.
            }
        }
        return result;
    }

    private static bool DefaultQueryWheaFatal(DateTime since)
    {
        try
        {
            var sinceUtc = since.ToUniversalTime();
            string query = $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and EventID=1 and TimeCreated[@SystemTime>='{sinceUtc:o}']]]";
            var logQuery = new EventLogQuery("System", PathType.LogName, query);

            using var reader = new EventLogReader(logQuery);
            return reader.ReadEvent() is not null;
        }
        catch
        {
            return false;
        }
    }
}
