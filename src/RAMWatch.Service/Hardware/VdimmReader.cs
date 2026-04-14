using System.Diagnostics;

namespace RAMWatch.Service.Hardware;

/// <summary>
/// Attempts to read DRAM voltage from vendor-specific WMI. Returns 0.0 on any failure.
///
/// Desktop AMD boards do not expose VDIMM through CPU registers. Most boards
/// (MSI, some ASUS/Gigabyte) route it through AMD_ACPI in root\WMI — the same
/// interface ZenTimings uses via BiosMemController. ASRock generally does not
/// expose it at all.
///
/// WMI uses COM reflection and is not compatible with Native AOT. Queries run
/// via a PowerShell subprocess so the service binary stays AOT-clean.
/// The subprocess is slow (~200–500 ms) and is therefore cached: one read on
/// startup, one per explicit timing read cycle, never on a tight poll.
///
/// The AMD_ACPI path: invoke GetObjectID/GetObjectID2 to discover function IDs
/// at runtime, then call RunCommand("Get APCB Config") and read MemVddio (ushort,
/// offset 27, units of millivolts) out of the returned buffer.
///
/// Plausibility bounds: DDR4 VDIMM is in [0.8, 2.0] V. Values outside that
/// range are discarded. Values above 100.0 are assumed to be millivolts and
/// are divided by 1000 before the plausibility check.
/// </summary>
public static class VdimmReader
{
    // DDR4 VDIMM plausibility bounds.
    private const double VdimmMin = 0.8;
    private const double VdimmMax = 2.0;

    // Millivolt threshold: raw values above this are assumed to be in mV.
    private const double MillivoltThreshold = 100.0;

    /// <summary>
    /// Attempt to read DRAM voltage from WMI.
    /// Tries AMD_ACPI (covers most MSI boards and many others), then falls back
    /// to the ASUS WMI sensor path. Returns 0.0 if all paths fail.
    /// </summary>
    public static double ReadVdimm()
    {
        double v;

        v = TryAmdAcpi();
        if (v > 0) return v;

        v = TryAsusWmi();
        if (v > 0) return v;

        return 0.0;
    }

    // ── Source implementations ──────────────────────────────────────────────

    /// <summary>
    /// Read VDIMM via AMD_ACPI in root\WMI.
    ///
    /// The APCB config table returned by RunCommand contains MemVddio as a
    /// little-endian ushort at byte offset 27 (millivolts). Some boards also
    /// expose a "Get memory voltages" function whose buffer at offset 27
    /// overrides the base APCB value with a more current reading.
    ///
    /// The function IDs are not fixed — they are discovered at runtime by
    /// calling GetObjectID (and GetObjectID2 as a fallback) on the class
    /// instance. This dynamic lookup requires reflection, so the whole path
    /// runs in a PowerShell subprocess.
    ///
    /// Returns 0.0 if AMD_ACPI is absent, the function list is empty, the
    /// command returns an empty buffer, or the PowerShell call fails.
    /// </summary>
    internal static double TryAmdAcpi()
    {
        // The PowerShell script does the following:
        // 1. Query root\WMI for AMD_ACPI — bail if not found.
        // 2. Open a ManagementObject on the first instance.
        // 3. Invoke GetObjectID (and GetObjectID2 on failure) to get a list of
        //    (IDString, ID) pairs describing available BIOS functions.
        // 4. Find the function ID for "Get APCB Config".
        // 5. Build an 8-byte RunCommand input buffer (cmd=ID, arg=0).
        // 6. Invoke RunCommand and extract Outbuf.Result.
        // 7. Check for a "Get memory voltages" function and, if present, overlay
        //    bytes 27–30 of the APCB buffer with the more current voltage values.
        // 8. Read MemVddio as a LE ushort at offset 27. Print as decimal.
        // 9. Print "0" on any error.
        const string script = """
            try {
                $searcher = New-Object System.Management.ManagementObjectSearcher("root\WMI", "SELECT * FROM AMD_ACPI")
                $results  = $searcher.Get()
                $enum     = $results.GetEnumerator()
                if (-not $enum.MoveNext()) { Write-Output "0"; exit }
                $obj = $enum.Current
                $instanceName = $obj.GetPropertyValue("InstanceName")
                $classInst = [wmi]"root\WMI:AMD_ACPI.InstanceName='$instanceName'"

                # Discover BIOS function IDs from GetObjectID (or GetObjectID2 fallback)
                $idDict = @{}
                foreach ($methodName in @("GetObjectID", "GetObjectID2")) {
                    try {
                        $inP = $classInst.GetMethodParameters($methodName)
                        $outP = $classInst.InvokeMethod($methodName, $inP, $null)
                        $pack = $outP.Properties["pack"].Value
                        $ids     = $pack.GetPropertyValue("ID")
                        $strings = $pack.GetPropertyValue("IDString")
                        $length  = [int]$pack.GetPropertyValue("Length")
                        for ($i = 0; $i -lt $length; $i++) {
                            if ($strings[$i] -ne "") {
                                $idDict[$strings[$i]] = $ids[$i]
                            }
                        }
                        break
                    } catch { }
                }

                if (-not $idDict.ContainsKey("Get APCB Config")) { Write-Output "0"; exit }
                $apcbCmdId = $idDict["Get APCB Config"]

                # Build RunCommand input: [cmdId LE32][arg LE32]
                $inBuf = [byte[]]@(
                    [byte]($apcbCmdId -band 0xFF),
                    [byte](($apcbCmdId -shr 8) -band 0xFF),
                    [byte](($apcbCmdId -shr 16) -band 0xFF),
                    [byte](($apcbCmdId -shr 24) -band 0xFF),
                    0, 0, 0, 0
                )
                $inParams = $classInst.GetMethodParameters("RunCommand")
                $inParams["Inbuf"] = $inBuf
                $outParams = $classInst.InvokeMethod("RunCommand", $inParams, $null)
                $outbuf = $outParams.Properties["Outbuf"].Value
                $apcb   = $outbuf.GetPropertyValue("Result")

                if ($apcb -eq $null -or $apcb.Length -lt 31) { Write-Output "0"; exit }

                # Optional overlay from "Get memory voltages" (bytes 27-30)
                if ($idDict.ContainsKey("Get memory voltages")) {
                    try {
                        $vCmdId = $idDict["Get memory voltages"]
                        $vBuf = [byte[]]@(
                            [byte]($vCmdId -band 0xFF),
                            [byte](($vCmdId -shr 8) -band 0xFF),
                            [byte](($vCmdId -shr 16) -band 0xFF),
                            [byte](($vCmdId -shr 24) -band 0xFF),
                            0, 0, 0, 0
                        )
                        $vParams = $classInst.GetMethodParameters("RunCommand")
                        $vParams["Inbuf"] = $vBuf
                        $vOut = $classInst.InvokeMethod("RunCommand", $vParams, $null)
                        $vBufOut = $vOut.Properties["Outbuf"].Value
                        $voltages = $vBufOut.GetPropertyValue("Result")
                        if ($voltages -ne $null -and $voltages.Length -ge 31) {
                            for ($i = 27; $i -le 30; $i++) {
                                if ($voltages[$i] -gt 0) { $apcb[$i] = $voltages[$i] }
                            }
                        }
                    } catch { }
                }

                # MemVddio is LE ushort at offset 27 (millivolts)
                $mv = [int]$apcb[27] + ([int]$apcb[28] -shl 8)
                Write-Output $mv
            } catch {
                Write-Output "0"
            }
            """;

        string raw = RunPowerShellScript(script);
        return ParseRawMillivolts(raw);
    }

