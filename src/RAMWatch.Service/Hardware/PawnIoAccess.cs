using System.Runtime.InteropServices;

namespace RAMWatch.Service.Hardware;

/// <summary>
/// PawnIO driver backend. Communicates via IOCTL to the PawnIO signed kernel driver.
/// PawnIO handles MSR, PCI config space, MMIO, and I/O port access.
///
/// Phase 2: PCI config space reads for UMC registers.
/// The driver is installed system-wide — no user-supplied DLL path (B5 fix).
/// Service detects availability by attempting to open the device handle.
///
/// Reference: https://github.com/namazso/PawnIO
/// </summary>
public sealed class PawnIoAccess : IHardwareAccess
{
    private bool _available;
    private string _status = "Not initialized";

    public bool IsAvailable => _available;
    public string StatusDescription => _status;

    /// <summary>
    /// Attempt to initialize PawnIO. Call once at service startup.
    /// If PawnIO is not installed, sets IsAvailable = false gracefully.
    /// </summary>
    public void Initialize()
    {
        try
        {
            // PawnIO exposes a device at \\.\PawnIO
            // Try to open it to check if the driver is loaded
            if (!TryOpenPawnIoDevice())
            {
                _available = false;
                _status = "PawnIO driver not installed. Install PawnIO to enable timing reads.";
                return;
            }

            _available = true;
            _status = "PawnIO driver loaded";
        }
        catch (Exception ex)
        {
            _available = false;
            _status = $"PawnIO initialization failed: {ex.Message}";
        }
    }

    public uint ReadPciConfigDword(uint bus, uint device, uint function, uint offset)
    {
        if (!_available)
            throw new InvalidOperationException("PawnIO driver not available");

        // PawnIO PCI config space read via IOCTL
        // The actual IOCTL implementation depends on the PawnIO userspace library.
        // For now, this is the interface contract. The IOCTL details will be filled
        // in when we integrate the actual PawnIO SDK/library.
        //
        // PCI config address: bus << 20 | device << 15 | function << 12 | offset
        uint pciAddress = (bus << 20) | (device << 15) | (function << 12) | (offset & 0xFFC);
        return ReadPciConfigImpl(pciAddress);
    }

    public ulong ReadMsr(uint index)
    {
        if (!_available)
            throw new InvalidOperationException("PawnIO driver not available");

        return ReadMsrImpl(index);
    }

    public void WritePciConfigDword(uint bus, uint device, uint function, uint offset, uint value)
    {
        if (!_available)
            throw new InvalidOperationException("PawnIO driver not available");

        uint pciAddress = (bus << 20) | (device << 15) | (function << 12) | (offset & 0xFFC);
        WritePciConfigImpl(pciAddress, value);
    }

    public void Dispose()
    {
        // Close device handle if open
    }

    // ── Private implementation stubs ─────────────────────────
    // These will be replaced with actual PawnIO IOCTL calls when
    // the PawnIO userspace library or direct IOCTL wiring is added.

    private static bool TryOpenPawnIoDevice()
    {
        try
        {
            // Check if the PawnIO device exists
            // CreateFile on \\.\PawnIO — if it succeeds, driver is loaded
            using var handle = File.Open(@"\\.\PawnIO", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static uint ReadPciConfigImpl(uint address)
    {
        // TODO: Implement via PawnIO IOCTL
        // DeviceIoControl(hDevice, IOCTL_PAWNIO_READ_PCI_CONFIG, &address, 4, &result, 4, ...)
        throw new NotImplementedException("PawnIO PCI config read not yet implemented");
    }

    private static ulong ReadMsrImpl(uint index)
    {
        // TODO: Implement via PawnIO IOCTL
        throw new NotImplementedException("PawnIO MSR read not yet implemented");
    }

    private static void WritePciConfigImpl(uint address, uint value)
    {
        // TODO: Implement via PawnIO IOCTL
        throw new NotImplementedException("PawnIO PCI config write not yet implemented");
    }
}
