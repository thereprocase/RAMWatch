using System.Diagnostics.Eventing.Reader;
using RAMWatch.Core.Models;
using RAMWatch.Service.Hardware;

namespace RAMWatch.Service.Services;

/// <summary>
/// Push-based Windows event log monitoring using EventLogWatcher (kernel callback).
/// Zero CPU between events. On startup, performs a one-time historical scan
/// for events since last boot, then switches to live watching.
/// </summary>
public sealed class EventLogMonitor : IDisposable
{
    private readonly List<EventLogWatcher> _watchers = [];
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ErrorSource> _errorSources = new();
    private readonly List<MonitoredEvent> _recentEvents = [];
    private readonly Dictionary<string, DateTime> _lastEventTime = new();
    private readonly Dictionary<string, int> _coalescedCounts = new();
    // Dedup by (LogName, RecordId) to prevent double-counting when Windows
    // re-delivers events after a watcher reconnect, or when the historical
    // scan races with the live watcher's subscription window. RecordId is
    // monotonic per channel; a second delivery of the same id is a duplicate.
    private readonly HashSet<(string, long)> _seenRecordIds = new();
    private const int MaxDedupCacheSize = 10000;
    private const int MinEventIntervalMs = 1000;
    private DateTime _bootTime;
    private bool _historicalScanComplete;

    /// <summary>
    /// CPU family for MCA bank classification. Set before Start() by the service.
    /// When Unknown, the classifier uses the default Zen 2/3 bank ranges.
    /// </summary>
    public CpuDetect.CpuFamily CpuFamily { get; set; }

    /// <summary>
    /// Fired when a new event is detected (live, not historical scan).
    /// </summary>
    public event Action<MonitoredEvent>? EventDetected;

    /// <summary>
    /// All watched event sources with their current counts.
    /// </summary>
    public static readonly WatchedSource[] WatchedSources =
    [
        // Hardware — WHEA
        new("WHEA Hardware Errors", "Microsoft-Windows-WHEA-Logger", EventCategory.Hardware,
            [17, 18, 19, 20, 46, 47, 48], EventSeverity.Warning),
        new("Machine Check Exception", "Microsoft-Windows-WHEA-Logger", EventCategory.Hardware,
            [1], EventSeverity.Critical),
        // Kernel-WHEA is a separate provider that routes corrected errors on some
        // Windows builds / AGESA versions. Same event structure as WHEA-Logger.
        new("Kernel WHEA Errors", "Microsoft-Windows-Kernel-WHEA", EventCategory.Hardware,
            [1, 17, 18, 19, 20, 46, 47, 48], EventSeverity.Warning),
        // PCIe Advanced Error Reporting — fabric-adjacent on Zen platforms where
        // Infinity Fabric connects to PCIe root complexes.
        new("PCIe Bus Errors", "Microsoft-Windows-Kernel-PCI", EventCategory.Hardware,
            [1, 3, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23], EventSeverity.Warning),
        new("Kernel Bugcheck", "Microsoft-Windows-WER-SystemErrorReporting", EventCategory.Hardware,
            [1001], EventSeverity.Critical),
        new("Unexpected Shutdown", "Microsoft-Windows-Kernel-Power", EventCategory.Hardware,
            [41], EventSeverity.Critical),

        // Filesystem
        new("Disk Error", "disk", EventCategory.Filesystem,
            [7, 11, 15, 51, 52], EventSeverity.Warning),
        new("NTFS Error", "Ntfs", EventCategory.Filesystem,
            [55, 98, 137, 140], EventSeverity.Warning),
        new("Volume Shadow Copy", "volsnap", EventCategory.Filesystem,
            [14, 25, 35, 36], EventSeverity.Warning),

        // Integrity
        new("Code Integrity", "Microsoft-Windows-CodeIntegrity", EventCategory.Integrity,
            [3001, 3002, 3003, 3004, 3033], EventSeverity.Notice),
        new("Filter Manager", "Microsoft-Windows-FilterManager", EventCategory.Integrity,
            [3, 6], EventSeverity.Notice),

        // Application
        new("Application Crash", "Application Error", EventCategory.Application,
            [1000], EventSeverity.Notice),
        new("Application Hang", "Application Hang", EventCategory.Application,
            [1002], EventSeverity.Notice),
        new("Memory Diagnostics", "Microsoft-Windows-MemoryDiagnostics-Results", EventCategory.Hardware,
            [1001, 1002], EventSeverity.Warning),
    ];

