using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.Service.Services;

namespace RAMWatch.Tests;

public class EventClassificationTests
{
    // ── Watched source configuration ─────────────────────────

    [Fact]
    public void WatchedSources_CoverAllCategories()
    {
        var categories = EventLogMonitor.WatchedSources
            .Select(s => s.Category)
            .Distinct()
            .ToList();

        Assert.Contains(EventCategory.Hardware, categories);
        Assert.Contains(EventCategory.Filesystem, categories);
        Assert.Contains(EventCategory.Integrity, categories);
        Assert.Contains(EventCategory.Application, categories);
    }

    [Fact]
    public void WatchedSources_WheaEventIds_AreCorrect()
    {
        var whea = EventLogMonitor.WatchedSources
            .First(s => s.Name == "WHEA Hardware Errors");

        Assert.Equal(EventCategory.Hardware, whea.Category);
        Assert.Contains(17, whea.EventIds);
        Assert.Contains(18, whea.EventIds);
        Assert.Contains(19, whea.EventIds);
        Assert.Contains(20, whea.EventIds);
        Assert.Contains(47, whea.EventIds);
        Assert.Equal(EventSeverity.Warning, whea.DefaultSeverity);
    }

    [Fact]
    public void WatchedSources_MceFires_AsCritical()
    {
        var mce = EventLogMonitor.WatchedSources
            .First(s => s.Name == "Machine Check Exception");

        Assert.Equal(EventSeverity.Critical, mce.DefaultSeverity);
        Assert.Contains(1, mce.EventIds);
    }

    [Fact]
    public void WatchedSources_BugcheckFires_AsCritical()
    {
        var bugcheck = EventLogMonitor.WatchedSources
            .First(s => s.Name == "Kernel Bugcheck");

        Assert.Equal(EventSeverity.Critical, bugcheck.DefaultSeverity);
        Assert.Contains(1001, bugcheck.EventIds);
    }

    [Fact]
    public void WatchedSources_UnexpectedShutdownFires_AsCritical()
    {
        var shutdown = EventLogMonitor.WatchedSources
            .First(s => s.Name == "Unexpected Shutdown");

        Assert.Equal(EventSeverity.Critical, shutdown.DefaultSeverity);
        Assert.Contains(41, shutdown.EventIds);
    }

    [Fact]
    public void WatchedSources_AppCrashFires_AsNotice()
    {
        var crash = EventLogMonitor.WatchedSources
            .First(s => s.Name == "Application Crash");

        Assert.Equal(EventSeverity.Notice, crash.DefaultSeverity);
        Assert.Contains(1000, crash.EventIds);
    }

    [Fact]
    public void WatchedSources_DiskErrors_AreFilesystemCategory()
    {
        var disk = EventLogMonitor.WatchedSources
            .First(s => s.Name == "Disk Error");

        Assert.Equal(EventCategory.Filesystem, disk.Category);
        Assert.Equal(5, disk.EventIds.Length);
    }

    [Fact]
    public void WatchedSources_CodeIntegrity_IsIntegrityCategory()
    {
        var ci = EventLogMonitor.WatchedSources
            .First(s => s.Name == "Code Integrity");

        Assert.Equal(EventCategory.Integrity, ci.Category);
        Assert.Equal(EventSeverity.Notice, ci.DefaultSeverity);
    }

    // ── Log name resolution ──────────────────────────────────

    [Fact]
    public void WatchedSource_ApplicationError_ResolvesToApplicationLog()
    {
        var source = EventLogMonitor.WatchedSources
            .First(s => s.Name == "Application Crash");

        Assert.Equal("Application", source.LogName);
        Assert.Equal("Application Error", source.ProviderName);
    }

    [Fact]
    public void WatchedSource_DiskError_ResolvesToSystemLog()
    {
        var source = EventLogMonitor.WatchedSources
            .First(s => s.Name == "Disk Error");

        Assert.Equal("System", source.LogName);
    }

    [Fact]
    public void WatchedSource_Whea_UsesOwnLogName()
    {
        var source = EventLogMonitor.WatchedSources
            .First(s => s.Name == "WHEA Hardware Errors");

        // WHEA Logger events live in the System log; ProviderName is the ETW provider
        Assert.Equal("System", source.LogName);
        Assert.Equal("Microsoft-Windows-WHEA-Logger", source.ProviderName);
    }

    // ── Source count ─────────────────────────────────────────

    [Fact]
    public void WatchedSources_HasExpectedCount()
    {
        // 14 watched sources: 12 original + Kernel-WHEA + Kernel-PCI
        Assert.Equal(14, EventLogMonitor.WatchedSources.Length);
    }

    [Fact]
    public void WatchedSources_NoDuplicateNames()
    {
        var names = EventLogMonitor.WatchedSources.Select(s => s.Name).ToList();
        Assert.Equal(names.Distinct().Count(), names.Count);
    }

    // ── Severity tiers match architecture ────────────────────

    [Fact]
    public void SeverityTiers_CriticalSources()
    {
        var criticals = EventLogMonitor.WatchedSources
            .Where(s => s.DefaultSeverity == EventSeverity.Critical)
            .Select(s => s.Name)
            .ToList();

        Assert.Contains("Machine Check Exception", criticals);
        Assert.Contains("Kernel Bugcheck", criticals);
        Assert.Contains("Unexpected Shutdown", criticals);
    }

    [Fact]
    public void SeverityTiers_WarningSources()
    {
        var warnings = EventLogMonitor.WatchedSources
            .Where(s => s.DefaultSeverity == EventSeverity.Warning)
            .Select(s => s.Name)
            .ToList();

        Assert.Contains("WHEA Hardware Errors", warnings);
        Assert.Contains("Disk Error", warnings);
        Assert.Contains("NTFS Error", warnings);
    }

    [Fact]
    public void SeverityTiers_NoticeSources()
    {
        var notices = EventLogMonitor.WatchedSources
            .Where(s => s.DefaultSeverity == EventSeverity.Notice)
            .Select(s => s.Name)
            .ToList();

        Assert.Contains("Code Integrity", notices);
        Assert.Contains("Application Crash", notices);
        Assert.Contains("Application Hang", notices);
    }
}
