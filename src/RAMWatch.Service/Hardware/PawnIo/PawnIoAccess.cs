using System.Reflection;
using System.Security.Cryptography;

namespace RAMWatch.Service.Hardware.PawnIo;

/// <summary>
/// IHardwareAccess implementation backed by PawnIO.
/// Loads AMDFamily17.bin from embedded resources, opens the PawnIO executor,
/// and exposes TryReadSmn/TryReadMsr via the module's named functions.
///
/// The module is hash-verified before loading (B8: module integrity).
/// The driver handle is opened once and reused for all reads.
/// </summary>
public sealed class PawnIoAccess : IHardwareAccess
{
    private PawnIoDriver? _driver;
    private bool _available;
    private string _status = "Not initialized";

    // SHA-256 of the bundled AMDFamily17.bin — verified before kernel load.
    private const string ExpectedModuleHash =
        "099DC01D6DB97EA997FEC4A461E191CC64B9D7CE47C9D2153C451C56C2ADCF50";

    public bool IsAvailable => _available;
    public string StatusDescription => _status;
    public string DriverName => "PawnIO";

    /// <summary>
    /// Initialize the PawnIO connection. Call once at service startup.
    /// </summary>
    public void Initialize()
    {
        try
        {
            // Step 1: Check PawnIOLib.dll is installed
            if (!PawnIoDriver.IsInstalled)
            {
                _status = "PawnIO not installed. Install PawnIO to enable timing reads.";
                return;
            }

            // Step 2: Open the PawnIO executor
            _driver = new PawnIoDriver();
            if (!_driver.Open())
            {
                _status = "PawnIO driver not responding. Is the PawnIO service running?";
                _driver.Dispose();
                _driver = null;
                return;
            }

            // Step 3: Load AMDFamily17.bin module from embedded resources
            byte[]? moduleBytes = LoadEmbeddedModule("AMDFamily17.bin");
            if (moduleBytes is null)
            {
                _status = "AMDFamily17.bin module not found in assembly resources.";
                _driver.Dispose();
                _driver = null;
                return;
            }

            // Step 4: Verify module hash (B8: never load unverified kernel code)
            string actualHash = ComputeHash(moduleBytes);
            if (!string.Equals(actualHash, ExpectedModuleHash, StringComparison.OrdinalIgnoreCase))
            {
                _status = $"AMDFamily17.bin hash mismatch. Expected {ExpectedModuleHash[..16]}..., got {actualHash[..16]}...";
                _driver.Dispose();
                _driver = null;
                return;
            }

            // Step 5: Load the module into the PawnIO kernel driver
            if (!_driver.LoadModule(moduleBytes))
            {
                _status = "PawnIO module load failed. Module may be incompatible with installed driver version.";
                _driver.Dispose();
                _driver = null;
                return;
            }

            _available = true;
            _status = "PawnIO driver loaded with AMDFamily17 module.";
        }
        catch (DllNotFoundException)
        {
            _status = "PawnIOLib.dll not found at expected path.";
        }
        catch (Exception ex)
        {
            _status = $"PawnIO initialization failed: {ex.Message}";
            _driver?.Dispose();
            _driver = null;
        }
    }

    public bool TryReadSmn(uint address, out uint value)
    {
        value = 0;
        if (_driver is null || !_available) return false;

        try
        {
            var result = _driver.Execute("ioctl_read_smn", [address], 1);
            if (result is null || result.Length == 0) return false;

            value = unchecked((uint)result[0]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryReadMsr(uint index, out ulong value)
    {
        value = 0;
        if (_driver is null || !_available) return false;

        try
        {
            var result = _driver.Execute("ioctl_read_msr", [index], 1);
            if (result is null || result.Length == 0) return false;

            value = result[0];
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _driver?.Dispose();
        _driver = null;
        _available = false;
    }

    private static byte[]? LoadEmbeddedModule(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        // Resource name: RAMWatch.Service.Resources.PawnIo.AMDFamily17.bin
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null) return null;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static string ComputeHash(byte[] data)
    {
        byte[] hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }
}
