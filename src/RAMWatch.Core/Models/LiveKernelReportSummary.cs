namespace RAMWatch.Core.Models;

/// <summary>
/// Summary of Windows LiveKernelReports dump files found on the system.
/// The service runs as SYSTEM and can read C:\Windows\LiveKernelReports\.
/// These mini-dumps are generated for WHEA events without requiring a full BSOD.
/// </summary>
public sealed class LiveKernelReportSummary
{
    /// <summary>Total number of dump files found across all subdirectories.</summary>
    public required int TotalCount { get; init; }

    /// <summary>Number of dumps in the WHEA subdirectory specifically.</summary>
    public int WheaCount { get; init; }

    /// <summary>Timestamp of the most recent dump file, or null if none found.</summary>
    public DateTime? MostRecent { get; init; }

    /// <summary>Timestamp of the oldest dump file, or null if none found.</summary>
    public DateTime? Oldest { get; init; }

    /// <summary>
    /// Subdirectory names that contain dumps (e.g., "WHEA", "WATCHDOG", "BN2").
    /// Helps classify what kinds of kernel reports exist.
    /// </summary>
    public List<string>? Categories { get; init; }
}
