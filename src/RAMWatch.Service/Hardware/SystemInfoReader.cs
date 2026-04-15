using Microsoft.Win32;

namespace RAMWatch.Service.Hardware;

/// <summary>
/// Reads static system identification from the Windows registry.
/// Called once at service startup — these values don't change during a boot.
/// All registry reads, no WMI — Native AOT safe.
/// </summary>
public static class SystemInfoReader
{
    public sealed record SystemInfo(
        string CpuName,
        string BoardVendor,
        string BoardModel,
        string BiosVersion,
        string AgesaVersion);

    /// <summary>
    /// Read system identification from the registry. Returns a record with
    /// all fields populated (empty string when a field is not available).
    /// Never throws — returns empty strings on any failure.
    /// </summary>
    public static SystemInfo Read()
    {
        string cpuName = "";
        string boardVendor = "";
        string boardModel = "";
        string biosVersion = "";
        string agesaVersion = "";

        try
        {
            // CPU name — same key CpuDetect already reads
            using var cpuKey = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (cpuKey is not null)
            {
                cpuName = cpuKey.GetValue("ProcessorNameString") as string ?? "";
            }
        }
        catch { }

        try
        {
            // BIOS key — same key BiosLayouts.DetectVendor already opens
            using var biosKey = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\BIOS");
            if (biosKey is not null)
            {
                boardVendor = biosKey.GetValue("BaseBoardManufacturer") as string ?? "";
                boardModel = biosKey.GetValue("BaseBoardProduct") as string ?? "";

                // SystemBiosVersion is REG_MULTI_SZ on most boards.
                // One entry typically contains the AGESA version string.
                var biosValue = biosKey.GetValue("SystemBiosVersion");
                if (biosValue is string[] multiSz)
                {
                    biosVersion = ExtractBiosVersion(multiSz);
                    agesaVersion = ExtractAgesaVersion(multiSz);
                }
                else if (biosValue is string single)
                {
                    biosVersion = single.Trim();
                    if (single.Contains("AGESA", StringComparison.OrdinalIgnoreCase))
                        agesaVersion = ExtractAgesaFromString(single);
                }

                // Fallback: BIOSVersion (single string) if SystemBiosVersion was empty
                if (string.IsNullOrWhiteSpace(biosVersion))
                {
                    biosVersion = biosKey.GetValue("BIOSVersion") as string ?? "";
                }
            }
        }
        catch { }

        return new SystemInfo(
            cpuName.Trim(),
            boardVendor.Trim(),
            boardModel.Trim(),
            biosVersion.Trim(),
            agesaVersion.Trim());
    }

    /// <summary>
    /// From a REG_MULTI_SZ, find the most useful BIOS version string.
    /// Vendors put different things in here — AMI puts "ALASKA - 1072009",
    /// the actual version, and sometimes the AGESA string. Pick the one
    /// that looks like a real version number.
    /// </summary>
    internal static string ExtractBiosVersion(string[] entries)
    {
        // Prefer entries that look like version numbers (contain digits and dots/letters)
        // but aren't the AGESA string (handled separately).
        foreach (var entry in entries)
        {
            var trimmed = entry.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (trimmed.Contains("AGESA", StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.StartsWith("ALASKA", StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.StartsWith("American Megatrends", StringComparison.OrdinalIgnoreCase)) continue;

            // If it has a digit, it's likely a version string
            if (trimmed.Any(char.IsDigit))
                return trimmed;
        }

        // Fallback: first non-empty entry
        return entries.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e))?.Trim() ?? "";
    }

    /// <summary>
    /// From a REG_MULTI_SZ, find the AGESA version string.
    /// Typically looks like "AMD AGESA V2 PI 1.2.0.7" or "AGESA ComboAM4v2PI 1.2.0.7".
    /// </summary>
    internal static string ExtractAgesaVersion(string[] entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Contains("AGESA", StringComparison.OrdinalIgnoreCase))
                return ExtractAgesaFromString(entry);
        }
        return "";
    }

    /// <summary>
    /// Extract the AGESA version identifier from a string that contains "AGESA".
    /// Input examples:
    ///   "AMD AGESA V2 PI 1.2.0.7" → "V2 PI 1.2.0.7"
    ///   "AGESA ComboAM4v2PI 1.2.0.7" → "ComboAM4v2PI 1.2.0.7"
    ///   "AGESA CastlePeakPI-SP3r3 1.0.0.6" → "CastlePeakPI-SP3r3 1.0.0.6"
    /// Returns everything after "AGESA " (trimmed).
    /// </summary>
    internal static string ExtractAgesaFromString(string s)
    {
        int idx = s.IndexOf("AGESA", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";

        var after = s[(idx + 5)..].Trim();
        // Remove leading "AMD " if present
        if (after.StartsWith("AMD ", StringComparison.OrdinalIgnoreCase))
            after = after[4..].Trim();

        return after;
    }
}
