using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Append-only daily CSV logger for timing snapshots. One file per day: timings_YYYY-MM-DD.csv.
/// Uses FileShare.Read so mirror logger and external tools can read concurrently.
/// Handles midnight rotation. Retention is handled by the caller alongside event CSV retention.
/// </summary>
public sealed class TimingCsvLogger : IDisposable
{
    private readonly string _logDirectory;
    private readonly Lock _lock = new();
    private StreamWriter? _writer;
    private string _currentDate = "";
    private string _currentFilePath = "";

    // Column order matches the task spec and TimingSnapshot field order.
    private const string Header =
        "timestamp,boot_id,mclk,fclk,uclk," +
        "cl,rcdrd,rcdwr,rp,ras,rc,cwl," +
        "rfc,rfc2,rfc4," +
        "rrds,rrdl,faw,wtrs,wtrl,wr," +
        "rdrdscl,wrwrscl,gdm,cmd2t," +
        "vsoc,vdimm";

    /// <summary>
    /// The absolute path of the currently open CSV file, or empty string if no
    /// snapshot has been logged this session.
    /// </summary>
    public string CurrentFilePath
    {
        get { lock (_lock) { return _currentFilePath; } }
    }

    public TimingCsvLogger(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    /// <summary>
    /// Append a timing snapshot to the daily CSV. Handles midnight rotation automatically.
    /// </summary>
    public void LogSnapshot(TimingSnapshot snapshot)
    {
        lock (_lock)
        {
            string date = snapshot.Timestamp.ToString("yyyy-MM-dd");

            if (date != _currentDate)
            {
                RotateFile(date);
            }

            _writer?.WriteLine(FormatRow(snapshot));
            _writer?.Flush();
        }
    }

    private void RotateFile(string date)
    {
        _writer?.Dispose();
        _currentDate = date;
        _currentFilePath = Path.Combine(_logDirectory, $"timings_{date}.csv");

        bool isNew = !File.Exists(_currentFilePath);

        var stream = new FileStream(
            _currentFilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read); // B7: allow concurrent reads

        _writer = new StreamWriter(stream) { AutoFlush = false };

        if (isNew)
        {
            _writer.WriteLine(Header);
            _writer.Flush();
        }
    }

    /// <summary>
    /// Format a single CSV row. Boolean fields use 1/0, voltages use 4 decimal places.
    /// </summary>
    internal static string FormatRow(TimingSnapshot s)
    {
        return string.Join(",",
            s.Timestamp.ToString("o"),
            s.BootId,
            s.MemClockMhz,
            s.FclkMhz,
            s.UclkMhz,
            s.CL,
            s.RCDRD,
            s.RCDWR,
            s.RP,
            s.RAS,
            s.RC,
            s.CWL,
            s.RFC,
            s.RFC2,
            s.RFC4,
            s.RRDS,
            s.RRDL,
            s.FAW,
            s.WTRS,
            s.WTRL,
            s.WR,
            s.RDRDSCL,
            s.WRWRSCL,
            s.GDM ? 1 : 0,
            s.Cmd2T ? 1 : 0,
            s.VSoc.ToString("F4"),
            s.VDimm.ToString("F4")
        );
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
