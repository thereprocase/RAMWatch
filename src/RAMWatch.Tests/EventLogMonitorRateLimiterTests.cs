using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.Service.Services;

namespace RAMWatch.Tests;

/// <summary>
/// Tests for the per-source rate limiter in EventLogMonitor.
/// Uses InjectLiveEventForTest to drive the rate-limiter path without
/// touching real Windows event logs.
/// </summary>
public class EventLogMonitorRateLimiterTests : IDisposable
{
    private readonly EventLogMonitor _monitor;
    private readonly WatchedSource _source;
    private readonly List<MonitoredEvent> _fired = [];

    public EventLogMonitorRateLimiterTests()
    {
        _monitor = new EventLogMonitor();
        // Use the first watched source — the test doesn't care which one.
        _source = EventLogMonitor.WatchedSources[0];
        _monitor.EventDetected += evt => _fired.Add(evt);
    }

    public void Dispose() => _monitor.Dispose();

    [Fact]
    public void FirstEvent_Fires_Immediately()
    {
        var evt = MakeEvent("first");
        _monitor.InjectLiveEventForTest(_source, evt);

        Assert.Single(_fired);
        Assert.Equal("first", _fired[0].Summary);
    }

    [Fact]
    public void SecondEvent_WithinCooldown_IsRateLimited_DoesNotFire()
    {
        _monitor.InjectLiveEventForTest(_source, MakeEvent("first"));
        _monitor.InjectLiveEventForTest(_source, MakeEvent("second"));

        // Only the first event should have fired; second is within the 1-second window.
        Assert.Single(_fired);
        Assert.Equal("first", _fired[0].Summary);
    }

    [Fact]
    public void MultipleRapidEvents_Coalesce_FiredOnNextOutsideCooldown()
    {
        // Fire three rapid events: first fires, second and third are suppressed.
        _monitor.InjectLiveEventForTest(_source, MakeEvent("rapid-1"));
        _monitor.InjectLiveEventForTest(_source, MakeEvent("rapid-2"));
        _monitor.InjectLiveEventForTest(_source, MakeEvent("rapid-3"));

        Assert.Single(_fired);
        Assert.Equal("rapid-1", _fired[0].Summary);

        // Force the clock past the cooldown by manipulating via a sleep.
        // In the unit test we use Thread.Sleep(>= MinEventIntervalMs).
        Thread.Sleep(1100);

        // Next event after cooldown includes the coalesced count of 2.
        _monitor.InjectLiveEventForTest(_source, MakeEvent("after-cooldown"));

        Assert.Equal(2, _fired.Count);
        Assert.Contains("[+2 suppressed]", _fired[1].Summary);
        Assert.Contains("after-cooldown", _fired[1].Summary);
    }

    [Fact]
    public void DifferentSources_RateLimitedIndependently()
    {
        // Two different sources — each has its own cooldown window.
        var source2 = EventLogMonitor.WatchedSources[1];

        _monitor.InjectLiveEventForTest(_source,  MakeEvent("src1-first"));
        _monitor.InjectLiveEventForTest(source2,  MakeEvent("src2-first"));

        // Both fire: they are tracked independently.
        Assert.Equal(2, _fired.Count);
    }

    [Fact]
    public void CoalescedCountResets_AfterNextFiredEvent()
    {
        // First event fires.
        _monitor.InjectLiveEventForTest(_source, MakeEvent("e1"));

        // Two rapid events suppressed — coalesced count becomes 2.
        _monitor.InjectLiveEventForTest(_source, MakeEvent("e2"));
        _monitor.InjectLiveEventForTest(_source, MakeEvent("e3"));

        // Wait out the cooldown.
        Thread.Sleep(1100);

        // Next event fires with coalesced count 2, then resets count to 0.
        _monitor.InjectLiveEventForTest(_source, MakeEvent("e4"));

        Assert.Equal(2, _fired.Count);
        Assert.Contains("[+2 suppressed]", _fired[1].Summary);

        // Another rapid event after e4 is rate-limited, no coalesced history carried forward.
        _monitor.InjectLiveEventForTest(_source, MakeEvent("e5"));
        Assert.Equal(2, _fired.Count); // Still 2 — e5 suppressed.

        // After another cooldown the count should reflect only e5 (1 suppressed).
        Thread.Sleep(1100);
        _monitor.InjectLiveEventForTest(_source, MakeEvent("e6"));

        Assert.Equal(3, _fired.Count);
        Assert.Contains("[+1 suppressed]", _fired[2].Summary);
    }

    [Fact]
    public void DuplicateRecordId_IsDedupedWithinSameLog()
    {
        // Windows can re-deliver the same event across watcher reconnects or during
        // the historical-to-live handoff. Dedup by (LogName, RecordId) prevents
        // double-counting the ErrorSource count and inflating the recent-events list.
        _monitor.InjectLiveEventForTest(_source, MakeEvent("evt"), recordId: 12345L);
        _monitor.InjectLiveEventForTest(_source, MakeEvent("evt-dup"), recordId: 12345L);

        Assert.Single(_fired);

        var source = _monitor.GetErrorSources().First(s => s.Name == _source.Name);
        Assert.Equal(1, source.Count);
    }

    [Fact]
    public void SameRecordId_DifferentLogs_BothCounted()
    {
        // RecordId is per-channel. Two different logs may coincidentally use the
        // same numeric RecordId — dedup must be scoped by (LogName, RecordId).
        var other = EventLogMonitor.WatchedSources.First(s => s.LogName != _source.LogName);

        _monitor.InjectLiveEventForTest(_source, MakeEvent("same-id-on-log-A"), recordId: 99L);
        _monitor.InjectLiveEventForTest(other,   MakeEvent("same-id-on-log-B"), recordId: 99L);

        Assert.Equal(2, _fired.Count);
    }

    // -------------------------------------------------------------------------

    private MonitoredEvent MakeEvent(string summary) =>
        new(DateTime.UtcNow, _source.Name, _source.Category, 17, _source.DefaultSeverity, summary);
}
