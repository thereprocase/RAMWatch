using RAMWatch.Core.Models;

namespace RAMWatch.Service.Hardware;

/// <summary>
/// Orchestrates hardware detection and timing reads.
/// Manages the driver lifecycle with graceful degradation:
/// PawnIO available → full timing reads.
/// PawnIO missing → NullHardwareAccess, all reads return null.
///
/// The driver backend is a swappable IHardwareAccess implementation.
/// </summary>
public sealed class HardwareReader : IDisposable
{
    private readonly IHardwareAccess _driver;
    private readonly UmcDecode? _umcDecode;
    private readonly CpuDetect.CpuFamily _cpuFamily;

    public bool IsAvailable => _driver.IsAvailable;
    public string DriverStatus => _driver.IsAvailable ? "loaded" : "not_found";
    public string DriverDescription => _driver.StatusDescription;
    public CpuDetect.CpuFamily CpuFamily => _cpuFamily;

    public HardwareReader()
    {
        _driver = DetectDriver();
        _cpuFamily = CpuDetect.Detect(_driver);

        if (_driver.IsAvailable && _cpuFamily != CpuDetect.CpuFamily.Unknown)
        {
            _umcDecode = new UmcDecode(_driver);
        }
    }

    /// <summary>
    /// Read current DRAM timings. Returns null if driver or CPU is unsupported.
    /// Safe to call on any system — never throws.
    /// </summary>
    public TimingSnapshot? ReadTimings(string bootId)
    {
        if (_umcDecode is null) return null;

        try
        {
            return _umcDecode.ReadTimings(bootId);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _driver.Dispose();
    }

    /// <summary>
    /// Try to initialize PawnIO. Fall back to NullHardwareAccess if unavailable.
    /// </summary>
    private static IHardwareAccess DetectDriver()
    {
        try
        {
            var pawnIo = new PawnIoAccess();
            pawnIo.Initialize();
            if (pawnIo.IsAvailable)
                return pawnIo;

            pawnIo.Dispose();
        }
        catch
        {
            // PawnIO initialization failed entirely
        }

        return new NullHardwareAccess();
    }
}
