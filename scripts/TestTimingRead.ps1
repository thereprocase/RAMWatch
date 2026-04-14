#Requires -RunAsAdministrator
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
$project = "F:\Claude\projects\RAMwatch\src\RAMWatch.Service"

Write-Host "=== RAMWatch Timing Read Test ===" -ForegroundColor Cyan
Write-Host "Reading DRAM timings via PawnIO on this 5800X3D...`n"

# Temporarily replace Program.cs with a test harness, run it, then restore
$programCs = Join-Path $project "Program.cs"
$backup = Get-Content $programCs -Raw

$testCode = @'
using RAMWatch.Service.Hardware;
using RAMWatch.Service.Hardware.PawnIo;

Console.WriteLine("=== RAMWatch Timing Read Test ===\n");
Console.WriteLine($"PawnIOLib.dll: {(PawnIoDriver.IsInstalled ? "YES" : "NO")}");

using var reader = new HardwareReader();
Console.WriteLine($"Driver: {reader.DriverName} — {reader.DriverDescription}");
Console.WriteLine($"CPU: {reader.CpuFamily}");
Console.WriteLine($"Available: {reader.IsAvailable}\n");

if (!reader.IsAvailable) { Console.WriteLine("NOT AVAILABLE. Run as admin."); return; }

var s = reader.ReadTimings("test");
if (s is null) { Console.WriteLine("ReadTimings returned null."); return; }

Console.WriteLine($"Primaries: CL={s.CL} RCDRD={s.RCDRD} RCDWR={s.RCDWR} RP={s.RP} RAS={s.RAS} RC={s.RC}");
Console.WriteLine($"CWL={s.CWL}  GDM={(s.GDM ? "On" : "Off")}  Cmd={(s.Cmd2T ? "2T" : "1T")}");
Console.WriteLine($"tRFC: {s.RFC}/{s.RFC2}/{s.RFC4}");
Console.WriteLine($"Secondaries: RRDS={s.RRDS} RRDL={s.RRDL} FAW={s.FAW} WTRS={s.WTRS} WTRL={s.WTRL} WR={s.WR}");
Console.WriteLine($"SCL: RDRD={s.RDRDSCL} WRWR={s.WRWRSCL}");
Console.WriteLine($"RDWR={s.RDWR} WRRD={s.WRRD}");
Console.WriteLine($"Misc: REFI={s.REFI} CKE={s.CKE} STAG={s.STAG} MOD={s.MOD} MRD={s.MRD}");
Console.WriteLine($"PHY: A={s.PHYRDL_A} B={s.PHYRDL_B}");
Console.WriteLine($"Clock: MCLK~{s.MemClockMhz}MHz");
'@

try {
    Set-Content $programCs -Value $testCode
    & $dotnet run --project $project 2>&1
} finally {
    Set-Content $programCs -Value $backup
    Write-Host "`nProgram.cs restored." -ForegroundColor Green
}
