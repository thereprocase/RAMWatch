namespace RAMWatch.Service.Hardware;

/// <summary>
/// Swappable hardware access interface. The driver backend (PawnIO, InpOutx64,
/// or any future driver) implements this. The decode logic depends only on
/// this interface, never on a specific driver.
///
/// If the driver project loses its signing cert or Microsoft tightens HVCI rules,
/// we swap the implementation without rewriting the decode layer.
/// </summary>
public interface IHardwareAccess : IDisposable
{
    /// <summary>
    /// Whether the driver is loaded and ready for reads.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Human-readable driver status for the UI.
    /// </summary>
    string StatusDescription { get; }

    /// <summary>
    /// Read a 32-bit value from PCI configuration space.
    /// Used for UMC register reads (AMD memory controller).
    /// </summary>
    uint ReadPciConfigDword(uint bus, uint device, uint function, uint offset);

    /// <summary>
    /// Read a 64-bit Model Specific Register.
    /// Used for SVI2 voltage telemetry and SMU communication.
    /// </summary>
    ulong ReadMsr(uint index);

    /// <summary>
    /// Write a 32-bit value to PCI configuration space.
    /// Used for SMU mailbox communication (write command, read response).
    /// </summary>
    void WritePciConfigDword(uint bus, uint device, uint function, uint offset, uint value);
}

/// <summary>
/// Null driver that always reports unavailable. Used when no real driver
/// is installed — enables graceful degradation without null checks everywhere.
/// </summary>
public sealed class NullHardwareAccess : IHardwareAccess
{
    public bool IsAvailable => false;
    public string StatusDescription => "No hardware driver available. Install PawnIO to enable timing reads.";

    public uint ReadPciConfigDword(uint bus, uint device, uint function, uint offset)
        => throw new InvalidOperationException("Hardware driver not available");

    public ulong ReadMsr(uint index)
        => throw new InvalidOperationException("Hardware driver not available");

    public void WritePciConfigDword(uint bus, uint device, uint function, uint offset, uint value)
        => throw new InvalidOperationException("Hardware driver not available");

    public void Dispose() { }
}
