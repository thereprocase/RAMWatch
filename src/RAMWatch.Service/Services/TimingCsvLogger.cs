using System.Text;
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
    // Internal so the lock-in test can assert header column count == row column
    // count, catching the "added a field to FormatRow but forgot the header"
    // class of regression before any user CSV gets misaligned.
    internal const string Header =
        "timestamp,boot_id,mclk,fclk,uclk," +
        "cl,rcdrd,rcdwr,rp,ras,rc,cwl," +
        "rfc,rfc2,rfc4," +
        "rrds,rrdl,faw,wtrs,wtrl,wr,rtp," +
        "rdrdscl,wrwrscl," +
        "rdrdsc,rdrdsd,rdrddd,wrwrsc,wrwrsd,wrwrdd,rdwr,wrrd," +
        "refi,cke,stag,mod,mrd," +
        "phyrdl_a,phyrdl_b,powerdown,gdm,cmd2t," +
        "vsoc,vdimm," +
        "vcore,vddp,vddg_iod,vddg_ccd,vtt,vpp," +
        "procodt,rttnom,rttwr,rttpark," +
        "clkdrv,addrcmddrv,csodtdrv,ckedrv";

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

        _writer = new StreamWriter(stream) { AutoFlush = true };

        if (isNew)
        {
            _writer.WriteLine(Header);
        }
    }

    /// <summary>
    /// Format a single CSV row. Boolean fields use 1/0, voltages use 4 decimal places.
    /// </summary>
    // Field-list source of truth: TimingSnapshotFields (src/RAMWatch.Core/TimingSnapshotFields.cs).
    // The column order here is load-bearing — existing CSV files depend on it being eternal.
    // Do NOT reorder or derive columns from the helper at runtime: that would break header stability.
    // The lock-in test in TimingSnapshotFieldsTests.FormatRow_ColumnCount_MatchesHelperFieldCount
    // will fail if the helper grows a field the CSV row does not cover.
    // Reused per call to avoid boxing from string.Join(params object[]).
    [ThreadStatic] private static StringBuilder? _csvBuf;

    internal static string FormatRow(TimingSnapshot s)
    {
        var sb = _csvBuf ??= new StringBuilder(512);
        sb.Clear();

        sb.Append(s.Timestamp.ToString("o")).Append(',');
        sb.Append(s.BootId).Append(',');
        // Clocks
        sb.Append(s.MemClockMhz).Append(',').Append(s.FclkMhz).Append(',').Append(s.UclkMhz).Append(',');
        // Primaries
        sb.Append(s.CL).Append(',').Append(s.RCDRD).Append(',').Append(s.RCDWR).Append(',');
        sb.Append(s.RP).Append(',').Append(s.RAS).Append(',').Append(s.RC).Append(',').Append(s.CWL).Append(',');
        // tRFC
        sb.Append(s.RFC).Append(',').Append(s.RFC2).Append(',').Append(s.RFC4).Append(',');
        // Secondaries
        sb.Append(s.RRDS).Append(',').Append(s.RRDL).Append(',').Append(s.FAW).Append(',');
        sb.Append(s.WTRS).Append(',').Append(s.WTRL).Append(',').Append(s.WR).Append(',').Append(s.RTP).Append(',');
        // SCL + turn-around
        sb.Append(s.RDRDSCL).Append(',').Append(s.WRWRSCL).Append(',');
        sb.Append(s.RDRDSC).Append(',').Append(s.RDRDSD).Append(',').Append(s.RDRDDD).Append(',');
        sb.Append(s.WRWRSC).Append(',').Append(s.WRWRSD).Append(',').Append(s.WRWRDD).Append(',');
        sb.Append(s.RDWR).Append(',').Append(s.WRRD).Append(',');
        // Misc
        sb.Append(s.REFI).Append(',').Append(s.CKE).Append(',').Append(s.STAG).Append(',');
        sb.Append(s.MOD).Append(',').Append(s.MRD).Append(',');
        // PHY + controller
        sb.Append(s.PHYRDL_A).Append(',').Append(s.PHYRDL_B).Append(',');
        sb.Append(s.PowerDown ? 1 : 0).Append(',');
        sb.Append(s.GDM ? 1 : 0).Append(',').Append(s.Cmd2T ? 1 : 0).Append(',');
        // Voltages
        sb.Append(s.VSoc.ToString("F4")).Append(',').Append(s.VDimm.ToString("F4")).Append(',');
        sb.Append(s.VCore.ToString("F4")).Append(',').Append(s.VDDP.ToString("F4")).Append(',');
        sb.Append(s.VDDG_IOD.ToString("F4")).Append(',').Append(s.VDDG_CCD.ToString("F4")).Append(',');
        sb.Append(s.Vtt.ToString("F4")).Append(',').Append(s.Vpp.ToString("F4")).Append(',');
        // Signal integrity
        sb.Append(s.ProcODT.ToString("F1")).Append(',');
        sb.Append(s.RttNom).Append(',').Append(s.RttWr).Append(',').Append(s.RttPark).Append(',');
        sb.Append(s.ClkDrvStren.ToString("F1")).Append(',').Append(s.AddrCmdDrvStren.ToString("F1")).Append(',');
        sb.Append(s.CsOdtCmdDrvStren.ToString("F1")).Append(',').Append(s.CkeDrvStren.ToString("F1"));

        return sb.ToString();
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
