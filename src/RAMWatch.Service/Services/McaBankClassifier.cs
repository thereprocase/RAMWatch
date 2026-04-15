using System.Globalization;
using System.Xml;
using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Parses MCA (Machine Check Architecture) bank data from WHEA event XML
/// and classifies errors into tuning-relevant categories.
///
/// Bank-to-component mapping is best-effort for AMD Zen 2/3/4 (Family 17h/19h/1Ah).
/// The mapping depends on CPU topology, so we use the combination of bank number,
/// error type, and known patterns rather than a fixed table.
/// </summary>
public static class McaBankClassifier
{
    // MCI_STATUS bit positions
    private const int BitVal = 63;
    private const int BitOver = 62;
    private const int BitUc = 61;
    private const int BitEn = 60;
    private const int BitMiscV = 59;
    private const int BitAddrV = 58;
    private const int BitPcc = 57;

    /// <summary>
    /// Attempt to parse MCA details from a WHEA event record's XML.
    /// Returns null if the XML doesn't contain MCA bank data (not all WHEA events do).
    /// </summary>
    public static McaDetails? TryParse(string? rawXml)
    {
        if (string.IsNullOrEmpty(rawXml))
            return null;

        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(rawXml);

            var nsMgr = new XmlNamespaceManager(doc.NameTable);
            nsMgr.AddNamespace("e", "http://schemas.microsoft.com/win/2004/08/events/event");

            // Extract named data fields from EventData
            var dataNodes = doc.SelectNodes("//e:EventData/e:Data[@Name]", nsMgr);
            if (dataNodes is null || dataNodes.Count == 0)
                return null;

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode node in dataNodes)
            {
                var name = node.Attributes?["Name"]?.Value;
                var value = node.InnerText;
                if (name is not null)
                    fields[name] = value;
            }

            // MCABank is the key field — if it's missing, this isn't an MCA event
            if (!fields.TryGetValue("MCABank", out var bankStr) ||
                !int.TryParse(bankStr, out int bankNumber))
                return null;

            // Parse MCI_STATUS (hex string like "0x982000000002080b")
            ulong mciStatus = 0;
            if (fields.TryGetValue("MciStat", out var statStr))
                mciStatus = ParseHex(statStr);

            ulong mciAddr = 0;
            bool hasAddr = false;
            if (fields.TryGetValue("MciAddr", out var addrStr))
            {
                mciAddr = ParseHex(addrStr);
                hasAddr = mciAddr != 0 || GetBit(mciStatus, BitAddrV);
            }

            ulong mciMisc = 0;
            bool hasMisc = false;
            if (fields.TryGetValue("MciMisc", out var miscStr))
            {
                mciMisc = ParseHex(miscStr);
                hasMisc = mciMisc != 0 || GetBit(mciStatus, BitMiscV);
            }

            int apicId = 0;
            if (fields.TryGetValue("ApicId", out var apicStr))
                int.TryParse(apicStr, out apicId);

            int errorType = 0;
            if (fields.TryGetValue("ErrorType", out var etStr))
                int.TryParse(etStr, out errorType);

            // Decode MCI_STATUS flags
            bool isUncorrectable = GetBit(mciStatus, BitUc);
            bool isOverflow = GetBit(mciStatus, BitOver);
            bool isPcc = GetBit(mciStatus, BitPcc);

            // Classify the bank
            var (component, classification) = ClassifyBank(bankNumber, mciStatus, errorType);

