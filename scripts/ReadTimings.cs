// Quick console test — reads actual DRAM timings via PawnIO.
// Run from admin PowerShell:
//   & "$HOME\.dotnet\dotnet.exe" script F:\Claude\projects\RAMwatch\scripts\ReadTimings.cs

using RAMWatch.Service.Hardware;
using RAMWatch.Service.Hardware.PawnIo;

Console.WriteLine("=== RAMWatch Timing Read Test ===\n");

// Check PawnIO
Console.Write("PawnIOLib.dll installed: ");
Console.WriteLine(PawnIoDriver.IsInstalled ? "YES" : "NO");
if (!PawnIoDriver.IsInstalled)
{
    Console.WriteLine("Cannot proceed without PawnIO. Exiting.");
    return;
}

// Try to open and read
using var reader = new HardwareReader();
Console.WriteLine($"Driver: {reader.DriverName}");
Console.WriteLine($"Status: {reader.DriverDescription}");
Console.WriteLine($"CPU: {reader.CpuFamily}");
Console.WriteLine($"Available: {reader.IsAvailable}\n");

if (!reader.IsAvailable)
{
    Console.WriteLine("Hardware reads unavailable. Check that this runs as admin.");
    return;
}

var snapshot = reader.ReadTimings("test_read");
if (snapshot is null)
{
    Console.WriteLine("ReadTimings returned null.");
    return;
}

Console.WriteLine($"Primaries: CL={snapshot.CL} RCDRD={snapshot.RCDRD} RCDWR={snapshot.RCDWR} RP={snapshot.RP} RAS={snapshot.RAS} RC={snapshot.RC}");
Console.WriteLine($"CWL={snapshot.CWL}  GDM={snapshot.GDM}  Cmd2T={snapshot.Cmd2T}");
Console.WriteLine($"tRFC: {snapshot.RFC}/{snapshot.RFC2}/{snapshot.RFC4}");
Console.WriteLine($"Secondaries: RRDS={snapshot.RRDS} RRDL={snapshot.RRDL} FAW={snapshot.FAW} WTRS={snapshot.WTRS} WTRL={snapshot.WTRL} WR={snapshot.WR}");
Console.WriteLine($"SCL: RDRD={snapshot.RDRDSCL} WRWR={snapshot.WRWRSCL}");
Console.WriteLine($"Turn-around: RDRDSC={snapshot.RDRDSC} RDRDSD={snapshot.RDRDSD} RDRDDD={snapshot.RDRDDD}");
Console.WriteLine($"             WRWRSC={snapshot.WRWRSC} WRWRSD={snapshot.WRWRSD} WRWRDD={snapshot.WRWRDD}");
Console.WriteLine($"             RDWR={snapshot.RDWR} WRRD={snapshot.WRRD}");
Console.WriteLine($"Misc: REFI={snapshot.REFI} CKE={snapshot.CKE} STAG={snapshot.STAG} MOD={snapshot.MOD} MRD={snapshot.MRD}");
Console.WriteLine($"PHY: PHYRDL_A={snapshot.PHYRDL_A} PHYRDL_B={snapshot.PHYRDL_B}");
Console.WriteLine($"Controller: PowerDown={snapshot.PowerDown}");
Console.WriteLine($"Clock: MemClockMhz={snapshot.MemClockMhz}");
