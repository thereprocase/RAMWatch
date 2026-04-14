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
