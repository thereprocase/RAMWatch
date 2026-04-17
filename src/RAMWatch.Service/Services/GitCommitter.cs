using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RAMWatch.Core;
using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Enqueues git commit requests and drains them on a background task.
/// Writes CURRENT.md, LKG.md, timings.json, and CHANGELOG.md into the
/// history repo directory, then commits via git subprocess.
///
/// Uses a Channel for thread-safe enqueue-and-drain. Caller never blocks.
/// Circuit breaker stops attempts after 10 consecutive failures.
/// </summary>
public sealed class GitCommitter : IAsyncDisposable
{
    private readonly SettingsManager _settings;
    private readonly ILogger _logger;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<ProcessResult>> _runProcess;
    private readonly Channel<GitCommitRequest> _channel;
    private readonly CancellationTokenSource _cts = new();
    private Task? _drainTask;

    private const int MaxConsecutiveFailures = 10;
    private const int LocalGitTimeoutMs      = 30_000;
    private const int PushTimeoutMs          = 120_000;
    private const int MaxChangelogEntries    = 500;

    private int _consecutiveFailures;

    // Repo path — defaults to RepoPath, overridable for tests
    internal string RepoPath { get; }

    // Set during construction — stable after that.
    public bool IsAvailable { get; }
    public bool CanPush { get; }

    // Production constructor
    public GitCommitter(SettingsManager settings, ILogger logger)
        : this(settings, logger, DefaultRunProcessAsync, null) { }

    // Testable constructor — accepts an injected process runner and optional repo path
    internal GitCommitter(
        SettingsManager settings,
        ILogger logger,
        Func<ProcessStartInfo, CancellationToken, Task<ProcessResult>> processRunner,
        string? repoPath = null)
    {
        _settings   = settings;
        _logger     = logger;
        _runProcess = processRunner;
        RepoPath    = repoPath ?? DataDirectory.HistoryRepoPath;

        // Bounded so a drift storm + slow git (large CHANGELOG, slow disk)
        // can't pile up GitCommitRequest objects — each references a full
        // TimingSnapshot + designation map + validation list (~2 KB). An
        // unbounded channel would hold every request in memory until the
        // single-reader drain catches up. DropOldest preserves the latest
        // state, which is what matters for CURRENT.md / timings.json.
        _channel = Channel.CreateBounded<GitCommitRequest>(
            new BoundedChannelOptions(1000)
            {
                SingleReader = true,
                FullMode     = BoundedChannelFullMode.DropOldest,
            });

        IsAvailable = CheckToolOnPath("git");
        CanPush     = CheckToolOnPath("gh");
    }

    /// <summary>
    /// Initialises the history git repo and starts the background drain task.
    /// Safe to call when git is not available — becomes a no-op.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        if (!IsAvailable)
        {
            _logger.LogInformation("git not found on PATH — git integration disabled");
            return;
        }

        try
        {
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(DataDirectory.GhConfigPath);

            await RunGitAsync(["init"], LocalGitTimeoutMs, ct);

            var cfg = _settings.Current;
            // Sanitize display name — newlines could inject git config stanzas
            string rawName = cfg.GitUserDisplayName ?? "";
            rawName = rawName.Replace("\r", "").Replace("\n", "").Replace("\0", "").Trim();
            if (rawName.Length > 100) rawName = rawName[..100];
            string name = string.IsNullOrWhiteSpace(rawName) ? "RAMWatch" : rawName;
            string email = "ramwatch@localhost";

            await RunGitAsync(["config", "user.name",  name],  LocalGitTimeoutMs, ct);
            await RunGitAsync(["config", "user.email", email], LocalGitTimeoutMs, ct);

            _drainTask = DrainAsync(_cts.Token);
            _logger.LogInformation("Git integration initialised. Repo: {Path}", RepoPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Git initialisation failed — git commits disabled");
        }
    }

    /// <summary>
    /// Enqueues a commit request. Returns immediately. No-op if git is unavailable,
    /// integration is disabled, or the circuit breaker has tripped.
    /// </summary>
    public void Enqueue(GitCommitRequest request)
    {
        if (!IsAvailable) return;
        if (!_settings.Current.EnableGitIntegration) return;
        if (_consecutiveFailures >= MaxConsecutiveFailures) return;
        if (_drainTask is null) return;

        _channel.Writer.TryWrite(request);
    }

