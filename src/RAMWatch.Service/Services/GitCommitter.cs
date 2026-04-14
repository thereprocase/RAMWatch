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

    // Set during construction — stable after that.
    public bool IsAvailable { get; }
    public bool CanPush { get; }

    // Production constructor
    public GitCommitter(SettingsManager settings, ILogger logger)
        : this(settings, logger, DefaultRunProcessAsync) { }

    // Testable constructor — accepts an injected process runner
    internal GitCommitter(
        SettingsManager settings,
        ILogger logger,
        Func<ProcessStartInfo, CancellationToken, Task<ProcessResult>> processRunner)
    {
        _settings   = settings;
        _logger     = logger;
        _runProcess = processRunner;

        _channel = Channel.CreateUnbounded<GitCommitRequest>(
            new UnboundedChannelOptions { SingleReader = true });

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
            Directory.CreateDirectory(DataDirectory.HistoryRepoPath);
            Directory.CreateDirectory(DataDirectory.GhConfigPath);

            await RunGitAsync(["init"], LocalGitTimeoutMs, ct);

            var cfg = _settings.Current;
            string name  = string.IsNullOrWhiteSpace(cfg.GitUserDisplayName)
                ? "RAMWatch"
                : cfg.GitUserDisplayName;
            string email = "ramwatch@localhost";

            await RunGitAsync(["config", "user.name",  name],  LocalGitTimeoutMs, ct);
            await RunGitAsync(["config", "user.email", email], LocalGitTimeoutMs, ct);

            _drainTask = DrainAsync(_cts.Token);
            _logger.LogInformation("Git integration initialised. Repo: {Path}", DataDirectory.HistoryRepoPath);
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
            string repoPath = DataDirectory.HistoryRepoPath;

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
            if (cfg.EnableGitPush && CanPush && !string.IsNullOrWhiteSpace(cfg.GitRemoteRepo))
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

    private static void UpdateChangelog(string repoPath, string message, TimingSnapshot snap)
    {
        string changelogPath = Path.Combine(repoPath, "CHANGELOG.md");
        string newEntry = BuildChangelogEntry(message, snap);

        string existing = File.Exists(changelogPath) ? File.ReadAllText(changelogPath) : "";

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
            WorkingDirectory = DataDirectory.HistoryRepoPath
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
            WorkingDirectory = DataDirectory.HistoryRepoPath
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
        string tmp = path + ".tmp";
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
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }
}

/// <summary>
/// Result from running a subprocess.
/// </summary>
internal record ProcessResult(int ExitCode, string StdOut, string StdErr);