    public void Start()
    {
        _bootTime = GetLastBootTime();
        InitializeErrorSources();
        RunHistoricalScan();
        StartLiveWatchers();
    }

    public List<ErrorSource> GetErrorSources()
    {
        lock (_lock)
        {
            return _errorSources.Values.ToList();
        }
    }

    public List<MonitoredEvent> GetRecentEvents(int count = 50)
    {
        lock (_lock)
        {
            return _recentEvents.TakeLast(count).ToList();
        }
    }

    private void InitializeErrorSources()
    {
        lock (_lock)
        {
            foreach (var source in WatchedSources)
            {
                _errorSources[source.Name] = new ErrorSource(
                    source.Name, source.Category, 0, null);
            }
        }
    }

    /// <summary>
    /// Scan event logs for events since last boot. Runs once on startup.
    /// This catches events that fired before the service started.
    /// </summary>
    private void RunHistoricalScan()
    {
        foreach (var source in WatchedSources)
        {
            try
            {
                string query = BuildXPathQuery(source, _bootTime);
                var logQuery = new EventLogQuery(source.LogName, PathType.LogName, query);

                using var reader = new EventLogReader(logQuery);
                EventRecord? record;
                while ((record = reader.ReadEvent()) is not null)
                {
                    using (record)
                    {
                        RecordEvent(source, record, isHistorical: true);
                    }
                }
            }
            catch (EventLogNotFoundException)
            {
                // This event log doesn't exist on this system — skip it
            }
            catch (UnauthorizedAccessException)
            {
                // Insufficient permissions — skip this source
            }
            catch
            {
                // Other errors reading the log — skip, don't crash
            }
        }

        _historicalScanComplete = true;
    }

    private void StartLiveWatchers()
    {
        foreach (var source in WatchedSources)
        {
            try
            {
                string query = BuildXPathQuery(source, DateTime.UtcNow);
                var logQuery = new EventLogQuery(source.LogName, PathType.LogName, query);
                var watcher = new EventLogWatcher(logQuery);

                var capturedSource = source;
                watcher.EventRecordWritten += (_, args) =>
                {
                    try
                    {
                        if (args.EventRecord is not null)
                        {
                            using (args.EventRecord)
                            {
                                RecordEvent(capturedSource, args.EventRecord, isHistorical: false);
                            }
                        }
                    }
                    catch
                    {
                        // Background callback — must not crash the service
                    }
                };

                watcher.Enabled = true;
                _watchers.Add(watcher);
            }
            catch (EventLogNotFoundException)
            {
                // Log doesn't exist on this system
            }
            catch (UnauthorizedAccessException)
            {
                // Insufficient permissions
            }
            catch
            {
                // Other setup error — skip this watcher
            }
        }
    }