    /// <summary>
    /// Reset the circuit breaker (e.g., on settings change).
    /// </summary>
    public void ResetFailures()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _cts.CancelAsync();

        if (_drainTask is not null)
        {
            try { await _drainTask; }
            catch (OperationCanceledException) { }
        }

        _cts.Dispose();
    }

    // -------------------------------------------------------------------------
    // Background drain
    // -------------------------------------------------------------------------

    private async Task DrainAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var req in _channel.Reader.ReadAllAsync(ct))
            {
                if (_consecutiveFailures >= MaxConsecutiveFailures)
                {
                    _logger.LogWarning(
                        "Git circuit breaker open ({Failures} consecutive failures) — skipping commit",
                        _consecutiveFailures);
                    continue;
                }

                await ProcessCommitAsync(req, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private async Task ProcessCommitAsync(GitCommitRequest req, CancellationToken ct)
    {
        try
        {
            string repoPath = RepoPath;

            // 1. Write files
            var cfg = _settings.Current;
            var validations = req.RecentValidations ?? new List<ValidationResult>();
            var lastValidation = req.Validation
                ?? validations.Where(v => v.Passed).MaxBy(v => v.Timestamp);

            WriteAtomic(
                Path.Combine(repoPath, "CURRENT.md"),
                CurrentMdBuilder.Build(req.CurrentSnapshot, req.Designations, lastValidation));

            string? lkgMd = LkgMdBuilder.Build(req.LkgSnapshot, req.Designations, lastValidation);
            if (lkgMd is not null)
                WriteAtomic(Path.Combine(repoPath, "LKG.md"), lkgMd);

            WriteAtomic(
                Path.Combine(repoPath, "timings.json"),
                JsonSerializer.Serialize(req.CurrentSnapshot, RamWatchJsonContext.Default.TimingSnapshot));

            string message = CommitMessageBuilder.Build(req);
            UpdateChangelog(repoPath, message, req.CurrentSnapshot);

            // 2. Stage named files only (never -A)
            var filesToAdd = new List<string> { "CURRENT.md", "timings.json", "CHANGELOG.md" };
            if (lkgMd is not null) filesToAdd.Add("LKG.md");

            var addArgs = new List<string> { "add", "--" };
            addArgs.AddRange(filesToAdd);
            await RunGitAsync(addArgs, LocalGitTimeoutMs, ct);

            // 3. Commit
            var commitResult = await RunGitAsync(
                ["commit", "-m", message],
                LocalGitTimeoutMs,
                ct);

            if (commitResult.ExitCode != 0)
            {
                // "nothing to commit" is exit code 1 on some git versions — not a real failure
                if (commitResult.StdOut.Contains("nothing to commit") ||
                    commitResult.StdErr.Contains("nothing to commit"))
                {
                    return;
                }

                _logger.LogWarning("git commit failed (exit {Code}): {Err}", commitResult.ExitCode, commitResult.StdErr);
                Interlocked.Increment(ref _consecutiveFailures);
                return;
            }

            Interlocked.Exchange(ref _consecutiveFailures, 0);

            // 4. Optional push via gh
            if (cfg.EnableGitPush && CanPush &&
                !string.IsNullOrWhiteSpace(cfg.GitRemoteRepo) &&
                AppSettings.IsValidRemoteRepo(cfg.GitRemoteRepo))
            {
                await PushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Git commit failed");
            Interlocked.Increment(ref _consecutiveFailures);

            if (_consecutiveFailures >= MaxConsecutiveFailures)
            {
                _logger.LogWarning(
                    "Git circuit breaker tripped after {Max} consecutive failures — " +
                    "git commits suspended until settings are updated",
                    MaxConsecutiveFailures);
            }
        }
    }

    // -------------------------------------------------------------------------
    // CHANGELOG.md management
    // -------------------------------------------------------------------------

    // Defence against a pathologically grown or adversarial CHANGELOG.md
    // (e.g. imported from another machine, tampered on disk). Reading the
    // whole file into memory on every commit means a 100 MB file costs
    // 100 MB of transient allocation; an AOT service on a constrained box
    // could OOM.
    private const long MaxChangelogBytes = 16L * 1024 * 1024;

    private static void UpdateChangelog(string repoPath, string message, TimingSnapshot snap)
    {
        string changelogPath = Path.Combine(repoPath, "CHANGELOG.md");
        string newEntry = BuildChangelogEntry(message, snap);

        string existing = "";
        if (File.Exists(changelogPath))
        {
            var info = new FileInfo(changelogPath);
            if (info.Length > MaxChangelogBytes)
            {
                // Refuse to concatenate a runaway file. TrimChangelog would
                // bring it back under MaxChangelogEntries anyway, so we can
                // safely discard the tail and preserve only the head.
                using var stream = info.OpenRead();
                var buf = new byte[MaxChangelogBytes];
                int read = stream.Read(buf, 0, buf.Length);
                existing = Encoding.UTF8.GetString(buf, 0, read);
            }
            else
            {
                existing = File.ReadAllText(changelogPath);
            }
        }

        // Prepend new entry at top, then trim to max entries.
        string updated = newEntry + existing;
        updated = TrimChangelog(updated, MaxChangelogEntries);

        WriteAtomic(changelogPath, updated);
    }

    private static string BuildChangelogEntry(string message, TimingSnapshot snap)
    {
        string date = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        return $"## {date}\n{message}\n\n";
    }

    /// <summary>
    /// Trims a CHANGELOG.md to at most maxEntries H2 sections.
    /// Entries are delimited by "## " at the start of a line.
    /// </summary>
    private static string TrimChangelog(string content, int maxEntries)
    {
        if (string.IsNullOrEmpty(content)) return content;

        var lines  = content.Split('\n');
        int count  = 0;
        int cutAt  = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("## ", StringComparison.Ordinal))
            {
                count++;
                if (count > maxEntries)
                {
                    cutAt = i;
                    break;
                }
            }
        }

        if (cutAt < 0) return content;

        // Keep only lines before the (maxEntries+1)th entry
        return string.Join('\n', lines[..cutAt]);
    }

    // -------------------------------------------------------------------------
    // Subprocess helpers
    // -------------------------------------------------------------------------

    private async Task<ProcessResult> RunGitAsync(
        IEnumerable<string> args,
        int timeoutMs,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepoPath
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);

        try
        {
            return await _runProcess(psi, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout rather than shutdown
            _logger.LogWarning("git command timed out after {Ms}ms: {Args}",
                timeoutMs, string.Join(' ', psi.ArgumentList));
            throw;
        }
    }

    private async Task PushAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo("gh")
        {
            WorkingDirectory = RepoPath
        };
        psi.ArgumentList.Add("repo");
        psi.ArgumentList.Add("sync");
        psi.Environment["GH_CONFIG_DIR"] = DataDirectory.GhConfigPath;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PushTimeoutMs);

        try
        {
            var result = await _runProcess(psi, timeout.Token);
            if (result.ExitCode != 0)
                _logger.LogWarning("gh repo sync failed (exit {Code}): {Err}", result.ExitCode, result.StdErr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("gh push timed out after {Ms}ms", PushTimeoutMs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "gh push failed");
        }
    }

    // -------------------------------------------------------------------------
    // Atomic file write (write to .tmp then rename)
    // -------------------------------------------------------------------------

    private static void WriteAtomic(string path, string content)
    {
        // Guid suffix prevents collisions if this helper is ever called
        // concurrently for the same path. ProcessCommitAsync drains
        // single-reader today, so races aren't reachable, but the journals
        // use this pattern — matching it here avoids a silent regression if
        // a future caller runs in parallel with the drain loop.
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, content, Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Tool detection
    // -------------------------------------------------------------------------

    private static bool CheckToolOnPath(string toolName)
    {
        try
        {
            var psi = new ProcessStartInfo(toolName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            // Use --version for git; for gh any argument that doesn't error out.
            if (toolName == "git")
                psi.ArgumentList.Add("--version");
            else
                psi.ArgumentList.Add("--version");

            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Default process runner (production)
    // -------------------------------------------------------------------------

    private static async Task<ProcessResult> DefaultRunProcessAsync(
        ProcessStartInfo psi,
        CancellationToken ct)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError  = true;
        psi.UseShellExecute        = false;
        psi.CreateNoWindow         = true;

        using var process = Process.Start(psi)!;
        try
        {
            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }
}

/// <summary>
/// Result from running a subprocess.
/// </summary>
internal record ProcessResult(int ExitCode, string StdOut, string StdErr);
