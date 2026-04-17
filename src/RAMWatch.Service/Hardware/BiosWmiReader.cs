using System.Diagnostics;
using System.Globalization;

namespace RAMWatch.Service.Hardware;

/// <summary>
/// Reads DRAM voltages, termination resistance, and drive strength parameters
/// from the AMD BIOS ACPI WMI interface. Returns a structured result with all
/// available values.
///
/// The APCB (AMD Platform Configuration Block) buffer contains:
///   Offset 27: MemVddio (ushort LE, millivolts) — DRAM voltage
///   Offset 29: MemVtt   (ushort LE, millivolts) — termination voltage
///   Offset 31: MemVpp   (ushort LE, millivolts) — pump charge voltage
///   Offset 33: ProcODT  (byte, lookup index)
///   Offset 65: RttNom   (byte, lookup index)
///   Offset 66: RttWr    (byte, lookup index)
///   Offset 67: RttPark  (byte, lookup index)
///   Offset 86: AddrCmdSetup (byte, raw)
///   Offset 87: CsOdtSetup   (byte, raw)
///   Offset 88: CkeSetup     (byte, raw)
///   Offset 89: ClkDrvStren       (byte, lookup index)
///   Offset 90: AddrCmdDrvStren   (byte, lookup index)
///   Offset 91: CsOdtCmdDrvStren  (byte, lookup index)
///   Offset 92: CkeDrvStren       (byte, lookup index)
///
/// WMI uses COM reflection, incompatible with Native AOT. All queries run via
/// a PowerShell subprocess so the service binary stays AOT-clean.
/// </summary>
public static class BiosWmiReader
{
    /// <summary>
    /// Result of a BIOS WMI read. All voltages in volts, resistances in ohms.
    /// Zero means unavailable.
    /// </summary>
    public readonly record struct BiosConfig(
        double VDimm,
        double Vtt,
        double Vpp,
        double ProcODT,
        string RttNom,
        string RttWr,
        string RttPark,
        double ClkDrvStren,
        double AddrCmdDrvStren,
        double CsOdtCmdDrvStren,
        double CkeDrvStren,
        string AddrCmdSetup,
        string CsOdtSetup,
        string CkeSetup);

    private const double VoltageMin = 0.3;
    private const double VoltageMax = 2.5;
    private const double MillivoltThreshold = 100.0;

    /// <summary>
    /// Read all available BIOS config values. Falls back to ASUS WMI for
    /// VDimm-only when AMD_ACPI is absent. Returns a zero-valued struct on failure.
    /// </summary>
    public static BiosConfig ReadAll()
    {
        var result = TryAmdAcpiAll();
        if (result.VDimm > 0) return result;

        // ASUS fallback: only provides VDimm
        double vdimm = TryAsusWmi();
        if (vdimm > 0)
            return new BiosConfig(VDimm: vdimm, Vtt: 0, Vpp: 0, ProcODT: 0,
                RttNom: "", RttWr: "", RttPark: "",
                ClkDrvStren: 0, AddrCmdDrvStren: 0, CsOdtCmdDrvStren: 0, CkeDrvStren: 0,
                AddrCmdSetup: "", CsOdtSetup: "", CkeSetup: "");

        return default;
    }

    // ── AMD_ACPI path ──────────────────────────────────────────────────────

