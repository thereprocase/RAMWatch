using System.Text.Json;
using RAMWatch.Core;
using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Loads and saves settings with atomic write-to-temp-then-rename (B7).
/// If the file is missing or corrupt, returns defaults without crashing.
/// Service is the sole writer. GUI sends changes via pipe.
/// </summary>
public sealed class SettingsManager
{
    private readonly string _path;
    private readonly Lock _lock = new();
    private AppSettings _current;

    public SettingsManager(string? path = null)
    {
        _path = path ?? DataDirectory.SettingsPath;
        _current = new AppSettings();
    }

    public AppSettings Current
    {
        get { lock (_lock) { return _current; } }
    }

    /// <summary>
    /// Load settings from disk. Returns defaults on missing or corrupt file.
    /// </summary>
    public AppSettings Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                _current = new AppSettings();
                Save(_current);
                return _current;
            }

            try
            {
                string json = File.ReadAllText(_path);
                var settings = JsonSerializer.Deserialize(json, RamWatchJsonContext.Default.AppSettings);
                _current = settings ?? new AppSettings();
            }
            catch (Exception)
            {
                // Corrupt or unreadable — preserve the original so the user
                // can recover their settings, then fall back to defaults.
                DataDirectory.ArchiveCorruptFile(_path);
                _current = new AppSettings();
            }

            // A settings.json written by an earlier version (or hand-edited)
            // may hold out-of-range numerics. Clamp before returning so every
            // consumer sees sane values.
            _current.ClampNumerics();

            return _current;
        }
    }

    /// <summary>
    /// Save settings using atomic write-to-temp-then-rename (B7).
    /// Atomic on NTFS: the rename either succeeds completely or not at all.
    /// </summary>
    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            string json = JsonSerializer.Serialize(settings, RamWatchJsonContext.Default.AppSettings);

            string dir = Path.GetDirectoryName(_path)!;
            string tempPath = Path.Combine(dir, $"settings.{Guid.NewGuid():N}.tmp");

            try
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _path, overwrite: true);
            }
            catch
            {
                // Clean up temp file on failure
                try { File.Delete(tempPath); } catch { }
                throw;
            }

            // Commit the in-memory swap only after disk write succeeds so
            // _current and the persisted file can never diverge on failure.
            _current = settings;
        }
    }

    /// <summary>
    /// Apply a partial settings update (from GUI via pipe).
    /// Overwrites the in-memory settings with the caller-supplied object
    /// wholesale. Prefer <see cref="ApplyPatch"/> for IPC-originated updates
    /// so fields absent from the client payload aren't silently reset to
    /// their JSON defaults.
    /// </summary>
    public void Update(AppSettings incoming)
    {
        lock (_lock)
        {
            _current = incoming;
            Save(_current);
        }
    }

    /// <summary>
    /// Merge a JSON patch into the current settings. Only properties present
    /// in <paramref name="patch"/> are overwritten; everything else keeps
    /// its current value. Unknown property names are ignored for forward
    /// compatibility with newer client versions.
    ///
    /// Reason: System.Text.Json's typed deserialize can't distinguish
    /// "field absent from payload" from "field present with default value".
    /// A GUI that sends only RefreshIntervalSeconds would otherwise wipe
    /// GitRemoteRepo, MirrorDirectory, retention settings, etc.
    /// </summary>
    public void ApplyPatch(JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object) return;

        lock (_lock)
        {
            var merged = Clone(_current);

            foreach (var prop in patch.EnumerateObject())
            {
                ApplyField(merged, prop);
            }

            // Clamp before persisting so a patch that sets LogRetentionDays=0
            // (would nuke every CSV on next startup retention) or
            // MaxLogSizeMb=0 (evicts every row) can't reach disk.
            merged.ClampNumerics();

            // Save first, then commit the in-memory swap. On disk failure
            // (disk full, ACL denied) Save throws and _current keeps the
            // pre-patch value — otherwise memory would silently diverge
            // from disk and the next service restart would revert.
            Save(merged);
            _current = merged;
        }
    }

    private static AppSettings Clone(AppSettings src) => new()
    {
        SchemaVersion            = src.SchemaVersion,
        StartMinimized           = src.StartMinimized,
        MinimizeToTray           = src.MinimizeToTray,
        AlwaysOnTop              = src.AlwaysOnTop,
        LaunchAtLogon            = src.LaunchAtLogon,
        RefreshIntervalSeconds   = src.RefreshIntervalSeconds,
        EnableCsvLogging         = src.EnableCsvLogging,
        LogDirectory             = src.LogDirectory,
        LogRetentionDays         = src.LogRetentionDays,
        MaxLogSizeMb             = src.MaxLogSizeMb,
        MirrorDirectory          = src.MirrorDirectory,
        EnableToastNotifications = src.EnableToastNotifications,
        NotifyOnWhea             = src.NotifyOnWhea,
        NotifyOnBsod             = src.NotifyOnBsod,
        NotifyOnDrift            = src.NotifyOnDrift,
        NotifyOnCodeIntegrity    = src.NotifyOnCodeIntegrity,
        NotifyOnAppCrash         = src.NotifyOnAppCrash,
        NotifyCooldownSeconds    = src.NotifyCooldownSeconds,
        Theme                    = src.Theme,
        DebugLogging             = src.DebugLogging,
        BiosLayout               = src.BiosLayout,
        EnableGitIntegration     = src.EnableGitIntegration,
        EnableGitPush            = src.EnableGitPush,
        GitRemoteRepo            = src.GitRemoteRepo,
        GitUserDisplayName       = src.GitUserDisplayName,
    };

    private static void ApplyField(AppSettings target, JsonProperty prop)
    {
        // camelCase names match RamWatchJsonContext's PropertyNamingPolicy.
        // A malformed value for a known key is ignored rather than throwing,
        // so one bad field doesn't abandon the rest of the patch.
        try
        {
            switch (prop.Name)
            {
                case "schemaVersion":            target.SchemaVersion            = prop.Value.GetInt32();   break;
                case "startMinimized":           target.StartMinimized           = prop.Value.GetBoolean(); break;
                case "minimizeToTray":           target.MinimizeToTray           = prop.Value.GetBoolean(); break;
                case "alwaysOnTop":              target.AlwaysOnTop              = prop.Value.GetBoolean(); break;
                case "launchAtLogon":            target.LaunchAtLogon            = prop.Value.GetBoolean(); break;
                case "refreshIntervalSeconds":   target.RefreshIntervalSeconds   = prop.Value.GetInt32();   break;
                case "enableCsvLogging":         target.EnableCsvLogging         = prop.Value.GetBoolean(); break;
                case "logDirectory":             target.LogDirectory             = prop.Value.GetString() ?? ""; break;
                case "logRetentionDays":         target.LogRetentionDays         = prop.Value.GetInt32();   break;
                case "maxLogSizeMb":             target.MaxLogSizeMb             = prop.Value.GetInt32();   break;
                case "mirrorDirectory":          target.MirrorDirectory          = prop.Value.GetString() ?? ""; break;
                case "enableToastNotifications": target.EnableToastNotifications = prop.Value.GetBoolean(); break;
                case "notifyOnWhea":             target.NotifyOnWhea             = prop.Value.GetBoolean(); break;
                case "notifyOnBsod":             target.NotifyOnBsod             = prop.Value.GetBoolean(); break;
                case "notifyOnDrift":            target.NotifyOnDrift            = prop.Value.GetBoolean(); break;
                case "notifyOnCodeIntegrity":    target.NotifyOnCodeIntegrity    = prop.Value.GetBoolean(); break;
                case "notifyOnAppCrash":         target.NotifyOnAppCrash         = prop.Value.GetBoolean(); break;
                case "notifyCooldownSeconds":    target.NotifyCooldownSeconds    = prop.Value.GetInt32();   break;
                case "theme":                    target.Theme                    = prop.Value.GetString() ?? ""; break;
                case "debugLogging":             target.DebugLogging             = prop.Value.GetBoolean(); break;
                case "biosLayout":               target.BiosLayout               = prop.Value.GetString() ?? ""; break;
                case "enableGitIntegration":     target.EnableGitIntegration     = prop.Value.GetBoolean(); break;
                case "enableGitPush":            target.EnableGitPush            = prop.Value.GetBoolean(); break;
                case "gitRemoteRepo":            target.GitRemoteRepo            = prop.Value.GetString() ?? ""; break;
                case "gitUserDisplayName":       target.GitUserDisplayName       = prop.Value.GetString() ?? ""; break;
                // Unknown keys ignored — forward compatibility with future client fields.
            }
        }
        catch { /* bad value for a known field: ignore, keep current */ }
    }
}
