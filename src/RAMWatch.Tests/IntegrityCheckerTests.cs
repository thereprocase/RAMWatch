using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.Service.Services;

namespace RAMWatch.Tests;

public class IntegrityCheckerTests
{
    // ── CBS.log parsing ──────────────────────────────────────

    [Fact]
    public void CbsParse_CleanLog_ReturnsZero()
    {
        string log = """
            2026-04-14 09:15:00, Info CBS Starting TrustedInstaller initialization
            2026-04-14 09:15:01, Info CBS Loaded servicing stack
            2026-04-14 09:15:02, Info CBS Session completed. No reboot required.
            """;

        Assert.Equal(0, IntegrityChecker.CountCbsCorruptionInText(log));
    }

    [Fact]
    public void CbsParse_StoreCorruption_Detected()
    {
        string log = """
            2026-04-14 09:15:00, Info CBS Starting TrustedInstaller initialization
            2026-04-14 09:15:01, Error CBS Store corruption detected in component
            2026-04-14 09:15:02, Info CBS Session completed.
            """;

        Assert.Equal(1, IntegrityChecker.CountCbsCorruptionInText(log));
    }

    [Fact]
    public void CbsParse_MultipleCorruptionMarkers()
    {
        string log = """
            2026-04-14 09:15:00, Error CBS Store corruption detected in component A
            2026-04-14 09:15:01, Error CBS Manifest hash mismatch for component B
            2026-04-14 09:15:02, Error CBS payloads cannot be found for package C
            """;

        Assert.Equal(3, IntegrityChecker.CountCbsCorruptionInText(log));
    }

    [Fact]
    public void CbsParse_RepairableStore()
    {
        string log = "2026-04-14 09:15:00, Warn CBS The component store is repairable\n";
        Assert.Equal(1, IntegrityChecker.CountCbsCorruptionInText(log));
    }

    [Fact]
    public void CbsParse_RemapFailure()
    {
        string log = "2026-04-14 09:15:00, Error CBS component was not remapped properly\n";
        Assert.Equal(1, IntegrityChecker.CountCbsCorruptionInText(log));
    }

    [Fact]
    public void CbsParse_EmptyLog_ReturnsZero()
    {
        Assert.Equal(0, IntegrityChecker.CountCbsCorruptionInText(""));
        Assert.Equal(0, IntegrityChecker.CountCbsCorruptionInText(null!));
    }

    [Fact]
    public void CbsParse_CaseInsensitive()
    {
        string log = "2026-04-14 09:15:00, Error CBS STORE CORRUPTION detected\n";
        Assert.Equal(1, IntegrityChecker.CountCbsCorruptionInText(log));
    }

    // ── SFC output parsing ───────────────────────────────────

    [Fact]
    public void SfcParse_Clean()
    {
        string output = """
            Beginning system scan.  This process will take some time.
            Verification 100% complete.
            Windows Resource Protection did not find any integrity violations.
            """;

        Assert.Equal(IntegrityCheckStatus.Clean, IntegrityChecker.ParseSfcOutput(output));
    }

    [Fact]
    public void SfcParse_CorruptionRepaired()
    {
        string output = """
            Windows Resource Protection found corrupt files and successfully repaired them.
            For online repairs, details are included in the CBS log file located at
            windir\Logs\CBS\CBS.log.
            """;

        Assert.Equal(IntegrityCheckStatus.CorruptionRepaired, IntegrityChecker.ParseSfcOutput(output));
    }

    [Fact]
    public void SfcParse_CorruptionFound()
    {
        string output = """
            Windows Resource Protection found corrupt files but was unable to fix some of them.
            Details are included in the CBS log file located at windir\Logs\CBS\CBS.log.
            """;

        Assert.Equal(IntegrityCheckStatus.CorruptionFound, IntegrityChecker.ParseSfcOutput(output));
    }

    [Fact]
    public void SfcParse_Failed()
    {
        string output = "Windows Resource Protection could not perform the requested operation.";
        Assert.Equal(IntegrityCheckStatus.Failed, IntegrityChecker.ParseSfcOutput(output));
    }

    [Fact]
    public void SfcParse_Unknown()
    {
        Assert.Equal(IntegrityCheckStatus.Unknown, IntegrityChecker.ParseSfcOutput("Something unexpected"));
        Assert.Equal(IntegrityCheckStatus.Unknown, IntegrityChecker.ParseSfcOutput(""));
        Assert.Equal(IntegrityCheckStatus.Unknown, IntegrityChecker.ParseSfcOutput(null!));
    }

    // ── DISM output parsing ──────────────────────────────────

    [Fact]
    public void DismParse_Clean()
    {
        string output = """
            Deployment Image Servicing and Management tool
            Image Version: 10.0.22631.1
            No component store corruption detected.
            The operation completed successfully.
            """;

        Assert.Equal(IntegrityCheckStatus.Clean, IntegrityChecker.ParseDismOutput(output));
    }

    [Fact]
    public void DismParse_CorruptionFound()
    {
        string output = """
            The component store is repairable.
            The operation completed successfully.
            """;

        Assert.Equal(IntegrityCheckStatus.CorruptionFound, IntegrityChecker.ParseDismOutput(output));
    }

    [Fact]
    public void DismParse_Repaired()
    {
        string output = """
            The restore operation completed successfully.
            The operation completed successfully.
            """;

        Assert.Equal(IntegrityCheckStatus.CorruptionRepaired, IntegrityChecker.ParseDismOutput(output));
    }

    [Fact]
    public void DismParse_Unknown()
    {
        Assert.Equal(IntegrityCheckStatus.Unknown, IntegrityChecker.ParseDismOutput(""));
        Assert.Equal(IntegrityCheckStatus.Unknown, IntegrityChecker.ParseDismOutput(null!));
    }
}
