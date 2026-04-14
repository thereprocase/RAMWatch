using Microsoft.Win32;

namespace RAMWatch.Core.Models;

/// <summary>
/// Which motherboard vendor's BIOS OC menu ordering to use when displaying timings.
/// "Auto" means detect from the registry at service startup.
/// </summary>
public enum BoardVendor
{
    Auto,
    MSI,
    ASUS,
    Gigabyte,
    ASRock,
    Default
}

/// <summary>
/// An ordered group of timing fields as they appear in a vendor's BIOS OC menu.
/// </summary>
public sealed class TimingGroup
{
    public required string Name { get; init; }
    public required string[] Fields { get; init; }
}

/// <summary>
/// Vendor-specific BIOS timing display layouts.
/// Each layout is an ordered list of named groups matching the vendor's OC menu.
/// Registry detection mirrors the approach used by CpuDetect for CPU identification.
/// </summary>
public static class BiosLayouts
{
    // ── Layout definitions ────────────────────────────────────────────────────

    private static readonly IReadOnlyList<TimingGroup> _msiLayout =
    [
        new TimingGroup { Name = "Primary",      Fields = ["CL", "RCDRD", "RCDWR", "RP", "RAS", "RC", "CWL"] },
        new TimingGroup { Name = "GDM/Cmd",      Fields = ["GDM", "Cmd2T"] },
        new TimingGroup { Name = "tRFC",         Fields = ["RFC", "RFC2", "RFC4"] },
        new TimingGroup { Name = "Secondary",    Fields = ["RRDS", "RRDL", "FAW", "WTRS", "WTRL", "WR", "RTP"] },
        new TimingGroup { Name = "Turn-around",  Fields = ["RDRDSCL", "WRWRSCL", "RDRDSC", "RDRDSD", "RDRDDD", "WRWRSC", "WRWRSD", "WRWRDD"] },
        new TimingGroup { Name = "Read/Write",   Fields = ["RDWR", "WRRD"] },
        new TimingGroup { Name = "Misc",         Fields = ["CKE", "STAG", "MOD", "MRD", "REFI"] },
        new TimingGroup { Name = "PHY",          Fields = ["PHYRDL_A", "PHYRDL_B"] },
    ];

    private static readonly IReadOnlyList<TimingGroup> _asusLayout =
    [
        new TimingGroup { Name = "Primary",      Fields = ["CL", "RCDRD", "RCDWR", "RP", "RAS", "RC"] },
        new TimingGroup { Name = "CWL/GDM",      Fields = ["CWL", "GDM", "Cmd2T"] },
        new TimingGroup { Name = "tRFC",         Fields = ["RFC", "RFC2", "RFC4"] },
        new TimingGroup { Name = "Secondary",    Fields = ["RRDS", "RRDL", "FAW", "WTRS", "WTRL", "WR", "RTP", "CKE", "REFI"] },
        new TimingGroup { Name = "Training",     Fields = ["RDRDSCL", "WRWRSCL"] },
        new TimingGroup { Name = "Turn-around",  Fields = ["RDRDSC", "RDRDSD", "RDRDDD", "WRWRSC", "WRWRSD", "WRWRDD", "RDWR", "WRRD"] },
        new TimingGroup { Name = "Misc",         Fields = ["STAG", "MOD", "MRD"] },
        new TimingGroup { Name = "PHY",          Fields = ["PHYRDL_A", "PHYRDL_B"] },
    ];

    private static readonly IReadOnlyList<TimingGroup> _gigabyteLayout =
    [
        new TimingGroup { Name = "Primary",      Fields = ["CL", "RCDRD", "RCDWR", "RP", "RAS", "RC", "CWL"] },
        new TimingGroup { Name = "GDM/Cmd",      Fields = ["GDM", "Cmd2T"] },
        new TimingGroup { Name = "tRFC",         Fields = ["RFC", "RFC2", "RFC4"] },
        new TimingGroup { Name = "Secondary",    Fields = ["RRDS", "RRDL", "FAW", "WTRS", "WTRL", "WR", "RTP"] },
        new TimingGroup { Name = "SCL",          Fields = ["RDRDSCL", "WRWRSCL"] },
        new TimingGroup { Name = "Turn-around",  Fields = ["RDRDSC", "RDRDSD", "RDRDDD", "WRWRSC", "WRWRSD", "WRWRDD"] },
        new TimingGroup { Name = "Read/Write",   Fields = ["RDWR", "WRRD"] },
        new TimingGroup { Name = "Misc",         Fields = ["CKE", "STAG", "MOD", "MRD", "REFI"] },
        new TimingGroup { Name = "PHY",          Fields = ["PHYRDL_A", "PHYRDL_B"] },
    ];