            return new McaDetails
            {
                BankNumber = bankNumber,
                MciStatus = FormatHex(mciStatus),
                MciAddr = hasAddr ? FormatHex(mciAddr) : null,
                MciMisc = hasMisc ? FormatHex(mciMisc) : null,
                ApicId = apicId,
                Component = component,
                Classification = classification,
                IsUncorrectable = isUncorrectable,
                IsOverflow = isOverflow,
                IsContextCorrupted = isPcc,
                WheaErrorType = errorType
            };
        }
        catch
        {
            // XML parsing failed — don't crash the event pipeline
            return null;
        }
    }

    /// <summary>
    /// Classify an MCA bank into a human-readable component name and tuning category.
    ///
    /// AMD Zen 2/3/4 MCA bank layout (approximate — shifts with core count):
    ///   Per-core banks (repeated per core):
    ///     LS (Load-Store), IF (Instruction Fetch), L2 Cache, DE (Decode), EX (Execution), FP
    ///   Shared/uncore banks (higher numbers):
    ///     L3 Cache (one bank per slice), UMC (one per channel), PIE/CS/DF (Data Fabric),
    ///     NBIO (PCIe/IO), IOHC
    ///
    /// Since exact numbering depends on topology, we use heuristics:
    /// - ErrorType 10 (Bus/Interconnect) on high bank numbers → Data Fabric
    /// - Known UMC bank ranges for common core counts
    /// - Error code patterns in MCI_STATUS lower bits
    /// </summary>
    private static (string Component, McaClassification Classification) ClassifyBank(
        int bankNumber, ulong mciStatus, int errorType)
    {
        // Extract the MCA error code from the lower 16 bits of MCI_STATUS
        ushort errorCode = (ushort)(mciStatus & 0xFFFF);

        // WHEA ErrorType values (from Windows SDK WHEA_ERROR_TYPE):
        //   0 = Internal          5 = TLB
        //   1 = Bus               6 = Cache
        //   2 = Memory Access     7 = Function Unit
        //   3 = Memory Hierarchy  8 = Self-Test
        //   4 = Micro-Arch        10 = Bus/Interconnect

        // Bank 27 on Zen 3 desktop (Vermeer) is consistently the PIE/Data Fabric bank.
        // Banks 24-31 in the uncore range are DF/PIE/NBIO/IOHC on most Zen topologies.
        if (bankNumber >= 24 && bankNumber <= 31)
        {
            if (errorType == 10 || IsBusInterconnectErrorCode(errorCode))
                return ("Data Fabric (PIE)", McaClassification.DataFabric);

            // Could still be NBIO/IOHC in this range
            return ($"Uncore Bank {bankNumber} (NBIO/IOHC/DF)", McaClassification.Pcie);
        }

        // UMC banks — on Zen 3 desktop, typically banks 16-23 (two channels, but
        // bank count per channel varies). Memory hierarchy errors here indicate
        // memory controller issues.
        if (bankNumber >= 16 && bankNumber <= 23)
        {
            int channel = (bankNumber - 16) % 2;
            if (errorType is 2 or 3 || IsMemoryErrorCode(errorCode))
                return ($"UMC Channel {channel}", McaClassification.Umc);

            return ($"UMC Bank {bankNumber}", McaClassification.Umc);
        }

        // L3 cache banks — typically banks 8-15 on Zen 3 (one per L3 slice).
        // Cache hierarchy errors here.
        if (bankNumber >= 8 && bankNumber <= 15)
        {
            if (errorType == 6 || IsCacheErrorCode(errorCode))
                return ($"L3 Cache (Slice {bankNumber - 8})", McaClassification.L3Cache);

            return ($"L3 Bank {bankNumber}", McaClassification.L3Cache);
        }

        // Per-core banks 0-7 — LS, IF, L2, DE, reserved, EX, FP
        if (bankNumber >= 0 && bankNumber <= 7)
        {
            string coreName = bankNumber switch
            {
                0 => "Load-Store Unit (LS)",
                1 => "Instruction Fetch Unit (IF)",
                2 => "L2 Cache",
                3 => "Decode Unit (DE)",
                4 => "Reserved",
                5 => "Execution Unit (EX)",
                6 => "Floating Point Unit (FP)",
                7 => "Floating Point Unit (FP)",
                _ => $"Core Bank {bankNumber}"
            };

            // L2 gets its own treatment — it's cache but not L3
            if (bankNumber == 2)
                return (coreName, McaClassification.Core);

            return (coreName, McaClassification.Core);
        }

        // Anything above 31 — unlikely on current Zen, but handle gracefully
        return ($"MCA Bank {bankNumber}", McaClassification.Unknown);
    }

    /// <summary>
    /// Check if the MCA error code (lower 16 bits of MCI_STATUS) indicates
    /// a bus/interconnect error. Format: 0000 1PPT RRRR IILL where PP=participation,
    /// T=timeout, RRRR=request, II=info, LL=level.
    /// </summary>
    private static bool IsBusInterconnectErrorCode(ushort code)
    {
        // Bus/interconnect errors have bit 11 set (0x0800)
        return (code & 0x0800) != 0;
    }

    /// <summary>
    /// Check if the MCA error code indicates a memory hierarchy error.
    /// Format: 0000 0001 RRRR TTLL
    /// </summary>
    private static bool IsMemoryErrorCode(ushort code)
    {
        // Memory hierarchy: bits [15:8] = 0000 0001 xxxx, bits [7:4] = TT (transaction type)
        return (code & 0xFF00) == 0x0100;
    }

    /// <summary>
    /// Check if the MCA error code indicates a cache hierarchy error.
    /// Format: 0000 0000 0001 TTLL
    /// </summary>
    private static bool IsCacheErrorCode(ushort code)
    {
        // Cache errors: upper byte clear, transaction type (bits [7:4]) non-zero.
        return (code & 0xFF00) == 0x0000 && (code & 0x00F0) != 0;
    }

    private static bool GetBit(ulong value, int bit)
    {
        return ((value >> bit) & 1) == 1;
    }

    private static ulong ParseHex(string s)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];

        if (ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var result))
            return result;

        return 0;
    }

    private static string FormatHex(ulong value)
    {
        return $"0x{value:x16}";
    }
}
