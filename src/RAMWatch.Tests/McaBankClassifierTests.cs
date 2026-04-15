using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.Service.Services;

namespace RAMWatch.Tests;

/// <summary>
/// Tests for MCA bank classification from WHEA event XML.
/// Uses real event XML captured from a Zen 3 (Vermeer) system.
/// </summary>
public class McaBankClassifierTests
{
    /// <summary>
    /// Real WHEA Event ID 19 XML from a Zen 3 system showing a corrected
    /// Bus/Interconnect error on MCA Bank 27 (Data Fabric / PIE).
    /// </summary>
    private const string Zen3DataFabricEventXml = """
        <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>
          <System>
            <Provider Name='Microsoft-Windows-WHEA-Logger' Guid='{C26C4F3C-3F66-4E99-8F8A-39405CFED220}'/>
            <EventID>19</EventID>
            <Level>3</Level>
            <TimeCreated SystemTime='2026-04-15T19:40:00.1225687Z'/>
          </System>
          <EventData>
            <Data Name='ErrorSource'>1</Data>
            <Data Name='ApicId'>0</Data>
            <Data Name='MCABank'>27</Data>
            <Data Name='MciStat'>0x982000000002080b</Data>
            <Data Name='MciAddr'>0x0</Data>
            <Data Name='MciMisc'>0xd01a0ffe00000000</Data>
            <Data Name='ErrorType'>10</Data>
            <Data Name='TransactionType'>256</Data>
            <Data Name='Participation'>0</Data>
            <Data Name='RequestType'>0</Data>
            <Data Name='MemorIO'>2</Data>
            <Data Name='MemHierarchyLvl'>3</Data>
            <Data Name='Timeout'>0</Data>
            <Data Name='OperationType'>256</Data>
            <Data Name='Channel'>256</Data>
            <Data Name='Length'>2063</Data>
            <Data Name='RawData'>435045521002FFFF</Data>
          </EventData>
        </Event>
        """;

    [Fact]
    public void Zen3_DataFabric_Bank27_ClassifiedCorrectly()
    {
        var mca = McaBankClassifier.TryParse(Zen3DataFabricEventXml);

        Assert.NotNull(mca);
        Assert.Equal(27, mca.BankNumber);
        Assert.Equal(McaClassification.DataFabric, mca.Classification);
        Assert.Contains("Data Fabric", mca.Component);
        Assert.False(mca.IsUncorrectable);
        Assert.False(mca.IsOverflow);
        Assert.False(mca.IsContextCorrupted);
        Assert.Equal(0, mca.ApicId);
        Assert.Equal(10, mca.WheaErrorType);
    }

    [Fact]
    public void MciStatus_FlagsDecodedCorrectly()
    {
        var mca = McaBankClassifier.TryParse(Zen3DataFabricEventXml);

        Assert.NotNull(mca);
        // 0x982000000002080b: Val=1, Over=0, UC=0, EN=1, MiscV=1, AddrV=0, PCC=0
        Assert.False(mca.IsUncorrectable);
        Assert.False(mca.IsOverflow);
        Assert.False(mca.IsContextCorrupted);
        Assert.Equal("0x982000000002080b", mca.MciStatus);
    }

    [Fact]
    public void MciAddr_NullWhenZeroAndAddrVClear()
    {
        var mca = McaBankClassifier.TryParse(Zen3DataFabricEventXml);

        Assert.NotNull(mca);
        // AddrV bit (58) is clear in 0x982000000002080b, and addr is 0x0
        Assert.Null(mca.MciAddr);
    }

    [Fact]
    public void MciMisc_PopulatedWhenMiscVSet()
    {
        var mca = McaBankClassifier.TryParse(Zen3DataFabricEventXml);

        Assert.NotNull(mca);
        // MiscV bit (59) is set in 0x982000000002080b
        Assert.NotNull(mca.MciMisc);
        Assert.Equal("0xd01a0ffe00000000", mca.MciMisc);
    }

    [Fact]
    public void NullXml_ReturnsNull()
    {
        Assert.Null(McaBankClassifier.TryParse(null));
        Assert.Null(McaBankClassifier.TryParse(""));
    }

    [Fact]
    public void MalformedXml_ReturnsNull()
    {
        Assert.Null(McaBankClassifier.TryParse("<not valid xml"));
    }