    private void RecordEvent(WatchedSource source, EventRecord record, bool isHistorical)
    {
        var timestamp = record.TimeCreated ?? DateTime.UtcNow;
        var summary = TruncateSummary(record.FormatDescription() ?? $"Event {record.Id} from {source.Name}");

        string? rawXml = null;
        try { rawXml = record.ToXml(); } catch { }

        // For WHEA events, attempt to decode MCA bank data from the raw XML.
        McaDetails? mca = null;
        if (source.ProviderName is "Microsoft-Windows-WHEA-Logger" or "Microsoft-Windows-Kernel-WHEA")
        {
            mca = McaBankClassifier.TryParse(rawXml, CpuFamily);
        }

        var evt = new MonitoredEvent(
            timestamp,
            source.Name,
            source.Category,
            record.Id,
            source.DefaultSeverity,
            summary,
            rawXml,
            mca);

        bool shouldFire = false;
        MonitoredEvent? coalescedEvt = null;

        lock (_lock)
        {
            // Dedup by (LogName, RecordId). RecordId is a 64-bit monotonic
            // identifier per channel; a repeat means Windows re-delivered
            // the same event (watcher reconnect, or historical/live overlap).
            if (record.RecordId is long recordId)
            {
                if (!_seenRecordIds.Add((source.LogName, recordId)))
                    return;

                // Bound the cache. On overflow, clearing is pragmatic — we
                // accept the small risk of one duplicate slipping through
                // right after the reset in exchange for O(1) memory.
                if (_seenRecordIds.Count > MaxDedupCacheSize)
                    _seenRecordIds.Clear();
            }

            if (_errorSources.TryGetValue(source.Name, out var current))
            {
                var lastSeen = timestamp > (current.LastSeen ?? DateTime.MinValue)
                    ? timestamp : current.LastSeen;
                _errorSources[source.Name] = current with
                {
                    Count = current.Count + 1,
                    LastSeen = lastSeen
                };
            }

            _recentEvents.Add(evt);
            if (_recentEvents.Count > 500)
                _recentEvents.RemoveRange(0, _recentEvents.Count - 500);

            if (!isHistorical && _historicalScanComplete)
            {
                // Critical events (MCE, kernel bugcheck, unexpected shutdown)
                // bypass the 1s rate limiter. RAMBurn's idle-stability phase
                // correlates WHEAs against deliberate C-state transitions
                // within a sub-second window — a coalesce delay would put the
                // event in the wrong correlation bucket. Non-critical events
                // still coalesce the same as before.
                bool isCritical = evt.Severity == EventSeverity.Critical;
                bool withinCooldown =
                    _lastEventTime.TryGetValue(source.Name, out var lastTime) &&
                    (DateTime.UtcNow - lastTime).TotalMilliseconds < MinEventIntervalMs;

                if (!isCritical && withinCooldown)
                {
                    // Rate-limited — count but don't fire yet.
                    _coalescedCounts[source.Name] =
                        _coalescedCounts.GetValueOrDefault(source.Name) + 1;
                }
                else
                {
                    // Outside the cooldown window, or a Critical bypass — fire,
                    // folding in any coalesced count.
                    int coalesced = _coalescedCounts.GetValueOrDefault(source.Name);
                    _coalescedCounts[source.Name] = 0;
                    _lastEventTime[source.Name] = DateTime.UtcNow;

                    shouldFire = true;
                    // When there were suppressed events, rewrite the summary to include
                    // the count so the GUI shows the true volume.
                    if (coalesced > 0)
                    {
                        coalescedEvt = evt with
                        {
                            Summary = $"[+{coalesced} suppressed] {evt.Summary}"
                        };
                    }
                }
            }
        }

        if (shouldFire)
            EventDetected?.Invoke(coalescedEvt ?? evt);
    }

    private static string BuildXPathQuery(WatchedSource source, DateTime since)
    {
        var sinceUtc = since.ToUniversalTime();
        string ids = string.Join(" or ", source.EventIds.Select(id => $"EventID={id}"));
        return $"*[System[Provider[@Name='{source.ProviderName}'] and ({ids}) and TimeCreated[@SystemTime>='{sinceUtc:o}']]]";
    }

    private static string TruncateSummary(string? text, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static DateTime GetLastBootTime()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return DateTime.UtcNow - uptime;
    }

