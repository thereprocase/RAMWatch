namespace RAMWatch.Service.Hardware;

/// <summary>
/// Swappable hardware access interface. The driver backend (PawnIO or any
/// future driver) implements this. The decode logic depends only on this
/// interface, never on a specific driver.
///
/// Operations are SMN-level, not PCI-level. The SMN indirect access pattern
/// (write address to PCI 0x60, read data from PCI 0x64) is an implementation
/// detail hidden inside the driver backend. PawnIO does this atomically in
/// the kernel via ioctl_read_smn. A hypothetical InpOutx64 backend would do
/// it in userspace under a mutex.
///
/// Read-only contract: RAMWatch never modifies hardware configuration.
/// The SMN address-pointer write (to PCI 0x60) is part of the read protocol,
/// not a configuration change.
/// </summary>
public interface IHardwareAccess : IDisposable
{
    bool IsAvailable { get; }
    string StatusDescription { get; }
    string DriverName { get; }

    /// <summary>
    /// Read a 32-bit value from the AMD SMN (System Management Network) address space.
    /// Returns false if the driver is unavailable or the read fails.
    /// </summary>
    bool TryReadSmn(uint address, out uint value);

    /// <summary>
    /// Read a 64-bit Model Specific Register.
    /// Used for SVI2 voltage telemetry.
    /// </summary>
    bool TryReadMsr(uint index, out ulong value);
}

/// <summary>
/// Null driver — always unavailable. Used when no real driver is installed.
/// Enables graceful degradation without null checks everywhere.
/// </summary>
public sealed class NullHardwareAccess : IHardwareAccess
{
    public bool IsAvailable => false;
    public string StatusDescription => "No hardware driver available. Install PawnIO to enable timing reads.";
    public string DriverName => "None";

    public bool TryReadSmn(uint address, out uint value) { value = 0; return false; }
    public bool TryReadMsr(uint index, out ulong value) { value = 0; return false; }
    public void Dispose() { }
}
