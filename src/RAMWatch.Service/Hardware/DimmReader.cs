using System.Globalization;
using RAMWatch.Core.Models;

namespace RAMWatch.Service.Hardware;

/// <summary>
/// Reads installed DIMM information from Win32_PhysicalMemory WMI.
/// Called once at service startup. Returns null if the query fails.
///
/// Uses a PowerShell subprocess (same pattern as BiosWmiReader) because
/// WMI COM reflection is incompatible with Native AOT.
/// </summary>
public static class DimmReader
{
    /// <summary>
    /// Query Win32_PhysicalMemory and return a list of installed DIMMs.
    /// Returns null on failure. Empty list means no DIMMs found (unlikely).
    /// </summary>
    public static List<DimmInfo>? ReadDimms()
    {
        const string script = """
            try {
                $dimms = Get-CimInstance -ClassName Win32_PhysicalMemory
                foreach ($d in $dimms) {
                    $slot = ($d.BankLabel ?? $d.DeviceLocator ?? "").Trim()
                    $cap  = [long]$d.Capacity
                    $spd  = [int]$d.Speed
                    $mfr  = ($d.Manufacturer ?? "").Trim()
                    $pn   = ($d.PartNumber ?? "").Trim()
                    Write-Output "$slot|$cap|$spd|$mfr|$pn"
                }
                if (-not $dimms) { Write-Output "" }
            } catch {
                Write-Output "ERROR"
            }
            """;

        string raw = BiosWmiReader.RunPowerShellScriptAll(script);
        return ParseDimmOutput(raw);
    }

    /// <summary>
    /// Parse pipe-delimited DIMM output lines. Returns null on error.
    /// The RunPowerShellScript helper only returns the first line; for
    /// multi-DIMM systems we need all lines.
    /// </summary>
    internal static List<DimmInfo>? ParseDimmOutput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "ERROR")
            return null;

        var result = new List<DimmInfo>();
        foreach (string line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('|');
            if (parts.Length < 5) continue;

            long.TryParse(parts[1], out long cap);
            int.TryParse(parts[2], out int speed);

            result.Add(new DimmInfo
            {
                Slot = parts[0],
                CapacityBytes = cap,
                SpeedMTs = speed,
                Manufacturer = parts[3],
                PartNumber = parts[4],
            });
        }

        return result;
    }

}
