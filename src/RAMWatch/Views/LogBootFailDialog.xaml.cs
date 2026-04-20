using System.Windows;
using RAMWatch.Core.Models;

namespace RAMWatch.Views;

/// <summary>
/// Dialog for manually logging a failed boot attempt — used when RAMWatch
/// missed the capture window (no POST, crash before login, crash before the
/// cold-tier UMC read completed). Collects failure kind, attempted changes
/// the user typed into BIOS, and free-text notes. Returns a populated
/// LogBootFailMessage on OK.
/// </summary>
public partial class LogBootFailDialog : System.Windows.Window
{
    // Populated when the user clicks Log Attempt; null when cancelled.
    public LogBootFailMessage? Result { get; private set; }

    // Pre-filled by the caller when a recent stable snapshot is known.
    // Becomes BaseSnapshotId on the outgoing message so the service can
    // anchor the attempted changes against a real before-state.
    public string? BaseSnapshotId { get; set; }

    private static readonly (BootFailKind Kind, string Display)[] Kinds =
    [
        (BootFailKind.NoPost,   "No POST — config rejected at boot"),
        (BootFailKind.BootLoop, "Boot loop — repeated retrain attempts"),
        (BootFailKind.Unstable, "Unstable — booted but crashed"),
        (BootFailKind.Other,    "Other"),
    ];

    public LogBootFailDialog()
    {
        InitializeComponent();

        KindCombo.ItemsSource = Kinds;
        KindCombo.DisplayMemberPath = "Display";
        KindCombo.SelectedIndex = 2; // default to Unstable — the common case

        ChangesBox.Text = "";
        NotesBox.Text = "";
    }

    private void OnLogAttempt(object sender, RoutedEventArgs e)
    {
        ValidationErrorText.Text = "";

        if (!TryBuildMessage(out var message, out var error))
        {
            ValidationErrorText.Text = error;
            return;
        }

        Result = message;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
        Close();
    }

    private bool TryBuildMessage(out LogBootFailMessage? message, out string error)
    {
        message = null;
        error = "";

        if (KindCombo.SelectedItem is not (BootFailKind kind, _))
        {
            error = "Select a failure type.";
            return false;
        }

        if (!TryParseChanges(ChangesBox.Text, out var attempted, out var parseError))
        {
            error = parseError;
            return false;
        }

        var notes = NotesBox.Text.Trim();

        message = new LogBootFailMessage
        {
            Type = "logBootFail",
            RequestId = Guid.NewGuid().ToString("N"),
            AttemptTimestamp = DateTime.UtcNow,
            Kind = kind,
            BaseSnapshotId = BaseSnapshotId,
            AttemptedChanges = attempted.Count > 0 ? attempted : null,
            Notes = string.IsNullOrEmpty(notes) ? null : notes
        };

        return true;
    }

    /// <summary>
    /// Parse one-FIELD=VALUE-per-line input. Blank lines are skipped.
    /// Rejects lines without '=' or with an empty field/value.
    /// Duplicate field names: last one wins (common spreadsheet behaviour).
    /// </summary>
    private static bool TryParseChanges(string input, out Dictionary<string, string> result, out string error)
    {
        result = new Dictionary<string, string>(StringComparer.Ordinal);
        error = "";

        if (string.IsNullOrWhiteSpace(input))
            return true; // empty is allowed — Notes-only entry is valid

        var lines = input.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;

            int eq = line.IndexOf('=');
            if (eq <= 0 || eq == line.Length - 1)
            {
                error = $"Line {i + 1}: expected FIELD=VALUE (got '{line}').";
                result.Clear();
                return false;
            }

            var field = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (field.Length == 0 || value.Length == 0)
            {
                error = $"Line {i + 1}: field and value must be non-empty.";
                result.Clear();
                return false;
            }

            result[field] = value;
        }

        return true;
    }
}
