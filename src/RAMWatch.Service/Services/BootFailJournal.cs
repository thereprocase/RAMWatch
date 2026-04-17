using System.Text.Json;
using RAMWatch.Core;
using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Persists boot fail entries to %ProgramData%\RAMWatch\boot-fails.json.
/// Service is the sole writer. Atomic write-to-temp-then-rename (B7).
/// </summary>
public sealed class BootFailJournal
{
    private const int MaxEntries = 500;

    private readonly string _path;
    private readonly Lock _lock = new();
    private List<BootFailEntry> _entries = [];

    public BootFailJournal(string? dataDirectory = null)
    {
        string dir = dataDirectory ?? DataDirectory.BasePath;
        _path = Path.Combine(dir, "boot-fails.json");
    }

    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                _entries = [];
                return;
            }

            try
            {
                string json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize(json, RamWatchJsonContext.Default.ListBootFailEntry);
                _entries = loaded ?? [];
            }
            catch
            {
                DataDirectory.ArchiveCorruptFile(_path);
                _entries = [];
            }
        }
    }

    public void Save(BootFailEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);

            while (_entries.Count > MaxEntries)
                _entries.RemoveAt(0);

            Persist();
        }
    }

    public List<BootFailEntry> GetAll()
    {
        lock (_lock)
        {
            return new List<BootFailEntry>(_entries);
        }
    }

    public List<BootFailEntry> GetRecent(int count)
    {
        lock (_lock)
        {
            int take = Math.Min(Math.Max(0, count), _entries.Count);
            int start = _entries.Count - take;
            return _entries.GetRange(start, take);
        }
    }

    public bool DeleteById(string bootFailId)
    {
        lock (_lock)
        {
            int idx = _entries.FindIndex(e => e.BootFailId == bootFailId);
            if (idx < 0)
                return false;

            _entries.RemoveAt(idx);
            Persist();
            return true;
        }
    }

    private void Persist()
    {
        string json = JsonSerializer.Serialize(_entries, RamWatchJsonContext.Default.ListBootFailEntry);
        string dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        string tempPath = Path.Combine(dir, $"boot-fails.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }
}
