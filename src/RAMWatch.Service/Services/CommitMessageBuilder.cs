using System.Text;
using RAMWatch.Core.Models;

namespace RAMWatch.Service.Services;

/// <summary>
/// Builds a git commit message from a GitCommitRequest.
/// Pure function: no I/O, no side effects.
/// </summary>
public static class CommitMessageBuilder
{
    /// <summary>
    /// Sanitize user-supplied string fields: strip leading # (prevents markdown
    /// heading injection in CHANGELOG.md), replace newlines, cap length.
    /// </summary>
    private static string Sanitize(string? s, int maxLen = 64)
    {
        if (string.IsNullOrEmpty(s)) return "";
        string clean = s.TrimStart('#').Replace('\n', ' ').Replace('\r', ' ').Trim();
        return clean.Length > maxLen ? clean[..maxLen] : clean;
    }

    public static string Build(GitCommitRequest request)
    {
        return request.Reason switch
        {
            GitCommitReason.ConfigChange   => BuildConfigChange(request),
            GitCommitReason.ValidationTest => BuildValidationTest(request),
            GitCommitReason.DriftDetected  => BuildDriftDetected(request),
            GitCommitReason.ManualSnapshot => "Manual snapshot",
            GitCommitReason.LkgUpdated     => "LKG updated",
            _                              => "RAMWatch update"
        };
    }

    private static string BuildConfigChange(GitCommitRequest request)
    {
        if (request.Change is null || request.Change.Changes.Count == 0)
            return "Config change";

        // Format: "Config change: CL 18->16, CWL 18->16"
        var sb = new StringBuilder("Config change: ");
        bool first = true;
        foreach (var (name, delta) in request.Change.Changes)
        {
            if (!first) sb.Append(", ");
            sb.Append($"{name} {delta.Before}->{delta.After}");
            first = false;
        }
        return sb.ToString();
    }

    private static string BuildValidationTest(GitCommitRequest request)
    {
        if (request.Validation is null)
            return "Validation test";

        var v    = request.Validation;
        var snap = request.CurrentSnapshot;
        string result = v.Passed ? "PASS" : "FAIL";

        // Metric: "12400%" or "25 cycles"
        string metric = v.MetricUnit.ToLowerInvariant() switch
        {
            "%" or "percent" or "coverage" => $"{v.MetricValue:0}{v.MetricUnit}",
            "cycles"                        => $"{v.MetricValue:0} cycles",
            _                               => $"{v.MetricValue:0}{v.MetricUnit}"
        };

        // Primaries: "16-22-22-42 tRFC577"
        string primaries = $"{snap.CL}-{snap.RCDRD}-{snap.RP}-{snap.RAS} tRFC{snap.RFC}";

        // Format: "Karhu 12400% PASS @ 16-22-22-42 tRFC577"
        return $"{Sanitize(v.TestTool)} {metric} {result} @ {primaries}";
    }

    private static string BuildDriftDetected(GitCommitRequest request)
    {
        if (request.DriftEvents is null || request.DriftEvents.Count == 0)
            return "Drift detected";

        // Report the first drift event inline; list extras as "+N more" if needed
        var first = request.DriftEvents[0];
        string summary = $"Drift detected: {Sanitize(first.TimingName)} {first.ExpectedValue}->{first.ActualValue} (auto-trained)";

        if (request.DriftEvents.Count > 1)
            summary += $" +{request.DriftEvents.Count - 1} more";

        return summary;
    }
}
