using Xunit;
using RAMWatch.Service.Services;
using RAMWatch.Core.Models;

namespace RAMWatch.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public SettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ramwatch-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var mgr = new SettingsManager(_settingsPath);
        var settings = mgr.Load();

        Assert.Equal(1, settings.SchemaVersion);
        Assert.Equal(60, settings.RefreshIntervalSeconds);
        Assert.True(settings.MinimizeToTray);
        Assert.True(settings.EnableCsvLogging);
    }

    [Fact]
    public void Load_MissingFile_CreatesFileWithDefaults()
    {
        var mgr = new SettingsManager(_settingsPath);
        mgr.Load();

        Assert.True(File.Exists(_settingsPath));
        string json = File.ReadAllText(_settingsPath);
        Assert.Contains("schemaVersion", json);
        Assert.Contains("refreshIntervalSeconds", json);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var mgr = new SettingsManager(_settingsPath);
        var settings = new AppSettings
        {
            RefreshIntervalSeconds = 30,
            MinimizeToTray = false,
            LogRetentionDays = 45,
            MirrorDirectory = @"D:\Sync\RAMWatch"
        };

        mgr.Save(settings);

        var mgr2 = new SettingsManager(_settingsPath);
        var loaded = mgr2.Load();

        Assert.Equal(30, loaded.RefreshIntervalSeconds);
        Assert.False(loaded.MinimizeToTray);
        Assert.Equal(45, loaded.LogRetentionDays);
        Assert.Equal(@"D:\Sync\RAMWatch", loaded.MirrorDirectory);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsDefaults()
    {
        File.WriteAllText(_settingsPath, "{ this is not valid json !!!");

        var mgr = new SettingsManager(_settingsPath);
        var settings = mgr.Load();

        Assert.Equal(60, settings.RefreshIntervalSeconds);
        Assert.True(settings.MinimizeToTray);
    }

    [Fact]
    public void Load_TruncatedJson_ReturnsDefaults()
    {
        File.WriteAllText(_settingsPath, """{"schemaVersion":1,"refreshInt""");

        var mgr = new SettingsManager(_settingsPath);
        var settings = mgr.Load();

        Assert.Equal(60, settings.RefreshIntervalSeconds);
    }

    [Fact]
    public void Load_EmptyFile_ReturnsDefaults()
    {
        File.WriteAllText(_settingsPath, "");

        var mgr = new SettingsManager(_settingsPath);
        var settings = mgr.Load();

        Assert.Equal(60, settings.RefreshIntervalSeconds);
    }

    [Fact]
    public void Save_IsAtomic_NoPartialFiles()
    {
        var mgr = new SettingsManager(_settingsPath);
        mgr.Save(new AppSettings { RefreshIntervalSeconds = 42 });

        // After save, there should be no .tmp files in the directory
        var tmpFiles = Directory.GetFiles(_tempDir, "*.tmp");
        Assert.Empty(tmpFiles);

        // The settings file should contain valid JSON
        string json = File.ReadAllText(_settingsPath);
        Assert.Contains("42", json);
    }

    [Fact]
    public void Update_ReplacesAndSaves()
    {
        var mgr = new SettingsManager(_settingsPath);
        mgr.Load();

        Assert.Equal(60, mgr.Current.RefreshIntervalSeconds);

        mgr.Update(new AppSettings { RefreshIntervalSeconds = 15 });

        Assert.Equal(15, mgr.Current.RefreshIntervalSeconds);

        // Verify persisted to disk
        var mgr2 = new SettingsManager(_settingsPath);
        var loaded = mgr2.Load();
        Assert.Equal(15, loaded.RefreshIntervalSeconds);
    }

    [Fact]
    public void SchemaVersion_DefaultsToOne()
    {
        var settings = new AppSettings();
        Assert.Equal(1, settings.SchemaVersion);
    }
}
