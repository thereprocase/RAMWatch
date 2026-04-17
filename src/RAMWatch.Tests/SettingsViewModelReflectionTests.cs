using System.Reflection;
using RAMWatch.Core.Models;
using RAMWatch.ViewModels;
using Xunit;

namespace RAMWatch.Tests;

/// <summary>
/// Guards the SettingsViewModel ↔ AppSettings contract against silent field
/// drift. The service-side ApplyPatch merges by JSON field presence and
/// System.Text.Json emits every property (including defaults) — so if the GUI
/// builds an AppSettings in ToSettings that omits a property, the service
/// still sees a default value in the payload and wipes the previous setting
/// (that was W1 from the 2026-04-17 session).
///
/// The test below constructs an AppSettings with a non-default value for
/// every public settable property, runs it through LoadFromSettings →
/// ToSettings, and asserts each property survives unchanged. Any new
/// AppSettings property that isn't wired through both directions will flip
/// this test to red.
/// </summary>
public class SettingsViewModelReflectionTests
{
    [Fact]
    public void ToSettings_PreservesEveryAppSettingsProperty_ThroughLoadRoundTrip()
    {
        var input = BuildDistinctAppSettings();

        // MainViewModel is only referenced from AutoSaveAsync, which is gated
        // behind OnPropertyChanged + _suppressAutoSave. LoadFromSettings sets
        // the suppression flag and ToSettings is a pure read, so neither
        // touches _main. null! keeps the test construction light.
        var vm = new SettingsViewModel(main: null!);
        vm.LoadFromSettings(input);
        var output = vm.ToSettings();

        var missing = new List<string>();
        foreach (var prop in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetSetMethod(nonPublic: false) is null) continue;

            var expected = prop.GetValue(input);
            var actual   = prop.GetValue(output);

            if (!Equals(expected, actual))
            {
                missing.Add($"{prop.Name}: expected={Format(expected)} actual={Format(actual)}");
            }
        }

        Assert.True(
            missing.Count == 0,
            "AppSettings properties lost through SettingsViewModel round-trip:\n  " +
            string.Join("\n  ", missing));
    }

    /// <summary>
    /// Builds an AppSettings where every property holds a value distinct from
    /// its type/constructor default. A round-trip that silently emits a
    /// default for a forgotten property will therefore fail the equality
    /// check above.
    /// </summary>
    private static AppSettings BuildDistinctAppSettings() => new()
    {
        SchemaVersion            = 99,
        StartMinimized           = true,
        MinimizeToTray           = false,
        AlwaysOnTop              = true,
        LaunchAtLogon            = true,
        RefreshIntervalSeconds   = 123,   // inside clamp range 5..3600
        EnableCsvLogging         = false,
        LogDirectory             = @"D:\custom\logs",
        LogRetentionDays         = 45,    // inside 1..3650
        MaxLogSizeMb             = 250,   // inside 1..10000
        MirrorDirectory          = @"D:\mirror",
        EnableToastNotifications = false,
        NotifyOnWhea             = false,
        NotifyOnBsod             = false,
        NotifyOnDrift            = false,
        NotifyOnCodeIntegrity    = true,
        NotifyOnAppCrash         = true,
        NotifyCooldownSeconds    = 600,   // inside 0..86400
        Theme                    = "highcontrast",
        DebugLogging             = true,
        BiosLayout               = "MSI",
        EnableGitIntegration     = true,
        EnableGitPush            = true,
        GitRemoteRepo            = "thereprocase/RAMWatch",
        GitUserDisplayName       = "TestUser",
    };

    private static string Format(object? value) => value switch
    {
        null      => "<null>",
        string s  => $"\"{s}\"",
        _         => value.ToString() ?? "<null>",
    };
}
