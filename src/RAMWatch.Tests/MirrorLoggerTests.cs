using Xunit;
using RAMWatch.Service.Services;

namespace RAMWatch.Tests;

public class MirrorLoggerTests : IDisposable
{
    private readonly string _sourceDir;
    private readonly string _mirrorDir;

    public MirrorLoggerTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"ramwatch-mirror-{Guid.NewGuid():N}");
        _sourceDir = Path.Combine(baseDir, "source");
        _mirrorDir = Path.Combine(baseDir, "mirror");
        Directory.CreateDirectory(_sourceDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_sourceDir)!, true); } catch { }
    }

    [Fact]
    public void IsEnabled_FalseWhenEmpty()
    {
        var logger = new MirrorLogger("");
        Assert.False(logger.IsEnabled);
    }

    [Fact]
    public void IsEnabled_TrueWhenSet()
    {
        var logger = new MirrorLogger(_mirrorDir);
        Assert.True(logger.IsEnabled);
    }

    [Fact]
    public async Task EnqueueCopy_CopiesFile()
    {
        var sourceFile = Path.Combine(_sourceDir, "test.csv");
        File.WriteAllText(sourceFile, "header\ndata1\ndata2");

        var logger = new MirrorLogger(_mirrorDir);
        logger.EnqueueCopy(sourceFile);

        // Wait for async fire-and-forget to complete
        await Task.Delay(500);

        var mirrorFile = Path.Combine(_mirrorDir, "test.csv");
        Assert.True(File.Exists(mirrorFile));
        Assert.Equal("header\ndata1\ndata2", File.ReadAllText(mirrorFile));
    }

    [Fact]
    public async Task EnqueueCopy_CreatesDirectory()
    {
        var sourceFile = Path.Combine(_sourceDir, "events.csv");
        File.WriteAllText(sourceFile, "data");

        Assert.False(Directory.Exists(_mirrorDir));

        var logger = new MirrorLogger(_mirrorDir);
        logger.EnqueueCopy(sourceFile);

        await Task.Delay(500);

        Assert.True(Directory.Exists(_mirrorDir));
    }

    [Fact]
    public void EnqueueCopy_DoesNothing_WhenDisabled()
    {
        var sourceFile = Path.Combine(_sourceDir, "test.csv");
        File.WriteAllText(sourceFile, "data");

        var logger = new MirrorLogger("");
        logger.EnqueueCopy(sourceFile);

        Assert.False(Directory.Exists(_mirrorDir));
    }

    [Fact]
    public async Task EnqueueCopy_SurvivesMissingSourceFile()
    {
        var logger = new MirrorLogger(_mirrorDir);
        logger.EnqueueCopy(Path.Combine(_sourceDir, "nonexistent.csv"));

        // Should not throw — fire-and-forget swallows errors
        await Task.Delay(200);
    }

    [Fact]
    public async Task EnqueueCopy_CanReadWhileSourceIsOpen()
    {
        var sourceFile = Path.Combine(_sourceDir, "open.csv");

        // Simulate CSV logger holding the file open with FileShare.Read
        using (var writer = new FileStream(sourceFile, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            using var sw = new StreamWriter(writer);
            sw.Write("data from open file");
            sw.Flush();

            var logger = new MirrorLogger(_mirrorDir);
            logger.EnqueueCopy(sourceFile);

            await Task.Delay(500);
        }

        var mirrorFile = Path.Combine(_mirrorDir, "open.csv");
        Assert.True(File.Exists(mirrorFile));
    }

    [Fact]
    public void ResetFailures_ResetsCounter()
    {
        var logger = new MirrorLogger(_mirrorDir);
        // No direct way to assert counter, but ensure it doesn't throw
        logger.ResetFailures();
    }
}
