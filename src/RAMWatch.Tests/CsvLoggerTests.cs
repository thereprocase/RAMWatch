using Xunit;
using RAMWatch.Core.Models;
using RAMWatch.Service.Services;

namespace RAMWatch.Tests;

public class CsvLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public CsvLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ramwatch-csv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void LogEvent_CreatesFileWithHeader()
    {
        using var logger = new CsvLogger(_tempDir);
        var evt = MakeEvent();

        logger.LogEvent(evt, "boot_0414_0901");

        var files = Directory.GetFiles(_tempDir, "events_*.csv");
        Assert.Single(files);

        var lines = ReadFileShared(files[0]);
        Assert.True(lines.Length >= 2); // header + data row
        Assert.StartsWith("timestamp,", lines[0]);
    }

    [Fact]
    public void LogEvent_AppendsMultipleRows()
    {
        using var logger = new CsvLogger(_tempDir);

        logger.LogEvent(MakeEvent("Event 1"), "boot_0414_0901");
        logger.LogEvent(MakeEvent("Event 2"), "boot_0414_0901");
        logger.LogEvent(MakeEvent("Event 3"), "boot_0414_0901");

        var files = Directory.GetFiles(_tempDir, "events_*.csv");
        var lines = ReadFileShared(files[0]);
        Assert.Equal(4, lines.Length); // header + 3 rows
    }

    [Fact]
    public void LogEvent_RotatesOnNewDate()
    {
        using var logger = new CsvLogger(_tempDir);

        var today = MakeEvent("Today");
        var tomorrow = MakeEvent("Tomorrow", DateTime.UtcNow.AddDays(1));

        logger.LogEvent(today, "boot_0414_0901");
        logger.LogEvent(tomorrow, "boot_0415_0901");

        var files = Directory.GetFiles(_tempDir, "events_*.csv");
        Assert.Equal(2, files.Length);
    }

    [Fact]
    public void FormatRow_EscapesCommasInSummary()
    {
        var evt = MakeEvent("Error: memory, disk, and CPU");
        string row = CsvLogger.FormatRow(evt, "boot_0414_0901");

        Assert.Contains("\"Error: memory, disk, and CPU\"", row);
    }

    [Fact]
    public void FormatRow_EscapesQuotesInSummary()
    {
        var evt = MakeEvent("Error in \"main\" module");
        string row = CsvLogger.FormatRow(evt, "boot_0414_0901");

        Assert.Contains("\"Error in \"\"main\"\" module\"", row);
    }

    [Fact]
    public void FormatRow_EscapesNewlinesInSummary()
    {
        var evt = MakeEvent("Line 1\nLine 2");
        string row = CsvLogger.FormatRow(evt, "boot_0414_0901");

        Assert.Contains("\"Line 1\nLine 2\"", row);
    }

    [Fact]
    public void FormatRow_PlainSummaryNotQuoted()
    {
        var evt = MakeEvent("Simple error");
        string row = CsvLogger.FormatRow(evt, "boot_0414_0901");

        Assert.EndsWith("Simple error", row);
        Assert.DoesNotContain("\"Simple error\"", row);
    }

    [Fact]
    public void CsvEscape_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", CsvLogger.CsvEscape(""));
        Assert.Equal("", CsvLogger.CsvEscape(null));
    }

    [Fact]
    public void GenerateBootId_ReturnsSequentialIds()
    {
        // Each call should return boot_NNNNNN where N increments.
        string id1 = CsvLogger.GenerateBootId(_tempDir);
        string id2 = CsvLogger.GenerateBootId(_tempDir);
        string id3 = CsvLogger.GenerateBootId(_tempDir);

        // All must match the expected format.
        Assert.Matches(@"^boot_\d{6}$", id1);
        Assert.Matches(@"^boot_\d{6}$", id2);
        Assert.Matches(@"^boot_\d{6}$", id3);

        // Each successive call must produce a strictly larger counter value.
        int n1 = int.Parse(id1[5..]);
        int n2 = int.Parse(id2[5..]);
        int n3 = int.Parse(id3[5..]);
        Assert.True(n2 == n1 + 1, $"Expected {n1 + 1} but got {n2}");
        Assert.True(n3 == n2 + 1, $"Expected {n2 + 1} but got {n3}");
    }

    [Fact]
    public void GenerateBootId_StartsAtOneWhenCounterMissing()
    {
        // Fresh directory with no counter file → first ID is boot_000001.
        string id = CsvLogger.GenerateBootId(_tempDir);
        Assert.Equal("boot_000001", id);
    }

    [Fact]
    public void GenerateBootId_PicksUpExistingCounter()
    {
        // Pre-seed the counter at 41 — next call should return boot_000042.
        File.WriteAllText(Path.Combine(_tempDir, "boot_counter.txt"), "41");
        string id = CsvLogger.GenerateBootId(_tempDir);
        Assert.Equal("boot_000042", id);
    }

    [Fact]
    public void RunRetention_DeletesOldFiles()
    {
        // Create files with old timestamps
        for (int i = 0; i < 5; i++)
        {
            var date = DateTime.UtcNow.AddDays(-100 - i);
            var path = Path.Combine(_tempDir, $"events_{date:yyyy-MM-dd}.csv");
            File.WriteAllText(path, "header\ndata");
            File.SetCreationTimeUtc(path, date);
        }

        // Create a recent file
        var recentPath = Path.Combine(_tempDir, $"events_{DateTime.UtcNow:yyyy-MM-dd}.csv");
        File.WriteAllText(recentPath, "header\ndata");

        var logger = new CsvLogger(_tempDir, retentionDays: 90);
        logger.RunRetention();
        logger.Dispose();

        var remaining = Directory.GetFiles(_tempDir, "events_*.csv");
        Assert.Single(remaining); // Only the recent file survives
    }

    [Fact]
    public void FileIsReadable_WhileLoggerHasItOpen()
    {
        using var logger = new CsvLogger(_tempDir);
        logger.LogEvent(MakeEvent(), "boot_0414_0901");

        var files = Directory.GetFiles(_tempDir, "events_*.csv");

        // Another process should be able to read (FileShare.Read)
        using var reader = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(reader);
        var content = sr.ReadToEnd();
        Assert.Contains("timestamp,", content);
    }

    /// <summary>
    /// Read file lines using FileShare.ReadWrite to avoid conflicts with the logger's open handle.
    /// </summary>
    private static string[] ReadFileShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static MonitoredEvent MakeEvent(string summary = "Test event", DateTime? timestamp = null)
    {
        return new MonitoredEvent(
            timestamp ?? DateTime.UtcNow,
            "WHEA Hardware Errors",
            EventCategory.Hardware,
            17,
            EventSeverity.Warning,
            summary);
    }
}