    internal static BiosConfig TryAmdAcpiAll()
    {
        // Script outputs a single CSV line with 14 raw integer values:
        // vddio,vtt,vpp,procodt,rttnom,rttwr,rttpark,acsetup,cssetup,ckesetup,clkdrv,acdrv,csdrv,ckedrv
        const string script = """
            try {
                $searcher = New-Object System.Management.ManagementObjectSearcher("root\WMI", "SELECT * FROM AMD_ACPI")
                $results  = $searcher.Get()
                $enum     = $results.GetEnumerator()
                if (-not $enum.MoveNext()) { Write-Output "0,0,0,0,0,0,0,0,0,0,0,0,0,0"; exit }
                $obj = $enum.Current
                $instanceName = $obj.GetPropertyValue("InstanceName") -replace "'","''"
                $classInst = [wmi]"root\WMI:AMD_ACPI.InstanceName='$instanceName'"

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

                if (-not $idDict.ContainsKey("Get APCB Config")) {
                    Write-Output "0,0,0,0,0,0,0,0,0,0,0,0,0,0"; exit
                }
                $apcbCmdId = $idDict["Get APCB Config"]

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

                if ($apcb -eq $null -or $apcb.Length -lt 33) {
                    Write-Output "0,0,0,0,0,0,0,0,0,0,0,0,0,0"; exit
                }

                # Optional overlay from "Get memory voltages" (bytes 27-32)
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
                        if ($voltages -ne $null -and $voltages.Length -ge 33) {
                            for ($i = 27; $i -le 32; $i++) {
                                if ($voltages[$i] -gt 0) { $apcb[$i] = $voltages[$i] }
                            }
                        }
                    } catch { }
                }

                # Extract raw values — voltages as LE ushort, rest as byte
                $vddio = [int]$apcb[27] + ([int]$apcb[28] -shl 8)
                $vtt   = [int]$apcb[29] + ([int]$apcb[30] -shl 8)
                $vpp   = [int]$apcb[31] + ([int]$apcb[32] -shl 8)
                $procodt = [int]$apcb[33]

                # Resistance and setup fields may not be present in short buffers
                $rttnom = 0; $rttwr = 0; $rttpark = 0
                $acsetup = 0; $cssetup = 0; $ckesetup = 0
                $clkdrv = 0; $acdrv = 0; $csdrv = 0; $ckedrv = 0

                if ($apcb.Length -ge 68) {
                    $rttnom  = [int]$apcb[65]
                    $rttwr   = [int]$apcb[66]
                    $rttpark = [int]$apcb[67]
                }
                if ($apcb.Length -ge 93) {
                    $acsetup = [int]$apcb[86]
                    $cssetup = [int]$apcb[87]
                    $ckesetup = [int]$apcb[88]
                    $clkdrv  = [int]$apcb[89]
                    $acdrv   = [int]$apcb[90]
                    $csdrv   = [int]$apcb[91]
                    $ckedrv  = [int]$apcb[92]
                }

                Write-Output "$vddio,$vtt,$vpp,$procodt,$rttnom,$rttwr,$rttpark,$acsetup,$cssetup,$ckesetup,$clkdrv,$acdrv,$csdrv,$ckedrv"
            } catch {
                Write-Output "0,0,0,0,0,0,0,0,0,0,0,0,0,0"
            }
            """;

        string raw = RunPowerShellScript(script);
        return ParseApcbCsv(raw);
    }

    // ── ASUS WMI fallback (VDimm only) ─────────────────────────────────────

    internal static double TryAsusWmi()
    {
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
        if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return ApplyVoltagePlausibility(v);
        return 0.0;
    }

    // ── CSV parsing and decode ──────────────────────────────────────────────

    /// <summary>
    /// Parse the 14-field CSV line from the AMD_ACPI script.
    /// All fields are raw integers; decode happens here for testability.
    /// </summary>
    internal static BiosConfig ParseApcbCsv(string csv)
    {
        var parts = csv.Trim().Split(',');
        if (parts.Length < 14) return default;

        if (!TryParseInts(parts, out int[] vals)) return default;

        return new BiosConfig(
            VDimm:             ApplyVoltagePlausibility(vals[0]),
            Vtt:               ApplyVoltagePlausibility(vals[1]),
            Vpp:               ApplyVoltagePlausibility(vals[2]),
            ProcODT:           DecodeProcODT(vals[3]),
            RttNom:            DecodeRttNom(vals[4]),
            RttWr:             DecodeRttWr(vals[5]),
            RttPark:           DecodeRttNom(vals[6]),  // RttPark uses same table as RttNom
            ClkDrvStren:       DecodeDriveStrength(vals[10]),
            AddrCmdDrvStren:   DecodeDriveStrength(vals[11]),
            CsOdtCmdDrvStren:  DecodeDriveStrength(vals[12]),
            CkeDrvStren:       DecodeDriveStrength(vals[13]),
            AddrCmdSetup:      DecodeSetup(vals[7]),
            CsOdtSetup:        DecodeSetup(vals[8]),
            CkeSetup:          DecodeSetup(vals[9]));
    }