    private static readonly IReadOnlyList<TimingGroup> _asrockLayout =
    [
        new TimingGroup { Name = "Primary",      Fields = ["CL", "RCDRD", "RCDWR", "RP", "RAS", "RC", "CWL", "GDM", "Cmd2T"] },
        new TimingGroup { Name = "tRFC",         Fields = ["RFC", "RFC2", "RFC4"] },
        new TimingGroup { Name = "Sub Timings",  Fields = ["RRDS", "RRDL", "FAW", "WTRS", "WTRL", "WR", "RTP",
                                                            "RDRDSCL", "WRWRSCL",
                                                            "RDRDSC", "RDRDSD", "RDRDDD",
                                                            "WRWRSC", "WRWRSD", "WRWRDD",
                                                            "RDWR", "WRRD",
                                                            "CKE", "STAG", "MOD", "MRD", "REFI"] },
        new TimingGroup { Name = "PHY",          Fields = ["PHYRDL_A", "PHYRDL_B"] },
    ];

    private static readonly IReadOnlyList<TimingGroup> _defaultLayout =
    [
        new TimingGroup { Name = "Primaries",    Fields = ["CL", "RCDRD", "RCDWR", "RP", "RAS", "RC", "CWL", "GDM", "Cmd2T"] },
        new TimingGroup { Name = "tRFC",         Fields = ["RFC", "RFC2", "RFC4"] },
        new TimingGroup { Name = "Secondaries",  Fields = ["RRDS", "RRDL", "FAW", "WTRS", "WTRL", "WR", "RTP", "RDRDSCL", "WRWRSCL"] },
        new TimingGroup { Name = "Turn-around",  Fields = ["RDRDSC", "RDRDSD", "RDRDDD", "WRWRSC", "WRWRSD", "WRWRDD", "RDWR", "WRRD"] },
        new TimingGroup { Name = "Misc",         Fields = ["CKE", "STAG", "MOD", "MRD", "REFI"] },
        new TimingGroup { Name = "PHY",          Fields = ["PHYRDL_A", "PHYRDL_B"] },
    ];

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the timing group layout for the given vendor.
    /// "Auto" is not a valid input here — resolve it to a concrete vendor first
    /// using <see cref="DetectVendor"/> before calling this method.
    /// </summary>
    public static IReadOnlyList<TimingGroup> GetLayout(BoardVendor vendor) => vendor switch
    {
        BoardVendor.MSI      => _msiLayout,
        BoardVendor.ASUS     => _asusLayout,
        BoardVendor.Gigabyte => _gigabyteLayout,
        BoardVendor.ASRock   => _asrockLayout,
        _                    => _defaultLayout,
    };

    /// <summary>
    /// Reads BaseBoardManufacturer from HKLM\HARDWARE\DESCRIPTION\System\BIOS
    /// and maps it to a concrete BoardVendor. Returns Default when the key is
    /// missing or the manufacturer string is not recognised.
    /// No WMI, no P/Invoke — registry only.
    /// </summary>
    public static BoardVendor DetectVendor()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\BIOS");
            if (key is null)
                return BoardVendor.Default;

            var manufacturer = key.GetValue("BaseBoardManufacturer") as string ?? "";
            return ClassifyManufacturer(manufacturer);
        }
        catch
        {
            return BoardVendor.Default;
        }
    }

    /// <summary>
    /// Resolves a vendor that may be "Auto" to a concrete value.
    /// When vendor is Auto, calls DetectVendor(). Otherwise returns vendor as-is.
    /// </summary>
    public static BoardVendor Resolve(BoardVendor vendor) =>
        vendor == BoardVendor.Auto ? DetectVendor() : vendor;

    /// <summary>
    /// Parses a BiosLayout settings string ("Auto", "MSI", "ASUS", etc.)
    /// into a BoardVendor. Returns Auto when the string is unrecognised or empty.
    /// </summary>
    public static BoardVendor ParseSetting(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting))
            return BoardVendor.Auto;

        return Enum.TryParse<BoardVendor>(setting, ignoreCase: true, out var vendor)
            ? vendor
            : BoardVendor.Auto;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Public so tests can verify string matching without needing InternalsVisibleTo.
    public static BoardVendor ClassifyManufacturer(string manufacturer)
    {
        if (manufacturer.Contains("MSI", StringComparison.OrdinalIgnoreCase) ||
            manufacturer.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase))
            return BoardVendor.MSI;

        if (manufacturer.Contains("ASUSTeK", StringComparison.OrdinalIgnoreCase) ||
            manufacturer.Contains("ASUS", StringComparison.OrdinalIgnoreCase))
            return BoardVendor.ASUS;

        if (manufacturer.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase))
            return BoardVendor.Gigabyte;

        if (manufacturer.Contains("ASRock", StringComparison.OrdinalIgnoreCase))
            return BoardVendor.ASRock;

        return BoardVendor.Default;
    }
}