    [Fact]
    public void XmlWithoutMcaBank_ReturnsNull()
    {
        // Valid WHEA XML but no MCABank field (e.g., a non-MCA WHEA event)
        const string xml = """
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>
              <System>
                <Provider Name='Microsoft-Windows-WHEA-Logger'/>
                <EventID>19</EventID>
              </System>
              <EventData>
                <Data Name='ErrorSource'>3</Data>
                <Data Name='ErrorType'>2</Data>
              </EventData>
            </Event>
            """;

        Assert.Null(McaBankClassifier.TryParse(xml));
    }

    [Fact]
    public void UmcBank_ClassifiedAsUmc()
    {
        string xml = MakeWheaXml(bankNumber: 18, mciStat: "0x9800000000020108", errorType: 3);
        var mca = McaBankClassifier.TryParse(xml);

        Assert.NotNull(mca);
        Assert.Equal(McaClassification.Umc, mca.Classification);
        Assert.Contains("UMC", mca.Component);
    }

    [Fact]
    public void L3CacheBank_ClassifiedAsL3()
    {
        string xml = MakeWheaXml(bankNumber: 10, mciStat: "0x9800000000010015", errorType: 6);
        var mca = McaBankClassifier.TryParse(xml);

        Assert.NotNull(mca);
        Assert.Equal(McaClassification.L3Cache, mca.Classification);
        Assert.Contains("L3", mca.Component);
    }

    [Fact]
    public void CoreBank_ClassifiedAsCore()
    {
        string xml = MakeWheaXml(bankNumber: 0, mciStat: "0x9800000000000001", errorType: 0);
        var mca = McaBankClassifier.TryParse(xml);

        Assert.NotNull(mca);
        Assert.Equal(McaClassification.Core, mca.Classification);
        Assert.Contains("Load-Store", mca.Component);
    }

    [Fact]
    public void UncorrectableError_FlagSet()
    {
        // Set UC bit (61) in MCI_STATUS: 0xBA... (bits 63=1, 62=0, 61=1, 60=1, 59=1, 58=0)
        string xml = MakeWheaXml(bankNumber: 27, mciStat: "0xba2000000002080b", errorType: 10);
        var mca = McaBankClassifier.TryParse(xml);

        Assert.NotNull(mca);
        Assert.True(mca.IsUncorrectable);
    }

    [Fact]
    public void OverflowError_FlagSet()
    {
        // Set OVER bit (62): 0xD82... (bits 63=1, 62=1, 61=0, 60=1, 59=1)
        string xml = MakeWheaXml(bankNumber: 27, mciStat: "0xd82000000002080b", errorType: 10);
        var mca = McaBankClassifier.TryParse(xml);

        Assert.NotNull(mca);
        Assert.True(mca.IsOverflow);
    }

    [Fact]
    public void ContextCorrupted_FlagSet()
    {
        // Set PCC bit (57): 0x9A2... (bits 63=1, 62=0, 61=0, 60=1, 59=1, 58=0, 57=1)
        string xml = MakeWheaXml(bankNumber: 27, mciStat: "0x9a2000000002080b", errorType: 10);
        var mca = McaBankClassifier.TryParse(xml);

        Assert.NotNull(mca);
        Assert.True(mca.IsContextCorrupted);
    }

    /// <summary>
    /// Helper to generate WHEA event XML with specified MCA fields.
    /// </summary>
    private static string MakeWheaXml(int bankNumber, string mciStat, int errorType,
        string mciAddr = "0x0", string mciMisc = "0x0", int apicId = 0)
    {
        return $"""
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>
              <System>
                <Provider Name='Microsoft-Windows-WHEA-Logger'/>
                <EventID>19</EventID>
              </System>
              <EventData>
                <Data Name='ErrorSource'>1</Data>
                <Data Name='ApicId'>{apicId}</Data>
                <Data Name='MCABank'>{bankNumber}</Data>
                <Data Name='MciStat'>{mciStat}</Data>
                <Data Name='MciAddr'>{mciAddr}</Data>
                <Data Name='MciMisc'>{mciMisc}</Data>
                <Data Name='ErrorType'>{errorType}</Data>
              </EventData>
            </Event>
            """;
    }
}