    private static bool TryParseInts(string[] parts, out int[] vals)
    {
        vals = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i].Trim(), out vals[i]))
                return false;
        }
        return true;
    }

    // ── Voltage plausibility ────────────────────────────────────────────────

    /// <summary>
    /// Millivolt detection + range guard. Values above 100 are assumed mV.
    /// Returns 0.0 on out-of-range or zero input.
    /// </summary>
    internal static double ApplyVoltagePlausibility(double raw)
    {
        if (raw <= 0) return 0.0;
        double v = raw > MillivoltThreshold ? raw / 1000.0 : raw;
        if (v < VoltageMin || v > VoltageMax) return 0.0;
        return Math.Round(v, 4);
    }

    // ── Decode tables (JEDEC DDR4 spec / AMD PPR — hardware facts) ──────

    /// <summary>
    /// ProcODT byte → ohms. Returns 0 for unknown values.
    /// </summary>
    internal static double DecodeProcODT(int raw) => raw switch
    {
        1  => 480.0,
        2  => 240.0,
        3  => 160.0,
        8  => 120.0,
        9  => 96.0,
        10 => 80.0,
        11 => 68.6,
        24 => 60.0,
        25 => 53.3,
        26 => 48.0,
        27 => 43.6,
        56 => 40.0,
        57 => 36.9,
        58 => 34.3,
        59 => 32.0,
        62 => 30.0,
        63 => 28.2,
        _  => 0.0
    };

    /// <summary>
    /// Drive strength byte → ohms. Same table for Clk, AddrCmd, CsOdt, Cke.
    /// Returns 0 for unknown values.
    /// </summary>
    internal static double DecodeDriveStrength(int raw) => raw switch
    {
        0  => 120.0,
        1  => 60.0,
        3  => 40.0,
        7  => 30.0,
        15 => 24.0,
        31 => 20.0,
        _  => 0.0
    };

    /// <summary>
    /// RttNom / RttPark byte → string. Same decode for both.
    /// </summary>
    internal static string DecodeRttNom(int raw) => raw switch
    {
        0 => "Disabled",
        1 => "RZQ/4",
        2 => "RZQ/2",
        3 => "RZQ/6",
        4 => "RZQ/1",
        5 => "RZQ/5",
        6 => "RZQ/3",
        7 => "RZQ/7",
        _ => ""
    };

    /// <summary>
    /// RttWr byte → string.
    /// </summary>
    internal static string DecodeRttWr(int raw) => raw switch
    {
        0 => "Off",
        1 => "RZQ/2",
        2 => "RZQ/1",
        3 => "Hi-Z",
        4 => "RZQ/3",
        _ => ""
    };

    /// <summary>
    /// Setup timing byte → "quotient/remainder" string.
    /// </summary>
    internal static string DecodeSetup(int raw) =>
        raw == 0 ? "" : $"{raw / 32}/{raw % 32}";

    // ── Subprocess helper ────────────────────────────────────────────────────

    /// <summary>
    /// Run a PowerShell script and return ALL stdout (multi-line).
    /// Used by DimmReader for per-DIMM output.
    /// </summary>
    internal static string RunPowerShellScriptAll(string script)
        => RunPowerShellCore(script, firstLineOnly: false);

    internal static string RunPowerShellScript(string script)
        => RunPowerShellCore(script, firstLineOnly: true);

    /// <summary>
    /// Maximum wall-clock time allowed for a WMI PowerShell invocation.
    /// WMI occasionally hangs indefinitely on AMD_ACPI queries (previously seen on
    /// this codebase — commit 9d547f2 addressed one path). If the child doesn't
    /// produce output within this window, kill it and return the "0" sentinel.
    /// </summary>
    private static readonly TimeSpan PowerShellTimeout = TimeSpan.FromSeconds(10);

    private static string RunPowerShellCore(string script, bool firstLineOnly)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            process = Process.Start(psi);
            if (process is null) return "0";

            // Read stdout on a worker so the main thread can enforce a
            // wall-clock timeout. Plain ReadToEnd() blocks until the child
            // closes stdout, which never happens if WMI hangs inside the
            // PowerShell invocation — that would deadlock the caller under
            // the HardwareReader driver lock.
            var readTask = Task.Run(() => process.StandardOutput.ReadToEnd());

            if (!readTask.Wait(PowerShellTimeout))
            {
                // Child is hung. Kill the process tree; the Kill closes stdout
                // which lets readTask complete naturally, so it won't leak.
                try { process.Kill(entireProcessTree: true); } catch { }
                return "0";
            }

            string output = readTask.Result;
            process.WaitForExit(2000);

            if (!firstLineOnly) return output.Trim();
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                         .FirstOrDefault()?.Trim() ?? "0";
        }
        catch
        {
            // On exception, ensure the child doesn't outlive this method.
            try { process?.Kill(entireProcessTree: true); } catch { }
            return "0";
        }
        finally
        {
            process?.Dispose();
        }
    }
}
