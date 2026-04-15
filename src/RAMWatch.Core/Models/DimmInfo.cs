namespace RAMWatch.Core.Models;

/// <summary>
/// Per-DIMM information from Win32_PhysicalMemory WMI.
/// Read once at service startup. Pushed to GUI via state message.
/// </summary>
public sealed class DimmInfo
{
    public string Slot { get; set; } = "";
    public long CapacityBytes { get; set; }
    public int SpeedMTs { get; set; }
    public string Manufacturer { get; set; } = "";
    public string PartNumber { get; set; } = "";
}
