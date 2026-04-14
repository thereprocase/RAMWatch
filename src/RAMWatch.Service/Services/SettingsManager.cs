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

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        TypeInfoResolver = RamWatchJsonContext.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        TypeInfoResolver = RamWatchJsonContext.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

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
                var settings = JsonSerializer.Deserialize<AppSettings>(json, ReadOptions);
                _current = settings ?? new AppSettings();
            }
            catch (Exception)
            {
                // Corrupt or unreadable — use defaults, don't crash (architecture requirement)
                _current = new AppSettings();
            }

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
            _current = settings;
            string json = JsonSerializer.Serialize(settings, WriteOptions);

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
        }
    }

    /// <summary>
    /// Apply a partial settings update (from GUI via pipe).
    /// Merges non-default values, saves atomically.
    /// </summary>
    public void Update(AppSettings incoming)
    {
        lock (_lock)
        {
            // For Phase 1, just replace wholesale. Phase 3 can add merge logic.
            _current = incoming;
            Save(_current);
        }
    }
}
