using System.Text.RegularExpressions;

namespace RAMWatch.Core.Models;

/// <summary>
/// Phase 1 settings subset. Additional fields added in later phases.
/// </summary>
public sealed class AppSettings
{
    // Validation for GitRemoteRepo — must be "owner/repo" format or empty.
    private static readonly Regex ValidRepoPattern =
        new(@"^[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+$", RegexOptions.Compiled);

    public static bool IsValidRemoteRepo(string? repo) =>
        string.IsNullOrEmpty(repo) || ValidRepoPattern.IsMatch(repo);

    /// <summary>
    /// Clamp every numeric field into a sane range. Applied after load and
    /// before save so a malformed settings.json (or an adversarial IPC
    /// update) can't drive retention to zero, log size to a terabyte, or
    /// toast cooldown to a negative value that skips the rate limiter.
    /// </summary>
    public void ClampNumerics()
    {
        RefreshIntervalSeconds = Math.Clamp(RefreshIntervalSeconds, 5, 3600);
        // Retention: min 1 day (avoid deleting today's log on next startup),
        // max ~10 years (arbitrary ceiling so AddDays math can't overflow).
        LogRetentionDays = Math.Clamp(LogRetentionDays, 1, 3650);
        // Log size: min 1 MB (avoid immediate eviction), max 10 GB.
        MaxLogSizeMb = Math.Clamp(MaxLogSizeMb, 1, 10_000);
        // Toast cooldown: 0 = no rate limit, cap at 1 day to keep the rate
        // limiter's time arithmetic in a normal range.
        NotifyCooldownSeconds = Math.Clamp(NotifyCooldownSeconds, 0, 86_400);
    }

    /// <summary>
    /// Validate a user-supplied data path before the LocalSystem service acts on it.
    /// Empty/null is accepted (caller uses the default path instead).
    /// Rejects UNC paths, paths that still contain ".." after full resolution,
    /// and paths rooted in system directories (Windows, Program Files).
    /// </summary>
    public static bool IsValidDataPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true; // empty = use default
        try
        {
            var full = Path.GetFullPath(path);
            // Resolve reparse points (symlinks, junctions) so a junction whose
            // target is a UNC share or a system directory can't slip past the
            // prefix checks below. Only applies if the path already exists;
            // non-existent paths fall through to the string-level checks.
            if (Directory.Exists(full))
            {
                try
                {
                    var info = new DirectoryInfo(full);
                    var final = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (final is not null) full = final.FullName;
                }
                catch
                {
                    // If we can't resolve the reparse target, refuse to act
                    // on it rather than trusting the unresolved path.
                    return false;
                }
            }
            // Reject UNC paths — the service should never write to a network share.
            if (full.StartsWith(@"\\")) return false;
            // GetFullPath resolves ".." but the result should never still contain it.
            if (full.Contains("..")) return false;
            // Reject system directories the service must never be directed to write into.
            var sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(sysRoot) &&
                full.StartsWith(sysRoot, StringComparison.OrdinalIgnoreCase)) return false;
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles) &&
                full.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase)) return false;
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(programFilesX86) &&
                full.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        catch { return false; }
    }

    public int SchemaVersion { get; set; } = 1;

    // General
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool LaunchAtLogon { get; set; }

    // Monitoring
    public int RefreshIntervalSeconds { get; set; } = 60;

    // Logging
    public bool EnableCsvLogging { get; set; } = true;
    public string LogDirectory { get; set; } = "";
    public int LogRetentionDays { get; set; } = 90;
    public int MaxLogSizeMb { get; set; } = 100;
    public string MirrorDirectory { get; set; } = "";

    // Notifications
    public bool EnableToastNotifications { get; set; } = true;
    public bool NotifyOnWhea { get; set; } = true;
    public bool NotifyOnBsod { get; set; } = true;
    public bool NotifyOnDrift { get; set; } = true;
    public bool NotifyOnCodeIntegrity { get; set; }
    public bool NotifyOnAppCrash { get; set; }
    public int NotifyCooldownSeconds { get; set; } = 300;

    // Display
    public string Theme { get; set; } = "dark";

    // Advanced
    public bool DebugLogging { get; set; }

    // BIOS layout — "Auto" detects from board vendor, or override to specific vendor
    public string BiosLayout { get; set; } = "Auto";

    // Git integration (Phase 4)
    public bool EnableGitIntegration { get; set; }
    public bool EnableGitPush { get; set; }
    public string GitRemoteRepo { get; set; } = "";
    public string GitUserDisplayName { get; set; } = "RAMWatch";
}