    /// <summary>
    /// Test seam: inject a synthetic live event as if it arrived from an EventLogWatcher.
    /// Sets <c>_historicalScanComplete</c> to true so the rate limiter is active.
    /// Only callable by RAMWatch.Tests (InternalsVisibleTo).
    /// </summary>
    internal void InjectLiveEventForTest(WatchedSource source, MonitoredEvent evt, long? recordId = null)
    {
        bool shouldFire = false;
        MonitoredEvent? coalescedEvt = null;

        lock (_lock)
        {
            // Ensure historical scan is marked complete so the rate-limiter path runs.
            _historicalScanComplete = true;

            // Mirror the dedup block in RecordEvent so tests can exercise it.
            if (recordId is long id)
            {
                if (!_seenRecordIds.Add((source.LogName, id)))
                    return;

                if (_seenRecordIds.Count > MaxDedupCacheSize)
                    _seenRecordIds.Clear();
            }

            if (_errorSources.TryGetValue(source.Name, out var current))
            {
                _errorSources[source.Name] = current with
                {
                    Count    = current.Count + 1,
                    LastSeen = evt.Timestamp
                };
            }
            else
            {
                _errorSources[source.Name] = new ErrorSource(source.Name, source.Category, 1, evt.Timestamp);
            }

            _recentEvents.Add(evt);
            if (_recentEvents.Count > 500)
                _recentEvents.RemoveRange(0, _recentEvents.Count - 500);

            // Critical events bypass the rate limiter — mirror of the
            // production path so the test seam exercises the same behavior.
            bool isCritical = evt.Severity == EventSeverity.Critical;
            bool withinCooldown =
                _lastEventTime.TryGetValue(source.Name, out var lastTime) &&
                (DateTime.UtcNow - lastTime).TotalMilliseconds < MinEventIntervalMs;

            if (!isCritical && withinCooldown)
            {
                _coalescedCounts[source.Name] =
                    _coalescedCounts.GetValueOrDefault(source.Name) + 1;
            }
            else
            {
                int coalesced = _coalescedCounts.GetValueOrDefault(source.Name);
                _coalescedCounts[source.Name] = 0;
                _lastEventTime[source.Name] = DateTime.UtcNow;

                shouldFire = true;
                if (coalesced > 0)
                {
                    coalescedEvt = evt with
                    {
                        Summary = $"[+{coalesced} suppressed] {evt.Summary}"
                    };
                }
            }
        }

        if (shouldFire)
            EventDetected?.Invoke(coalescedEvt ?? evt);
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.Enabled = false;
                watcher.Dispose();
            }
            catch { }
        }
        _watchers.Clear();
    }
}

/// <summary>
/// Defines a Windows event log source to watch.
/// LogName is the provider name used in EventLogQuery (e.g., "Microsoft-Windows-WHEA-Logger").
/// For classic logs like "Application Error", the log name is "Application".
/// </summary>
public sealed record WatchedSource(
    string Name,
    string LogName,
    EventCategory Category,
    int[] EventIds,
    EventSeverity DefaultSeverity
)
{
    /// <summary>
    /// The actual event log to query. Classic providers use "Application" or "System".
    /// ETW providers use their own log names.
    /// </summary>
    public string LogName { get; } = LogName switch
    {
        "Application Error" or "Application Hang" => "Application",
        "disk" or "Ntfs" or "volsnap" => "System",
        "Microsoft-Windows-WER-SystemErrorReporting" => "System",
        "Microsoft-Windows-Kernel-Power" => "System",
        "Microsoft-Windows-Kernel-PCI" => "System",
        "Microsoft-Windows-Kernel-WHEA" => "System",
        "Microsoft-Windows-FilterManager" => "System",
        "Microsoft-Windows-WHEA-Logger" => "System",
        "Microsoft-Windows-MemoryDiagnostics-Results" => "System",
        "Microsoft-Windows-CodeIntegrity" => "Microsoft-Windows-CodeIntegrity/Operational",
        _ => LogName
    };

    /// <summary>
    /// The provider name used in XPath queries.
    /// </summary>
    public string ProviderName { get; } = LogName;
}
