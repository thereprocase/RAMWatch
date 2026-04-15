using System.Text.Json;
using RAMWatch.Core;
using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Persists tuning eras to %ProgramData%\RAMWatch\eras.json.
/// Service is the sole writer. Atomic write-to-temp-then-rename (B7).
/// </summary>
public sealed class EraJournal
{
    private readonly string _path;
    private readonly Lock _lock = new();
    private List<TuningEra> _eras = [];

    public EraJournal(string? dataDirectory = null)
    {
        string dir = dataDirectory ?? DataDirectory.BasePath;
        _path = Path.Combine(dir, "eras.json");
    }

    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                _eras = [];
                return;
            }

            try
            {
                string json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize(json, RamWatchJsonContext.Default.ListTuningEra);
                _eras = loaded ?? [];
            }
            catch
            {
                _eras = [];
            }
        }
    }

    /// <summary>
    /// Returns the currently active era (EndTimestamp == null), or null if none.
    /// </summary>
    public TuningEra? GetActive()
    {
        lock (_lock)
        {
            var active = _eras.Find(e => e.EndTimestamp is null);
            return active is not null ? CloneEra(active) : null;
        }
    }

    public List<TuningEra> GetAll()
    {
        lock (_lock)
        {
            return _eras.Select(CloneEra).ToList();
        }
    }

    private static TuningEra CloneEra(TuningEra e) => new()
    {
        EraId = e.EraId,
        Name = e.Name,
        StartTimestamp = e.StartTimestamp,
        EndTimestamp = e.EndTimestamp,
        Notes = e.Notes,
    };

    /// <summary>
    /// Create a new era. If another era is active, close it first.
    /// </summary>
    public TuningEra Create(string name)
    {
        lock (_lock)
        {
            // Close any active era.
            var active = _eras.Find(e => e.EndTimestamp is null);
            if (active is not null)
                active.EndTimestamp = DateTime.UtcNow;

            var era = new TuningEra
            {
                EraId = Guid.NewGuid().ToString("N"),
                Name = name is { Length: > 256 } ? name[..256] : name,
                StartTimestamp = DateTime.UtcNow
            };
            _eras.Add(era);
            Persist();
            return era;
        }
    }

    /// <summary>
    /// Close an era by setting its EndTimestamp.
    /// </summary>
    public bool Close(string eraId)
    {
        lock (_lock)
        {
            var era = _eras.Find(e => e.EraId == eraId);
            if (era is null || era.EndTimestamp is not null)
                return false;

            era.EndTimestamp = DateTime.UtcNow;
            Persist();
            return true;
        }
    }

    private void Persist()
    {
        string json = JsonSerializer.Serialize(_eras, RamWatchJsonContext.Default.ListTuningEra);
        string dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        string tempPath = Path.Combine(dir, $"eras.{Guid.NewGuid():N}.tmp");

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