    /// <summary>
    /// Read VDIMM from ASUS WMI sensor named "DRAM Voltage".
    ///
    /// ASUS exposes hardware monitor sensors through ASUSHW or ASUSManagement in
    /// root\WMI. The sensor value is a floating-point string (e.g. "1.350").
    ///
    /// Returns 0.0 if the class or sensor is absent.
    /// </summary>
    internal static double TryAsusWmi()
    {
        // Query ASUS hardware monitor for a sensor named "DRAM Voltage".
        // The sensor Value property is already a voltage string (no /1000 needed).
        const string script = """
            try {
                $found = $false
                foreach ($cls in @("ASUSHW", "ASUSManagement")) {
                    try {
                        $searcher = New-Object System.Management.ManagementObjectSearcher("root\WMI", "SELECT * FROM $cls")
                        foreach ($obj in $searcher.Get()) {
                            $name = $obj.GetPropertyValue("SensorName")
                            if ($name -eq "DRAM Voltage") {
                                $val = $obj.GetPropertyValue("Value")
                                Write-Output $val
                                $found = $true
                                break
                            }
                        }
                        if ($found) { break }
                    } catch { }
                }
                if (-not $found) { Write-Output "0" }
            } catch {
                Write-Output "0"
            }
            """;

        string raw = RunPowerShellScript(script);

        // ASUS sensor value is already in volts (e.g. "1.3500")
        if (double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v))
        {
            return ApplyPlausibilityGuard(v);
        }
        return 0.0;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Parse a raw WMI output string representing a voltage in millivolts
    /// (e.g. "1350") or volts (e.g. "1.35"), apply the millivolt detection
    /// heuristic, and run the plausibility guard.
    ///
    /// Returns 0.0 if parsing fails or the value is out of the plausible range.
    /// </summary>
    internal static double ParseRawMillivolts(string raw)
    {
        if (!double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v))
            return 0.0;

        return ApplyPlausibilityGuard(v);
    }

    /// <summary>
    /// Apply millivolt detection and plausibility guard.
    ///
    /// Values above the millivolt threshold (100.0) are assumed to be in
    /// millivolts and divided by 1000. Values outside [VdimmMin, VdimmMax]
    /// are discarded and 0.0 is returned.
    /// </summary>
    internal static double ApplyPlausibilityGuard(double raw)
    {
        double v = raw;

        // Millivolt detection: typical millivolt readings are ~1350 for DDR4.
        // Anything above 100 in a "voltage" field is certainly not volts.
        if (v > MillivoltThreshold)
            v /= 1000.0;

        if (v < VdimmMin || v > VdimmMax)
            return 0.0;

        return Math.Round(v, 4);
    }

    /// <summary>
    /// Run a PowerShell script in a subprocess and return the first line of stdout.
    ///
    /// Uses ArgumentList (array form) per B5 guidance — never string-interpolates
    /// into a shell command. The script is passed as the -Command argument.
    ///
    /// Returns "0" on process launch failure, timeout, or empty output.
    /// Timeout is 5 seconds — WMI queries typically complete in under 500 ms.
    /// </summary>
    internal static string RunPowerShellScript(string script)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            using var process = Process.Start(psi);
            if (process is null) return "0";

            // 5 second hard ceiling — WMI should never take this long.
            // If the WMI service is hung, we return 0 rather than blocking.
            bool exited = process.WaitForExit(5000);
            if (!exited)
            {
                try { process.Kill(); } catch { }
                return "0";
            }

            string output = process.StandardOutput.ReadToEnd();
            string firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                     .FirstOrDefault()?.Trim() ?? "0";
            return firstLine;
        }
        catch
        {
            return "0";
        }
    }
}
