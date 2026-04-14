namespace RAMWatch.Service.Hardware;

/// <summary>
/// Detect AMD CPU family/model via CPUID to select the correct register map.
/// Register offsets can shift between Zen generations.
/// </summary>
public static class CpuDetect
{
    public enum CpuFamily
    {
        Unknown,
        Zen,        // Family 17h Model 01h (Summit Ridge)
        ZenPlus,    // Family 17h Model 08h (Pinnacle Ridge)
        Zen2,       // Family 17h Model 71h (Matisse), Model 31h (Castle Peak)
        Zen3,       // Family 19h Model 21h (Vermeer), Model 50h (Cezanne)
        Zen4,       // Family 19h Model 61h (Raphael)
        Zen5,       // Family 1Ah (Granite Ridge)
    }

    /// <summary>
    /// Read CPUID and identify the Zen generation.
    /// Uses PCI config space Function 3 offset 0x00 on bus 0, device 0x18.
    /// The CPU family/model is also available via WMI but CPUID via PCI is
    /// more reliable and doesn't require WMI dependencies.
    /// </summary>
    public static CpuFamily Detect(IHardwareAccess hw)
    {
        if (!hw.IsAvailable)
            return CpuFamily.Unknown;

        try
        {
            // Read CPUID from PCI config space: bus 0, device 0x18, function 3, offset 0x00
            // This gives us the device/vendor ID. For the family/model, we need offset 0xFC
            // which contains the hardware revision ID on AMD processors.
            //
            // Alternative: use the .NET Environment or WMI to get CPUID without driver access.
            // For now, use managed CPUID via the processor info available in .NET.
            return DetectFromEnvironment();
        }
        catch
        {
            return CpuFamily.Unknown;
        }
    }

    /// <summary>
    /// Detect CPU family from processor name string (WMI-free fallback).
    /// Less precise than CPUID but works without a driver.
    /// </summary>
    internal static CpuFamily DetectFromEnvironment()
    {
        try
        {
            // Read from registry — always available, no WMI dependency
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key is null) return CpuFamily.Unknown;

            var name = key.GetValue("ProcessorNameString") as string ?? "";
            var identifier = key.GetValue("Identifier") as string ?? "";

            // Parse family from Identifier string: "AMD64 Family 25 Model 33 Stepping 2"
            return ParseFamilyModel(identifier, name);
        }
        catch
        {
            return CpuFamily.Unknown;
        }
    }

    internal static CpuFamily ParseFamilyModel(string identifier, string name)
    {
        // Quick check: must be AMD
        if (!name.Contains("AMD", StringComparison.OrdinalIgnoreCase) &&
            !identifier.Contains("AMD", StringComparison.OrdinalIgnoreCase))
            return CpuFamily.Unknown;

        // Parse "Family NN Model NN" from identifier
        int family = 0, model = 0;
        var parts = identifier.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("Family", StringComparison.OrdinalIgnoreCase))
                int.TryParse(parts[i + 1], out family);
            if (parts[i].Equals("Model", StringComparison.OrdinalIgnoreCase))
                int.TryParse(parts[i + 1], out model);
        }

        // AMD64 Family 23 = 0x17 (Zen/Zen+/Zen2)
        // AMD64 Family 25 = 0x19 (Zen3/Zen4)
        // AMD64 Family 26 = 0x1A (Zen5)
        return (family, model) switch
        {
            (23, 1) => CpuFamily.Zen,
            (23, 8) => CpuFamily.ZenPlus,
            (23, 17 or 18) => CpuFamily.ZenPlus, // Raven Ridge, Picasso
            (23, 49 or 113 or 96 or 104 or 71) => CpuFamily.Zen2, // Castle Peak, Matisse, Renoir, Lucienne
            (25, 33 or 80) => CpuFamily.Zen3, // Vermeer, Cezanne
            (25, 68) => CpuFamily.Zen3, // Rembrandt
            (25, 97 or 116) => CpuFamily.Zen4, // Raphael, Phoenix
            (26, _) => CpuFamily.Zen5,
            _ => CpuFamily.Unknown
        };
    }

    /// <summary>
    /// Get the UMC base addresses for the given CPU family.
    /// AMD Zen processors have two UMC instances (one per channel) at known
    /// PCI device addresses on bus 0.
    /// </summary>
    public static (uint Bus, uint Device, uint Function)[] GetUmcAddresses(CpuFamily family)
    {
        // All Zen families use the same BDF for UMC: bus 0, device 0x18, functions 0-7
        // UMC0 is accessed via function 0, UMC1 via function 1
        // The UMC register space is in the SMN (System Management Network), accessed
        // indirectly via PCI config space at device 0x18 function 0.
        //
        // SMN access pattern: write address to reg 0x60, read data from reg 0x64
        // on bus 0, device 0, function 0.
        return family switch
        {
            CpuFamily.Unknown => [],
            _ =>
            [
                (0, 0, 0), // SMN access port: bus 0, device 0, function 0
            ]
        };
    }
}
